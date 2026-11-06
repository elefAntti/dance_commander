using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Operations;
using DanceCommander.Core.Security;
using DanceCommander.Core.Transfer;
using DanceCommander.Providers.Local;
using DanceCommander.Providers.Sftp;
using DanceCommander.Providers.WebDav;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit.Abstractions;

namespace DanceCommander.Tests;

// PLAN.md Version 1, Safety: the torture run. The fault sweep fails every step of every operation
// against fakes; this runs the operations against what the fakes stand for: another process
// holding a file, read-only files, paths past 260 characters, a volume that fills up, and real
// SFTP and WebDAV servers killed in the middle of a transfer. Slow, and it needs Docker, the
// wsgidav venv and (for the full volume) an elevated shell, so it runs before a release from
// scripts/torture.ps1 and is skipped everywhere else.
//
// Every case ends with the same two rules as the sweep's first two: each file that was there is
// still whole at a real name (or was one the user chose to replace or delete), and nothing at a
// real name is half of a file. What a case leaves under our temporary names is written to the
// test output rather than failed, since a .part after a killed server is expected; the report
// says where each one is.

/// <summary>Runs only in the torture run, and only when what it needs was set up.</summary>
public sealed class TortureFactAttribute : FactAttribute
{
    public TortureFactAttribute(params string[] needs)
    {
        if (Environment.GetEnvironmentVariable("DC_TORTURE") != "1")
            Skip = "The torture run: scripts/torture.ps1 sets it up and runs it.";
        else if (needs.FirstOrDefault(n => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(n))) is { } missing)
            Skip = $"{missing} is not set; scripts/torture.ps1 says why.";
    }
}

/// <summary>A folder of its own under %TEMP%, the services an operation needs, and the checks.</summary>
public sealed class TortureWorld : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<string> _cleanUp = [];

    public TortureWorld(ITestOutputHelper output, params IFileSystemProvider[] remotes)
    {
        _out = output;
        Root = Directory.CreateTempSubdirectory("dc-torture-").FullName;
        _cleanUp.Add(Root);
        Providers = new ProviderRegistry([new LocalFileSystemProvider(), .. remotes]);
        Answers = new TortureAnswers(output);
    }

    public string Root { get; }

    public IProviderRegistry Providers { get; }

    public UndoStack Undo { get; } = new();

    public ActivityLog Activity { get; } = new();

    /// <summary>The real one: what goes in comes back out by undo, as in the app.</summary>
    public IRecycleBin Bin { get; } = new WindowsRecycleBin();

    public TortureAnswers Answers { get; }

    public FileOperationService Operations => new(Providers, Undo, Activity, Bin);

    public void CleanUpToo(string folder) => _cleanUp.Add(folder);

    public string Local(string relative) => Path.Combine(Root, relative.Replace('/', '\\'));

    public static FsUri Uri(string path) => FsUri.Local(path);

    public TransferJob Start(IEnumerable<FsUri> sources, FsUri target, TransferMode mode) =>
        new TransferService(Providers, Undo, Activity, Bin, new JobService(), NullLogger<TransferService>.Instance)
            .Enqueue(sources.ToList(), target, mode, Answers);

    /// <summary>A transfer to its end; one that never gets there is a finding, and is cancelled.</summary>
    public async Task<TransferJob> TransferAsync(IEnumerable<FsUri> sources, FsUri target, TransferMode mode, TimeSpan? limit = null)
    {
        var job = Start(sources, target, mode);
        await FinishedAsync(job, limit ?? TimeSpan.FromMinutes(2));
        return job;
    }

    public async Task FinishedAsync(TransferJob job, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (!job.IsFinished && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        if (!job.IsFinished)
        {
            job.Cancel();
            for (var i = 0; i < 500 && !job.IsFinished; i++)
                await Task.Delay(20);
        }

        Say($"Transfer: {job.State}{(job.Error is { } e ? $" — {e}" : "")}");
        job.IsFinished.ShouldBeTrue($"the transfer was still going after {limit.TotalSeconds:N0} s, and a cancel didn't end it");
    }

    public async Task<FsEntry> EntryAsync(FsUri uri) =>
        await Providers.Resolve(uri).StatAsync(uri, CancellationToken.None)
        ?? throw new InvalidOperationException($"{uri} is not there to start with.");

    public async Task<List<FsEntry>> EntriesAsync(params FsUri[] uris)
    {
        var entries = new List<FsEntry>();
        foreach (var uri in uris)
            entries.Add(await EntryAsync(uri));
        return entries;
    }

    /// <summary>Undoes everything recorded, saying what each undo did or why it couldn't.</summary>
    public async Task UndoAllAsync()
    {
        while (Undo.CanUndo)
        {
            var what = Undo.NextDescription;
            try
            {
                await Undo.UndoAsync(CancellationToken.None);
                Say($"Undone: {what}");
            }
            catch (Exception ex) when (ex is CannotUndoException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Say($"Undo of \"{what}\" refused: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public void Say(string line) => _out.WriteLine(line);

    /// <summary>The log, once at the end of a case, for the report.</summary>
    public void SayLog()
    {
        foreach (var entry in Activity.Entries)
            Say($"  log: {entry.Outcome} {entry.Description}{(entry.Detail is { } d ? $" — {d}" : "")}");
    }

    public void Dispose()
    {
        foreach (var folder in _cleanUp)
        {
            try
            {
                // The read-only cases mark folders too, and a read-only folder can't be removed.
                foreach (var entry in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
                    File.SetAttributes(entry, File.GetAttributes(entry) & ~FileAttributes.ReadOnly);
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _out.WriteLine($"Left behind: {folder} ({ex.Message})");
            }
        }
    }

    // ---- the files and the rules ----------------------------------------------------------------

    /// <summary>Our own temporary names: debris with an obvious name is allowed, a half file at a real one isn't.</summary>
    public static bool IsOurTemporary(string path) =>
        path.EndsWith(".part", StringComparison.Ordinal)
        || path.Split('/', '\\').Any(s => s.Contains(".dc-incoming-", StringComparison.Ordinal)
                                          || s.Contains(".dc-old-", StringComparison.Ordinal)
                                          || s.Contains(".dc-upload-", StringComparison.Ordinal));

    /// <summary>Every file under the given folders, by "label/relative path", as a hash of its bytes.</summary>
    public static Dictionary<string, string> Snapshot(params (string Label, string Dir)[] roots)
    {
        var files = new Dictionary<string, string>();
        foreach (var (label, dir) in roots)
        {
            if (!Directory.Exists(dir))
                continue;
            foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                files[$"{label}/{Path.GetRelativePath(dir, path).Replace('\\', '/')}"] = Hash(path);
        }

        return files;
    }

    public static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Rules 1 and 2, and the leftovers said. <paramref name="mayGo"/> names what the user let go
    /// of: replaced, deleted for good, or (before an undo) in the Recycle Bin.
    /// </summary>
    public void Conserved(
        Dictionary<string, string> before, Dictionary<string, string> after, string when,
        Func<string, bool>? mayGo = null, IEnumerable<string>? alsoWhole = null)
    {
        var problems = new List<string>();
        var real = after.Where(f => !IsOurTemporary(f.Key)).ToList();
        var present = real.Select(f => f.Value).ToHashSet();
        foreach (var (path, hash) in before)
        {
            if (mayGo?.Invoke(path) == true || present.Contains(hash))
                continue;
            var temporary = after.FirstOrDefault(f => f.Value == hash && IsOurTemporary(f.Key)).Key;
            problems.Add(temporary is null
                ? $"LOST {path}: its bytes are nowhere."
                : $"{path} survives only under our temporary name {temporary}.");
        }

        var whole = before.Values.Concat(alsoWhole ?? []).ToHashSet();
        foreach (var (path, hash) in real)
        {
            if (!whole.Contains(hash))
                problems.Add($"{path} is at a real name and holds bytes that were never a whole file.");
        }

        foreach (var leftover in after.Keys.Where(IsOurTemporary))
            Say($"{when}: left under a temporary name: {leftover}");

        problems.ShouldBeEmpty($"{when}:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A file of random bytes, so no two are alike and a half of one is never another.</summary>
    public static void WriteRandom(string path, long length, int seed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var random = new Random(seed);
        var buffer = new byte[1024 * 1024];
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        for (long left = length; left > 0; left -= buffer.Length)
        {
            random.NextBytes(buffer);
            stream.Write(buffer, 0, (int)Math.Min(left, buffer.Length));
        }
    }

    /// <summary>Waits until a file under <paramref name="dir"/> has grown to at least so many bytes.</summary>
    public static async Task<string?> GrowingAsync(string dir, long atLeast, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (IsOurTemporary(file) && new FileInfo(file).Length >= atLeast)
                        return file;
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(10);
        }

        return null;
    }
}

/// <summary>Answers questions as each case says, and writes every one down for the report.</summary>
public sealed class TortureAnswers(ITestOutputHelper output) : IDecisionHost
{
    /// <summary>The answer to each question, by what it asks and offers. Null dismisses it.</summary>
    public Func<ChoiceRequest, string?> Choose { get; set; } = _ => null;

    public List<string> Asked { get; } = [];

    public Task<string?> AskAsync(ChoiceRequest request, CancellationToken ct)
    {
        var answer = Choose(request);
        var line = $"Asked: {request.Title} [{string.Join("/", request.Options.Select(o => o.Id))}] → {answer ?? "dismissed"}";
        lock (Asked)
            Asked.Add(line);
        output.WriteLine(line);
        return Task.FromResult(answer);
    }

    public Task<string?> AskTextAsync(TextRequest request, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<SecretAnswer?> AskSecretAsync(SecretRequest request, CancellationToken ct) => Task.FromResult<SecretAnswer?>(null);

    public void Report(Notice notice) => output.WriteLine($"Notice ({notice.Severity}): {notice.Message}{(notice.Detail is { } d ? $" — {d}" : "")}");

    /// <summary>The first of these the question offers.</summary>
    public static Func<ChoiceRequest, string?> First(params string[] ids) =>
        request => ids.FirstOrDefault(id => request.Options.Any(o => o.Id == id));
}

/// <summary>Another process with a file open and no sharing, or standing in a folder, as an editor or a shell does.</summary>
public sealed class OtherProcess : IDisposable
{
    private readonly Process _process;

    private OtherProcess(Process process) => _process = process;

    /// <summary>Opens <paramref name="file"/> for reading and writing, sharing nothing, until released.</summary>
    public static OtherProcess Open(string file)
    {
        var holder = Launch("powershell.exe", null, "-NoProfile", "-Command",
            "$s = [IO.File]::Open($env:DC_HOLD, 'Open', 'ReadWrite', 'None'); 'held'; [Console]::In.ReadLine() | Out-Null; $s.Close()",
            ("DC_HOLD", file));
        holder.WaitFor("held");
        return holder;
    }

    /// <summary>A shell whose current folder is <paramref name="folder"/>, which Windows won't let anyone move or remove.</summary>
    public static OtherProcess StandIn(string folder)
    {
        var holder = Launch("cmd.exe", folder, "/q", "/k", "echo held");
        holder.WaitFor("held");
        return holder;
    }

    private static OtherProcess Launch(string exe, string? workingDirectory, params object[] args)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var arg in args)
        {
            if (arg is ValueTuple<string, string> (var name, var value))
                info.Environment[name] = value;
            else
                info.ArgumentList.Add((string)arg);
        }

        return new OtherProcess(Process.Start(info)!);
    }

    private void WaitFor(string word)
    {
        var read = _process.StandardOutput.ReadLineAsync();
        if (!read.Wait(TimeSpan.FromSeconds(30)) || read.Result?.Trim() != word)
            throw new InvalidOperationException($"The holding process never said \"{word}\" (said \"{(read.IsCompleted ? read.Result : "nothing")}\").");
    }

    public void Release()
    {
        if (_process.HasExited)
            return;
        try
        {
            _process.StandardInput.WriteLine("exit");
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        if (!_process.WaitForExit(10_000))
            _process.Kill(entireProcessTree: true);
        _process.WaitForExit();
    }

    public void Dispose()
    {
        Release();
        _process.Dispose();
    }
}

[Trait("Category", "Torture")]
public sealed class LocalTortureTests(ITestOutputHelper output) : IDisposable
{
    private readonly TortureWorld _w = new(output);

    public void Dispose() => _w.Dispose();

    private Dictionary<string, string> Files() => TortureWorld.Snapshot(("t", _w.Root));

    private FsUri At(string relative) => TortureWorld.Uri(_w.Local(relative));

    private static readonly Func<ChoiceRequest, string?> NoAnswer = _ => null;

    // ---- a file another process has open ---------------------------------------------------------

    [TortureFact]
    public async Task CopyingAFileAnotherProcessHolds_RetriedOnceItLetsGo()
    {
        TortureWorld.WriteRandom(_w.Local("src/held.bin"), 3_000_000, 1);
        Directory.CreateDirectory(_w.Local("dst"));
        var before = Files();

        using var holder = OtherProcess.Open(_w.Local("src/held.bin"));
        _w.Answers.Choose = request =>
        {
            if (request.Options.Any(o => o.Id == "retry"))
            {
                holder.Release();
                return "retry";
            }

            return null;
        };
        var job = await _w.TransferAsync([At("src/held.bin")], At("dst"), TransferMode.Copy);

        _w.SayLog();
        _w.Answers.Asked.ShouldNotBeEmpty("a file another process holds was copied without anyone being asked");
        job.State.ShouldBe(JobState.Completed);
        TortureWorld.Hash(_w.Local("dst/held.bin")).ShouldBe(before["t/src/held.bin"]);
        _w.Conserved(before, Files(), "after the copy");
    }

    [TortureFact]
    public async Task MovingATreeWithAFileAnotherProcessHolds_Skipped()
    {
        Tree("src/tree");
        TortureWorld.WriteRandom(_w.Local("src/tree/inner/held.bin"), 2_000_000, 2);
        Directory.CreateDirectory(_w.Local("dst"));
        var before = Files();

        using (OtherProcess.Open(_w.Local("src/tree/inner/held.bin")))
        {
            _w.Answers.Choose = TortureAnswers.First("skip", "cancel");
            await _w.TransferAsync([At("src/tree")], At("dst"), TransferMode.Move);
            _w.SayLog();
            File.Exists(_w.Local("src/tree/inner/held.bin")).ShouldBeTrue("the held file left its folder while it was held");
        }

        // Read once it's let go: nobody can read a file held with no sharing, the rules included.
        _w.Conserved(before, Files(), "after the move");
        await _w.UndoAllAsync();
        _w.Conserved(before, Files(), "after undo");
        Files().Keys.Where(k => !TortureWorld.IsOurTemporary(k)).Order().ShouldBe(before.Keys.Order(), "undo didn't put the tree back as it was");
    }

    [TortureFact]
    public async Task DeletingForGoodATreeWithAFileAnotherProcessHolds_Skipped()
    {
        Tree("src/tree");
        TortureWorld.WriteRandom(_w.Local("src/tree/inner/held.bin"), 1_000_000, 3);
        TortureWorld.Write(_w.Local("src/tree.txt"), "named like the folder, not chosen");
        var before = Files();

        using (OtherProcess.Open(_w.Local("src/tree/inner/held.bin")))
        {
            _w.Answers.Choose = TortureAnswers.First("delete", "skip", "cancel");
            await Swallow(async () => await _w.Operations.DeleteAsync(await _w.EntriesAsync(At("src/tree")), permanent: true, _w.Answers, CancellationToken.None));
        }

        _w.SayLog();
        var after = Files();
        after.ShouldContainKey("t/src/tree/inner/held.bin");
        after.ShouldContainKey("t/src/tree.txt");
        _w.Conserved(before, after, "after the delete", mayGo: p => p.StartsWith("t/src/tree/", StringComparison.Ordinal) && !p.EndsWith("held.bin", StringComparison.Ordinal));
    }

    [TortureFact]
    public async Task RecyclingAFolderAShellStandsIn_RefusedThenDoneThenUndone()
    {
        Tree("src/tree");
        var before = Files();

        using (OtherProcess.StandIn(_w.Local("src/tree/inner")))
        {
            _w.Answers.Choose = TortureAnswers.First("cancel");
            await Swallow(async () => await _w.Operations.DeleteAsync(await _w.EntriesAsync(At("src/tree")), permanent: false, _w.Answers, CancellationToken.None));
            _w.SayLog();
            _w.Conserved(before, Files(), "after recycling a folder in use");

            // Said as "shell error 0x7C" until 2026-10-11, with nothing to do about it.
            _w.Answers.Asked.ShouldContain(q => q.Contains("[retry", StringComparison.Ordinal), "a folder another program stands in wasn't said to be in use");
        }

        // A Recycle Bin that took the part it could reach has the rest; undo has to bring both back.
        await _w.UndoAllAsync();
        _w.Conserved(before, Files(), "after undo of that");

        _w.Answers.Choose = NoAnswer;
        await _w.Operations.DeleteAsync(await _w.EntriesAsync(At("src/tree")), permanent: false, _w.Answers, CancellationToken.None);
        Directory.Exists(_w.Local("src/tree")).ShouldBeFalse("released, the folder still didn't go to the Recycle Bin");
        await _w.UndoAllAsync();
        _w.SayLog();
        _w.Conserved(before, Files(), "after recycling it released, and undo");
    }

    [TortureFact]
    public async Task CopyingOverAFileAnotherProcessHolds_TheOldOneStays()
    {
        TortureWorld.Write(_w.Local("src/a.txt"), "the new a");
        TortureWorld.Write(_w.Local("dst/a.txt"), "the old a, held open");
        var before = Files();

        TransferJob job;
        using (OtherProcess.Open(_w.Local("dst/a.txt")))
        {
            _w.Answers.Choose = TortureAnswers.First("overwrite", "cancel");
            job = await _w.TransferAsync([At("src/a.txt")], At("dst"), TransferMode.Copy);
        }

        _w.SayLog();
        job.State.ShouldNotBe(JobState.Completed);
        File.ReadAllText(_w.Local("dst/a.txt")).ShouldBe("the old a, held open", "cancelled, the held file was replaced anyway");
        _w.Conserved(before, Files(), "after the cancelled replace");
    }

    /// <summary>As an editor that has the old file open: the user closes it and says Retry.</summary>
    [TortureFact]
    public async Task CopyingOverAFileAnotherProcessHolds_IsAskedAboutAndRetried()
    {
        TortureWorld.Write(_w.Local("src/a.txt"), "the new a");
        TortureWorld.Write(_w.Local("dst/a.txt"), "the old a, held open");
        var before = Files();

        TransferJob job;
        using (var holder = OtherProcess.Open(_w.Local("dst/a.txt")))
        {
            _w.Answers.Choose = request =>
            {
                if (request.Options.Any(o => o.Id == "retry"))
                {
                    holder.Release();
                    return "retry";
                }

                return TortureAnswers.First("overwrite")(request);
            };
            job = await _w.TransferAsync([At("src/a.txt")], At("dst"), TransferMode.Copy);
        }

        _w.SayLog();
        var now = File.ReadAllText(_w.Local("dst/a.txt"));
        _w.Conserved(before, Files(), "after the replace", mayGo: p => p == "t/dst/a.txt" && now == "the new a");
        _w.Answers.Asked.ShouldContain(q => q.Contains("[retry", StringComparison.Ordinal), "replacing a file another process has open never asked whether to retry");
        job.State.ShouldBe(JobState.Completed);
        now.ShouldBe("the new a");
    }

    // ---- read-only -------------------------------------------------------------------------------

    [TortureFact]
    public async Task ACopyOfAReadOnlyFile_IsUndone()
    {
        TortureWorld.Write(_w.Local("src/ro.txt"), "read-only");
        File.SetAttributes(_w.Local("src/ro.txt"), FileAttributes.ReadOnly);
        Directory.CreateDirectory(_w.Local("dst"));
        var before = Files();

        var job = await _w.TransferAsync([At("src/ro.txt")], At("dst"), TransferMode.Copy);
        job.State.ShouldBe(JobState.Completed);
        _w.Say($"The copy's attributes: {File.GetAttributes(_w.Local("dst/ro.txt"))}");

        await _w.UndoAllAsync();
        _w.SayLog();
        File.Exists(_w.Local("dst/ro.txt")).ShouldBeFalse("undo left the read-only copy in place");
        File.GetAttributes(_w.Local("src/ro.txt")).HasFlag(FileAttributes.ReadOnly).ShouldBeTrue();
        _w.Conserved(before, Files(), "after undo");
    }

    [TortureFact]
    public async Task CopyingOverAReadOnlyFile_KeepsItAndSaysWhy()
    {
        TortureWorld.Write(_w.Local("src/a.txt"), "the new a");
        TortureWorld.Write(_w.Local("dst/a.txt"), "the old a, read-only");
        File.SetAttributes(_w.Local("dst/a.txt"), FileAttributes.ReadOnly);
        var before = Files();

        _w.Answers.Choose = TortureAnswers.First("overwrite", "cancel");
        var job = await _w.TransferAsync([At("src/a.txt")], At("dst"), TransferMode.Copy);
        _w.SayLog();
        var now = File.ReadAllText(_w.Local("dst/a.txt"));
        _w.Say($"dst/a.txt now holds \"{now}\"");
        _w.Conserved(before, Files(), "after the replace", mayGo: p => p == "t/dst/a.txt" && now == "the new a");

        // Kept, and said why: a Replace here can't be undone, so the mark isn't cleared behind
        // the user's back (review, 2026-10-11).
        now.ShouldBe("the old a, read-only");
        job.Error.ShouldNotBeNull().ShouldContain("read-only");
    }

    [TortureFact]
    public async Task MovingAReadOnlyTree_AndBack()
    {
        Tree("src/tree");
        foreach (var file in Directory.GetFiles(_w.Local("src/tree"), "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.ReadOnly);
        File.SetAttributes(_w.Local("src/tree/inner"), FileAttributes.ReadOnly | FileAttributes.Directory);
        Directory.CreateDirectory(_w.Local("dst"));
        var before = Files();

        var job = await _w.TransferAsync([At("src/tree")], At("dst"), TransferMode.Move);
        job.State.ShouldBe(JobState.Completed);
        _w.Conserved(before, Files(), "after the move");

        await _w.UndoAllAsync();
        _w.SayLog();
        Files().Keys.Order().ShouldBe(before.Keys.Order());
        _w.Conserved(before, Files(), "after undo");
    }

    [TortureFact]
    public async Task DeletingAReadOnlyFile_ToTheBinAndForGood()
    {
        TortureWorld.Write(_w.Local("src/ro.txt"), "read-only");
        TortureWorld.Write(_w.Local("src/ro2.txt"), "read-only too");
        TortureWorld.Write(_w.Local("src/ro.txt.bak"), "named like it, not chosen");
        File.SetAttributes(_w.Local("src/ro.txt"), FileAttributes.ReadOnly);
        File.SetAttributes(_w.Local("src/ro2.txt"), FileAttributes.ReadOnly);
        var before = Files();

        await _w.Operations.DeleteAsync(await _w.EntriesAsync(At("src/ro.txt")), permanent: false, _w.Answers, CancellationToken.None);
        File.Exists(_w.Local("src/ro.txt")).ShouldBeFalse("a read-only file didn't go to the Recycle Bin");
        await _w.UndoAllAsync();
        _w.Conserved(before, Files(), "after recycling and undo");
        File.Exists(_w.Local("src/ro.txt")).ShouldBeTrue("undo didn't bring the read-only file back from the Recycle Bin");

        _w.Answers.Choose = TortureAnswers.First("delete");
        await Swallow(async () => await _w.Operations.DeleteAsync(await _w.EntriesAsync(At("src/ro2.txt")), permanent: true, _w.Answers, CancellationToken.None));
        _w.SayLog();
        _w.Say($"Deleted for good: {(File.Exists(_w.Local("src/ro2.txt")) ? "no, still there" : "yes")}");
        _w.Conserved(before, Files(), "after the delete for good", mayGo: p => p == "t/src/ro2.txt");
    }

    // ---- long paths and long names ---------------------------------------------------------------

    /// <summary>A folder whose files are past 300 characters from the drive letter.</summary>
    private string Deep()
    {
        var deep = "src/" + string.Join("/", Enumerable.Range(1, 6).Select(i => $"folder-{i}-" + new string((char)('a' + i), 50)));
        Tree(deep + "/tree");
        _w.Local(deep + "/tree/inner/deeper/four.txt").Length.ShouldBeGreaterThan(300);
        Directory.CreateDirectory(_w.Local("dst"));
        return deep;
    }

    [TortureFact]
    public async Task LongPaths_CopiedMovedAndUndone()
    {
        var deep = Deep();
        var before = Files();

        (await _w.TransferAsync([At(deep + "/tree")], At("dst"), TransferMode.Copy)).State.ShouldBe(JobState.Completed);
        (await _w.TransferAsync([At(deep + "/tree")], At(deep + "/.."), TransferMode.Move)).State.ShouldBe(JobState.Completed);
        _w.Conserved(before, Files(), "after copy and move", alsoWhole: before.Values);

        await _w.UndoAllAsync();
        _w.SayLog();
        Files().Keys.Order().ShouldBe(before.Keys.Order());
        _w.Conserved(before, Files(), "after undo");
    }

    [TortureFact]
    public async Task LongPaths_RenamedGroupedRecycledAndUndone()
    {
        var deep = Deep();
        var before = Files();

        await _w.Operations.RenameAsync(await _w.EntryAsync(At(deep + "/tree/one.txt")), "one, renamed.txt", CancellationToken.None, _w.Answers);
        await _w.Operations.GroupIntoFolderAsync(
            await _w.EntriesAsync(At(deep + "/tree/two.txt"), At(deep + "/tree/inner")), At(deep + "/tree"), "grouped", CancellationToken.None);
        await _w.Operations.DeleteAsync(await _w.EntriesAsync(At(deep + "/tree/grouped")), permanent: false, _w.Answers, CancellationToken.None);
        _w.SayLog();
        Directory.Exists(_w.Local(deep + "/tree/grouped")).ShouldBeFalse("the long-path folder didn't go to the Recycle Bin");

        await _w.UndoAllAsync();
        _w.SayLog();
        Files().Keys.Order().ShouldBe(before.Keys.Order());
        _w.Conserved(before, Files(), "after undo");
    }

    [TortureFact]
    public async Task LongPaths_DeletedForGood()
    {
        var deep = Deep();
        TortureWorld.Write(_w.Local(deep + "/tree/inner.txt"), "named like the folder, not chosen");
        var before = Files();

        _w.Answers.Choose = TortureAnswers.First("delete");
        await _w.Operations.DeleteAsync(await _w.EntriesAsync(At(deep + "/tree/inner")), permanent: true, _w.Answers, CancellationToken.None);
        _w.SayLog();
        Directory.Exists(_w.Local(deep + "/tree/inner")).ShouldBeFalse();
        var after = Files();
        _w.Conserved(before, after, "after the delete", mayGo: p => p.Contains("/tree/inner/", StringComparison.Ordinal));
        after.Count.ShouldBe(before.Count - 2);
    }

    /// <summary>
    /// 255 characters is the most a name can have, and our temporary names add to it: a .part, a
    /// .dc-old- set-aside. A file with such a name has to copy and be replaced all the same.
    /// </summary>
    [TortureFact]
    public async Task TheLongestName_CopiedAndReplaced()
    {
        var name = new string('n', 251) + ".txt";
        TortureWorld.Write(_w.Local("src/" + name), "the longest name, new");
        TortureWorld.Write(_w.Local("dst/" + name), "the longest name, old");
        TortureWorld.Write(_w.Local("src2/" + name), "the longest name, copied to a fresh folder");
        Directory.CreateDirectory(_w.Local("dst2"));
        var before = Files();

        var fresh = await _w.TransferAsync([At("src2/" + name)], At("dst2"), TransferMode.Copy);
        _w.Answers.Choose = TortureAnswers.First("overwrite", "cancel");
        var over = await _w.TransferAsync([At("src/" + name)], At("dst"), TransferMode.Copy);
        _w.SayLog();
        _w.Conserved(before, Files(), "after the copies", mayGo: p => p == "t/dst/" + name && File.ReadAllText(_w.Local("dst/" + name)) == "the longest name, new");
        fresh.State.ShouldBe(JobState.Completed, "a file with a 255-character name couldn't be copied");
        over.State.ShouldBe(JobState.Completed, "a file with a 255-character name couldn't be replaced");
    }

    // ---- a full volume ---------------------------------------------------------------------------

    private string SmallVolume()
    {
        var folder = Path.Combine(Environment.GetEnvironmentVariable("DC_TORTURE_SMALL_VOLUME")!, $"dc-torture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        _w.CleanUpToo(folder);
        return folder;
    }

    /// <summary>Fills the volume until only so much is free, with a file the rules count like any other.</summary>
    private static void FillUntil(string folder, long free)
    {
        var available = new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace;
        if (available > free)
            TortureWorld.WriteRandom(Path.Combine(folder, "filler.bin"), available - free, 99);
    }

    private Dictionary<string, string> Files(string small) => TortureWorld.Snapshot(("t", _w.Root), ("v", small));

    [TortureFact("DC_TORTURE_SMALL_VOLUME")]
    public async Task ACopyTooBigForTheVolume_LeavesNothingHalfAtItsName()
    {
        var small = SmallVolume();
        FillUntil(small, 8 * 1024 * 1024);
        TortureWorld.WriteRandom(_w.Local("src/big.bin"), 16 * 1024 * 1024, 4);
        var before = Files(small);

        var job = await _w.TransferAsync([At("src/big.bin")], TortureWorld.Uri(small), TransferMode.Copy);
        _w.SayLog();
        job.State.ShouldNotBe(JobState.Completed);
        _w.Conserved(before, Files(small), "after the copy");
    }

    [TortureFact("DC_TORTURE_SMALL_VOLUME")]
    public async Task ReplacingAFileOnAFullVolume_KeepsTheOldOne()
    {
        var small = SmallVolume();
        TortureWorld.WriteRandom(Path.Combine(small, "a.bin"), 4 * 1024 * 1024, 5);
        FillUntil(small, 6 * 1024 * 1024);
        TortureWorld.WriteRandom(_w.Local("src/a.bin"), 12 * 1024 * 1024, 6);
        var before = Files(small);

        _w.Answers.Choose = TortureAnswers.First("overwrite", "cancel");
        var job = await _w.TransferAsync([At("src/a.bin")], TortureWorld.Uri(small), TransferMode.Copy);
        _w.SayLog();
        var after = Files(small);
        _w.Conserved(before, after, "after the replace");
        if (job.State != JobState.Completed)
            after["v/a.bin"].ShouldBe(before["v/a.bin"], "the replace failed and the old file isn't at its name");
    }

    [TortureFact("DC_TORTURE_SMALL_VOLUME")]
    public async Task MovingATreeOntoAFullVolume_KeepsEveryFile()
    {
        var small = SmallVolume();
        FillUntil(small, 10 * 1024 * 1024);
        for (var i = 1; i <= 6; i++)
            TortureWorld.WriteRandom(_w.Local($"src/tree/{(i > 3 ? "inner/" : "")}part{i}.bin"), 3 * 1024 * 1024, 10 + i);
        var before = Files(small);

        var job = await _w.TransferAsync([At("src/tree")], TortureWorld.Uri(small), TransferMode.Move);
        _w.SayLog();
        job.State.ShouldNotBe(JobState.Completed);
        _w.Conserved(before, Files(small), "after the move");

        await _w.UndoAllAsync();
        _w.SayLog();
        var after = Files(small);
        _w.Conserved(before, after, "after undo");
        after.Keys.Where(k => k.StartsWith("t/", StringComparison.Ordinal)).Order()
            .ShouldBe(before.Keys.Where(k => k.StartsWith("t/", StringComparison.Ordinal)).Order(), "undo didn't bring the tree back");
    }

    // ---- a Recycle Bin too small for it -----------------------------------------------------------

    /// <summary>
    /// Whether the rule Del asks by (an item larger than the whole bin is destroyed, from the
    /// bin's settings in the registry) is the one Windows goes by. Checked against the shell itself,
    /// with its own box turned off, on this throwaway volume: just under the bin's size goes to
    /// the bin, just over doesn't, and a folder counts as its total (2026-10-17).
    /// </summary>
    [TortureFact("DC_TORTURE_SMALL_VOLUME")]
    public async Task TooLargeForTheBin_IsAskedAboutFirst_AndThatIsWindowsOwnRule()
    {
        var small = SmallVolume();
        var bin = (WindowsRecycleBin)_w.Bin;

        // A bin used once has its settings written, which a volume made a moment ago may not.
        var first = Path.Combine(small, "first.txt");
        TortureWorld.Write(first, "first");
        var since = DateTimeOffset.Now;
        await bin.RecycleAsync([TortureWorld.Uri(first)], CancellationToken.None);
        await bin.RestoreAsync([TortureWorld.Uri(first)], since, CancellationToken.None);

        var size = WindowsRecycleBin.BinSizeOf(small).ShouldNotBeNull("no Recycle Bin settings for the test volume in the registry");
        _w.Say($"The test volume's Recycle Bin: {size:N0} bytes.");
        size.ShouldBeLessThan(40L * 1024 * 1024, "the test volume's bin is too large to go past on it");
        const long margin = 512 * 1024;

        var over = Path.Combine(small, "over.bin");
        var under = Path.Combine(small, "under.bin");
        var folder = Path.Combine(small, "folder");
        TortureWorld.WriteRandom(over, size + margin, 1);
        TortureWorld.WriteRandom(under, size - margin, 2);
        TortureWorld.WriteRandom(Path.Combine(folder, "a.bin"), size * 6 / 10, 3);
        TortureWorld.WriteRandom(Path.Combine(folder, "b.bin"), size * 6 / 10, 4);
        var entries = await _w.EntriesAsync(TortureWorld.Uri(over), TortureWorld.Uri(under), TortureWorld.Uri(folder));

        // What Del asks by: files only. A folder isn't walked (85,000 files took 12 s); the shell's
        // refusal below is what keeps one too large.
        (await bin.WontFitAsync(entries, CancellationToken.None)).Select(w => w.Entry.Name)
            .ShouldBe(["over.bin"]);

        // Asked inline, and Cancel sends nothing.
        _w.Answers.Choose = TortureAnswers.First("cancel");
        await _w.Operations.DeleteAsync(entries, permanent: false, _w.Answers, CancellationToken.None);
        _w.Answers.Asked.ShouldContain(q => q.Contains("Delete over.bin permanently?", StringComparison.Ordinal));
        File.Exists(over).ShouldBeTrue();
        File.Exists(under).ShouldBeTrue();
        Directory.Exists(folder).ShouldBeTrue();

        // What Windows goes by: each on its own, with no box to stop it.
        (await ShellRecycle.RecycleAsync([under], askBeforeDestroying: false)).NotRecycled.ShouldBeNull("just under the bin's size didn't go to the bin");
        await bin.RestoreAsync([TortureWorld.Uri(under)], since, CancellationToken.None);
        File.Exists(under).ShouldBeTrue("just under the bin's size went to the bin but didn't come back");

        var file = await ShellRecycle.RecycleAsync([over], askBeforeDestroying: false);
        _w.Say($"just over the bin's size: {file}");
        (file.Refused || file.NotRecycled is not null).ShouldBeTrue("just over the bin's size went to the bin: the rule Del asks by is not Windows' own");

        // A folder isn't asked about, so this is what keeps it: the shell, unable to take it whole,
        // deletes it file by file, and the first of those is refused with nothing gone.
        var whole = await ShellRecycle.RecycleAsync([folder], askBeforeDestroying: false);
        _w.Say($"a folder over the bin's size in total: {whole}");
        whole.Refused.ShouldBeTrue("a folder over the bin's size wasn't refused");
        TortureWorld.Snapshot(("f", folder)).Count.ShouldBe(2, "a folder over the bin's size lost files though refused");
    }

    // ---- a FAT32 stick ---------------------------------------------------------------------------

    private string FatVolume()
    {
        var folder = Path.Combine(Environment.GetEnvironmentVariable("DC_TORTURE_FAT_VOLUME")!, $"dc-torture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        _w.CleanUpToo(folder);
        return folder;
    }

    /// <summary>
    /// A file over FAT32's 4 GB, outside the snapshots (hashing it would take longer than the
    /// test): a length set rather than written, so it is made at once.
    /// </summary>
    private string Oversized(string relative)
    {
        var folder = Directory.CreateTempSubdirectory("dc-torture-big-").FullName;
        _w.CleanUpToo(folder);
        var path = Path.Combine(folder, relative.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = new FileStream(path, FileMode.CreateNew))
            file.SetLength(5L * 1024 * 1024 * 1024);
        return folder;
    }

    [TortureFact("DC_TORTURE_FAT_VOLUME")]
    public async Task AFileTooBigForFat32_IsAskedAboutBeforeAnythingIsWritten()
    {
        var fat = FatVolume();
        var sources = Oversized("film/cut.mkv");
        TortureWorld.Write(Path.Combine(sources, "film", "notes.txt"), "the notes, small");

        _w.Answers.Choose = TortureAnswers.First("skip");
        var job = await _w.TransferAsync([TortureWorld.Uri(Path.Combine(sources, "film"))], TortureWorld.Uri(fat), TransferMode.Copy);
        _w.SayLog();

        _w.Answers.Asked.ShouldContain(line => line.Contains("too big"), "the size wasn't asked about");
        job.State.ShouldBe(JobState.Completed);
        File.ReadAllText(Path.Combine(fat, "film", "notes.txt")).ShouldBe("the notes, small");
        Directory.GetFiles(fat, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ShouldBe(["notes.txt"]);
    }

    [TortureFact("DC_TORTURE_FAT_VOLUME")]
    public async Task ReplacingAFolderOnFat32_KeepsTheOldFileOfOneTooBig()
    {
        // The review's case: the stick's old cut is the only copy, the new one is too big and
        // skipped, and "Replace it entirely" used to discard the old folder with the old cut in it.
        var fat = FatVolume();
        TortureWorld.Write(Path.Combine(fat, "film", "cut.mkv"), "the old cut, the only copy");
        TortureWorld.Write(Path.Combine(fat, "film", "old-notes.txt"), "only on the stick");
        var sources = Oversized("film/cut.mkv");
        TortureWorld.Write(Path.Combine(sources, "film", "new-notes.txt"), "new notes");

        _w.Answers.Choose = TortureAnswers.First("replace", "skip");
        var job = await _w.TransferAsync([TortureWorld.Uri(Path.Combine(sources, "film"))], TortureWorld.Uri(fat), TransferMode.Copy);
        _w.SayLog();

        job.State.ShouldBe(JobState.Completed);
        File.ReadAllText(Path.Combine(fat, "film", "cut.mkv")).ShouldBe("the old cut, the only copy", "the old file of a skipped one went with the replaced folder");
        File.ReadAllText(Path.Combine(fat, "film", "new-notes.txt")).ShouldBe("new notes");
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private void Tree(string at)
    {
        TortureWorld.Write(_w.Local(at + "/one.txt"), "one " + at);
        TortureWorld.Write(_w.Local(at + "/two.txt"), "two, a little longer " + at);
        TortureWorld.Write(_w.Local(at + "/inner/three.txt"), "three, inside " + at);
        TortureWorld.Write(_w.Local(at + "/inner/deeper/four.txt"), "four, deeper still " + at);
    }

    /// <summary>Failing is allowed; losing a file isn't, and the rules after say which.</summary>
    private async Task Swallow(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            _w.Say($"Threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>A server the torture run can kill in the middle of something and start again.</summary>
public interface ITortureServer
{
    /// <summary>The folder on this machine that the server's root is.</summary>
    string Root { get; }

    IFileSystemProvider Provider { get; }

    FsUri At(string path);

    Task KillAsync();

    Task StartAgainAsync();
}

/// <summary>An SFTP server in a container, its upload folder bind-mounted from %TEMP%.</summary>
public sealed class SftpServer : ITortureServer, IAsyncLifetime
{
    private const string Container = "dc-torture-sftp";
    private const int Port = 2232;
    private readonly string? _image = Environment.GetEnvironmentVariable("DC_TORTURE_SFTP_IMAGE");
    private string _home = "";

    public string Root { get; private set; } = "";

    public IFileSystemProvider Provider { get; private set; } = null!;

    public FsUri At(string path) => FsUri.Remote("sftp", $"dance@127.0.0.1:{Port}", "/upload/" + path.TrimStart('/'));

    public async Task InitializeAsync()
    {
        if (_image is null || Environment.GetEnvironmentVariable("DC_TORTURE") != "1")
            return;

        Root = Directory.CreateTempSubdirectory("dc-torture-sftp-").FullName;
        _home = Directory.CreateTempSubdirectory("dc-torture-ssh-").FullName;
        await DockerAsync("rm", "-f", Container);
        (await DockerAsync("run", "-d", "--name", Container, "-p", $"127.0.0.1:{Port}:22",
            "-v", $"{Root}:/home/dance/upload", _image, "dance:commander:1001")).ShouldBe(0, "the SFTP container didn't start");
        await ReadyAsync();
        Provider = new SftpFileSystemProvider(new SftpConnectionPool(
            new KnownHostsStore(_home), new NullCredentialStore(), new Prompts(), NullLogger<SftpConnectionPool>.Instance));
    }

    public async Task DisposeAsync()
    {
        if (_image is null || Root == "")
            return;
        await DockerAsync("rm", "-f", Container);
        foreach (var folder in (string[])[Root, _home])
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    public async Task KillAsync() => await DockerAsync("kill", Container);

    public async Task StartAgainAsync()
    {
        await DockerAsync("start", Container);
        await ReadyAsync();
    }

    /// <summary>Until sshd says hello: Docker's port is open before anyone is listening behind it.</summary>
    private static async Task ReadyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", Port);
                var buffer = new byte[4];
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var read = await client.GetStream().ReadAsync(buffer, cts.Token);
                if (read == 4 && Encoding.ASCII.GetString(buffer) == "SSH-")
                    return;
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
            {
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("The SFTP container didn't answer within a minute.");
    }

    private static async Task<int> DockerAsync(params string[] args)
    {
        var info = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        await Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync(), process.WaitForExitAsync());
        return process.ExitCode;
    }

    private sealed class Prompts : ISftpPrompts
    {
        public Task<SftpPasswordAnswer?> AskPasswordAsync(SftpTarget target, bool retry, CancellationToken ct) =>
            Task.FromResult(retry ? null : new SftpPasswordAnswer("commander", Remember: false));

        public Task<bool> ConfirmUnknownHostAsync(SftpTarget target, HostKeyPresentation key, CancellationToken ct) => Task.FromResult(true);

        // The container keeps its keys across a restart: a changed one would be a finding.
        public Task<bool> ConfirmChangedHostAsync(SftpTarget target, HostKeyPresentation key, CancellationToken ct) => Task.FromResult(false);
    }
}

/// <summary>wsgidav from the venv scripts/webdav-test-server.ps1 makes, on a port of its own.</summary>
public sealed class DavServer : ITortureServer, IAsyncLifetime
{
    private const int Port = 8128;
    private readonly string? _exe = Environment.GetEnvironmentVariable("DC_TORTURE_WSGIDAV");
    private string _config = "";
    private Process? _process;

    public string Root { get; private set; } = "";

    public IFileSystemProvider Provider { get; private set; } = null!;

    public FsUri At(string path) => FsUri.Remote(DavTarget.Scheme, $"dance@127.0.0.1:{Port}", "/" + path.TrimStart('/'));

    public async Task InitializeAsync()
    {
        if (_exe is null || Environment.GetEnvironmentVariable("DC_TORTURE") != "1")
            return;

        Root = Directory.CreateTempSubdirectory("dc-torture-dav-").FullName;
        _config = Path.Combine(Directory.CreateTempSubdirectory("dc-torture-davconf-").FullName, "wsgidav.yaml");
        File.WriteAllText(_config, $"""
            host: 127.0.0.1
            port: {Port}
            provider_mapping:
              "/": "{Root.Replace('\\', '/')}"
            simple_dc:
              user_mapping:
                "*":
                  dance:
                    password: commander
            verbose: 0
            """);
        await StartAgainAsync();
        Provider = new WebDavFileSystemProvider(new DavConnections(new NullCredentialStore(), new Prompts()));
    }

    public Task DisposeAsync()
    {
        if (_exe is null || Root == "")
            return Task.CompletedTask;
        _ = KillAsync();
        foreach (var folder in (string[])[Root, Path.GetDirectoryName(_config)!])
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        return Task.CompletedTask;
    }

    public Task KillAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }

        return Task.CompletedTask;
    }

    public async Task StartAgainAsync()
    {
        var info = new ProcessStartInfo(_exe!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("--config");
        info.ArgumentList.Add(_config);
        _process = Process.Start(info)!;
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", Port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }

        throw new TimeoutException("wsgidav didn't start listening within 30 s.");
    }

    private sealed class Prompts : IDavPrompts
    {
        public Task<DavPasswordAnswer?> AskPasswordAsync(DavTarget target, bool retry, CancellationToken ct) =>
            Task.FromResult(retry ? null : new DavPasswordAnswer("dance", "commander", Remember: false));
    }
}

/// <summary>The same cases against each server: the round trip, then the server killed part way.</summary>
/// <remarks>Both servers are asked for by every case: the attribute is the base class's, shared by both.</remarks>
public abstract class ServerTortureTests(ITestOutputHelper output, ITortureServer server) : IDisposable
{
    private const long Big = 160L * 1024 * 1024;
    private const long KillAfter = 8L * 1024 * 1024;

    private readonly TortureWorld _w = new(output, server.Provider);
    private readonly string _case = Guid.NewGuid().ToString("N")[..8];

    public void Dispose() => _w.Dispose();

    /// <summary>This case's own folder on the server, so cases sharing a server don't meet.</summary>
    private string Remote(string relative = "") => $"{_case}/{relative}".TrimEnd('/');

    private string RemoteLocal(string relative = "") => Path.Combine(server.Root, Remote(relative).Replace('/', '\\'));

    private FsUri Local(string relative) => TortureWorld.Uri(_w.Local(relative));

    private Dictionary<string, string> Files() => TortureWorld.Snapshot(("t", _w.Root), ("s", RemoteLocal()));

    private void Tree(string at, Func<string, string> where)
    {
        TortureWorld.Write(where(at + "/one.txt"), "one " + at);
        TortureWorld.Write(where(at + "/a name with spaces, ääkköset.txt"), "two, not ASCII " + at);
        // Not longer: wsgidav keeps its files under %TEMP%, and Python on a Windows without long
        // paths turned on can't go past 260 characters. The local cases take the longest name.
        TortureWorld.Write(where(at + "/" + new string('l', 120) + ".txt"), "a long name " + at);
        TortureWorld.Write(where(at + "/inner/three.txt"), "three, inside " + at);
        TortureWorld.Write(where(at + "/inner/deeper/four.txt"), "four, deeper still " + at);
    }

    [TortureFact("DC_TORTURE_SFTP_IMAGE", "DC_TORTURE_WSGIDAV")]
    public async Task TheRoundTrip()
    {
        Tree("src/tree", _w.Local);
        Tree("remote/tree", RemoteLocal);
        TortureWorld.Write(_w.Local("src/a.txt"), "the new a");
        TortureWorld.Write(RemoteLocal("over/a.txt"), "the old a, on the server");
        Directory.CreateDirectory(_w.Local("dst"));
        Directory.CreateDirectory(RemoteLocal("up"));
        Directory.CreateDirectory(RemoteLocal("moved"));
        var before = Files();

        (await _w.TransferAsync([Local("src/tree")], server.At(Remote("up")), TransferMode.Copy)).State.ShouldBe(JobState.Completed, "upload");
        (await _w.TransferAsync([server.At(Remote("remote/tree"))], Local("dst"), TransferMode.Copy)).State.ShouldBe(JobState.Completed, "download");
        _w.Answers.Choose = TortureAnswers.First("overwrite");
        (await _w.TransferAsync([Local("src/a.txt")], server.At(Remote("over")), TransferMode.Copy)).State.ShouldBe(JobState.Completed, "replace");
        await _w.Operations.RenameAsync(await _w.EntryAsync(server.At(Remote("remote/tree/one.txt"))), "one, renamed.txt", CancellationToken.None, _w.Answers);
        (await _w.TransferAsync([server.At(Remote("remote/tree/inner"))], server.At(Remote("moved")), TransferMode.Move)).State.ShouldBe(JobState.Completed, "move on the server");
        _w.Answers.Choose = TortureAnswers.First("delete");
        await _w.Operations.DeleteAsync(await _w.EntriesAsync(server.At(Remote("up/tree/inner"))), permanent: true, _w.Answers, CancellationToken.None);
        _w.SayLog();

        var after = Files();
        after[$"s/over/a.txt"].ShouldBe(before["t/src/a.txt"]);
        _w.Conserved(before, after, "after the round trip", mayGo: p => p == "s/over/a.txt");

        await _w.UndoAllAsync();
        _w.SayLog();
        _w.Conserved(before, Files(), "after undo", mayGo: p => p == "s/over/a.txt");
    }

    [TortureFact("DC_TORTURE_SFTP_IMAGE", "DC_TORTURE_WSGIDAV")]
    public async Task ReplaceForTheRest_AsksAboutASaveAfterTheAnswerAndNothingElse()
    {
        // A server's listing and its look at one file must give the same size and time, or every
        // clash after "Replace for the rest" reads as changed and is asked about again.
        Tree("src/tree", _w.Local);
        Tree("up/tree", RemoteLocal);
        var before = Files();

        _w.Answers.Choose = TortureAnswers.First("overwrite");
        (await _w.TransferAsync([Local("src/tree")], server.At(Remote("up")), TransferMode.Copy)).State.ShouldBe(JobState.Completed, "merge");
        _w.Answers.Asked.Count.ShouldBe(1, "a merge onto the server asked more than the first clash");

        // Saved on the server while the question stands, a moment after the look.
        _w.Answers.Asked.Clear();
        var saved = false;
        _w.Answers.Choose = request =>
        {
            if (request.Title.Contains("after Replace was chosen", StringComparison.Ordinal))
                return "skip";
            if (!saved)
            {
                saved = true;
                Thread.Sleep(1100); // past WebDAV's one-second times
                TortureWorld.Write(RemoteLocal("up/tree/inner/three.txt"), "someone's newer three");
            }
            return "overwrite";
        };
        (await _w.TransferAsync([Local("src/tree")], server.At(Remote("up")), TransferMode.Copy)).State.ShouldBe(JobState.Completed, "merge again");
        _w.SayLog();
        File.ReadAllText(RemoteLocal("up/tree/inner/three.txt")).ShouldBe("someone's newer three");
        _w.Answers.Asked.Count.ShouldBe(2);
        TortureWorld.Hash(RemoteLocal("up/tree/one.txt")).ShouldBe(before["t/src/tree/one.txt"]);
    }

    [TortureFact("DC_TORTURE_SFTP_IMAGE", "DC_TORTURE_WSGIDAV")]
    public async Task KilledDuringAnUpload_TheSourceStaysAndNothingHalfHasAName()
    {
        TortureWorld.WriteRandom(_w.Local("src/big.bin"), Big, 21);
        Directory.CreateDirectory(RemoteLocal("up"));
        var before = Files();

        var job = await KilledPartWayAsync([Local("src/big.bin")], server.At(Remote("up")), TransferMode.Copy, RemoteLocal());
        job.State.ShouldNotBe(JobState.Completed);
        _w.Conserved(before, Files(), "after the kill");

        // A leftover from the killed upload mustn't stand in the way of the next one.
        var again = await _w.TransferAsync([Local("src/big.bin")], server.At(Remote("up")), TransferMode.Copy);
        _w.SayLog();
        again.State.ShouldBe(JobState.Completed, "the upload didn't go through again once the server was back");
        TortureWorld.Hash(RemoteLocal("up/big.bin")).ShouldBe(before["t/src/big.bin"]);
    }

    [TortureFact("DC_TORTURE_SFTP_IMAGE", "DC_TORTURE_WSGIDAV")]
    public async Task KilledDuringAnUploadOverAFile_TheOldOneStays()
    {
        TortureWorld.WriteRandom(_w.Local("src/big.bin"), Big, 22);
        TortureWorld.Write(RemoteLocal("up/big.bin"), "the old big.bin, on the server");
        var before = Files();

        _w.Answers.Choose = TortureAnswers.First("overwrite");
        var job = await KilledPartWayAsync([Local("src/big.bin")], server.At(Remote("up")), TransferMode.Copy, RemoteLocal());
        job.State.ShouldNotBe(JobState.Completed);
        var after = Files();
        _w.Conserved(before, after, "after the kill");
        after.GetValueOrDefault("s/up/big.bin").ShouldBe(before["s/up/big.bin"], "the old file isn't at its name after the upload over it was cut off");
    }

    [TortureFact("DC_TORTURE_SFTP_IMAGE", "DC_TORTURE_WSGIDAV")]
    public async Task KilledDuringAnUploadingMove_TheSourceStays()
    {
        TortureWorld.WriteRandom(_w.Local("src/tree/big.bin"), Big, 23);
        TortureWorld.Write(_w.Local("src/tree/small.txt"), "small, moved first or after");
        Directory.CreateDirectory(RemoteLocal("up"));
        var before = Files();

        var job = await KilledPartWayAsync([Local("src/tree")], server.At(Remote("up")), TransferMode.Move, RemoteLocal());
        job.State.ShouldNotBe(JobState.Completed);
        File.Exists(_w.Local("src/tree/big.bin")).ShouldBeTrue("the source of a move that was cut off is gone");
        _w.Conserved(before, Files(), "after the kill");

        await _w.UndoAllAsync();
        _w.SayLog();
        _w.Conserved(before, Files(), "after undo");
    }

    [TortureFact("DC_TORTURE_SFTP_IMAGE", "DC_TORTURE_WSGIDAV")]
    public async Task KilledDuringADownloadingMove_TheServersCopyStays()
    {
        TortureWorld.WriteRandom(RemoteLocal("down/big.bin"), Big, 24);
        Directory.CreateDirectory(_w.Local("dst"));
        var before = Files();

        var job = await KilledPartWayAsync([server.At(Remote("down/big.bin"))], Local("dst"), TransferMode.Move, _w.Local("dst"));
        job.State.ShouldNotBe(JobState.Completed);
        // In words, as an upload's is: it said "Client not connected." and "Unable to read data from
        // the transport connection" until 2026-10-11.
        job.Error.ShouldNotBeNull().ShouldMatch("was lost|stopped after about");
        File.Exists(RemoteLocal("down/big.bin")).ShouldBeTrue("the server's copy of a move that was cut off is gone");
        _w.Conserved(before, Files(), "after the kill");

        await _w.UndoAllAsync();
        _w.SayLog();
        _w.Conserved(before, Files(), "after undo");
    }

    /// <summary>
    /// Starts the transfer, kills the server once <see cref="KillAfter"/> bytes have gone, waits
    /// for the transfer to end, and starts the server again. By the job's count, not the size of
    /// the temporary file under <paramref name="watch"/>: a download's .part is made full size
    /// before a byte arrives, so its size said "part way" when it was already done.
    /// </summary>
    private async Task<TransferJob> KilledPartWayAsync(FsUri[] sources, FsUri target, TransferMode mode, string watch)
    {
        var job = _w.Start(sources, target, mode);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (job.BytesDone < KillAfter && !job.IsFinished && DateTime.UtcNow < deadline)
            await Task.Delay(5);
        var partWay = !job.IsFinished && job.BytesDone < job.BytesTotal;
        await server.KillAsync();
        var growing = await TortureWorld.GrowingAsync(watch, 0, TimeSpan.FromSeconds(1));
        _w.Say($"Killed with {job.BytesDone:N0} of {job.BytesTotal:N0} bytes sent{(growing is null ? "" : $", into {Path.GetFileName(growing)}")}");
        try
        {
            await _w.FinishedAsync(job, TimeSpan.FromMinutes(2));
        }
        finally
        {
            await server.StartAgainAsync();
        }

        _w.SayLog();
        partWay.ShouldBeTrue("the transfer finished or failed before the server could be killed part way");
        return job;
    }
}

[Trait("Category", "Torture")]
public sealed class SftpTortureTests(ITestOutputHelper output, SftpServer server)
    : ServerTortureTests(output, server), IClassFixture<SftpServer>
{
}

[Trait("Category", "Torture")]
public sealed class DavTortureTests(ITestOutputHelper output, DavServer server)
    : ServerTortureTests(output, server), IClassFixture<DavServer>
{
}
