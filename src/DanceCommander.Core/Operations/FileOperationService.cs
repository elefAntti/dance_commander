using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Operations;

/// <summary>
/// Moving entries to the Recycle Bin, where the platform supports it. Deleting without a way back
/// should be something the user asks for explicitly, not the default.
/// </summary>
public interface IRecycleBin
{
    bool CanRecycle(FsUri uri);

    Task RecycleAsync(IReadOnlyList<FsUri> paths, CancellationToken ct);

    /// <summary>
    /// Which of <paramref name="entries"/> the bin would not take though their disk has one, each
    /// with why: the bin turned off there, or the item larger than the whole bin. Windows destroys
    /// those instead, after a modal box of its own; asked here first, the question is ours and
    /// inline. Empty when it can't tell, which leaves Windows' box as the backstop.
    /// </summary>
    Task<IReadOnlyList<WontFit>> WontFitAsync(IReadOnlyList<FsEntry> entries, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WontFit>>([]);

    /// <summary>
    /// The most the bin <paramref name="uri"/> would go to takes in one item, in bytes: 0 when it
    /// is turned off, null when that can't be told.
    /// </summary>
    long? CapacityOf(FsUri uri) => null;

    /// <summary>
    /// Puts back what <see cref="RecycleAsync"/> took, the copies of these paths deleted at or
    /// after <paramref name="deletedSince"/>: the undo of a delete (§1.2/6). Fails, in words, when
    /// something has been put back already, the bin was emptied, or the name is taken again.
    /// </summary>
    Task RestoreAsync(IReadOnlyList<FsUri> paths, DateTimeOffset deletedSince, CancellationToken ct) =>
        throw new NotSupportedException("This Recycle Bin cannot put things back.");

    /// <summary>
    /// As above, but only what was deleted by <paramref name="deletedBefore"/> as well: the end of
    /// the operation that recycled it. "The newest since" alone brought back a later item of the
    /// same path instead, one that went to the bin after (delete P, copy something to P, undo
    /// both: the copy came back, AUDIT.md §2 kind 21).
    /// </summary>
    Task RestoreAsync(IReadOnlyList<FsUri> paths, DateTimeOffset deletedSince, DateTimeOffset deletedBefore, CancellationToken ct) =>
        RestoreAsync(paths, deletedSince, ct);
}

/// <summary>What a long operation tells whoever shows it: what it is about to take, and how far it is.</summary>
public interface IOperationProgress
{
    /// <summary>These are about to go: after every question, before any of them is touched.</summary>
    void Starting(IReadOnlyCollection<FsUri> items);

    /// <summary>How far, 0..1, or null for "can't tell"; and what it is on now, or null to leave it.</summary>
    void Report(double? fraction, string? detail);
}

/// <summary>An entry the Recycle Bin would not take, and why, in a sentence.</summary>
public sealed record WontFit(FsEntry Entry, string Why);

/// <summary>Undo met an operation that has no way back, such as a permanent delete.</summary>
public sealed class CannotUndoException(string message) : InvalidOperationException(message);

/// <summary>
/// On the undo stack for something that cannot be undone, so Ctrl+Z says so instead of reaching
/// past it and quietly undoing whatever came before (as it did for every delete, 2026-10-02).
/// </summary>
public sealed class Irreversible(string description, string shellEquivalent, string why) : IUndoableOperation
{
    public string Description { get; } = description;

    public string ShellEquivalent { get; } = shellEquivalent;

    public Task UndoAsync(CancellationToken ct) => throw new CannotUndoException(why);
}

public interface IFileOperationService
{
    Task<FsUri?> CreateFolderAsync(FsUri directory, string name, CancellationToken ct);

    /// <summary>
    /// Writes a new file with content that came from somewhere other than a file — the clipboard,
    /// today (§6.14). Never overwrites: <paramref name="name"/> is a proposal, and the name it
    /// actually used comes back.
    /// </summary>
    /// <param name="shellEquivalent">
    /// How the activity log should say this was done. The caller supplies it because only the
    /// caller knows where the bytes came from; the log's promise is that it can always tell you
    /// what happened to your files (§1.2/6), and "wrote 4 kB" would not keep it.
    /// </param>
    Task<FsUri?> CreateFileAsync(
        FsUri directory, string name, byte[] content, string shellEquivalent, CancellationToken ct);

    /// <param name="decisions">Where to ask about a file in use (§1.3); without one, that fails as it is.</param>
    Task<FsUri?> RenameAsync(FsEntry entry, string newName, CancellationToken ct, IDecisionHost? decisions = null);

    /// <param name="progress">Told when the entries are about to go, after every question, and how far it is.</param>
    Task DeleteAsync(
        IReadOnlyList<FsEntry> entries, bool permanent, IDecisionHost decisions, CancellationToken ct,
        IOperationProgress? progress = null);

    /// <summary>Creates a folder and moves the selection into it, as one undoable step (§6.7).</summary>
    /// <param name="decisions">Where to ask about an item in use (§1.3); without one, that fails as it is.</param>
    Task<FsUri?> GroupIntoFolderAsync(
        IReadOnlyList<FsEntry> entries, FsUri directory, string folderName, CancellationToken ct,
        IDecisionHost? decisions = null);

    /// <summary>The name to propose when grouping: the selection's common prefix, else the folder.</summary>
    string ProposeGroupName(IReadOnlyList<FsEntry> entries, FsUri directory);
}

public sealed class FileOperationService(
    IProviderRegistry providers,
    IUndoStack undo,
    IActivityLog activity,
    IRecycleBin recycleBin,
    IRelocationFeed? relocations = null,
    IArrivalFeed? arrivals = null,
    IUsageFinder? usage = null) : IFileOperationService
{
    /// <remarks>
    /// <paramref name="name"/> may be a path — "docs/2026/q3" — and every level that is missing is
    /// made, the way <c>mkdir -p</c> does. Levels that already exist are left alone and are not part
    /// of the undo: undoing a folder you created must never remove one that was there before, which
    /// it used to do when "creating" a folder that already existed and was empty.
    /// </remarks>
    public async Task<FsUri?> CreateFolderAsync(FsUri directory, string name, CancellationToken ct)
    {
        var segments = name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            return null;

        var provider = providers.Resolve(directory);
        // Each operation here holds Ctrl+Z while it runs, as a transfer does: on a slow server, or
        // waiting on an in-use question, it can take long enough for a Ctrl+Z meanwhile to undo
        // something beside it.
        using var working = undo.Working($"creating {name}");
        var created = new List<FsUri>();
        var target = directory;

        foreach (var segment in segments)
        {
            target = target.Child(segment);
            if (await provider.StatAsync(target, ct) is { IsDirectory: true })
                continue;

            await provider.CreateDirectoryAsync(target, ct);
            created.Add(target);
        }

        var shown = string.Join('/', segments);
        if (created.Count == 0)
            return target; // all of it was already there: nothing made, nothing to undo

        var commands = ShellText.Lines(created.Select(ShellText.MakeDirectory));
        activity.Record($"Created {shown}", commands);
        // The outermost folder made: "docs/2026" in a folder without docs shows docs arriving.
        arrivals?.Report([created[0]]);
        working.Push(new DelegateUndo(
            $"Created {shown}",
            commands,
            async token =>
            {
                // Deepest first, and only while empty: undo must not destroy anything put inside.
                for (var i = created.Count - 1; i >= 0; i--)
                {
                    // Still a folder, and empty: a provider's delete removes a file of that name too.
                    // One that isn't is said: "Undid" over a folder still full looked like the
                    // undo had done what it never does (2026-10-17).
                    var now = await provider.StatAsync(created[i], token);
                    if (now is null)
                        continue;
                    if (now is not { IsDirectory: true })
                        throw new CannotUndoException($"{created[i].Name} is not the folder that was made any more, so it stays.");
                    if (await HasChildrenAsync(provider, created[i], token))
                        throw new CannotUndoException($"{created[i].Name} has something in it now, so it stays.");

#pragma warning disable RS0030 // §1.6: a folder this operation made, still a folder and empty, not recursive.
                    await provider.DeleteAsync(created[i], recursive: false, token);
#pragma warning restore RS0030
                    activity.Record(
                        $"Undid: create {created[i].Name}", ShellText.RemoveDirectory(created[i]),
                        ActivityOutcome.Undone);
                }
            }));

        return target;
    }

    public async Task<FsUri?> CreateFileAsync(
        FsUri directory, string name, byte[] content, string shellEquivalent, CancellationToken ct)
    {
        var provider = providers.Resolve(directory);
        using var working = undo.Working($"creating {name}");

        // Stepped aside rather than overwritten. Everything else that writes a file in this
        // application asks first (the transfer service) or is replacing a file the user named (a
        // rename); a paste names itself, so it has no business landing on top of something.
        var target = await AvailableAsync(provider, directory.Child(name), ct);

        await using (var stream = await provider.OpenWriteAsync(target, content.LongLength, ct))
        {
            await stream.WriteAsync(content, ct);
            await stream.CommitAsync(replace: false, ct);
        }

        // What it was when made, so undo can tell whether it is still only what we wrote.
        var made = await provider.StatAsync(target, ct);

        activity.Record($"Created {target.Name}", shellEquivalent);
        arrivals?.Report([target]);
        working.Push(new DelegateUndo(
            $"Created {target.Name}",
            shellEquivalent,
            token => UndoCreateFileAsync(provider, target, made, token)));

        return target;
    }

    /// <summary>
    /// Takes away a file this application made, but only as it was made. Paste text as a file,
    /// write in it, press Ctrl+Z: deleting it then would delete the writing (§1.6). A file that
    /// has changed goes to the Recycle Bin instead, or, where there is none, stays.
    /// </summary>
    private async Task UndoCreateFileAsync(IFileSystemProvider provider, FsUri target, FsEntry? made, CancellationToken ct)
    {
        var now = await provider.StatAsync(target, ct);
        if (now is null)
            throw new CannotUndoException($"{target.Name} is not there any more.");

        // Marked read-only since is the user protecting it, though its bytes are the same: kept.
        if (made is not null && now.Size == made.Size && now.Modified == made.Modified
            && !(now.Attributes.HasFlag(FsEntryAttributes.ReadOnly) && !made.Attributes.HasFlag(FsEntryAttributes.ReadOnly)))
        {
            // To the bin where there is one: pasted clipboard content may exist nowhere else.
            if (recycleBin.CanRecycle(target))
            {
                await recycleBin.RecycleAsync([target], ct);
            }
            else
            {
#pragma warning disable RS0030 // §1.6: exactly the bytes this operation wrote, unchanged since, where no bin can take them.
                await provider.DeleteAsync(target, recursive: false, ct);
#pragma warning restore RS0030
            }

            activity.Record($"Undid: create {target.Name}", ShellText.Delete(target), ActivityOutcome.Undone);
            return;
        }

        if (!recycleBin.CanRecycle(target))
            throw new CannotUndoException($"{target.Name} has changed since it was made, so it stays.");

        await recycleBin.RecycleAsync([target], ct);
        activity.Record(
            $"Undid: create {target.Name}", ShellText.Delete(target), ActivityOutcome.Undone,
            "It had changed since it was made, so it went to the Recycle Bin.");
    }

    /// <summary>
    /// <paramref name="wanted"/> if nothing is there, else the same name with a number, the way
    /// the transfer service resolves "keep both".
    /// </summary>
    private static async Task<FsUri> AvailableAsync(IFileSystemProvider provider, FsUri wanted, CancellationToken ct)
    {
        if (await provider.StatAsync(wanted, ct) is null)
            return wanted;

        var dot = wanted.Name.LastIndexOf('.');
        var stem = dot > 0 ? wanted.Name[..dot] : wanted.Name;
        var extension = dot > 0 ? wanted.Name[dot..] : string.Empty;
        var parent = wanted.Parent ?? wanted;

        for (var n = 2; n < 1000; n++)
        {
            var candidate = parent.Child($"{stem} ({n}){extension}");
            if (await provider.StatAsync(candidate, ct) is null)
                return candidate;
        }

        return wanted;
    }

    public async Task<FsUri?> RenameAsync(FsEntry entry, string newName, CancellationToken ct, IDecisionHost? decisions = null)
    {
        var parent = entry.Uri.Parent ?? throw new InvalidOperationException("Nothing to rename.");
        var target = parent.Child(newName);
        var provider = providers.Resolve(entry.Uri);
        // An in-use question can stand for minutes.
        using var working = undo.Working($"renaming {entry.Name}");

        if (decisions is null)
            await provider.MoveAsync(entry.Uri, target, ct);
        else if (!await WhileInUseAsync([entry], () => provider.MoveAsync(entry.Uri, target, ct), decisions, ct))
            return null;
        relocations?.Report([new Relocation(entry.Uri, target)]);

        var command = ShellText.Move(entry.Uri, target);
        activity.Record($"Renamed {entry.Name} to {newName}", command);
        working.Push(new DelegateUndo(
            $"Renamed {entry.Name} to {newName}",
            command,
            async token =>
            {
                await provider.MoveAsync(target, entry.Uri, token);
                relocations?.Report([new Relocation(target, entry.Uri)]);
                activity.Record($"Undid: rename to {newName}", ShellText.Move(target, entry.Uri), ActivityOutcome.Undone);
            }));

        return target;
    }

    public async Task DeleteAsync(
        IReadOnlyList<FsEntry> entries, bool permanent, IDecisionHost decisions, CancellationToken ct,
        IOperationProgress? progress = null)
    {
        if (entries.Count == 0)
            return;

        var recyclable = !permanent && entries.All(e => recycleBin.CanRecycle(e.Uri));
        var what = entries.Count == 1 ? entries[0].Name : $"{entries.Count:N0} items";

        // From the first question to the record: Ctrl+Z meanwhile says it is still going rather
        // than undo what came before it.
        using var working = undo.Working(recyclable ? $"sending {what} to the Recycle Bin" : $"deleting {what}");

        if (!recyclable)
        {
            // Permanent deletion has no way back, so it is the one thing that always asks first.
            if (!await ConfirmPermanentAsync(what, "This cannot be undone.", decisions, ct))
                return;
            progress?.Starting(entries.Select(e => e.Uri).ToList());
            await DeletePermanentlyAsync(entries, decisions, working, ct, progress);
            return;
        }

        // What the bin wouldn't take is destroyed by Windows instead, after a modal box of its
        // own (2026-10-17): asked here, inline, before anything goes. Each of those is deleted
        // outright once the user says so, and the rest still goes to the bin.
        var wontFit = await recycleBin.WontFitAsync(entries, ct);
        if (wontFit.Count == 0)
        {
            progress?.Starting(entries.Select(e => e.Uri).ToList());
            await RecycleEntriesAsync(entries, decisions, working, ct, progress);
            return;
        }

        var outright = wontFit.Select(w => w.Entry.Uri).ToHashSet();
        var toBin = entries.Where(e => !outright.Contains(e.Uri)).ToList();
        var those = wontFit.Count == 1 ? wontFit[0].Entry.Name : $"{wontFit.Count:N0} items";
        // A bin turned off says the same of each item: once.
        var reasons = wontFit.Select(w => w.Why).Distinct().ToList();
        var why = string.Join(" ", reasons.Take(3)) + (reasons.Count > 3 ? $" And {reasons.Count - 3:N0} more like that." : "");
        var rest = toBin.Count;
        var after = rest switch
        {
            0 => "",
            1 => " The other item goes to the Recycle Bin.",
            _ => $" The other {rest:N0} go to the Recycle Bin.",
        };
        if (!await ConfirmPermanentAsync(those, $"{why} This cannot be undone.{after}", decisions, ct))
            return;
        progress?.Starting(entries.Select(e => e.Uri).ToList());

        // The ones with no way back first, so the last thing on the undo stack is the one Ctrl+Z
        // can undo; and a delete stopped there, in use and given up on, sends nothing else.
        if (!await DeletePermanentlyAsync(entries.Where(e => outright.Contains(e.Uri)).ToList(), decisions, working, ct, progress))
            return;
        if (rest > 0)
            await RecycleEntriesAsync(toBin, decisions, working, ct, progress);
    }

    private static async Task<bool> ConfirmPermanentAsync(string what, string detail, IDecisionHost decisions, CancellationToken ct) =>
        await decisions.AskAsync(
            new ChoiceRequest(
                $"Delete {what} permanently?",
                [
                    new DecisionOption("cancel", "Cancel", IsDefault: true, IsCancel: true),
                    new DecisionOption("delete", "Delete permanently"),
                ],
                detail,
                NoticeSeverity.Warning),
            ct) == "delete";

    private async Task RecycleEntriesAsync(
        IReadOnlyList<FsEntry> entries, IDecisionHost decisions, IUndoSlot working, CancellationToken ct,
        IOperationProgress? progress)
    {
        // One call for the lot: how far it is, the shell keeps to itself.
        progress?.Report(null, entries.Count == 1 ? entries[0].Name : $"{entries.Count:N0} items");
        var startedAt = DateTimeOffset.Now;
        var recycled = new List<FsUri>();
        var stoppedPartWay = false;

        // One call for the lot, because the shell is far quicker that way than item by item.
        // But a call stopped by a file in use may already have sent some, without saying which,
        // and the Retry sent those again, from where they no longer were. So after a stopped
        // try, what is gone from its place is taken as sent, and only the rest is tried again.
        async Task SendWhatIsLeft()
        {
            var left = entries.Select(e => e.Uri).Where(u => !recycled.Contains(u)).ToList();
            if (stoppedPartWay)
                left = await StillThereAsync(left, recycled);

            try
            {
                if (left.Count > 0)
                    await recycleBin.RecycleAsync(left, ct);
            }
            catch
            {
                stoppedPartWay = true;
                throw;
            }

            recycled.AddRange(left);
        }

        try
        {
            await WhileInUseAsync(entries, SendWhatIsLeft, decisions, ct);
        }
        finally
        {
            // Whatever stopped it, what is in the bin is logged and can come back: giving up on
            // a file in use, part way, used to record nothing, and Ctrl+Z undid something older.
            if (stoppedPartWay)
                await StillThereAsync(entries.Select(e => e.Uri).Where(u => !recycled.Contains(u)).ToList(), recycled);
            RecordRecycled(entries, recycled, startedAt, working);
        }
    }

    /// <summary>After the user has confirmed it. True when every one of them went.</summary>
    private async Task<bool> DeletePermanentlyAsync(
        IReadOnlyList<FsEntry> entries, IDecisionHost decisions, IUndoSlot working, CancellationToken ct,
        IOperationProgress? progress)
    {
        var what = entries.Count == 1 ? entries[0].Name : $"{entries.Count:N0} items";

        // One at a time, so a Retry after the third is in use does not try the first two again,
        // and the log says what went even when the user gives up halfway.
        var deleted = new List<FsEntry>();
        try
        {
            foreach (var entry in entries)
            {
                progress?.Report((double)deleted.Count / entries.Count, entry.Name);
                var provider = providers.Resolve(entry.Uri);
#pragma warning disable RS0030 // §1.6: the user's own Delete permanently, of what they selected, after they confirmed it above.
                if (!await WhileInUseAsync([entry], () => provider.DeleteAsync(entry.Uri, recursive: true, ct), decisions, ct))
                    break;
#pragma warning restore RS0030
                deleted.Add(entry);
            }
        }
        finally
        {
            if (deleted.Count > 0)
            {
                var done = deleted.Count == entries.Count
                    ? $"Deleted {what} permanently"
                    : $"Deleted {deleted.Count:N0} of {entries.Count:N0} items permanently";
                var shell = ShellText.Lines(deleted.Select(e => ShellText.DeletePermanently(e.Uri, e.IsDirectory)));
                activity.Record(done, shell);

                // There is no way back, and Ctrl+Z says so rather than reaching past it.
                working.Push(new Irreversible(done, shell, $"{done}: that cannot be undone."));
            }
        }

        return deleted.Count == entries.Count;
    }

    /// <summary>
    /// After a recycle that stopped part way: which of <paramref name="uris"/> are still in their
    /// place. The ones that are not went to the bin in that try, and are added to <paramref name="gone"/>.
    /// </summary>
    private async Task<List<FsUri>> StillThereAsync(List<FsUri> uris, List<FsUri> gone)
    {
        var there = new List<FsUri>();
        foreach (var uri in uris)
        {
            try
            {
                if (await providers.Resolve(uri).StatAsync(uri, CancellationToken.None) is null)
                    gone.Add(uri);
                else
                    there.Add(uri);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Can't tell: treated as still there, so it is tried again and never claimed as sent.
                there.Add(uri);
            }
        }

        return there;
    }

    private void RecordRecycled(IReadOnlyList<FsEntry> entries, List<FsUri> recycled, DateTimeOffset startedAt, IUndoSlot working)
    {
        if (recycled.Count == 0)
            return;

        // Everything this delete sent went by now: the undo restores what went between the two
        // times, not whatever of these names went to the bin last.
        var endedAt = DateTimeOffset.Now;
        var uris = recycled.ToList();
        var whole = uris.Count == entries.Count;
        var what = uris.Count == 1 ? uris[0].Name : $"{uris.Count:N0} items";
        var description = whole ? $"Sent {what} to the Recycle Bin" : $"Sent {uris.Count:N0} of {entries.Count:N0} items to the Recycle Bin";
        var shell = ShellText.Lines(uris.Select(ShellText.Delete));
        activity.Record(
            description,
            shell,
            whole ? ActivityOutcome.Succeeded : ActivityOutcome.Failed,
            whole ? "Ctrl+Z puts them back." : "Stopped part way; Ctrl+Z puts back the ones that went.");

        // Undoable, as every mutation is (§1.2/6). A delete never reached the stack, so Ctrl+Z
        // after one quietly undid whatever had come before it (2026-10-02).
        working.Push(new DelegateUndo(description, shell, async token =>
        {
            // One at a time, carrying on past one that can't come back (gone from the bin, or
            // never in it because something else removed it while a question waited): stopping
            // at the first left every one after it in the bin, with the undo already used up.
            var restored = new List<FsUri>();
            var missing = new List<string>();
            foreach (var uri in uris)
            {
                try
                {
                    await recycleBin.RestoreAsync([uri], startedAt, endedAt, token);
                    restored.Add(uri);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Its own reason: gone from the bin, or something else has the name now.
                    missing.Add($"{uri.Name} ({ex.Message.TrimEnd('.')})");
                }
            }

            arrivals?.Report(restored);
            var put = ShellText.Lines(restored.Select(u => $"restore {ShellText.Quote(u)}"));
            if (missing.Count == 0)
            {
                activity.Record($"Undid: {description}", put, ActivityOutcome.Undone);
                return;
            }

            var not = string.Join("; ", missing.Take(5)) + (missing.Count > 5 ? "; …" : "");
            activity.Record($"Undid part of: {description}", put, ActivityOutcome.Failed, $"Not put back: {not}.");
            throw new CannotUndoException($"Put back {restored.Count:N0}. Not put back: {not}.");
        }));
    }

    private async Task<bool> WhileInUseAsync(
        IReadOnlyList<FsEntry> entries, Func<Task> attempt, IDecisionHost decisions, CancellationToken ct) =>
        await InUse.RetryAsync(entries.Select(e => e.Uri).ToList(), attempt, decisions, usage, offerSkip: false, ct)
            == InUseOutcome.Done;

    public async Task<FsUri?> GroupIntoFolderAsync(
        IReadOnlyList<FsEntry> entries, FsUri directory, string folderName, CancellationToken ct,
        IDecisionHost? decisions = null)
    {
        if (entries.Count == 0)
            return null;

        var folder = directory.Child(folderName);
        var provider = providers.Resolve(folder);
        using var working = undo.Working($"grouping into {folderName}");

        // A new folder, never one that is there: creating a folder locally is a no-op when it
        // exists, so the items were merged into it, and undo then removed it if it ended empty,
        // though it was there before. Putting things into a folder that exists is Move's job.
        if (await provider.StatAsync(folder, ct) is not null)
            throw new IOException($"{folderName} is already here; choose another name, or move the items into it.");

        await provider.CreateDirectoryAsync(folder, ct);

        var moved = new List<(FsUri From, FsUri To)>();
        var commands = new List<string> { ShellText.MakeDirectory(folder) };

        try
        {
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var target = folder.Child(entry.Name);
                var source = providers.Resolve(entry.Uri);

                // An item in use asks Retry, naming the program, as Rename does: Windows' bare
                // "being used by another process" said neither which item nor who (2026-10-26).
                // Cancel stops here, with what already went recorded and undoable below.
                if (decisions is null)
                    await source.MoveAsync(entry.Uri, target, ct);
                else if (!await WhileInUseAsync([entry], () => source.MoveAsync(entry.Uri, target, ct), decisions, ct))
                    break;
                moved.Add((entry.Uri, target));
                commands.Add(ShellText.Move(entry.Uri, target));
            }
        }
        finally
        {
            // Stopped part way or not, what moved is logged and can be put back: failing after
            // three of five pushed nothing, so Ctrl+Z undid something older instead.
            RecordGroup(entries.Count, moved, commands, folder, folderName, provider, working);
        }

        return folder;
    }

    private void RecordGroup(
        int asked, List<(FsUri From, FsUri To)> moved, List<string> commands, FsUri folder, string folderName,
        IFileSystemProvider provider, IUndoSlot working)
    {
        var whole = moved.Count == asked;
        var description = whole
            ? $"Grouped {moved.Count:N0} item{(moved.Count == 1 ? "" : "s")} into {folderName}"
            : $"Grouped {moved.Count:N0} of {asked:N0} items into {folderName} (stopped)";
        activity.Record(description, ShellText.Lines(commands), whole ? ActivityOutcome.Succeeded : ActivityOutcome.Failed);

        // One undo entry for the whole thing: the folder and the moves go back together.
        working.Push(new DelegateUndo(description, ShellText.Lines(commands), async token =>
        {
            foreach (var (from, to) in moved)
                await providers.Resolve(to).MoveAsync(to, from, token);
            relocations?.Report(moved.Select(m => new Relocation(m.To, m.From)).ToList());
            arrivals?.Report(moved.Select(m => m.From).ToList());

            // Still a folder, and empty: a provider's delete removes a file of that name too.
            if (await provider.StatAsync(folder, token) is { IsDirectory: true } && !await HasChildrenAsync(provider, folder, token))
            {
#pragma warning disable RS0030 // §1.6: the folder this group made, still a folder and empty once its items are back.
                await provider.DeleteAsync(folder, recursive: false, token);
#pragma warning restore RS0030
            }

            activity.Record($"Undid: {description}",
                ShellText.Lines(moved.Select(m => ShellText.Move(m.To, m.From)).Append(ShellText.RemoveDirectory(folder))),
                ActivityOutcome.Undone);
        }));

        // Told only once the log and the undo are in place: a listener that threw before them
        // left the group unrecorded, with Ctrl+Z reaching something older.
        try
        {
            relocations?.Report(moved.Select(m => new Relocation(m.From, m.To)).ToList());
            arrivals?.Report([folder]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning($"A listener failed after grouping into {folderName}: {ex}");
        }
    }

    public string ProposeGroupName(IReadOnlyList<FsEntry> entries, FsUri directory)
    {
        if (entries.Count == 0)
            return "New folder";

        var names = entries.Select(e => Path.GetFileNameWithoutExtension(e.Name)).ToList();
        var prefix = CommonPrefix(names);

        // A prefix that stops part-way through a number is an artefact, not a name: "invoice-01"
        // and "invoice-02" share "invoice-0", and nobody wants a folder called that.
        if (prefix.Length > 0 && char.IsAsciiDigit(prefix[^1]) &&
            names.Any(n => n.Length > prefix.Length && char.IsAsciiDigit(n[prefix.Length])))
        {
            prefix = prefix.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        }

        prefix = prefix.Trim(' ', '-', '_', '.', '(', '[');

        if (prefix.Length >= 3)
            return prefix;

        return directory.IsRoot ? "New folder" : directory.Name;
    }

    private static string CommonPrefix(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            return string.Empty;

        var prefix = names[0];
        foreach (var name in names.Skip(1))
        {
            var length = 0;
            while (length < prefix.Length && length < name.Length &&
                   char.ToUpperInvariant(prefix[length]) == char.ToUpperInvariant(name[length]))
            {
                length++;
            }

            prefix = prefix[..length];
            if (prefix.Length == 0)
                break;
        }

        return prefix;
    }

    private static async Task<bool> HasChildrenAsync(IFileSystemProvider provider, FsUri directory, CancellationToken ct)
    {
        await foreach (var _ in provider.ListAsync(directory, ct).ConfigureAwait(false))
            return true;
        return false;
    }
}
