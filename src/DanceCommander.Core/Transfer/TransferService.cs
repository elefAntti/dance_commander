using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Operations;
using Microsoft.Extensions.Logging;

namespace DanceCommander.Core.Transfer;

public interface ITransferService
{
    /// <summary>Starts a copy or move. It appears in the job strip through <see cref="IJobService"/>.</summary>
    TransferJob Enqueue(IReadOnlyList<FsUri> sources, FsUri targetDirectory, TransferMode mode, IDecisionHost decisions);
}

/// <summary>
/// Copy and move, including across providers — which is why this is a service and not a method on
/// <see cref="IFileSystemProvider"/> (§4.4). Jobs run in the background against a bounded number
/// of slots and report through the job strip; nothing here ever blocks the user.
/// </summary>
public sealed class TransferService(
    IProviderRegistry providers,
    IUndoStack undo,
    IActivityLog activity,
    IRecycleBin recycleBin,
    IJobService jobs,
    ILogger<TransferService> log,
    IRelocationFeed? relocations = null,
    IArrivalFeed? arrivals = null,
    IUsageFinder? usage = null) : ITransferService
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>How long a commit runs before the job says what it is waiting for.</summary>
    private static readonly TimeSpan FinishingAfter = TimeSpan.FromMilliseconds(500);

    private readonly SemaphoreSlim _slots = new(2);

    public TransferJob Enqueue(
        IReadOnlyList<FsUri> sources, FsUri targetDirectory, TransferMode mode, IDecisionHost decisions)
    {
        var job = new TransferJob(sources, targetDirectory, mode);
        jobs.Add(job);

        // From the moment it is asked for until its own undo is on the stack: Ctrl+Z meanwhile,
        // the reflex for "stop that", undid the operation before it beside the running job, whose
        // files that undo might be moving (review, 2026-10-22). Del and Compress already did this.
        // Let go once it is recorded and before it says it has finished, so a Ctrl+Z the moment
        // it ends finds its undo and not "still copying"; and again at the very end, whatever
        // happened, as the handle lets go only once.
        var working = undo.Working($"{(mode == TransferMode.Copy ? "copying" : "moving")} to {targetDirectory.Name}");
        _ = Task.Run(async () =>
        {
            using (working)
                await RunAsync(job, decisions, working).ConfigureAwait(false);
        });
        return job;
    }

    private async Task RunAsync(TransferJob job, IDecisionHost decisions, IUndoSlot working)
    {
        try
        {
            await _slots.WaitAsync(job.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled while queued: nothing was done, and that is said. It used to stay
            // "Waiting to start" for good, which no close could get past.
            working.Dispose();
            job.CurrentName = "Cancelled before it started";
            job.State = JobState.Cancelled;
            activity.Record(job.Title, "(cancelled)", ActivityOutcome.Failed, "Cancelled before it started.");
            return;
        }

        // Declared out here so a job that stops part way can put back what it staged and record
        // what it did get done.
        ReplacementPlan? staging = null;
        var done = new Done { Working = working };

        // Once a second the strip's line is brought up to date: the time left, or how long nothing
        // has moved, which can change while no byte arrives to say so.
        using var ticker = new Timer(_ => job.Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        Begin(job);
        try
        {
            job.State = JobState.Running;
            job.Started();

            // Copies into their own folder first, and out of the collision question: a folder
            // pasted beside itself is not landing on anything, and asking whether to replace it
            // offered to put the original itself through a replace.
            var duplicates = await PlanDuplicatesAsync(job).ConfigureAwait(false);

            // Before anything is copied: does a whole folder land on a folder of the same name?
            // Asked here rather than per file, because it is one decision about one thing.
            // Nothing in its own folder is scanned, duplicate or not: it can only collide with itself.
            staging = await PlanFolderCollisionsAsync(
                job, decisions, job.Sources.Where(s => s.Parent is not { } p || !p.Equals(job.TargetDirectory)).ToList())
                .ConfigureAwait(false);
            if (staging is null)
            {
                working.Dispose();
                job.CurrentName = "Cancelled before it started";
                job.State = JobState.Cancelled;
                activity.Record(job.Title, "(cancelled)", ActivityOutcome.Failed, "Cancelled before it started.");
                return;
            }

            var plan = staging;
            foreach (var (source, free) in duplicates)
                plan.Duplicate(source.Name, free);

            // A folder moved on one disk is one rename, whatever it holds: no walk, no file by
            // file. Moving each file took a tree of thousands minutes, with a bar to watch, and a
            // stop part way left it split between two places (2026-10-26). What can't go whole
            // goes as before.
            var whole = job.Mode == TransferMode.Move
                ? await MoveWholeFoldersAsync(job, plan, done).ConfigureAwait(false)
                : new HashSet<FsUri>();
            var items = await FlattenAsync(job, whole).ConfigureAwait(false);

            // A folder being replaced is built beside the old one and swapped in at the end, so a
            // failure part way leaves the original untouched rather than half-deleted.
            if (plan.Skipped.Count > 0)
                items = items.Where(i => !IsUnder(i.RelativePath, plan.Skipped)).ToList();
            // A name from a server or an archive is only text, and on Windows "..\..\x" or "a\b"
            // in it is a path: written as it stands, it lands outside the folder being copied
            // into. What Windows cannot hold as one name is left out, with everything inside it.
            if (job.TargetDirectory.Anchor.IsLocal)
            {
                done.Unsafe.AddRange(items.Where(i => !IsSafeOnWindows(i.RelativePath)).Select(i => i.RelativePath));
                if (done.Unsafe.Count > 0)
                    items = items.Where(i => IsSafeOnWindows(i.RelativePath)).ToList();
            }

            // Files the volume can't hold, a FAT32 stick's 4 GB: asked about once, before anything is
            // written, rather than failing the job at the first of them in the disk's own words.
            if (items.Any(i => !i.IsDirectory && i.Size > VolumeLimits.Smallest) &&
                VolumeLimits.Of(job.TargetDirectory.Anchor) is { } limit &&
                items.Where(i => !i.IsDirectory && i.Size > limit.MaxFile).ToList() is { Count: > 0 } tooBig)
            {
                var answer = await WhileAsking(job, () => decisions.AskAsync(
                    new ChoiceRequest(
                        tooBig.Count == 1
                            ? $"{tooBig[0].Source.Name} is too big for {job.TargetDirectory.Anchor.Path[..2]}."
                            : $"{tooBig.Count:N0} files are too big for {job.TargetDirectory.Anchor.Path[..2]}.",
                        [
                            new DecisionOption("skip", tooBig.Count == 1 ? "Skip it" : "Skip them", IsDefault: true),
                            new DecisionOption("cancel", "Cancel", IsCancel: true),
                        ],
                        $"Its file system ({limit.Format}) holds files of up to {limit.Said}. Formatting it as exFAT or NTFS " +
                        "lifts that, and erases what is on it." +
                        (tooBig.Count > 1 ? " " + string.Join(", ", tooBig.Take(5).Select(i => i.Source.Name)) + (tooBig.Count > 5 ? ", …" : ".") : ""),
                        NoticeSeverity.Warning),
                    job.Token)).ConfigureAwait(false);

                if (answer != "skip")
                {
                    await StopAsync(job, plan, done, JobState.Cancelled, error: null).ConfigureAwait(false);
                    return;
                }

                done.TooBig.AddRange(tooBig.Select(i => i.Source.Name));
                done.LeftOut.AddRange(tooBig.Select(i => i.RelativePath));
                items = items.Except(tooBig).ToList();
            }

            job.ItemsTotal = done.Folders.Count + items.Count(i => !i.IsDirectory);
            job.BytesTotal = items.Sum(i => i.Size);

            var policy = ConflictPolicy.Ask;
            using var replaced = new ReplaceAnswer(); // when "Replace" for the rest was asked (ChangedSince)

            foreach (var item in items)
            {
                job.Token.ThrowIfCancellationRequested();
                job.CurrentName = item.RelativePath;

                // Last, straight to where they will stay: see MoveLinksAsync.
                if (item.IsLink)
                    continue;

                var target = Resolve(job.TargetDirectory, plan.Redirect(item.RelativePath));
                var targetProvider = providers.Resolve(target);

                if (item.IsDirectory)
                {
                    // A folder cannot be created where a file of that name already sits. Saying so
                    // plainly beats the framework's "a file or directory with the same name".
                    var already = await targetProvider.StatAsync(target, job.Token).ConfigureAwait(false);
                    if (already is { IsDirectory: false })
                    {
                        throw new IOException(
                            $"Cannot create the folder \"{target.Name}\" in {job.TargetDirectory.Name}: " +
                            "a file of that name is already there. Rename or remove it first.");
                    }

                    // Only the folders this job made: undoing a copy removes those once empty, and
                    // never one that was there before. Made only when not there: Windows takes a
                    // second make in its stride, while SFTP's mkdir and WebDAV's MKCOL refuse it,
                    // and a merge into a folder on a server failed on its first line (2026-11-03).
                    if (already is null)
                    {
                        await targetProvider.CreateDirectoryAsync(target, job.Token).ConfigureAwait(false);
                        done.MadeDirectories.Add(target);
                    }
                    job.Estimate.Progressed(job.Now);
                    // Where it will be once any replacement is swapped in, not where it is built.
                    if (IsTopLevel(item.RelativePath))
                    {
                        done.Landed.Add(job.TargetDirectory.Child(plan.Final(item.RelativePath)));
                        if (plan.IsDuplicate(item.RelativePath))
                            done.Duplicates.Add(plan.Final(item.RelativePath));
                    }
                    continue;
                }

                // From here to the next file's start is this file's time, whatever becomes of it.
                job.Estimate.FileStarted(job.Now);

                // Before the look and any question about it: a file another transfer lands while
                // the question is open is news to the answer (review, 2026-10-23).
                var lookedAt = DateTimeOffset.Now;
                // One look for both questions below, a round trip on a server for every file.
                var there = await targetProvider.StatAsync(target, job.Token).ConfigureAwait(false);

                // The same clash the other way round: a file landing on a folder.
                if (there is { IsDirectory: true })
                {
                    throw new IOException(
                        $"Cannot copy the file \"{target.Name}\" into {job.TargetDirectory.Name}: " +
                        "a folder of that name is already there. Rename or remove it first.");
                }

                // A file that was there keeps its own mode, which the provider sees to; only a new
                // one has its mode decided here.
                var isNew = true;
                // Two files of one name in one job, as a flattened tree or a search easily gives:
                // the second never replaces the first, whatever was answered for a real clash,
                // because the first has just left its own folder and would be lost (§1.6).
                if (done.Written.Contains(target))
                {
                    target = await UniqueAsync(targetProvider, target).ConfigureAwait(false);
                    done.Renamed++;
                    there = await targetProvider.StatAsync(target, job.Token).ConfigureAwait(false);
                }

                if (there is not null)
                {
                    // One file under two names, through a subst drive or a junction: replacing
                    // "the other one" would be replacing the source itself (§1.6).
                    if (await IsSameFileAsync(item.Source, target, job.Token).ConfigureAwait(false))
                    {
                        done.SameFile++;
                        job.BytesDone += item.Size;
                        continue;
                    }

                    isNew = false;
                    var answeredBefore = policy != ConflictPolicy.Ask;
                    if (policy == ConflictPolicy.Ask)
                    {
                        var stampAsked = StampOf(targetProvider, target);
                        // A server keeps no change time to tell a later save by: what its folders
                        // held is listed while the question stands, in case the answer is Replace.
                        var snapshot = targetProvider is IChangeStamps
                            ? null
                            : new ServerSnapshot(targetProvider, FoldersAhead(job, plan, items, item), job.Token);
                        // This folder's listing before the question shows: the one most likely to
                        // be saved into while it stands, and listed late it would hold the save.
                        try
                        {
                            if (snapshot is not null)
                                await snapshot.HeldAsync(target).ConfigureAwait(false);
                            policy = await WhileAsking(job, () => AskAsync(decisions, target, job.Token)).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Not yet the answer's to dispose: the listing ahead stops with the job.
                            snapshot?.Dispose();
                            throw;
                        }
                        // As the question was put, not as it was answered: what was saved while it
                        // stood open is no more what was asked about than what comes later.
                        if (policy == ConflictPolicy.Overwrite)
                            replaced.Answered(lookedAt, target, stampAsked, there, snapshot);
                        else
                            snapshot?.Dispose();
                    }

                    // "Replace" for the rest was said about files there then. One another of our
                    // transfers has put there since this one began is someone's newer file, and
                    // it was written over unasked (review, 2026-10-22; AUDIT.md §2 kind 10); so is
                    // one any other program wrote or put there after the answer.
                    // The file asked about too: one saved while the question stood open is not the
                    // one the answer was about (review, 2026-10-28).
                    var thisOne = policy;
                    if (policy == ConflictPolicy.Overwrite)
                    {
                        if (answeredBefore && LandedByAnother(target, job))
                            thisOne = await WhileAsking(job, () => AskAboutArrivalAsync(decisions, target, byAnotherTransfer: true, job.Token)).ConfigureAwait(false);
                        else if (await ChangedSinceAsync(targetProvider, target, there, replaced, job.Token).ConfigureAwait(false))
                            thisOne = await WhileAsking(job, () => AskAboutArrivalAsync(decisions, target, byAnotherTransfer: false, job.Token)).ConfigureAwait(false);
                    }

                    switch (thisOne)
                    {
                        case ConflictPolicy.Skip:
                            done.Skipped++;
                            job.BytesDone += item.Size;
                            continue;
                        case ConflictPolicy.KeepBoth:
                            target = await UniqueAsync(targetProvider, target).ConfigureAwait(false);
                            isNew = true;
                            break;
                        case ConflictPolicy.Overwrite:
                            break;
                        default:
                            await StopAsync(job, plan, done, JobState.Cancelled, error: null).ConfigureAwait(false);
                            return;
                    }
                }

                // A file another program has open: asked about, with Skip as for a conflict,
                // rather than failing the whole job (§1.3). Bytes counted by a failed attempt are
                // taken back, so a retry does not take the bar past the end.
                byte[]? head = null;
                var bytesBefore = job.BytesDone;
                var landingAt = FinalOf(plan, target);
                var attempt = new Attempt(Replace: !isNew) { DecidedAt = lookedAt, Landed = () => NoteLanding(landingAt, job) };
                InUseOutcome outcome;
                try
                {
                    outcome = await InUse.RetryAsync(
                        [item.Source],
                        async () =>
                        {
                            if (!attempt.Copied)
                                job.BytesDone = bytesBefore;
                            head = await TransferOneAsync(job, item, target, attempt).ConfigureAwait(false);
                        },
                        new AskingFor(job, decisions), usage, offerSkip: true, job.Token, alsoAskAbout: [target]).ConfigureAwait(false);
                }
                catch (Exception) when (attempt.Copied)
                {
                    // The copy landed and the original then could not go, for any reason, not only
                    // because it was in use: the file is in both places, and the failed job's log
                    // has to say so (found by FaultSweepTests).
                    done.Written.Add(target);
                    NoteLanding(FinalOf(plan, target), job);
                    done.CopiedOnly.Add(target.Name);
                    throw;
                }

                // A move whose copy landed and whose original was then in use: the file is in both
                // places now, which the log has to say. It used to be counted as skipped, "because
                // it already existed", while a file it replaced was already gone.
                if (outcome != InUseOutcome.Done && attempt.Copied)
                {
                    done.Written.Add(target);
                    NoteLanding(FinalOf(plan, target), job);
                    done.CopiedOnly.Add(target.Name);
                }

                if (outcome == InUseOutcome.Skipped)
                {
                    if (!attempt.Copied)
                    {
                        done.InUse.Add(item.Source.Name);
                        done.LeftOut.Add(item.RelativePath);
                    }
                    job.BytesDone = bytesBefore + item.Size;
                    continue;
                }
                if (outcome == InUseOutcome.GaveUp)
                {
                    await StopAsync(job, plan, done, JobState.Cancelled, error: null).ConfigureAwait(false);
                    return;
                }

                done.Written.Add(target);
                NoteLanding(FinalOf(plan, target), job);
                var landed = new Landed(item.Source, target)
                {
                    Replaced = attempt.Replace,
                    Written = attempt.Written,
                    SourceRead = attempt.SourceRead,
                    LandedAt = DateTimeOffset.Now,
                    ChangeStamp = ChangeStampOf(targetProvider, target, attempt.Written),
                };
                done.Files.Add(landed);
                replaced.LearnClockFrom(targetProvider, target, landed.LandedAt);

                if (head is not null)
                {
                    if (isNew && targetProvider is IUnixModes modes)
                        landed.Mode = await ModeForNewFileAsync(item.Source, target, head, modes, job.Token).ConfigureAwait(false);

                    // Said whether or not the mode was ours to set: a script the server cannot run
                    // is worth knowing about on any Unix host, and nothing here rewrites its bytes.
                    if (targetProvider is IUnixModes &&
                        ExecutableContent.Detect(head) == ExecutableKind.ScriptWithWindowsLineEndings)
                    {
                        done.Unrunnable.Add(target.Name);
                    }
                }

                job.ItemsDone++;
                // Its own name, which "keep both" may have changed.
                if (IsTopLevel(item.RelativePath))
                {
                    done.Landed.Add(target);
                    // Said only once it has landed: a duplicate skipped or cut off by a cancel was
                    // not made, whatever the plan had in mind.
                    if (plan.IsDuplicate(item.RelativePath))
                        done.Duplicates.Add(target.Name);
                }
            }

            job.Estimate.Finished(job.Now);
            done.ReplacedFolders = plan.Staged.Count;
            await CommitReplacementsAsync(job, plan, done).ConfigureAwait(false);

            if (job.Mode == TransferMode.Move)
            {
                await MoveLinksAsync(job, plan, done, items.Where(i => i.IsLink).ToList()).ConfigureAwait(false);
                await RemoveEmptiedSourceDirectoriesAsync(job, plan, items, done).ConfigureAwait(false);
                foreach (var source in job.Sources)
                {
                    // Only for the record: a failure to ask is "still there", never a failed job.
                    try
                    {
                        if (await providers.Resolve(source).StatAsync(source, CancellationToken.None).ConfigureAwait(false) is null)
                            done.SourcesGone.Add(source);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogInformation(ex, "Could not tell whether {Source} is gone", source);
                    }
                }
            }

            // Record before completing, never after. Completing is what makes the job observably
            // finished, and pressing Ctrl+Z in the window between the two would undo the previous
            // operation instead of this one — silently putting back the wrong files. The safety
            // net has to be in place before anyone is told they have landed.
            Record(job, done, stopped: null);
            // Let go before telling anyone: the echo asks, once its turn comes on the UI thread,
            // whether Ctrl+Z would undo this now, and found it still held (review, 2026-10-30).
            working.Dispose();
            try
            {
                arrivals?.Report(done.Landed);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "An arrival listener failed");
            }
            // Nothing landed, and not by the user's choice: said as a failure, and kept in the
            // strip, rather than "Copied 0 items" retiring as a success (driven, 2026-10-28).
            if (NothingLanded(done) && LeftOut(done) is { } why)
            {
                job.Error = why;
                job.State = JobState.Failed;
                return;
            }
            job.State = JobState.Completed;
        }
        catch (OperationCanceledException)
        {
            await StopAsync(job, staging, done, JobState.Cancelled, error: null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Transfer failed");
            await StopAsync(job, staging, done, JobState.Failed, ex.Message).ConfigureAwait(false);
        }
        finally
        {
            End(job);
            _slots.Release();

            // A job that worked retires itself; one that failed or was cancelled stays until the
            // user has seen it and dismissed it.
            if (job.State == JobState.Completed)
                _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ => jobs.Forget(job), TaskScheduler.Default);
        }
    }

    /// <summary>One file that arrived, where it went, and the mode it was given if any.</summary>
    private sealed class Landed(FsUri from, FsUri to)
    {
        public FsUri From { get; } = from;

        public FsUri To { get; set; } = to;

        public int? Mode { get; set; }

        /// <summary>Whether it took the place of a file that was there, which a copy's undo can't bring back.</summary>
        public bool Replaced { get; init; }

        /// <summary>Its size as written, to tell later whether it is still the copy that was made.</summary>
        public long Written { get; init; }

        /// <summary>The source as it was read, to tell later whether the copy still stands for it.</summary>
        public FsEntry? SourceRead { get; init; }

        /// <summary>When it was committed: an edit after that is someone's work, not the copy.</summary>
        public DateTimeOffset LandedAt { get; init; }

        /// <summary>Its change stamp once committed, where the disk keeps one (<see cref="ChangeStampOf"/>).</summary>
        public long? ChangeStamp { get; init; }
    }

    /// <summary>What a job has got done so far, kept whether it finishes or stops part way.</summary>
    private sealed class Done
    {
        public List<Landed> Files { get; } = [];

        /// <summary>Folders this job created, which were not there before it.</summary>
        public List<FsUri> MadeDirectories { get; } = [];

        /// <summary>
        /// How many whole folders were replaced, their old selves to the Recycle Bin. Counted before
        /// the swaps, so one that fails part way still counts those it never reached; one whose old
        /// folder had gone replaced nothing and comes off.
        /// </summary>
        public int ReplacedFolders { get; set; }

        /// <summary>
        /// Each folder a Replace sent to the Recycle Bin, and when, so a move's undo can bring that
        /// one back and no other of its name. Before, the undo put the files back and left the old
        /// folder in the bin without a word (driving, 2026-10-14).
        /// </summary>
        public List<(FsUri Final, DateTimeOffset Since, DateTimeOffset By)> InBin { get; } = [];

        /// <summary>Folders a Replace removed where there is no bin (a server): no undo brings them back.</summary>
        public List<string> ReplacedForGood { get; } = [];

        /// <summary>
        /// Folders a Replace with no bin set aside and then kept, under the name they have now, so
        /// the undo and its barrier say where the old one is rather than that it is gone.
        /// </summary>
        public List<(string Name, FsUri Aside)> KeptAside { get; } = [];

        /// <summary>What arrived at the top level, for the views to point at.</summary>
        public List<FsUri> Landed { get; } = [];

        /// <summary>Skipped because the name was taken, as the user answered.</summary>
        public int Skipped { get; set; }

        /// <summary>
        /// Skipped because another program had them open, as the user answered. Counted with the
        /// above until 2026-10-11, and the log said these "already existed".
        /// </summary>
        public List<string> InUse { get; } = [];

        public int SameFile { get; set; }

        /// <summary>The names copies into their own folder were given, beside their originals.</summary>
        public List<string> Duplicates { get; } = [];

        /// <summary>Every file this job has written, so a later source of the same name steps aside.</summary>
        public HashSet<FsUri> Written { get; } = [];

        /// <summary>How many were given a numbered name because an earlier source had theirs.</summary>
        public int Renamed { get; set; }

        /// <summary>Names left out because Windows cannot hold them as a single name.</summary>
        public List<string> Unsafe { get; } = [];

        /// <summary>Files left out, as the user answered, because the target's file system can't hold one that size.</summary>
        public List<string> TooBig { get; } = [];

        /// <summary>Relative paths of files skipped after the folders to replace were decided: too big, in use.</summary>
        public List<string> LeftOut { get; } = [];

        /// <summary>The old folder's own files carried into its replacement, because their new ones were left out.</summary>
        public List<(FsUri Old, FsUri Staged)> Carried { get; } = [];

        /// <summary>Moved files that were copied but whose originals were in use, so are in both places.</summary>
        public List<string> CopiedOnly { get; } = [];

        /// <summary>Picked folders that are gone from where they were, so a reference to one follows it.</summary>
        public HashSet<FsUri> SourcesGone { get; } = [];

        public List<string> Unrunnable { get; } = [];

        /// <summary>Anything else the log line should say, such as an old folder left beside a new one.</summary>
        public List<string> Notes { get; } = [];

        /// <summary>Files built in a staging folder are, once it is swapped in, at the folder's real name.</summary>
        public void Rebase(FsUri staged, FsUri final)
        {
            foreach (var file in Files)
            {
                if (Rebased(file.To, staged, final) is { } moved)
                    file.To = moved;
            }

            // The folders made too: built at the staging name, they are at the real one now, and
            // an undo that removes what the job made has to find them there.
            for (var i = 0; i < MadeDirectories.Count; i++)
            {
                if (Rebased(MadeDirectories[i], staged, final) is { } at)
                    MadeDirectories[i] = at;
            }
        }

        /// <summary>The job's hold on Ctrl+Z, let go once it is recorded.</summary>
        public required IUndoSlot Working { get; init; }

        /// <summary>Whether <see cref="Record"/> has run: a job is logged and on the stack once.</summary>
        public bool Recorded { get; set; }

        /// <summary>Folder links a move carried as themselves, each from where to where.</summary>
        public List<(FsUri From, FsUri To)> Links { get; } = [];

        /// <summary>Folder links a move left where they were, each with why.</summary>
        public List<string> LinksLeft { get; } = [];

        /// <summary>Picked folders a move renamed whole, each from where to where (<see cref="MoveWholeFoldersAsync"/>).</summary>
        public List<(FsUri From, FsUri To)> Folders { get; } = [];

        /// <summary>
        /// Source folders a move emptied and removed. Putting the files back makes the folders a file
        /// is in; an empty one has no file to make it, and an undo left it gone from both places.
        /// </summary>
        public List<FsUri> RemovedSources { get; } = [];

        public IEnumerable<string> Commands(TransferMode mode)
        {
            foreach (var (from, to) in Folders)
                yield return ShellText.Move(from, to);

            foreach (var file in Files)
            {
                yield return mode == TransferMode.Copy ? ShellText.Copy(file.From, file.To) : ShellText.Move(file.From, file.To);
                if (file.Mode is { } chmod)
                    yield return ShellText.Chmod(file.To, chmod);
            }

            foreach (var (from, to) in Links)
                yield return ShellText.Move(from, to);
        }
    }

    /// <summary>
    /// The end of a job that did not finish: cancelled, failed, or stopped by an answer. Whatever
    /// a move put into a folder being replaced goes back first (§3.7 of JOBS_PLAN.md), and what
    /// did get done is recorded, and for a move undoable, exactly as a finished job's would be.
    /// Never throws: the job has to reach its final state whatever happens here.
    /// </summary>
    private async Task StopAsync(TransferJob job, ReplacementPlan? plan, Done done, JobState state, string? error)
    {
        try
        {
            await UnstageAsync(job, plan, done).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not put back what a stopped transfer had staged");
            done.Notes.Add("Some of what it had staged could not be put back; see the log.");
        }

        try
        {
            Record(job, done, state == JobState.Failed ? "failed" : "cancelled", error);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not record a stopped transfer");
        }

        // The error first: finishing is what observers react to, and they should find the reason
        // already there.
        if (error is not null)
            job.Error = error;
        // A cancelled job stays in the strip until dismissed; without this it went on naming the
        // file it was copying when stopped, and looked like it was still at it. The count is the
        // one the activity log gives: what is whole at the target, not what was started.
        // Before the sources were counted there is no "of" to give.
        if (state == JobState.Cancelled)
            job.CurrentName = job.ItemsTotal == 0
                ? "Cancelled before it started"
                : $"Cancelled after {done.Files.Count + done.Links.Count + done.Folders.Count:N0} of {job.ItemsTotal:N0}";
        done.Working.Dispose();
        job.State = state;
    }

    private static bool NothingLanded(Done done) =>
        done.Files.Count == 0 && done.Links.Count == 0 && done.Folders.Count == 0 && done.MadeDirectories.Count == 0
        && done.RemovedSources.Count == 0;

    /// <summary>
    /// Why items were left out that the user did not choose to skip, or null. Not files in use:
    /// those were skipped by an answer to the question about them; nor links left where they
    /// were, which is what a move does with a link it can't carry.
    /// </summary>
    private static string? LeftOut(Done done) =>
        done.Unsafe.Count > 0 ? "Windows cannot hold these names: " + string.Join(", ", done.Unsafe.Take(5)) + (done.Unsafe.Count > 5 ? ", …" : ".")
        : done.TooBig.Count > 0 ? "Too big for the target's file system: " + string.Join(", ", done.TooBig.Take(5)) + (done.TooBig.Count > 5 ? ", …" : ".")
        : null;

    /// <param name="stopped">"cancelled" or "failed" for a job that did not finish, else null.</param>
    private void Record(TransferJob job, Done done, string? stopped, string? error = null)
    {
        // Once: something throwing after the success path's record sent the job on to the stop
        // path, which recorded it again, and a second Ctrl+Z then acted on whatever was at those
        // names by then (review, 2026-10-30).
        if (done.Recorded)
            return;
        done.Recorded = true;
        var verb = job.Mode == TransferMode.Copy ? "Copied" : "Moved";
        var notes = new List<string>();
        if (error is not null)
            notes.Add(error);
        if (done.Skipped > 0)
            notes.Add($"{done.Skipped:N0} skipped because they already existed.");
        if (done.InUse.Count > 0)
        {
            notes.Add("Skipped, because another program had them open: " +
                      string.Join(", ", done.InUse.Take(5)) + (done.InUse.Count > 5 ? ", …" : "."));
        }
        if (done.Renamed > 0)
        {
            notes.Add(done.Renamed == 1
                ? "1 was given a numbered name: an earlier one in this transfer had the same name."
                : $"{done.Renamed:N0} were given numbered names: earlier ones in this transfer had the same names.");
        }
        if (done.CopiedOnly.Count > 0)
        {
            notes.Add($"Copied, but the original could not be removed and is still there too: " +
                      string.Join(", ", done.CopiedOnly.Take(5)) + (done.CopiedOnly.Count > 5 ? ", …" : "."));
        }
        if (done.Unsafe.Count > 0)
        {
            notes.Add($"Left out, because Windows cannot hold these names: " +
                      string.Join(", ", done.Unsafe.Take(5)) + (done.Unsafe.Count > 5 ? ", …" : "."));
        }
        if (done.TooBig.Count > 0)
        {
            notes.Add("Left out, too big for the target's file system: " +
                      string.Join(", ", done.TooBig.Take(5)) + (done.TooBig.Count > 5 ? ", …" : "."));
        }
        if (done.LinksLeft.Count > 0)
        {
            notes.Add("Links to other folders left where they were: " +
                      string.Join("; ", done.LinksLeft.Take(5)) + (done.LinksLeft.Count > 5 ? "; …" : "."));
        }
        if (done.Duplicates.Count > 0)
        {
            notes.Add("Copied beside the original as " + string.Join(", ", done.Duplicates.Take(5))
                      + (done.Duplicates.Count > 5 ? ", …" : "."));
        }
        if (done.SameFile > 0)
        {
            notes.Add(done.SameFile == 1
                ? "1 skipped because it is the same file as the one it would replace, under another name."
                : $"{done.SameFile:N0} skipped because each is the same file as the one it would replace, under another name.");
        }
        if (done.Unrunnable.Count > 0)
        {
            notes.Add(done.Unrunnable.Count == 1
                ? $"{done.Unrunnable[0]} has Windows line endings, so the server will not run it: " +
                  "its first line reads as a program name ending in a carriage return."
                : $"{done.Unrunnable.Count:N0} scripts have Windows line endings, so the server will not run them: " +
                  string.Join(", ", done.Unrunnable.Take(5)) + (done.Unrunnable.Count > 5 ? ", …" : "."));
        }
        notes.AddRange(done.Notes);
        var detail = notes.Count > 0 ? string.Join(" ", notes) : null;

        if (stopped is null && NothingLanded(done) && LeftOut(done) is { } why)
        {
            activity.Record(
                $"Nothing {(job.Mode == TransferMode.Copy ? "copied" : "moved")} to {job.TargetDirectory.Display}",
                "(nothing done)", ActivityOutcome.Failed, detail ?? why);
            return;
        }

        if (stopped is not null && done.Files.Count == 0 && done.Links.Count == 0 && done.Folders.Count == 0)
        {
            activity.Record(job.Title, $"({stopped})", ActivityOutcome.Failed,
                detail ?? (stopped == "failed" ? "Failed before anything was done." : "Cancelled before it finished."));
            return;
        }

        var count = stopped is null ? job.ItemsDone : done.Files.Count + done.Links.Count + done.Folders.Count;
        var description = stopped is null
            ? $"{verb} {count:N0} {(count == 1 ? "item" : "items")} to {job.TargetDirectory.Display}"
            : $"{verb} {count:N0} of {job.ItemsTotal:N0} items to {job.TargetDirectory.Display} ({stopped})";
        var commands = ShellText.Lines(done.Commands(job.Mode));

        activity.Record(description, commands, stopped is null ? ActivityOutcome.Succeeded : ActivityOutcome.Failed, detail);

        if (job.Mode == TransferMode.Copy)
        {
            PushCopyUndo(job, done, description, commands);
            return;
        }

        var moved = done.Files.Select(f => (f.From, f.To)).ToList();
        var links = done.Links.ToList();
        var folders = done.Folders.ToList();
        var relocated = Relocations(job, [.. moved, .. links, .. folders], done.SourcesGone);
        try
        {
            relocations?.Report(relocated);
        }
        catch (Exception ex)
        {
            // A listener failing must not fail a job that has done its work, nor record it twice.
            log.LogError(ex, "A relocation listener failed");
        }

        // A move can be put back exactly, and so can the part of one that stopped half way: those
        // files have left their folders, and Ctrl+Z is how they get home. A copy cannot be undone
        // by deleting the new files without risking data the user has since changed, so it is
        // logged but not undoable.
        if (moved.Count > 0 || links.Count > 0 || folders.Count > 0)
        {
            var made = done.MadeDirectories.ToList();
            var removed = done.RemovedSources.ToList();
            var inBin = done.InBin.ToList();
            var replacedFiles = done.Files.Count(f => f.Replaced);
            var replacedForGood = done.ReplacedForGood.ToList();
            var keptAside = done.KeptAside.ToList();
            done.Working.Push(new DelegateUndo(
                description,
                commands,
                async ct =>
                {
                    // A folder was moved as its files and then removed where it had been, so the
                    // way back has to make it again; and the folders the move made are removed
                    // once they are empty, so undo leaves the target as it found it.
                    // The links first, as themselves: put back as files, one across volumes was
                    // read through as a folder and failed. One that can't go back is said and
                    // stays where it is; it must not stop the files going home, as this undo is
                    // used up once it runs (review, 2026-10-23).
                    var linkNotes = new List<string>();
                    var stayed = new HashSet<FsUri>();
                    foreach (var (from, to) in links)
                    {
                        try
                        {
                            if (await providers.Resolve(from).StatAsync(from, ct).ConfigureAwait(false) is not null)
                                throw new IOException("something else has its name now");
                            if (from.Parent is { } home)
                                await EnsureDirectoryAsync(home, ct).ConfigureAwait(false);
                            await ((IMovesLinks)providers.Resolve(to)).MoveLinkAsync(to, from, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                        {
                            stayed.Add(to);
                            linkNotes.Add($"The link {to.Name} stays at {to.Display}: {ex.Message.TrimEnd('.')}.");
                        }
                    }
                    // A folder renamed whole goes back the same way, with whatever was put in it
                    // since, as Explorer's undo does. Only by renaming: copying a folder back is
                    // not something the files' put-back does. One that can't go back (something
                    // in it is open, its name is taken) stays where it is, said, all of it there.
                    // Last moved, first back: picked from a search, box/inner went before box,
                    // and putting inner back first made a new box for it, which then stood in the
                    // way of the real one (review, 2026-10-26).
                    foreach (var (from, to) in Enumerable.Reverse(folders))
                    {
                        try
                        {
                            if (await providers.Resolve(from).StatAsync(from, ct).ConfigureAwait(false) is not null)
                                throw new IOException("something else has its name now");
                            if (from.Parent is { } home)
                                await EnsureDirectoryAsync(home, ct).ConfigureAwait(false);
                            await providers.Resolve(to).MoveAsync(to, from, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            stayed.Add(to);
                            linkNotes.Add($"{to.Name} stays at {to.Display}: {ex.Message.TrimEnd('.')}. Moving it back by hand brings all of it.");
                        }
                    }
                    foreach (var (from, to) in moved)
                        await PutBackAsync(to, from, ct).ConfigureAwait(false);
                    // The source folders the move removed, the empty ones too: those had no file to
                    // bring them back (driven, 2026-10-28).
                    var remade = new List<FsUri>();
                    foreach (var directory in removed.OrderBy(d => d.Path.Length))
                    {
                        try
                        {
                            if (await providers.Resolve(directory).StatAsync(directory, ct).ConfigureAwait(false) is not null)
                                continue;
                            await EnsureDirectoryAsync(directory, ct).ConfigureAwait(false);
                            remade.Add(directory);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Any failure, said: the steps after this one put back what a Replace
                            // sent to the bin, and must not be skipped for an empty folder.
                            linkNotes.Add($"The empty folder {directory.Name} could not be made again at {directory.Display}: {ex.Message.TrimEnd('.')}.");
                        }
                    }
                    await RemoveMadeDirectoriesAsync(made, ct).ConfigureAwait(false);
                    var notes = await RestoreReplacedAsync(inBin, ct).ConfigureAwait(false);
                    notes.InsertRange(0, linkNotes);

                    // What no undo brings back is said, rather than "Undid" over a target that is
                    // not as it was: a file a Replace wrote over is gone (AUDIT.md §2 kind 25), and
                    // so is a folder replaced where there is no bin.
                    if (replacedFiles > 0)
                        notes.Add(replacedFiles == 1
                            ? "The file it replaced cannot be brought back."
                            : $"The {replacedFiles:N0} files it replaced cannot be brought back.");
                    foreach (var name in replacedForGood)
                        notes.Add($"The old {name} it replaced cannot be brought back: there is no Recycle Bin there.");
                    // Kept rather than deleted, but not renamed back: the files just put back may
                    // not have emptied its name, and a rename onto a taken one is no undo's to try.
                    foreach (var (name, aside) in keptAside)
                        notes.Add($"The old {name} is still at {aside.Name}; rename it back by hand.");

                    // Not a folder that stayed, nor anything in it: a reference sent back to where
                    // it isn't would point at nothing.
                    relocations?.Report(relocated
                        .Where(r => !stayed.Any(s => r.To == s || IsUnder(r.To, s)))
                        .Select(r => new Relocation(r.To, r.From)).ToList());
                    arrivals?.Report(job.Sources);
                    activity.Record($"Undid: {description}",
                        ShellText.Lines(links.Concat(folders).Where(f => !stayed.Contains(f.To)).Concat(moved).Select(m => ShellText.Move(m.To, m.From))
                            .Concat(remade.Select(ShellText.MakeDirectory))),
                        ActivityOutcome.Undone,
                        notes.Count == 0 ? null : string.Join(" ", notes));
                }));
        }
    }

    /// <summary>
    /// Whether a swap that reported failure took effect all the same. Asking can fail too, and
    /// then it didn't: the old folder comes back out of the bin, as after any failed swap, rather
    /// than the question's own error going past that (review, 2026-10-15).
    /// </summary>
    private static async Task<bool> SwapHappenedAsync(IFileSystemProvider provider, FsUri staged, FsUri final)
    {
        try
        {
            return !await ExistsAsync(provider, staged).ConfigureAwait(false) &&
                   await ExistsAsync(provider, final).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Brings each folder a Replace sent to the bin back to its name, once the files moved into
    /// its replacement have gone home and the replacement, emptied, is gone. Only that folder:
    /// the one of its name deleted in that moment, not a later one. Where it can't come back (the
    /// name is taken again, the bin was emptied), what is left says where it is.
    /// </summary>
    private async Task<List<string>> RestoreReplacedAsync(
        List<(FsUri Final, DateTimeOffset Since, DateTimeOffset By)> inBin, CancellationToken ct)
    {
        var notes = new List<string>();
        foreach (var (final, since, by) in inBin)
        {
            if (await providers.Resolve(final).StatAsync(final, ct).ConfigureAwait(false) is not null)
            {
                notes.Add($"The old {final.Name} is still in the Recycle Bin: something is at its name now, " +
                          "perhaps files of its own that were kept in the new one.");
                continue;
            }

            try
            {
                await recycleBin.RestoreAsync([final], since, by, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                notes.Add($"The old {final.Name} could not come back from the Recycle Bin: {ex.Message}");
            }
        }

        return notes;
    }

    /// <summary>
    /// What Ctrl+Z does after a copy. Before, nothing went on the stack, so it undid whatever came
    /// before the copy instead.
    /// </summary>
    /// <remarks>
    /// The copies go to the Recycle Bin, never deleted: a copy may have been changed since, and
    /// from the bin even an undo regretted comes back. Only where there is a bin, and only when
    /// nothing was replaced, because what a copy wrote over is gone and no undo brings it back;
    /// those get a barrier that says so, rather than Ctrl+Z reaching past them. A copy whose
    /// source has gone since is the only one left of that file, and stays.
    /// </remarks>
    private void PushCopyUndo(TransferJob job, Done done, string description, string commands)
    {
        var copies = done.Files.ToList();
        var made = done.MadeDirectories.ToList();
        if (copies.Count == 0 && made.Count == 0)
            return;

        // Every copy and every folder made must be somewhere with a bin: "all of none" is not a
        // yes, and a folder made on a server would otherwise be removed with no way back.
        // A folder kept aside is not gone, and the barrier says where it is rather than that.
        var why = done.ReplacedFolders > 0 || copies.Any(f => f.Replaced)
            ? done.KeptAside.Count > 0 && done.ReplacedFolders == done.KeptAside.Count && !copies.Any(f => f.Replaced)
                ? $"{description}: it replaced what was there; the old one is beside it, as " +
                  $"{string.Join(", ", done.KeptAside.Select(k => k.Aside.Name))}."
                : $"{description}: it replaced what was there, and that cannot be brought back."
            : copies.Any(c => !recycleBin.CanRecycle(c.To)) || made.Any(d => !recycleBin.CanRecycle(d))
                ? $"{description}: there is no Recycle Bin there to send the copies to."
                : null;

        if (why is not null)
        {
            done.Working.Push(new Irreversible(description, commands, why));
            return;
        }

        done.Working.Push(new DelegateUndo(
            description,
            commands,
            // Off the UI thread: the local disk answers a stat at once, so 85,000 of them ran
            // there, the window still before "Undoing: …" could show (review, 2026-10-17).
            ct => Task.Run(async () =>
            {
                var recycled = new List<FsUri>();
                var kept = new List<string>();
                var left = new List<string>();
                var sending = false;
                try
                {
                    // Each folder listed once, not each file asked about: 85,000 copies and their
                    // sources were 170,000 questions to the disk, 14 s of the undo (2026-10-17).
                    var nowAt = await StatManyAsync(copies.Select(c => c.To), ct).ConfigureAwait(false);
                    var sourceAt = await StatManyAsync(copies.Where(c => nowAt[c.To] is not null).Select(c => c.From), ct).ConfigureAwait(false);
                    // Each kept copy, and whether it is the copy that changed (true) or its original
                    // that has gone.
                    var changed = new Dictionary<string, bool>(StringComparer.Ordinal);
                    var originalChanged = new List<FsUri>();
                    foreach (var copy in copies)
                    {
                        if (nowAt[copy.To] is not { } now)
                            continue;

                        // Only a copy that is still what was made. Edited since, it is someone's
                        // work; its original gone, it is the only copy left. Either way it stays.
                        // An original changed since does not keep it (the user, 2026-10-18): the copy
                        // goes to the bin, not away, and a git client fetching in the original
                        // otherwise held every copied repository where it was.
                        var still = StillTheCopy(copy, now);
                        if (!still || sourceAt[copy.From] is null)
                        {
                            kept.Add(copy.To.Name);
                            changed[copy.To.Path] = !still;
                            continue;
                        }

                        // Said only where both times are known: no time is no news of a change.
                        if (copy.SourceRead is { Modified: not null } && sourceAt[copy.From] is { Modified: not null }
                            && !Unchanged(copy.SourceRead, sourceAt[copy.From]))
                            originalChanged.Add(copy.To);
                        recycled.Add(copy.To);
                    }

                    // A folder this copy made, holding only copies about to go and folders it made,
                    // goes as one item: file by file, 85,000 copies were 85,000 trips through the
                    // shell and as many entries in the bin (2026-10-17). Anything else in it, and
                    // its copies go one by one as before, leaving the rest where it is.
                    // Names as written, case and all: a folder that tells A.txt from a.txt may
                    // hold the user's own beside the copy's (review, 2026-10-17).
                    var going = recycled.Select(u => u.Path).ToHashSet(StringComparer.Ordinal);
                    var ours = made.ToHashSet();
                    var ourPaths = made.Select(u => u.Path).ToHashSet(StringComparer.Ordinal);
                    var landed = copies.ToDictionary(c => c.To);
                    var single = recycled.ToHashSet();
                    recycled = [];
                    sending = true;

                    // A folder that can't go whole, with more copies than the shell sends one by
                    // one in reasonable time, stays as it is, said: 85,000 one by one ran for an
                    // hour and was stopped a quarter through, after git had touched .git/index in
                    // the copy (2026-10-17). Del sends a folder whole, whatever is in it.
                    bool LeftWhole(FsUri top, List<FsUri> inside, string why)
                    {
                        if (inside.Count <= MostOneByOne)
                            return false;
                        left.Add($"{top.Name} stays as it is: {why}");
                        single.ExceptWith(inside);
                        return true;
                    }

                    foreach (var top in made.Where(d => d.Parent is not { } parent || !ours.Contains(parent)))
                    {
                        var inside = single.Where(f => IsUnder(f, top)).ToList();
                        if (inside.Count == 0)
                            continue;

                        // Larger than the bin, the shell won't take it whole, though it takes each
                        // of its files: those go one by one, as before. Each folder is walked just
                        // before it goes, so the gap for something to be saved into it is short.
                        if (recycleBin.CapacityOf(top) is { } capacity && inside.Sum(f => landed[f].Written) > capacity)
                        {
                            LeftWhole(top, inside, "it is larger than the Recycle Bin");
                            continue;
                        }
                        if (await NotOursAsync(top, going, ourPaths, changed, inside.Count, ct).ConfigureAwait(false) is { } why)
                        {
                            LeftWhole(top, inside, why);
                            continue;
                        }

                        try
                        {
                            await recycleBin.RecycleAsync([top], ct).ConfigureAwait(false);
                            recycled.Add(top);
                            single.ExceptWith(inside);
                        }
                        catch (IOException ex) when (!ct.IsCancellationRequested)
                        {
                            // Gone all the same (said removed with nothing in the bin): not one to
                            // carry on past as if refused, but said, as any send that stopped.
                            if (await providers.Resolve(top).StatAsync(top, ct).ConfigureAwait(false) is null)
                            {
                                recycled.Add(top);
                                throw;
                            }

                            // Refused whole (the shell would have destroyed it, or something in it
                            // is held), with nothing gone: its copies one by one instead, each
                            // looked at again, since it may have been changed meanwhile.
                            if (LeftWhole(top, inside, $"Windows would not take it whole ({ex.Message.TrimEnd('.')})"))
                                continue;
                            foreach (var file in inside)
                            {
                                if (await providers.Resolve(file).StatAsync(file, ct).ConfigureAwait(false) is not { } now
                                    || !StillTheCopy(landed[file], now))
                                {
                                    single.Remove(file);
                                    kept.Add(file.Name);
                                }
                            }
                        }
                    }

                    if (single.Count > 0)
                    {
                        var files = single.ToList();
                        try
                        {
                            await recycleBin.RecycleAsync(files, ct).ConfigureAwait(false);
                            recycled.AddRange(files);
                        }
                        catch
                        {
                            // Those it took before it stopped are named in the record, not left out.
                            foreach (var file in files)
                            {
                                if (await providers.Resolve(file).StatAsync(file, CancellationToken.None).ConfigureAwait(false) is null)
                                    recycled.Add(file);
                            }
                            throw;
                        }
                    }

                    // Deepest first, and only if empty: a folder something else was put in stays.
                    foreach (var directory in made.OrderByDescending(d => d.Path.Length))
                    {
                        try
                        {
                            // A folder still: a provider's delete removes a file of that name
                            // too, and one saved there since is nobody's copy.
                            if (await providers.Resolve(directory).StatAsync(directory, ct).ConfigureAwait(false) is not { IsDirectory: true })
                                continue;

#pragma warning disable RS0030 // §1.6: a folder this copy made, still a folder (checked above), removed only when empty.
                            await providers.Resolve(directory).DeleteAsync(directory, recursive: false, ct).ConfigureAwait(false);
#pragma warning restore RS0030
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // Something is in it now, or it can't be removed; it stays.
                        }
                    }

                    var detail = kept.Count > 0
                        ? "Kept, because each has changed since, or what it was copied from has gone: "
                          + string.Join(", ", kept.Take(5)) + (kept.Count > 5 ? ", …" : ".")
                        : "The copies went to the Recycle Bin.";
                    // Counted among what went, not what was meant to: a folder left whole keeps its own.
                    var wentAlone = recycled.ToHashSet();
                    var wentWhole = recycled.Where(ours.Contains).ToList();
                    var changedWent = originalChanged.Count(f => wentAlone.Contains(f) || wentWhole.Any(w => IsUnder(f, w)));
                    if (changedWent > 0)
                        detail += $" {changedWent:N0} of them were copies of originals that have changed since: their old versions are what went to the Recycle Bin.";
                    if (left.Count > 0)
                        detail = string.Join(" ", left.Select(l => l + ".")) + " Del sends a folder to the Recycle Bin whole. " + detail;
                    activity.Record(
                        left.Count > 0 ? $"Undid part of: {description}" : $"Undid: {description}",
                        ShellText.Lines(recycled.Select(ShellText.Delete)),
                        left.Count > 0 ? ActivityOutcome.Failed : ActivityOutcome.Undone,
                        detail);
                }
                catch (Exception) when (sending)
                {
                    // The bin may have taken some of them before it stopped: said, not left silent.
                    activity.Record(
                        $"Undo stopped partway: {description}",
                        ShellText.Lines(recycled.Select(ShellText.Delete)),
                        ActivityOutcome.Failed,
                        "Some of these may be in the Recycle Bin.");
                    throw;
                }

                // Said in the notice too: "Undid" over a folder still all there read as done.
                if (left.Count > 0)
                    throw new CannotUndoException(string.Join(" ", left.Select(l => l + ".")) + " Del sends it to the Recycle Bin whole.");
            }, ct)));
    }

    /// <summary>Past this many copies in a folder that can't go whole, the undo leaves it rather than send them one by one.</summary>
    private const int MostOneByOne = 1_000;

    /// <summary>
    /// Why <paramref name="folder"/> is not the copy's own alone, in words, or null when it is:
    /// each file one of <paramref name="going"/>, each folder one of <paramref name="made"/>, by
    /// path as written, and all <paramref name="expected"/> of its copies seen. Anything else, a
    /// name written differently, or a copy not seen (a folder that can't be read lists as
    /// empty), and its copies go one by one as before.
    /// </summary>
    private async Task<string?> NotOursAsync(
        FsUri folder, HashSet<string> going, HashSet<string> made, IReadOnlyDictionary<string, bool> changed, int expected, CancellationToken ct)
    {
        var provider = providers.Resolve(folder);
        var found = 0;
        try
        {
            // Still a folder: a file saved at its name since is nobody's copy.
            if (await provider.StatAsync(folder, ct).ConfigureAwait(false) is not { IsDirectory: true, LinkTarget: null })
                return "something else has its name now";

            var pending = new Stack<FsUri>([folder]);
            while (pending.TryPop(out var at))
            {
                await foreach (var entry in provider.ListAsync(at, ct).ConfigureAwait(false))
                {
                    if (entry.IsDirectory && entry.LinkTarget is null && made.Contains(entry.Uri.Path))
                        pending.Push(entry.Uri);
                    else if (!entry.IsDirectory && going.Contains(entry.Uri.Path))
                        found++;
                    else
                        // Which one changed, said: "in it" over a file whose original git had
                        // fetched into read as though the copy had been touched (2026-10-18).
                        return changed.TryGetValue(entry.Uri.Path, out var itself)
                            ? itself
                                ? $"{Relative(folder, entry.Uri)} in it has changed since the copy"
                                : $"the original of {Relative(folder, entry.Uri)} has gone since the copy, so this is the only copy left"
                            : $"{Relative(folder, entry.Uri)} in it is not part of the copy";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "not all of it could be read";
        }

        return found == expected ? null : "not all of it could be read";
    }

    /// <summary>"orbit/.git/index" for that file under C:\…\orbit.</summary>
    private static string Relative(FsUri folder, FsUri entry) =>
        folder.Name + entry.Path[folder.Path.Length..];

    /// <summary>
    /// What each of <paramref name="paths"/> is now, from one listing of each folder they are in.
    /// One not in its folder's listing, or in a folder that can't be listed, is asked about on its
    /// own, so a listing's gap is never taken for "gone".
    /// </summary>
    private async Task<Dictionary<FsUri, FsEntry?>> StatManyAsync(IEnumerable<FsUri> paths, CancellationToken ct)
    {
        var result = new Dictionary<FsUri, FsEntry?>();
        foreach (var group in paths.Distinct().GroupBy(p => p.Parent))
        {
            var listed = new Dictionary<FsUri, FsEntry>();
            if (group.Key is { } parent && group.Skip(1).Any())
            {
                try
                {
                    await foreach (var entry in providers.Resolve(parent).ListAsync(parent, ct).ConfigureAwait(false))
                        listed[entry.Uri] = entry;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    listed.Clear();
                }
            }

            foreach (var path in group)
            {
                result[path] = listed.TryGetValue(path, out var entry)
                    ? entry
                    : await providers.Resolve(path).StatAsync(path, ct).ConfigureAwait(false);
            }
        }

        return result;
    }

    /// <summary>
    /// The copy as it was made: its size as written, not modified after it landed, and where the
    /// disk keeps a change stamp, that too. Copies are only undone on a local disk, so its time and
    /// this one are the same machine's; the 2 s allows for FAT's 2 s timestamps.
    /// </summary>
    /// <remarks>
    /// The modified time alone was blind to its own copy: a local copy keeps the source's date, so
    /// anything of that size with an old date passed, and a VeraCrypt container used and dismounted
    /// (which puts its date back) went to the bin as "the copies" (AUDIT.md §2 kind 46).
    /// </remarks>
    private bool StillTheCopy(Landed copy, FsEntry now) =>
        now.Size == copy.Written && now.Modified is { } modified && modified <= copy.LandedAt + TimeSpan.FromSeconds(2)
        && (copy.ChangeStamp is not { } stamp
            || (providers.TryResolve(copy.To, out var provider) && provider is IChangeStamps stamps && stamps.ChangeStamp(copy.To) == stamp));

    /// <summary>
    /// A landed copy's change stamp, read once per file: only from 64 KB up, where an edit that
    /// keeps the size and the date happens (a container, a disk image); an undo of 85,000 small
    /// files stays a listing rather than as many opens.
    /// </summary>
    private static long? ChangeStampOf(IFileSystemProvider provider, FsUri target, long written)
    {
        if (written < 64 * 1024 || provider is not IChangeStamps stamps)
            return null;
        try
        {
            return stamps.ChangeStamp(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Unchanged(FsEntry? then, FsEntry? now) =>
        then is { Modified: not null } && now is { Modified: not null }
        && now.Size == then.Size && now.Modified == then.Modified;

    /// <summary>
    /// Moves one file back to where a move took it from. Within a provider that is a rename;
    /// across two it is a checked copy and a delete, as the move itself was. Calling the target's
    /// rename with an address on another provider was what undo used to do.
    /// </summary>
    private async Task PutBackAsync(FsUri from, FsUri to, CancellationToken ct)
    {
        if (to.Parent is { } home)
            await EnsureDirectoryAsync(home, ct).ConfigureAwait(false);

        var provider = providers.Resolve(from);
        if (CanRename(from, to))
        {
            try
            {
                await provider.MoveAsync(from, to, ct).ConfigureAwait(false);
                return;
            }
            catch (IOException ex) when (!InUse.Is(ex))
            {
                // Where a rename is refused (a server that won't rename across its own volumes, say),
                // the move itself fell back to a copy and a delete, so its undo must too; it used to
                // fail outright instead (found by FaultSweepTests, with a rename that refuses). Not
                // seen locally: Windows' File.Move copies across drives by itself. Logged, because a
                // failing fallback would otherwise speak only for itself.
                log.LogInformation(ex, "Putting {From} back by renaming failed; copying it back instead", from);
            }
        }

        if (await providers.Resolve(to).StatAsync(to, ct).ConfigureAwait(false) is not null)
            throw new IOException($"{to.Name} cannot be put back: something else has that name now.");

        var read = await provider.StatAsync(from, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"{from.Display} is not there any more.");
        var written = await CopyCheckedAsync(from, to, read.Size, seen: null, replace: false, ct, durable: true).ConfigureAwait(false);
        await DeleteMovedSourceAsync(from, to, written, read, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What a move relocated, for whatever keeps references (§6.19): each file moved, and each
    /// folder the user picked — which is moved as the files inside it, so its own new address is
    /// read off one of them, and a reference to the folder follows it as one to a file would.
    /// </summary>
    /// <param name="gone">
    /// The picked folders no longer where they were. Only those follow: a folder that still holds
    /// what was skipped or never reached is still there, and pointing a reference to it at the
    /// target pointed it at the file a skip had kept, or at nothing.
    /// </param>
    private static List<Relocation> Relocations(
        TransferJob job, List<(FsUri From, FsUri To)> moved, IReadOnlySet<FsUri> gone)
    {
        var result = new List<Relocation>();
        foreach (var source in job.Sources)
        {
            if (moved.Any(m => m.From == source) || !gone.Contains(source))
                continue;

            FsUri? landed = null;
            foreach (var (from, to) in moved)
            {
                var depth = 0;
                var at = from;
                while (at is not null && at != source)
                {
                    at = at.Parent;
                    depth++;
                }
                if (at is null)
                    continue;

                landed = to;
                for (var i = 0; i < depth && landed is not null; i++)
                    landed = landed.Parent;
                break;
            }

            if (landed is not null)
                result.Add(new Relocation(source, landed));
        }

        result.AddRange(moved.Select(m => new Relocation(m.From, m.To)));
        return result;
    }

    /// <summary>
    /// The mode a newly written file should have, set on it, or null when it keeps the one the
    /// server gave it.
    /// </summary>
    /// <remarks>
    /// In order of how much each knows. A source with modes of its own says exactly what the file
    /// was, and a copy between two servers should not have to guess at it. A source without them,
    /// which is to say Windows, leaves the content: a file that is plainly meant to run is made
    /// runnable, and anything else keeps the server's default. Neither failing fails the copy;
    /// the bytes are the job, and a mode is a courtesy on top.
    /// </remarks>
    private async Task<int?> ModeForNewFileAsync(
        FsUri source, FsUri target, byte[] head, IUnixModes modes, CancellationToken ct)
    {
        try
        {
            if (await modes.GetModeAsync(target, ct).ConfigureAwait(false) is not { } fresh)
                return null;

            var wanted = providers.Resolve(source) is IUnixModes sourceModes &&
                         await sourceModes.GetModeAsync(source, ct).ConfigureAwait(false) is { } original
                ? ExecutableContent.Inherit(original, fresh)
                : ExecutableContent.Detect(head) == ExecutableKind.None
                    ? fresh
                    : ExecutableContent.WithExecute(fresh);

            if (wanted == fresh)
                return null;

            await modes.SetModeAsync(target, wanted, ct).ConfigureAwait(false);
            return wanted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogInformation(ex, "Could not set the mode of {Target}", target);
            return null;
        }
    }

    /// <param name="IsLink">A folder link a move carries as itself, after everything else (<see cref="MoveLinksAsync"/>).</param>
    private sealed record Item(FsUri Source, string RelativePath, bool IsDirectory, long Size, bool IsLink = false);

    /// <summary>Expands directories into the files inside them, keeping paths relative to the target.</summary>
    /// <param name="moved">Sources already moved whole, left out.</param>
    private async Task<List<Item>> FlattenAsync(TransferJob job, IReadOnlySet<FsUri> moved)
    {
        var items = new List<Item>();

        foreach (var source in job.Sources)
        {
            if (moved.Contains(source))
                continue;

            var provider = providers.Resolve(source);
            var entry = await provider.StatAsync(source, job.Token).ConfigureAwait(false);
            if (entry is null)
                continue;

            if (!entry.IsDirectory)
            {
                items.Add(new Item(source, entry.Name, false, entry.Size));
                continue;
            }

            if (await HoldsAsync(source, job.TargetDirectory, job.Token).ConfigureAwait(false))
                throw new IOException(
                    $"{entry.Name} cannot be {(job.Mode == TransferMode.Move ? "moved" : "copied")} into itself or a folder inside it.");

            var move = job.Mode == TransferMode.Move;
            if (move && IsLink(entry))
            {
                items.Add(new Item(source, entry.Name, false, 0, IsLink: true));
                continue;
            }

            items.Add(new Item(source, entry.Name, true, 0));
            await WalkAsync(provider, source, entry.Name, items, move, job.Token).ConfigureAwait(false);
        }

        return items;
    }

    /// <summary>
    /// A folder link (a junction, a directory symbolic link), which a move carries as itself.
    /// </summary>
    /// <remarks>
    /// A move goes file by file, so walking through a link moved the files of the folder it points
    /// to, outside everything selected, and then removed the link as emptied: moving a project
    /// with a junction to a shared folder emptied the shared folder, and undo put its files back
    /// into a new real folder where the link had been (review, 2026-10-22). A copy walks through
    /// one as before: it only duplicates.
    /// </remarks>
    private static bool IsLink(FsEntry entry) => entry.IsDirectory && entry.LinkTarget is not null;

    /// <summary>Whether <paramref name="target"/> is <paramref name="folder"/> or somewhere inside it.</summary>
    /// <remarks>
    /// Asked of the folder itself, not its address: F5 and F6 never refused a folder copied into
    /// its own subfolder, and the drop's own check compares text, which a junction, a subst drive
    /// or a short name gets past (2026-10-14). Nothing was lost (the tree is listed before anything
    /// is written, so it made one nested copy), but nobody asks for that.
    /// </remarks>
    private async Task<bool> HoldsAsync(FsUri folder, FsUri target, CancellationToken ct)
    {
        for (var at = target.Anchor; at is not null; at = at.Parent)
        {
            if (await IsSameFileAsync(folder, at, ct).ConfigureAwait(false))
                return true;
        }

        return false;
    }

    private async Task WalkAsync(
        IFileSystemProvider provider, FsUri directory, string prefix, List<Item> items, bool move, CancellationToken ct)
    {
        await foreach (var child in provider.ListAsync(directory, ct).ConfigureAwait(false))
        {
            var relative = prefix + "/" + child.Name;
            if (move && IsLink(child))
            {
                items.Add(new Item(child.Uri, relative, false, 0, IsLink: true));
            }
            else if (child.IsDirectory)
            {
                items.Add(new Item(child.Uri, relative, true, 0));
                await WalkAsync(provider, child.Uri, relative, items, move, ct).ConfigureAwait(false);
            }
            else
            {
                items.Add(new Item(child.Uri, relative, false, child.Size));
            }
        }
    }

    /// <summary>
    /// A copy into the folder it came from, Ctrl+C then Ctrl+V in one place: a duplicate beside
    /// the original, as Keep both names one ("notes (2).txt", "photos (2)").
    /// </summary>
    /// <remarks>
    /// Without this the copy met itself at every name, each file was skipped as "the same file",
    /// and the job ended having done nothing while its bar said it had copied. Only the same
    /// address counts: one folder reached by two (a subst drive, a junction) is still caught by
    /// the same-file check and skipped, as before. A move into its own folder stays nothing to do.
    /// </remarks>
    /// <returns>Each source copied into its own folder, and the free name its copy will have.</returns>
    private async Task<Dictionary<FsUri, string>> PlanDuplicatesAsync(TransferJob job)
    {
        var duplicates = new Dictionary<FsUri, string>();
        if (job.Mode != TransferMode.Copy)
            return duplicates;

        // A free name is one nothing has, and nothing else in this job will take: another
        // source of that name, or another duplicate's, would land on it and the two would merge.
        var spoken = new HashSet<string>(job.Sources.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var targetProvider = providers.Resolve(job.TargetDirectory);

        // The plan rewrites by top-level name, and every item of a source carries that name: with
        // two sources of one name (T/work, and X/work from a search), rewriting one rewrote both,
        // and a Replace of the original then had nothing staged. A name shared in the job is left
        // as it was, so the same-file check skips the one in its own folder, as before.
        var shared = job.Sources.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var source in job.Sources)
        {
            if (source.Parent is not { } parent || !parent.Equals(job.TargetDirectory) || shared.Contains(source.Name))
                continue;

            var entry = await providers.Resolve(source).StatAsync(source, job.Token).ConfigureAwait(false);
            if (entry is null)
                continue;

            var dot = entry.IsDirectory ? -1 : source.Name.LastIndexOf('.');
            var stem = dot > 0 ? source.Name[..dot] : source.Name;
            var extension = dot > 0 ? source.Name[dot..] : string.Empty;

            for (var n = 2; n < 1000; n++)
            {
                var name = $"{stem} ({n}){extension}";
                if (spoken.Contains(name) || await ExistsAsync(targetProvider, parent.Child(name)).ConfigureAwait(false))
                    continue;

                spoken.Add(name);
                duplicates[source] = name;
                break;
            }

            // 999 copies already: not planned, so the same-file check skips it, as before.
        }

        return duplicates;
    }

    /// <summary>
    /// What to do about each folder landing on a folder of the same name, decided before a single
    /// byte moves. Null means the user cancelled the whole transfer.
    /// </summary>
    /// <param name="sources">The job's sources, less those copied beside themselves.</param>
    private async Task<ReplacementPlan?> PlanFolderCollisionsAsync(
        TransferJob job, IDecisionHost decisions, IReadOnlyList<FsUri> sources)
    {
        var collisions = await FolderCollisionScanner
            .ScanAsync(providers, sources, job.TargetDirectory, job.Token, linksAsThemselves: job.Mode == TransferMode.Move)
            .ConfigureAwait(false);

        var plan = new ReplacementPlan();

        foreach (var collision in collisions)
        {
            job.Token.ThrowIfCancellationRequested();

            // Nothing in the destination is missing from the source, so merging and replacing
            // leave the same files behind. Asking would be asking about a difference that does not
            // exist -- and copying a folder onto one of the same name is routine enough that a
            // prompt every time would train people to dismiss it.
            if (collision.Losses == 0)
                continue;

            // On a stick, a share or a server the swap deletes the old folder outright, and the
            // question read the same as where it goes to the bin: the "no bin" note came only
            // after it had gone (review, 2026-10-28). So the question and its button say it.
            var binned = recycleBin.CanRecycle(collision.Destination);
            var answer = await WhileAsking(job, () => decisions.AskAsync(
                new ChoiceRequest(
                    $"{collision.Destination.Name} already exists here.",
                    [
                        new DecisionOption("merge", "Merge into it", IsDefault: true),
                        new DecisionOption("replace", binned ? "Replace it entirely" : "Replace it, deleting the old one"),
                        new DecisionOption("skip", "Skip this folder"),
                        new DecisionOption("cancel", "Cancel", IsCancel: true),
                    ],
                    "Merging keeps what is already there and asks about files that clash. " +
                    $"Replacing makes it exactly the source: {collision.Cost} " +
                    (binned
                        ? "The old folder goes to the Recycle Bin."
                        : "There is no Recycle Bin here, so the old folder would be gone for good."),
                    NoticeSeverity.Warning),
                job.Token)).ConfigureAwait(false);

            switch (answer)
            {
                case "merge":
                    break;
                case "replace":
                    plan.Replace(collision);
                    break;
                case "skip":
                    plan.Skip(collision.Destination.Name);
                    break;
                default:
                    return null;
            }
        }

        return plan;
    }

    /// <summary>
    /// A file skipped inside a folder being replaced keeps the old folder's file of that name: the
    /// old one is moved into the replacement just before the swap.
    /// </summary>
    /// <remarks>
    /// Skipped means left as it was, but a replaced folder is discarded whole, and on a stick with
    /// no Recycle Bin for good: a file too big for FAT32, skipped, took the old folder's only copy
    /// of it with it, and Replace's cost had never counted it (review, 2026-10-14; AUDIT.md §2).
    /// Recorded before it moves, and not cancelled once begun: a server can do the rename and lose
    /// the answer, and a carry unrecorded would be inside a staging folder a stopped copy removes
    /// whole. Done per folder, at its swap, so the original spends as little time as it can there.
    /// </remarks>
    private async Task CarryLeftOutAsync(TransferJob job, ReplacementPlan plan, Done done, FsUri staged)
    {
        foreach (var relative in done.LeftOut)
        {
            var finalPath = plan.Final(relative);
            var stagedPath = plan.Redirect(relative);
            var into = Resolve(job.TargetDirectory, stagedPath);
            if (finalPath == stagedPath || !IsUnder(into, staged))
                continue; // not inside this folder being replaced

            var old = Resolve(job.TargetDirectory, finalPath);
            var provider = providers.Resolve(old);
            if (await provider.StatAsync(old, CancellationToken.None).ConfigureAwait(false) is not { IsDirectory: false })
                continue; // the old folder had none: nothing to keep
            if (await provider.StatAsync(into, CancellationToken.None).ConfigureAwait(false) is not null)
                throw new IOException($"{old.Name} would be lost in replacing {old.Parent?.Name}, so the old folder was kept.");

            await EnsureDirectoryAsync(into.Parent!, CancellationToken.None).ConfigureAwait(false);
            done.Carried.Add((old, into));
            await provider.MoveAsync(old, into, CancellationToken.None).ConfigureAwait(false);
            done.Notes.Add($"Kept the old {old.Name} in {old.Parent?.Name}: the new one was left out.");
        }
    }

    /// <summary>
    /// Files that arrived in a folder being replaced, or changed there, after the user agreed what
    /// replacing it would cost: carried into the new one before the swap, under a free name where
    /// the new one has that name.
    /// </summary>
    /// <remarks>
    /// "Replace it entirely" was said about the files there then. One that another job moved in,
    /// or a program saved, while this ran was never counted, and the swap discarded it with the
    /// old folder: on a server or a stick, with no bin, for good (reviews, 2026-10-22 and 23;
    /// AUDIT.md §2 kind 10). Told by the folder as it was when asked, by size and time, so only
    /// the old folder is walked again here. Carried as a skipped file is, recorded before it moves,
    /// so a swap that fails puts it back. What lands between this walk and the swap goes to the
    /// bin with the old folder unsaid; with no bin, <see cref="SwapAsideAsync"/> looks once more.
    /// </remarks>
    private async Task CarryArrivalsAsync(ReplacementPlan plan, Done done, FsUri final, FsUri staged, CancellationToken ct)
    {
        if (!plan.Agreed.TryGetValue(final, out var asked))
            return;

        var provider = providers.Resolve(final);
        // The looking can be stopped; once a file starts to move, nothing is.
        var now = await FolderCollisionScanner.EntriesAsync(provider, final, ct).ConfigureAwait(false);
        var arrived = now
            .Where(entry => !asked.Seen.TryGetValue(entry.Key, out var then) || Changed(then, entry.Value))
            .Select(entry => entry.Key)
            .ToList();

        var renamed = 0;
        foreach (var relative in arrived)
        {
            var from = Resolve(final, relative);
            var into = Resolve(staged, relative);
            if (await provider.StatAsync(into, CancellationToken.None).ConfigureAwait(false) is not null)
            {
                into = await UniqueAsync(provider, into).ConfigureAwait(false);
                renamed++;
            }

            await EnsureDirectoryAsync(into.Parent!, CancellationToken.None).ConfigureAwait(false);
            done.Carried.Add((from, into));
            await provider.MoveAsync(from, into, CancellationToken.None).ConfigureAwait(false);
        }

        if (arrived.Count > 0)
            done.Notes.Add(
                (arrived.Count == 1
                    ? $"Kept {arrived[0]} in the new {final.Name}: it arrived or changed after Replace was answered."
                    : $"Kept {arrived.Count:N0} files in the new {final.Name} that arrived or changed after Replace was answered.") +
                (renamed > 0 ? $" {renamed:N0} under another name, beside the new file of that name." : ""));
    }

    /// <summary>Whether a file is not the one seen when Replace was asked about.</summary>
    private static bool Changed(FsEntry then, FsEntry now) =>
        then.IsDirectory != now.IsDirectory || then.Size != now.Size || then.LinkTarget != now.LinkTarget ||
        (then.Modified is { } before && now.Modified is { } after && before != after);

    /// <summary>
    /// Swaps each staged folder into place: the old one goes to the Recycle Bin, the new one takes
    /// its name. Only reached once every file has arrived, so the original survives any failure
    /// before this point — which is the whole reason for staging rather than deleting first.
    /// </summary>
    private async Task CommitReplacementsAsync(TransferJob job, ReplacementPlan plan, Done done)
    {
        foreach (var (final, staged) in plan.Staged.ToList())
        {
            job.Token.ThrowIfCancellationRequested();
            await CarryLeftOutAsync(job, plan, done, staged).ConfigureAwait(false);

            var provider = providers.Resolve(final);

            // The folder being replaced went while the question was open or the files moved, so
            // there is nothing to set aside: the new one just takes the name. Setting aside what
            // wasn't there failed the job, and Replace left the target empty. The rename never
            // lands on a name that is taken again meanwhile; refused, the job puts back as after
            // any failed swap. A file at the name now was never agreed to: on WebDAV its listing
            // is empty, so nothing was carried out of it, and with no bin the swap deleted it
            // (review, 2026-10-26).
            var now = await provider.StatAsync(final, CancellationToken.None).ConfigureAwait(false);
            if (now is { IsDirectory: false })
                throw new IOException($"{final.Name} is a file now, not the folder Replace was agreed for, so it was left as it is.");
            if (now is null)
            {
                try
                {
                    await provider.MoveAsync(staged, final, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (!await SwapHappenedAsync(provider, staged, final).ConfigureAwait(false))
                        throw;
                    log.LogWarning(ex, "Moving {Staged} to {Final} reported a failure but took effect", staged, final);
                }

                done.Notes.Add($"The old {final.Name} had gone before it was replaced.");
                done.ReplacedFolders--;
                plan.Staged.Remove((final, staged));
                done.Rebase(staged, final);
                continue;
            }

            await CarryArrivalsAsync(plan, done, final, staged, job.Token).ConfigureAwait(false);

            // Recycled rather than deleted, so a replace the user regrets is still recoverable —
            // the same promise Delete already makes (§6.7). Once it has gone the swap is not
            // interrupted: a cancel between the two would leave neither folder at the name.
            if (recycleBin.CanRecycle(final))
            {
                var recycledAt = DateTimeOffset.Now;
                await recycleBin.RecycleAsync([final], job.Token).ConfigureAwait(false);
                var recycledBy = DateTimeOffset.Now;
                try
                {
                    await provider.MoveAsync(staged, final, CancellationToken.None).ConfigureAwait(false);
                    done.InBin.Add((final, recycledAt, recycledBy));
                }
                catch (Exception ex)
                {
                    // The swap happened and then said it failed (AUDIT.md §2 kind 17): staging is
                    // gone and the new folder has the name, which nothing else could have put
                    // there just after the old one went. Taken as done, so the record and the
                    // undo point where the files are, not into a staging folder that is no more.
                    if (await SwapHappenedAsync(provider, staged, final).ConfigureAwait(false))
                    {
                        log.LogWarning(ex, "Moving {Staged} to {Final} reported a failure but took effect", staged, final);
                        done.InBin.Add((final, recycledAt, recycledBy));
                        plan.Staged.Remove((final, staged));
                        done.Rebase(staged, final);
                        continue;
                    }

                    // The new one could not take the name, so the old one comes back out of the
                    // bin rather than leave nothing there (or what staging is about to give back).
                    try
                    {
                        await recycleBin.RestoreAsync([final], recycledAt, recycledBy, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception restore)
                    {
                        log.LogError(restore, "Could not bring {Final} back from the Recycle Bin", final);
                        done.Notes.Add($"The old {final.Name} is in the Recycle Bin.");
                    }

                    throw new IOException($"The new {final.Name} could not take its place: {ex.Message}", ex);
                }
            }
            else
            {
                await SwapAsideAsync(provider, final, staged, plan.Agreed.GetValueOrDefault(final), done).ConfigureAwait(false);
            }

            plan.Staged.Remove((final, staged));
            done.Rebase(staged, final);
        }
    }

    /// <summary>
    /// Where there is no Recycle Bin (a server), the old folder steps aside, the new one takes its
    /// name, and only then is the old one deleted. Deleting it first lost it whenever the rename
    /// after it failed.
    /// </summary>
    /// <param name="asked">The folder as it was when Replace was agreed, to tell what came after.</param>
    private async Task SwapAsideAsync(IFileSystemProvider provider, FsUri final, FsUri staged, FolderCollision? asked, Done done)
    {
        var aside = final.Parent!.Child(TemporaryName.Beside(final.Name, $".dc-old-{Guid.NewGuid():N}"));
        await provider.MoveAsync(final, aside, CancellationToken.None).ConfigureAwait(false);
        try
        {
            await provider.MoveAsync(staged, final, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await provider.MoveAsync(aside, final, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not put {Final} back after a failed swap; it is at {Aside}", final, aside);
                done.Notes.Add($"The old {final.Name} is at {aside.Name}.");
            }

            throw;
        }

        // A file that landed between the last look for arrivals and the step aside was never
        // agreed to, and with no bin the delete took it for good (BUGS.md, 2026-10-28). Aside,
        // the old folder is out of reach of anything saving by its name, so one more look now is
        // the last: anything new or changed in it keeps the whole folder, beside the new one.
        // With no record of what was agreed there is nothing to tell by, so it is kept too.
        string? why;
        try
        {
            // A folder still, before its listing counts: on WebDAV a file lists as nothing, so a
            // file another client put at the name during the long walk before the step aside read
            // as an empty folder and was deleted (review, 2026-10-30).
            if (await provider.StatAsync(aside, CancellationToken.None).ConfigureAwait(false) is not { IsDirectory: true })
                throw new IOException($"{aside.Name} is not a folder any more");
            var left = await FolderCollisionScanner
                .EntriesAsync(provider, aside, CancellationToken.None)
                .ConfigureAwait(false);
            var late = asked is null
                ? left.Count
                : left.Count(entry => !asked.Seen.TryGetValue(entry.Key, out var then) || Changed(then, entry.Value));
            why = late switch
            {
                0 => null,
                _ when asked is null => "what it held when Replace was answered is not known",
                1 => "a file arrived or changed in it just before the swap",
                _ => $"{late:N0} files arrived or changed in it just before the swap",
            };
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not look through the replaced folder {Aside} again", aside);
            why = "it could not be looked through again before deleting";
        }

        if (why is not null)
        {
            log.LogWarning("Kept the replaced folder {Aside}: {Why}", aside, why);
            done.Notes.Add($"The old {final.Name} was left beside the new one, as {aside.Name}: {why}.");
            done.KeptAside.Add((final.Name, aside));
            return;
        }

        try
        {
#pragma warning disable RS0030 // §1.6: the folder the user chose to replace, gone only once its replacement is in place.
            await provider.DeleteAsync(aside, recursive: true, CancellationToken.None).ConfigureAwait(false);
#pragma warning restore RS0030
            done.ReplacedForGood.Add(final.Name);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not remove the replaced folder {Aside}", aside);
            done.Notes.Add($"The old {final.Name} was left beside the new one, as {aside.Name}.");
            done.KeptAside.Add((final.Name, aside));
        }
    }

    /// <summary>
    /// Clears away anything staged but never swapped in, without losing a file. A copy staged
    /// only copies, whose sources are untouched, so they can go; but only they, as the staging
    /// folder may have been given something else meanwhile. A move staged the files themselves:
    /// they have left their sources, and deleting staging deleted them (found 2026-10-03). So
    /// they go back where they came from first. Either way the staging folder is removed only as
    /// far as it is then empty.
    /// </summary>
    private async Task UnstageAsync(TransferJob job, ReplacementPlan? plan, Done done)
    {
        if (plan is null)
            return;

        foreach (var (final, staged) in plan.Staged)
        {
            var provider = providers.Resolve(staged);
            var inside = done.Files.Where(f => IsUnder(f.To, staged)).ToList();

            // The old folder's own files first: they are nobody's copies, and the staging folder
            // is about to be removed.
            var keptBack = 0;
            foreach (var carried in done.Carried.Where(c => IsUnder(c.Staged, staged)).ToList())
            {
                try
                {
                    // Recorded before it moved: one that never left is where it was.
                    if (await provider.StatAsync(carried.Staged, CancellationToken.None).ConfigureAwait(false) is null &&
                        await provider.StatAsync(carried.Old, CancellationToken.None).ConfigureAwait(false) is not null)
                    {
                        done.Carried.Remove(carried);
                        continue;
                    }

                    // Not into a folder made up for it: with the old folder gone (in the Recycle Bin,
                    // or aside) a new one at its name held only this, and clashed with its restore.
                    if (carried.Old.Parent is { } home &&
                        await provider.StatAsync(home, CancellationToken.None).ConfigureAwait(false) is not { IsDirectory: true })
                    {
                        keptBack++;
                        continue;
                    }

                    await PutBackAsync(carried.Staged, carried.Old, CancellationToken.None).ConfigureAwait(false);
                    done.Carried.Remove(carried);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Could not put {File} back into the folder it came from", carried.Staged);
                    keptBack++;
                }
            }

            // Never removed whole while it holds them.
            if (keptBack > 0)
                done.Notes.Add($"{keptBack:N0} of the old {final.Name}'s files could not go back and are in {staged.Name}.");

            if (job.Mode == TransferMode.Copy && keptBack > 0)
            {
                await RemoveEmptyTreeAsync(provider, staged).ConfigureAwait(false);
                continue;
            }

            // A copy's own files go one by one, and the folder only as far as that empties it.
            // It was deleted whole as "only copies", but it sits in the pane under its own name
            // for the whole job, and whatever another job, a drop or Explorer put in it meanwhile
            // went with it, past the Recycle Bin (review, 2026-10-22).
            if (job.Mode == TransferMode.Copy)
            {
                foreach (var file in inside)
                {
                    done.Files.Remove(file);
                    try
                    {
                        if (await provider.StatAsync(file.To, CancellationToken.None).ConfigureAwait(false) is { IsDirectory: false } now &&
                            StillTheCopy(file, now))
                        {
#pragma warning disable RS0030 // §1.6: a copy this job wrote into its own staging folder, still as it was written (size, and not modified since it landed); its source is untouched.
                            await provider.DeleteAsync(file.To, recursive: false, CancellationToken.None).ConfigureAwait(false);
#pragma warning restore RS0030
                        }
                    }
                    catch (Exception ex)
                    {
                        // Debris is unpleasant; failing to report the real error would be worse.
                        log.LogWarning(ex, "Could not remove the copy {File} from staging", file.To);
                    }
                }

                await RemoveEmptyTreeAsync(provider, staged).ConfigureAwait(false);
                if (await provider.StatAsync(staged, CancellationToken.None).ConfigureAwait(false) is not null)
                    done.Notes.Add($"{staged.Name} was left, because something is still in it.");
                continue;
            }

            var stranded = 0;
            foreach (var file in inside)
            {
                try
                {
                    await PutBackAsync(file.To, file.From, CancellationToken.None).ConfigureAwait(false);
                    done.Files.Remove(file);
                }
                catch (Exception ex)
                {
                    // Left where it is and kept in the record, so undo can still try.
                    log.LogError(ex, "Could not put {File} back from staging", file.To);
                    stranded++;
                }
            }

            if (stranded > 0)
                done.Notes.Add($"{stranded:N0} could not be put back and are still in {staged.Name}.");

            await RemoveEmptyTreeAsync(provider, staged).ConfigureAwait(false);
        }

        plan.Staged.Clear();
    }

    /// <summary>Removes a folder and the folders inside it, deepest first, each only if empty.</summary>
    private async Task RemoveEmptyTreeAsync(IFileSystemProvider provider, FsUri directory)
    {
        try
        {
            // Still a folder: listing a file on WebDAV gives nothing rather than failing, and the
            // delete below would then remove a file of that name. And not a folder link: removing
            // one without recursing removes it whatever its folder holds, and walking into it
            // removed that folder's empty folders (review, 2026-10-23). A link inside keeps this
            // folder, as any file would.
            if (await provider.StatAsync(directory, CancellationToken.None).ConfigureAwait(false) is not { IsDirectory: true, LinkTarget: null })
                return;

            var children = new List<FsUri>();
            await foreach (var entry in provider.ListAsync(directory, CancellationToken.None).ConfigureAwait(false))
            {
                if (entry.IsDirectory && entry.LinkTarget is null)
                    children.Add(entry.Uri);
            }

            foreach (var child in children)
                await RemoveEmptyTreeAsync(provider, child).ConfigureAwait(false);

#pragma warning disable RS0030 // §1.6: a folder (checked above), removed only if empty.
            await provider.DeleteAsync(directory, recursive: false, CancellationToken.None).ConfigureAwait(false);
#pragma warning restore RS0030
        }
        catch (Exception ex)
        {
            // Something is still in it; it stays, and nothing in it is lost.
            log.LogWarning(ex, "Left the staging folder {Directory}", directory);
        }
    }

    /// <summary>One of the things picked, rather than something inside one of them.</summary>
    private static bool IsTopLevel(string relativePath) => !relativePath.Contains('/');

    private static bool IsUnder(string relativePath, IReadOnlySet<string> folders)
    {
        var slash = relativePath.IndexOf('/');
        var top = slash < 0 ? relativePath : relativePath[..slash];
        return folders.Contains(top);
    }

    /// <summary>
    /// Which folders are being replaced, where each is being built, and which are being skipped.
    /// </summary>
    private sealed class ReplacementPlan
    {
        // Exact case: two sources whose names differ only in case, on a server that tells them
        // apart, are two duplicates with two names.
        private readonly Dictionary<string, string> _duplicates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _redirects = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _skipped = new(StringComparer.OrdinalIgnoreCase);

        public List<(FsUri Final, FsUri Staged)> Staged { get; } = [];

        public IReadOnlySet<string> Skipped => _skipped;

        /// <summary>Each folder replaced as it was when the user agreed to it.</summary>
        public Dictionary<FsUri, FolderCollision> Agreed { get; } = [];

        public void Replace(FolderCollision asked)
        {
            var destination = asked.Destination;
            Agreed[destination] = asked;

            // Beside the target and on the same volume, so the final swap is a rename rather than
            // another copy. The name is unlikely enough to collide and obvious enough to explain
            // itself if a crash ever leaves one behind.
            var staged = TemporaryName.Beside(destination.Name, $".dc-incoming-{Guid.NewGuid():N}");
            _redirects[destination.Name] = staged;
            Staged.Add((destination, destination.Parent!.Child(staged)));
        }

        public void Skip(string name) => _skipped.Add(name);

        /// <summary>A copy into the folder it came from lands beside the original under <paramref name="free"/>.</summary>
        public void Duplicate(string name, string free) => _duplicates[name] = free;

        /// <summary>Where a relative path ends up once everything is in place: a duplicate's new name.</summary>
        public string Final(string relativePath) => Rewrite(relativePath, _duplicates);

        public bool IsDuplicate(string relativePath) => Final(relativePath) != relativePath;

        /// <summary>Where a relative path is written: its final place, or the staging folder instead.</summary>
        public string Redirect(string relativePath) => Rewrite(Final(relativePath), _redirects);

        private static string Rewrite(string relativePath, Dictionary<string, string> tops)
        {
            var slash = relativePath.IndexOf('/');
            var top = slash < 0 ? relativePath : relativePath[..slash];

            return tops.TryGetValue(top, out var other)
                ? other + (slash < 0 ? string.Empty : relativePath[slash..])
                : relativePath;
        }
    }

    private static FsUri Resolve(FsUri directory, string relativePath)
    {
        var result = directory;
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            result = result.Child(segment);
        return result;
    }

    /// <summary>How far one file got, kept across the in-use retries of it.</summary>
    /// <param name="Replace">Whether the user chose to replace a file already at the target.</param>
    private sealed record Attempt(bool Replace)
    {
        public bool Replace { get; set; } = Replace;

        /// <summary>When what was there was looked at, before any question: a landing since is news.</summary>
        public DateTimeOffset DecidedAt { get; init; }

        /// <summary>The copy has taken its name; only deleting a moved source is left.</summary>
        public bool Copied { get; set; }

        /// <summary>
        /// Says the copy has landed, for other transfers to know, as soon as it has. Said after the
        /// source's delete, a question about an original in use held it back for as long as the
        /// question stood, and another transfer could write over the copy unasked (review, 2026-10-23).
        /// </summary>
        public Action? Landed { get; init; }

        public long Written { get; set; }

        /// <summary>The source as it was when it was read, to tell whether it changed since.</summary>
        public FsEntry? SourceRead { get; set; }
    }

    /// <returns>The first bytes of what was copied, or null when a rename moved it in place.</returns>
    private async Task<byte[]?> TransferOneAsync(TransferJob job, Item item, FsUri target, Attempt attempt)
    {
        // Asked again after the copy landed and the original was in use: copying a second time
        // would meet our own copy at the name. Only the delete is tried again.
        // Unless the original changed meanwhile: "close it there, then Retry" is also "save my
        // edits, close it, Retry", and deleting it then deleted the edits, the copy being older.
        if (attempt.Copied)
        {
            var now = await providers.Resolve(item.Source).StatAsync(item.Source, job.Token).ConfigureAwait(false);
            if (now is not null && attempt.SourceRead is { } read && (now.Size != read.Size || now.Modified != read.Modified))
            {
                // Copied again, over our own copy of it, which is all that is at the name now.
                attempt.Copied = false;
                attempt.Replace = true;
            }
            else
            {
                await DeleteMovedSourceAsync(item.Source, target, attempt.Written, attempt.SourceRead, job.Token).ConfigureAwait(false);
                return null;
            }
        }

        var sourceProvider = providers.Resolve(item.Source);

        // Within one provider and one host a move is a rename: no bytes need to travel at all.
        if (job.Mode == TransferMode.Move && CanRename(item.Source, target))
        {
            try
            {
                await sourceProvider.MoveAsync(item.Source, target, job.Token).ConfigureAwait(false);
                job.BytesDone += item.Size;
                return null;
            }
            catch (IOException ex) when (!InUse.Is(ex))
            {
                // Across volumes a rename is not possible; fall through to copy-then-delete. Not
                // when the file is in use: copying it and then failing to delete the source left
                // it in both places and the job failed, where the user only needed asking.
            }
        }

        // The first bytes are kept on the way past: they are what says whether the file is meant to
        // run, and reading them again from the target would be a second round trip for each file.
        var head = new byte[ExecutableContent.HeadLength];
        var headLength = 0;
        attempt.SourceRead = await sourceProvider.StatAsync(item.Source, job.Token).ConfigureAwait(false);

        var written = await CopyCheckedAsync(item.Source, target, item.Size, chunk =>
        {
            if (headLength < head.Length)
            {
                var take = Math.Min(chunk.Length, head.Length - headLength);
                chunk.Span[..take].CopyTo(head.AsSpan(headLength));
                headLength += take;
            }

            job.BytesDone += chunk.Length;
            job.Estimate.Moved(chunk.Length, job.Now);
        }, attempt.Replace, job.Token, finishing: text => job.Finishing = text, counted: bytes =>
        {
            job.BytesDone += bytes;
            job.Estimate.Moved(bytes, job.Now);
        }, durable: job.Mode == TransferMode.Move,
        // Asked again at the commit: a large file takes minutes, and another transfer can land
        // its own file at the name meanwhile, which the check before the copy never saw
        // (review, 2026-10-23).
        mayReplace: () => !LandedByAnother(target, job, attempt.DecidedAt)).ConfigureAwait(false);

        attempt.Copied = true;
        attempt.Written = written;
        attempt.Landed?.Invoke();

        if (job.Mode == TransferMode.Move)
            await DeleteMovedSourceAsync(item.Source, target, written, attempt.SourceRead, job.Token).ConfigureAwait(false);

        return head[..headLength];
    }

    /// <summary>
    /// Copies one file and lets it take its name only if all of it arrived (§1.6).
    /// </summary>
    /// <remarks>
    /// A source that ends early without throwing — a server closing the connection mid-body —
    /// looks exactly like the end of the file. Fewer bytes than the listing promised is checked
    /// against the source as it is now, so a file that really did shrink still copies, and a cut
    /// short one is thrown away rather than kept, or worse, followed by deleting its source.
    /// </remarks>
    /// <param name="seen">Each piece, once written.</param>
    /// <returns>How many bytes were written.</returns>
    /// <param name="replace">Whether a file already at the target may be replaced: only when the user said so.</param>
    /// <param name="finishing">Told what the commit is waiting for when it takes a while, then null.</param>
    /// <param name="counted">Told the bytes moved when there are no pieces to see: a copy the provider does itself.</param>
    private async Task<long> CopyCheckedAsync(
        FsUri source, FsUri target, long expected, Action<ReadOnlyMemory<byte>>? seen, bool replace, CancellationToken ct,
        Action<string?>? finishing = null, Action<long>? counted = null, bool durable = false, Func<bool>? mayReplace = null)
    {
        var sourceProvider = providers.Resolve(source);
        var targetProvider = providers.Resolve(target);
        long written = 0;

        // Within one provider that copies for itself (local to local), it does: the file keeps its
        // dates, attributes and streams, which a copy of its bytes lost. Its own copy is to a .part
        // and committed as below, and it can't end early without saying so, as a stream can.
        if (ReferenceEquals(sourceProvider, targetProvider)
            && sourceProvider is ICopiesWithin within
            && within.CanCopyWithin(source, target))
        {
            return await within.CopyWithinAsync(source, target, replace, durable, n => counted?.Invoke(n), ct, mayReplace).ConfigureAwait(false);
        }

        await using var read = await sourceProvider.OpenReadAsync(source, ct).ConfigureAwait(false);
        await using var write = await targetProvider.OpenWriteAsync(target, expected, ct).ConfigureAwait(false);

        var buffer = new byte[BufferSize];
        int count;
        while ((count = await read.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await write.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            written += count;
            seen?.Invoke(buffer.AsMemory(0, count));
        }

        if (written < expected)
        {
            var now = await sourceProvider.StatAsync(source, ct).ConfigureAwait(false);
            if (now is null || now.Size > written)
            {
                throw new IOException(
                    $"Reading {source.Name} stopped after {written:N0} of {expected:N0} bytes, so the copy was not kept.");
            }
        }

        if (replace && mayReplace is not null && !mayReplace())
            throw ReplacedMeanwhile(target);

        await CommitSayingSoAsync(write, target, replace, durable, finishing, ct).ConfigureAwait(false);
        return written;
    }

    /// <summary>
    /// Commits, and if that takes a while, says what it is waiting for: a server joining an
    /// upload's pieces can take minutes after the last byte, with the bar at 100% meanwhile.
    /// Said only after <see cref="FinishingAfter"/>, so a local rename does not make the bar flicker.
    /// </summary>
    /// <param name="durable">
    /// On the disk before this returns: a move deletes its source next, and a download only in
    /// the disk's cache was the one copy left when the power went (§4, 2026-10-08).
    /// </param>
    private async Task CommitSayingSoAsync(
        Stream write, FsUri target, bool replace, bool durable, Action<string?>? finishing, CancellationToken ct)
    {
        if (finishing is null)
        {
            await write.CommitAsync(replace, durable, ct).ConfigureAwait(false);
            return;
        }

        var what = target.Authority.Length > 0
            ? $"Waiting for {target.Authority} to finish {target.Name}"
            : $"Finishing {target.Name}";
        var gate = new object();
        var over = false;

        _ = Task.Delay(FinishingAfter, CancellationToken.None).ContinueWith(
            _ =>
            {
                lock (gate)
                {
                    if (!over)
                        Say(what);
                }
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            await write.CommitAsync(replace, durable, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                over = true;
                Say(null);
            }
        }

        // Only what the strip shows: a listener that throws must not stand in for the commit's
        // own outcome, which decides whether the file counts as copied and a move's source goes.
        void Say(string? text)
        {
            try
            {
                finishing(text);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Showing what a commit waits for failed");
            }
        }
    }

    /// <summary>
    /// Deletes what a move copied, once the copy is known to be there and whole: before this, the
    /// source went as soon as the read loop ended, on nothing but the absence of an exception.
    /// </summary>
    /// <param name="read">The source as it was before it was copied: one saved since is newer than its copy.</param>
    private async Task DeleteMovedSourceAsync(FsUri source, FsUri target, long written, FsEntry? read, CancellationToken ct)
    {
        var landed = await providers.Resolve(target).StatAsync(target, ct).ConfigureAwait(false);
        if (landed is null || landed.IsDirectory || landed.Size != written)
        {
            throw new IOException(
                $"{target.Name} did not arrive whole, so {source.Name} was kept where it was.");
        }

        // Saved again while it was being copied: deleting it would throw away the newer one and
        // keep the older copy. Checked last, just before the delete, which is where it matters.
        var now = await providers.Resolve(source).StatAsync(source, ct).ConfigureAwait(false);
        if (read is not null && now is not null && (now.Size != read.Size || now.Modified != read.Modified))
        {
            throw new IOException(
                $"{source.Name} changed while it was being moved, so it was kept; the copy at {target.Name} is of it as it was.");
        }

#pragma warning disable RS0030 // §1.6: a move's source, once its copy is checked there and whole: no longer the last one.
        await providers.Resolve(source).DeleteAsync(source, recursive: false, ct).ConfigureAwait(false);
#pragma warning restore RS0030
    }

    /// <summary>
    /// Whether a move between these is one rename. One provider serves every address of its
    /// scheme, so being the same provider is not enough: a "rename" from one server to another
    /// renamed the file on the first server (WebDAV) or threw part way through an undo (SFTP).
    /// Local paths rename across drives, which File.Move does as a copy and a delete.
    /// </summary>
    /// <remarks>
    /// Locally, only on one drive or share. Across two, Windows' rename copies by itself, with no
    /// way to cancel it and straight to the real name, so a move closed or killed part way left a
    /// short file there; the transfer's copy goes to a .part, is checked, stops when asked, and
    /// (through <see cref="ICopiesWithin"/>) keeps the dates, attributes and streams the rename
    /// kept. Read from the address, whose drive is always written one way, not from the local
    /// path, which gains a \\?\ prefix past 240 characters and would look like another drive.
    /// </remarks>
    private bool CanRename(FsUri from, FsUri to) =>
        ReferenceEquals(providers.Resolve(from), providers.Resolve(to)) &&
        (from.IsLocal
            ? string.Equals(LocalRoot(from), LocalRoot(to), StringComparison.OrdinalIgnoreCase)
            : string.Equals(from.Authority, to.Authority, StringComparison.Ordinal));

    /// <summary>"C:" for <c>C:/x</c>, "//server/share" for <c>//server/share/x</c>.</summary>
    private static string LocalRoot(FsUri uri)
    {
        var path = uri.Path;
        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            var segments = path[2..].Split('/', 3);
            return segments.Length >= 2 ? $"//{segments[0]}/{segments[1]}" : path;
        }

        return path.Length >= 2 && path[1] == ':' ? path[..2] : "/";
    }

    /// <summary>
    /// Whether two addresses are one file. Only asked once the target is known to exist, so an
    /// ordinary copy into an empty folder never pays for it.
    /// </summary>
    private async Task<bool> IsSameFileAsync(FsUri source, FsUri target, CancellationToken ct) =>
        source == target ||
        providers.Resolve(source) is IFileIdentity identity &&
        ReferenceEquals(identity, providers.Resolve(target)) &&
        await identity.IsSameFileAsync(source, target, ct).ConfigureAwait(false);

    private static readonly char[] NotInWindowsNames = Path.GetInvalidFileNameChars();

    private static readonly HashSet<string> WindowsDevices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Whether every segment of a relative path is one plain name on Windows: no separator or
    /// other character Windows refuses, not "." or "..", no trailing dot or space (Windows drops
    /// them, so "a." would land on "a"), and not a device such as NUL or CON.txt.
    /// </summary>
    internal static bool IsSafeOnWindows(string relativePath)
    {
        foreach (var name in relativePath.Split('/'))
        {
            if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(NotInWindowsNames) >= 0 ||
                name[^1] is '.' or ' ')
            {
                return false;
            }

            var dot = name.IndexOf('.');
            if (WindowsDevices.Contains(dot < 0 ? name : name[..dot]))
                return false;
        }

        return true;
    }

    private static bool IsUnder(FsUri uri, FsUri directory)
    {
        for (var parent = uri.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent == directory)
                return true;
        }

        return false;
    }

    /// <summary><paramref name="uri"/> moved from under <paramref name="from"/> to under <paramref name="to"/>, or null if it is not under it.</summary>
    private static FsUri? Rebased(FsUri uri, FsUri from, FsUri to)
    {
        var names = new Stack<string>();
        for (var at = uri; at is not null; at = at.Parent)
        {
            if (at == from)
            {
                var result = to;
                while (names.Count > 0)
                    result = result.Child(names.Pop());
                return result;
            }

            names.Push(at.Name);
        }

        return null;
    }

    /// <summary>
    /// Moves a move's folder links as themselves, once everything else has landed and any folder
    /// replaced has been swapped in.
    /// </summary>
    /// <remarks>
    /// Last and straight to where they stay, so a link is never in a staging folder, where taking
    /// that folder apart walked through it; and a move that stops before here leaves every link
    /// where it was, with the folder holding it. One that can't go (to a server, onto a name in
    /// use, a drive mounted in a folder) stays where it is, and so does its folder, and the log
    /// says which and why. Not cancelled part way: each is one rename or one make-and-remove.
    /// </remarks>
    private async Task MoveLinksAsync(TransferJob job, ReplacementPlan plan, Done done, List<Item> links)
    {
        foreach (var item in links)
        {
            var target = Resolve(job.TargetDirectory, plan.Final(item.RelativePath));
            var provider = providers.Resolve(item.Source);
            var name = item.RelativePath.Replace('/', '\\');
            job.CurrentName = item.RelativePath;

            if (provider is not IMovesLinks mover || !ReferenceEquals(provider, providers.Resolve(target)))
            {
                done.LinksLeft.Add($"{name}, which can't be made in {job.TargetDirectory.Display}");
                continue;
            }

            try
            {
                if (await provider.StatAsync(target, CancellationToken.None).ConfigureAwait(false) is not null)
                {
                    done.LinksLeft.Add($"{name}, because something has its name there");
                    continue;
                }

                await mover.MoveLinkAsync(item.Source, target, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                log.LogWarning(ex, "Left the link {Link} where it was", item.Source);
                done.LinksLeft.Add($"{name} ({ex.Message.TrimEnd('.')})");
                continue;
            }

            done.Links.Add((item.Source, target));
            job.ItemsDone++;
            if (IsTopLevel(item.RelativePath))
                done.Landed.Add(target);
        }
    }

    /// <summary>
    /// Moves each picked folder that can go as one rename, before anything else is looked at.
    /// </summary>
    /// <remarks>
    /// Only locally, on one drive, to a name nothing has: Windows renames a folder there in one
    /// step that either happens or doesn't, so every file is in exactly one place throughout, and
    /// it never copies (across volumes, a folder mounted in a folder among them, it refuses). Not
    /// when another picked source has the same name: that one would then merge into this folder
    /// file by file, where the check that one source never replaces another's file can't see what
    /// arrived inside this one. Not into itself, which the walk refuses in words. Refused, because
    /// something in it is open or a terminal stands in it, it goes file by file as before, which
    /// asks about what is in use. Not cancelled part way: it is one step.
    /// <para>
    /// Only a name the user was not asked about: a folder that was there when they answered
    /// Replace or Skip and has gone since is still that answer. Renamed into its place, the swap
    /// sent the moved folder to the bin as the one being replaced (review, 2026-10-26). Only on a
    /// disk of this machine: on a network share the rename can happen with its answer lost, and
    /// the folder would be at the target with no record of it.
    /// </para>
    /// </remarks>
    /// <returns>The sources moved whole, for the walk to leave out.</returns>
    private async Task<HashSet<FsUri>> MoveWholeFoldersAsync(TransferJob job, ReplacementPlan plan, Done done)
    {
        var moved = new HashSet<FsUri>();
        if (!job.TargetDirectory.IsLocal || !IsThisMachinesDisk(job.TargetDirectory))
            return moved;

        // Into itself is refused before anything moves, as it was when every folder was walked
        // first: a job that moved two folders and then refused the third was half done.
        foreach (var source in job.Sources)
        {
            if (await HoldsAsync(source, job.TargetDirectory, job.Token).ConfigureAwait(false))
                return moved;
        }

        var shared = job.Sources.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var source in job.Sources)
        {
            job.Token.ThrowIfCancellationRequested();

            var target = job.TargetDirectory.Child(source.Name);
            if (!source.IsLocal || shared.Contains(source.Name) || !CanRename(source, target)
                || plan.Skipped.Contains(source.Name) || plan.Redirect(source.Name) != source.Name
                || source.Parent is not { } parent || parent.Equals(job.TargetDirectory))
                continue;

            var provider = providers.Resolve(source);
            if (await provider.StatAsync(source, job.Token).ConfigureAwait(false) is not { IsDirectory: true, LinkTarget: null }
                || await provider.StatAsync(target, job.Token).ConfigureAwait(false) is not null)
                continue;

            job.CurrentName = source.Name;
            try
            {
                await provider.MoveAsync(source, target, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Taken as moved only when it plainly was: gone from here, a folder there.
                if (!await WentWholeAsync(provider, source, target).ConfigureAwait(false))
                {
                    log.LogInformation(ex, "{Source} could not be moved in one step; moving what is in it instead", source);
                    continue;
                }
            }

            done.Folders.Add((source, target));
            done.Landed.Add(target);
            // Noted as a folder: another transfer meeting a file inside it asks, as it would about
            // one this job had moved on its own (see LandedByAnother).
            NoteLanding(target, job);
            moved.Add(source);
            job.ItemsTotal++;
            job.ItemsDone++;
        }

        return moved;
    }

    /// <summary>A drive letter on a fixed or removable disk here, not a share or a redirected drive.</summary>
    private static bool IsThisMachinesDisk(FsUri uri)
    {
        var root = LocalRoot(uri);
        if (root.Length != 2 || root[1] != ':')
            return false;
        try
        {
            return new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<bool> WentWholeAsync(IFileSystemProvider provider, FsUri source, FsUri target)
    {
        try
        {
            return await provider.StatAsync(source, CancellationToken.None).ConfigureAwait(false) is null
                   && await provider.StatAsync(target, CancellationToken.None).ConfigureAwait(false) is { IsDirectory: true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task RemoveEmptiedSourceDirectoriesAsync(TransferJob job, ReplacementPlan plan, List<Item> items, Done done)
    {
        // Deepest first, so a tree comes apart from the leaves upward.
        foreach (var item in items.Where(i => i.IsDirectory).OrderByDescending(i => i.RelativePath.Length))
        {
            try
            {
                // Still a folder: a provider's delete removes a file of that name too.
                if (await providers.Resolve(item.Source).StatAsync(item.Source, job.Token).ConfigureAwait(false) is not { IsDirectory: true, LinkTarget: null })
                    continue;

                // Not the folder it was moved to: a folder moved onto itself under another name
                // (its short 8.3 name, a subst drive, a junction) had every file skipped as the same
                // file, and then its empty subfolders removed as if this move had emptied them.
                if (await IsSameFileAsync(item.Source, Resolve(job.TargetDirectory, plan.Final(item.RelativePath)), job.Token).ConfigureAwait(false))
                    continue;

#pragma warning disable RS0030 // §1.6: a source folder this move emptied, still a folder and not a link, removed only if empty.
                await providers.Resolve(item.Source)
                    .DeleteAsync(item.Source, recursive: false, job.Token).ConfigureAwait(false);
#pragma warning restore RS0030
                done.RemovedSources.Add(item.Source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Something was skipped or added underneath; leaving the folder is the safe choice.
            }
        }
    }

    private async Task EnsureDirectoryAsync(FsUri directory, CancellationToken ct)
    {
        var provider = providers.Resolve(directory);
        if (await provider.StatAsync(directory, ct).ConfigureAwait(false) is not null)
            return;
        if (directory.Parent is { } parent)
            await EnsureDirectoryAsync(parent, ct).ConfigureAwait(false);
        await provider.CreateDirectoryAsync(directory, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The folders the move made, deepest first, each removed only if empty. Only those it made:
    /// worked out as "every folder a moved file is in", it also took a folder that was there
    /// before and was merged into, once the undo had emptied it (§4, 2026-10-08).
    /// </summary>
    private async Task RemoveMadeDirectoriesAsync(List<FsUri> made, CancellationToken ct)
    {
        foreach (var directory in made.Distinct().OrderByDescending(d => d.Path.Length))
        {
            try
            {
                // Still a folder: a provider's delete removes a file of that name too, and a folder
                // link whatever it points to.
                if (await providers.Resolve(directory).StatAsync(directory, ct).ConfigureAwait(false) is not { IsDirectory: true, LinkTarget: null })
                    continue;

#pragma warning disable RS0030 // §1.6: a folder the move made under the target, still a folder and not a link, removed only if empty.
                await providers.Resolve(directory).DeleteAsync(directory, recursive: false, ct).ConfigureAwait(false);
#pragma warning restore RS0030
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Something else is in it now; it stays.
            }
        }
    }

    private static async Task<bool> ExistsAsync(IFileSystemProvider provider, FsUri uri) =>
        await provider.StatAsync(uri, CancellationToken.None).ConfigureAwait(false) is not null;

    private static async Task<FsUri> UniqueAsync(IFileSystemProvider provider, FsUri target)
    {
        var name = target.Name;
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : string.Empty;
        var parent = target.Parent ?? target;

        for (var n = 2; n < 1000; n++)
        {
            var candidate = parent.Child($"{stem} ({n}){extension}");
            if (!await ExistsAsync(provider, candidate).ConfigureAwait(false))
                return candidate;
        }

        // Never the name itself: "keep both" then wrote over the one kept, and a carry under it
        // failed, or unstaging took someone else's file for ours (review, 2026-10-23).
        var last = parent.Child($"{stem} ({Guid.NewGuid().ToString("N")[..8]}){extension}");
        return await ExistsAsync(provider, last).ConfigureAwait(false)
            ? throw new IOException($"No free name was found beside {target.Name}, so nothing was written there.")
            : last;
    }

    /// <summary>
    /// Marks the job as waiting for the length of a question, so the strip says the transfer is
    /// stopped on you rather than showing a bar that has simply stalled.
    /// </summary>
    /// <summary>The job's questions, each marked on its strip while it waits (<see cref="WhileAsking"/>).</summary>
    private sealed class AskingFor(TransferJob job, IDecisionHost inner) : IDecisionHost
    {
        public Task<string?> AskAsync(ChoiceRequest request, CancellationToken ct) =>
            WhileAsking(job, () => inner.AskAsync(request, ct));

        public Task<string?> AskTextAsync(TextRequest request, CancellationToken ct) =>
            WhileAsking(job, () => inner.AskTextAsync(request, ct));

        public Task<SecretAnswer?> AskSecretAsync(SecretRequest request, CancellationToken ct) =>
            WhileAsking(job, () => inner.AskSecretAsync(request, ct));

        public void Report(Notice notice) => inner.Report(notice);
    }

    private static async Task<T> WhileAsking<T>(TransferJob job, Func<Task<T>> ask)
    {
        job.WaitingFor = "Waiting for your answer";
        // The wait for an answer is the user's time, not the copy's: kept out of the estimate.
        job.Estimate.Interrupted();
        try
        {
            return await ask().ConfigureAwait(false);
        }
        finally
        {
            job.Estimate.Resumed(job.Now);
            job.WaitingFor = null;
        }
    }

    // Files our transfers have landed, the latest landing of each with when and by which job, kept
    // while a job that began before it still runs: what LandedByAnother asks.
    private readonly object _landingsGate = new();
    private readonly Dictionary<FsUri, (DateTimeOffset At, TransferJob By)> _landings = [];
    private readonly Dictionary<TransferJob, DateTimeOffset> _running = [];

    private void Begin(TransferJob job)
    {
        lock (_landingsGate)
            _running[job] = DateTimeOffset.Now;
    }

    private void End(TransferJob job)
    {
        lock (_landingsGate)
        {
            _running.Remove(job);
            if (_running.Count == 0)
            {
                _landings.Clear();
                return;
            }

            var oldest = _running.Values.Min();
            foreach (var file in _landings.Where(l => l.Value.At < oldest).Select(l => l.Key).ToList())
                _landings.Remove(file);
        }
    }

    private void NoteLanding(FsUri file, TransferJob job)
    {
        lock (_landingsGate)
        {
            if (_running.Count > 1)
                _landings[file] = (DateTimeOffset.Now, job);
        }
    }

    /// <summary>
    /// Whether another of our transfers put <paramref name="file"/> there since
    /// <paramref name="since"/>, or since <paramref name="job"/> began.
    /// </summary>
    private bool LandedByAnother(FsUri file, TransferJob job, DateTimeOffset? since = null)
    {
        lock (_landingsGate)
        {
            if (!_running.TryGetValue(job, out var began))
                return false;

            // The file itself, or a folder a move renamed whole with it inside.
            for (var at = file; at is not null; at = at.Parent)
            {
                if (_landings.TryGetValue(at, out var landing) && !ReferenceEquals(landing.By, job) && landing.At >= (since ?? began))
                    return true;
            }
            return false;
        }
    }

    /// <summary>Where a file written into a staging folder will be once that folder is swapped in.</summary>
    /// <remarks>
    /// The address another job would meet it at: noted at its staging address, a file a Replace
    /// brought in was unknown at its real one, and written over there unasked (review, 2026-10-23).
    /// </remarks>
    private static FsUri FinalOf(ReplacementPlan plan, FsUri target) =>
        plan.Staged.Select(s => Rebased(target, s.Staged, s.Final)).FirstOrDefault(r => r is not null) ?? target;

    private static IOException ReplacedMeanwhile(FsUri target) => new(
        $"{target.Name} was put there by another transfer while this one was copying over it, so it was kept and this transfer stopped.");

    /// <summary>
    /// Whether the file was written, renamed or made after "Replace" was asked: its change time
    /// past the question, by the clock of whatever holds it. The modified time won't do, as a copy
    /// keeps the original's. A file that can't be told about is asked about.
    /// </summary>
    /// <remarks>
    /// The file asked about is told by its own stamp, unchanged since the look or not, which needs
    /// no clock. The rest: on this machine's disks by this machine's clock, give or take FAT's two
    /// seconds; on a share by the server's, which can run slow or fast of ours (one running slow
    /// made a file saved there just now look older than the question, review 2026-10-28). Its
    /// offset is read from the first file this transfer wrote there; until one has landed, every
    /// clash there is asked about. Where there is no bin, a file written over is gone for good,
    /// which is why the check can't simply be left out there (BUGS.md 4, kind 67).
    /// </remarks>
    private bool ChangedSince(IFileSystemProvider provider, FsUri file, ReplaceAnswer replaced)
    {
        if (replaced.At is not { } since || provider is not IChangeStamps stamps)
            return false;
        DateTimeOffset? stamp;
        try
        {
            stamp = stamps.ChangedAt(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        if (stamp is not { } changed)
            return true;
        var isAsked = file == replaced.Asked;
        if (isAsked && changed != replaced.AskedStamp)
            return true;
        if (recycleBin.CanRecycle(file) || IsThisMachinesDisk(file))
            return changed > since - SlackOf(file);
        if (replaced.ServerClock(file) is { } offset)
            return changed > since + offset - ClockTick;
        return !isAsked;
    }

    /// <summary>
    /// <see cref="ChangedSince"/>, or on a server, which keeps no change time: the file asked about
    /// looked at again, and the rest held against what their folders listed as the question stood.
    /// A size or modified time that differs, or a file that wasn't there then, is asked about.
    /// </summary>
    /// <remarks>
    /// A server was left out of the check, so a colleague's upload after the answer was written
    /// over unasked (BUGS.md 4, kind 67). A save of the same size within the server's own time
    /// resolution (a second on WebDAV) still reads as unchanged.
    /// </remarks>
    private async Task<bool> ChangedSinceAsync(
        IFileSystemProvider provider, FsUri file, FsEntry there, ReplaceAnswer replaced, CancellationToken ct)
    {
        if (provider is IChangeStamps || replaced.At is null)
            return ChangedSince(provider, file, replaced);
        if (file == replaced.Asked)
            return !Unchanged(replaced.AskedEntry, await provider.StatAsync(file, ct).ConfigureAwait(false));
        return replaced.Snapshot is not { } snapshot || !Unchanged(await snapshot.HeldAsync(file).ConfigureAwait(false), there);
    }

    /// <summary>The folders this transfer will write files into from <paramref name="from"/> on, in the order it reaches them.</summary>
    private static List<FsUri> FoldersAhead(TransferJob job, ReplacementPlan plan, List<Item> items, Item from)
    {
        var folders = new List<FsUri>();
        var seen = new HashSet<FsUri>();
        foreach (var item in items.Skip(items.IndexOf(from)))
        {
            if (item.IsDirectory || item.IsLink)
                continue;
            if (Resolve(job.TargetDirectory, plan.Redirect(item.RelativePath)).Parent is { } folder && seen.Add(folder))
                folders.Add(folder);
        }
        return folders;
    }

    /// <summary>
    /// What a server's target folders held as "Replace" was asked: listed one by one in the order
    /// the transfer reaches them, from the moment the question appears, so the transfer waits for
    /// no folder but the one it is in. A folder that couldn't be listed, or wasn't there, held
    /// nothing, and every clash in it is asked about. A save into a folder before its turn comes,
    /// whether the question still stands or was answered, is in its listing and is then replaced
    /// unasked: the window is the time the listings ahead of it take, minutes in a tree of
    /// thousands of folders on a slow server (review, 2026-11-03; BUGS.md 4).
    /// </summary>
    private sealed class ServerSnapshot : IDisposable
    {
        private readonly Dictionary<FsUri, TaskCompletionSource<Dictionary<string, FsEntry>?>> _folders = [];
        private readonly CancellationTokenSource _stop;

        public ServerSnapshot(IFileSystemProvider provider, List<FsUri> folders, CancellationToken ct)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            foreach (var folder in folders)
                _folders[folder] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var stop = _stop.Token;
            _ = Task.Run(() => ListAllAsync(provider, folders, stop), CancellationToken.None);
        }

        private async Task ListAllAsync(IFileSystemProvider provider, List<FsUri> folders, CancellationToken stop)
        {
            foreach (var folder in folders)
            {
                Dictionary<string, FsEntry>? held = null;
                try
                {
                    stop.ThrowIfCancellationRequested();
                    // Ordinal: a server's names differ by case, and one read as another would be
                    // the wrong file's times; a name that doesn't match is asked about instead.
                    held = new(StringComparer.Ordinal);
                    await foreach (var entry in provider.ListAsync(folder, stop).ConfigureAwait(false))
                        held[entry.Name] = entry;
                }
                catch (Exception)
                {
                    // Not there, refused, cut off or stopped: nothing known held, so asked.
                    held = null;
                }
                _folders[folder].TrySetResult(held);
            }
        }

        /// <summary>The file as its folder listed it, or null when it wasn't there or the folder isn't known.</summary>
        public async Task<FsEntry?> HeldAsync(FsUri file)
        {
            if (file.Parent is not { } folder || !_folders.TryGetValue(folder, out var listed))
                return null;
            return await listed.Task.ConfigureAwait(false) is { } held && held.TryGetValue(file.Name, out var entry) ? entry : null;
        }

        public void Dispose()
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }

    /// <summary>
    /// How far behind the moment of writing a file's times can be: Windows stamps them from a clock
    /// that moves on its timer tick, while the question's moment is read to the microsecond. A save
    /// within one tick of the look read as unchanged (BUGS.md 48, the flaky tests).
    /// </summary>
    private static readonly TimeSpan ClockTick = TimeSpan.FromMilliseconds(20);

    /// <summary>How far a disk here may stamp a file before the moment it was written: a clock tick, or FAT's two seconds.</summary>
    private static TimeSpan SlackOf(FsUri file)
    {
        try
        {
            return new DriveInfo(LocalRoot(file)).DriveFormat.StartsWith("FAT", StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.FromSeconds(2)
                : ClockTick;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return TimeSpan.FromSeconds(2);
        }
    }

    /// <summary>The file's change time, or null when it can't be read or the provider keeps none.</summary>
    private static DateTimeOffset? StampOf(IFileSystemProvider provider, FsUri file)
    {
        try
        {
            return provider is IChangeStamps stamps ? stamps.ChangedAt(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>"Replace" for the rest of one transfer: when it was asked, about what, and the target's clock.</summary>
    private sealed class ReplaceAnswer : IDisposable
    {
        private string? _serverRoot;
        private TimeSpan? _serverOffset;
        private bool _clockTried;

        /// <summary>When the question was put, by this machine's clock; null until Replace was answered.</summary>
        public DateTimeOffset? At { get; private set; }

        /// <summary>The file the question was about.</summary>
        public FsUri? Asked { get; private set; }

        /// <summary>Its change time at the look, by the clock of whatever holds it.</summary>
        public DateTimeOffset? AskedStamp { get; private set; }

        /// <summary>The file asked about as it was looked at, for a server, which keeps no stamp.</summary>
        public FsEntry? AskedEntry { get; private set; }

        /// <summary>What a server's folders held as the question stood; null on a disk.</summary>
        public ServerSnapshot? Snapshot { get; private set; }

        public void Answered(DateTimeOffset at, FsUri asked, DateTimeOffset? stamp, FsEntry? entry, ServerSnapshot? snapshot)
        {
            At = at;
            Asked = asked;
            AskedStamp = stamp;
            AskedEntry = entry;
            Snapshot = snapshot;
        }

        public void Dispose() => Snapshot?.Dispose();

        /// <summary>
        /// The share's clock less ours, from a file this transfer has just written there, once per
        /// transfer. Read after the file landed, so it can come out early by the time the write
        /// took to be told of, never late: a file from before the answer may be asked about, one
        /// after it is never missed. By the change time alone: the copy's modified time is its
        /// original's, and a freshly saved one read the offset minutes late (review, 2026-11-03).
        /// </summary>
        public void LearnClockFrom(IFileSystemProvider provider, FsUri written, DateTimeOffset landedAt)
        {
            if (_clockTried)
                return;
            _clockTried = true;
            if (written.IsLocal && provider is IChangeStamps stamps && !IsThisMachinesDisk(written) && ClockOf(stamps, written) is { } stamp)
            {
                _serverRoot = LocalRoot(written);
                _serverOffset = stamp - landedAt;
            }
        }

        private static DateTimeOffset? ClockOf(IChangeStamps stamps, FsUri file)
        {
            try
            {
                return stamps.ClockedAt(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>The offset learnt for the file's share, or null when none was.</summary>
        public TimeSpan? ServerClock(FsUri file) =>
            _serverRoot is not null && string.Equals(LocalRoot(file), _serverRoot, StringComparison.OrdinalIgnoreCase)
                ? _serverOffset
                : null;
    }

    /// <summary>The clash question for one file that arrived or changed after "Replace" was answered for the rest.</summary>
    private static async Task<ConflictPolicy> AskAboutArrivalAsync(IDecisionHost decisions, FsUri target, bool byAnotherTransfer, CancellationToken ct)
    {
        var answer = await decisions.AskAsync(
            new ChoiceRequest(
                byAnotherTransfer
                    ? $"{target.Name} in {target.Parent?.Name} was put there by another transfer since this one began."
                    : $"{target.Name} in {target.Parent?.Name} was written or put there after Replace was chosen.",
                [
                    new DecisionOption("keepBoth", "Keep both", IsDefault: true),
                    new DecisionOption("overwrite", "Replace"),
                    new DecisionOption("skip", "Skip"),
                    new DecisionOption("cancel", "Cancel", IsCancel: true),
                ],
                "This choice is for this file only; the one before it still applies to the rest.",
                NoticeSeverity.Warning),
            ct).ConfigureAwait(false);

        return answer switch
        {
            "keepBoth" => ConflictPolicy.KeepBoth,
            "overwrite" => ConflictPolicy.Overwrite,
            "skip" => ConflictPolicy.Skip,
            _ => ConflictPolicy.Ask,
        };
    }

    private static async Task<ConflictPolicy> AskAsync(IDecisionHost decisions, FsUri target, CancellationToken ct)
    {
        var answer = await decisions.AskAsync(
            new ChoiceRequest(
                $"{target.Name} already exists in {target.Parent?.Name}.",
                [
                    new DecisionOption("keepBoth", "Keep both", IsDefault: true),
                    new DecisionOption("overwrite", "Replace"),
                    new DecisionOption("skip", "Skip"),
                    new DecisionOption("cancel", "Cancel", IsCancel: true),
                ],
                "This choice applies to the rest of this transfer.",
                NoticeSeverity.Warning),
            ct).ConfigureAwait(false);

        return answer switch
        {
            "keepBoth" => ConflictPolicy.KeepBoth,
            "overwrite" => ConflictPolicy.Overwrite,
            "skip" => ConflictPolicy.Skip,
            _ => ConflictPolicy.Ask, // dismissed: treated as cancel by the caller
        };
    }
}
