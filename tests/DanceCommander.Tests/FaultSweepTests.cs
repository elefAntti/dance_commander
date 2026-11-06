using System.Security.Cryptography;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Operations;
using DanceCommander.Core.Transfer;
using DanceCommander.Core.Watching;
using DanceCommander.Providers.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// PLAN.md Version 1, Safety 3: every operation, and every undo, failed at each of its steps in turn.
/// </summary>
/// <remarks>
/// Each scenario runs once for every N, with the N-th call to the provider or the Recycle Bin
/// going wrong (a listing, a stat, a read, a write, a commit, a rename, a delete, a recycle),
/// until a run finishes without reaching N. How it goes wrong is the <see cref="Kind"/>: an error
/// before the call does anything; the call done and then reported as failed, as a server's
/// timeout can be; every call failing from there on, as a dropped connection does; the user
/// cancelling; and the file in use, answered Retry, Skip or Cancel, or Retry after the user has
/// edited a source file meanwhile. A listing can break at any entry, and a write at its flush.
/// After every run, four rules must hold:
///
/// 1. every file that existed before still exists, byte for byte, at a real name or in the
///    Recycle Bin (§1.6); a copy left only under our own temporary name does not count;
/// 2. nothing at a real name holds bytes that were not a whole file before (a half-written copy
///    would), only our own temporary names may;
/// 3. undoing everything that was recorded brings the tree back as it was, for the scenarios
///    that replace nothing; after a dropped connection the undo runs once it is back, and only
///    rules 1 and 2 are asked of it, since the operation could not see all it did;
/// 4. bytes are at their own name: a name that was there holds its own bytes (or, replaced as the
///    user chose, a same-named source's), a new name holds those of a file of that name, allowing
///    Keep both's " (2)" and a rename asked for. A permanent delete removes only what was chosen,
///    and leaves everything else exactly where it was.
///
/// A second test does the same to the undo: each operation runs cleanly, then its undo fails at
/// each step in turn, and rules 1 and 2 must hold after it.
///
/// Failures are collected rather than stopping at the first, so one run reports every step that
/// breaks a rule.
/// </remarks>
// About 40 s of the suite: "dotnet test --filter Category!=Sweep" leaves it out of a quick run.
[Trait("Category", "Sweep")]
public sealed class FaultSweepTests : IDisposable
{
    private const int MostCalls = 400;

    private readonly string _root = Directory.CreateTempSubdirectory("dc-sweep-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public enum Kind
    {
        /// <summary>An error before the call does anything.</summary>
        Before,

        /// <summary>The call takes effect, then reports an error, as a timed-out server call can.</summary>
        After,

        /// <summary>That call and every one after it fail, as a dropped connection does.</summary>
        Sticky,

        /// <summary>The user cancels at that moment.</summary>
        Cancel,

        /// <summary>The file is in use at that call, and the user answers Retry.</summary>
        InUse,

        /// <summary>In use, and the user answers Skip where it is offered (Cancel where not).</summary>
        InUseSkip,

        /// <summary>In use, and the user answers Cancel.</summary>
        InUseCancel,

        /// <summary>
        /// In use, and while the question waits the user edits a source file in the program that
        /// had it open, then answers Retry: what goes on from there is the edited file.
        /// </summary>
        InUseEdited,
    }

    /// <summary>The kinds that are "in use" at the failing call, each answered its own way.</summary>
    private static bool IsInUse(Kind kind) => kind is Kind.InUse or Kind.InUseSkip or Kind.InUseCancel or Kind.InUseEdited;

    /// <param name="Removes">
    /// What the user asked to have gone for good (a permanent delete): a path, or a folder and all
    /// in it. Only these may be missing afterwards.
    /// </param>
    /// <param name="StaysPut">Every file not in <paramref name="Removes"/> must still be at its own name.</param>
    /// <param name="Renames">A file's new name, where the operation gives it one (rename): file name to file name.</param>
    public sealed record Scenario(
        string Name,
        Action<string> Arrange,
        Func<Run, Task> Act,
        bool UndoRestores = true,
        bool MayReplace = false,
        string[]? Removes = null,
        bool StaysPut = false,
        IReadOnlyDictionary<string, string>? Renames = null)
    {
        public override string ToString() => Name;
    }

    public static TheoryData<Scenario> Scenarios() =>
    [
        new("copy a file", dir => Write(dir, "src/a.txt", "alpha"),
            run => run.Transfer(["src/a.txt"], "dst", TransferMode.Copy)),
        new("copy a tree", Tree,
            run => run.Transfer(["src/tree"], "dst", TransferMode.Copy)),
        new("copy a tree as bytes", Tree,
            run => run.Transfer(["src/tree"], "dst", TransferMode.Copy, asBytes: true)),
        new("move a file", dir => Write(dir, "src/a.txt", "alpha"),
            run => run.Transfer(["src/a.txt"], "dst", TransferMode.Move)),
        new("move a tree", Tree,
            run => run.Transfer(["src/tree"], "dst", TransferMode.Move)),
        new("move a tree where renames are refused", Tree,
            run => run.Transfer(["src/tree"], "dst", TransferMode.Move, renamesRefused: true)),
        new("copy over a file, replacing it", dir =>
            {
                Write(dir, "src/a.txt", "new alpha");
                Write(dir, "dst/a.txt", "old alpha");
            },
            run => run.Transfer(["src/a.txt"], "dst", TransferMode.Copy, answer: "overwrite"), UndoRestores: false, MayReplace: true),
        new("copy over a folder, replacing it", dir =>
            {
                Tree(dir);
                Write(dir, "dst/tree/only-here.txt", "only in the old folder");
            },
            run => run.Transfer(["src/tree"], "dst", TransferMode.Copy, answer: "replace"), UndoRestores: false),
        new("move over a folder, replacing it", dir =>
            {
                Tree(dir);
                Write(dir, "dst/tree/only-here.txt", "only in the old folder");
            },
            run => run.Transfer(["src/tree"], "dst", TransferMode.Move, answer: "replace")),
        new("rename", dir => Write(dir, "src/a.txt", "alpha"),
            run => run.Rename("src/a.txt", "b.txt"), Renames: new Dictionary<string, string> { ["a.txt"] = "b.txt" }),
        new("delete to the Recycle Bin", Tree,
            run => run.Recycle(["src/tree/one.txt", "src/tree/inner", "src/tree/two.txt"])),
        new("group into a folder", Tree,
            run => run.Group(["src/tree/one.txt", "src/tree/two.txt"], "src/tree", "grouped")),

        // Round two: the other answers to a name already taken.
        new("copy over a file, skipping it", OneOverAnother,
            run => run.Transfer(["src/a.txt"], "dst", TransferMode.Copy, answer: "skip")),
        new("copy over a file, keeping both", OneOverAnother,
            run => run.Transfer(["src/a.txt"], "dst", TransferMode.Copy, answer: "keepBoth")),
        new("move over a file, keeping both", OneOverAnother,
            run => run.Transfer(["src/a.txt"], "dst", TransferMode.Move, answer: "keepBoth")),
        new("copy over a folder, skipping it", TreeOverAnother,
            run => run.Transfer(["src/tree"], "dst", TransferMode.Copy, answer: "skip")),
        new("move into a folder, merging, keeping both of a name", TreeOverAnother,
            run => run.Transfer(["src/tree"], "dst", TransferMode.Move, answer: "merge,keepBoth")),

        // Round two: a permanent delete takes what was chosen and nothing beside it, including
        // names that begin the same way.
        new("delete permanently", dir =>
            {
                Tree(dir);
                Write(dir, "src/tree/inner.txt", "a file named like the folder");
                Write(dir, "src/tree/inner2/keep.txt", "a folder named like it");
                Write(dir, "src/tree/one.txt.bak", "one's backup");
            },
            run => run.DeletePermanently(["src/tree/one.txt", "src/tree/inner"]),
            UndoRestores: false, Removes: ["src/tree/one.txt", "src/tree/inner"], StaysPut: true),

        // Round two, last: a provider like a server (ServerLikeProvider). A copy there can't be undone
        // (no bin to send it to), which the app says rather than doing something else.
        new("upload a file", dir => Write(dir, "src/a.txt", "alpha"),
            run => run.Transfer(["src/a.txt"], "@a/dst", TransferMode.Copy), UndoRestores: false),
        new("upload a tree", Tree,
            run => run.Transfer(["src/tree"], "@a/dst", TransferMode.Copy), UndoRestores: false),
        new("upload over a file, replacing it", dir =>
            {
                Write(dir, "src/a.txt", "new alpha");
                Write(dir, "server/a/dst/a.txt", "old alpha");
            },
            run => run.Transfer(["src/a.txt"], "@a/dst", TransferMode.Copy, answer: "overwrite"), UndoRestores: false, MayReplace: true),
        new("download a tree, moving it", ServerTree,
            run => run.Transfer(["@a/src/tree"], "dst", TransferMode.Move)),
        new("move a tree between two servers", ServerTree,
            run => run.Transfer(["@a/src/tree"], "@b/dst", TransferMode.Move)),
        new("move over a file on a server, replacing it", dir =>
            {
                Write(dir, "server/a/src/a.txt", "new alpha");
                Write(dir, "server/a/dst/a.txt", "old alpha");
            },
            run => run.Transfer(["@a/src/a.txt"], "@a/dst", TransferMode.Move, answer: "overwrite"), UndoRestores: false, MayReplace: true),
        new("rename on a server", dir => Write(dir, "server/a/src/a.txt", "alpha"),
            run => run.Rename("@a/src/a.txt", "b.txt"), Renames: new Dictionary<string, string> { ["a.txt"] = "b.txt" }),
        new("delete on a server", dir =>
            {
                ServerTree(dir);
                Write(dir, "server/a/src/tree/inner.txt", "a file named like the folder");
            },
            run => run.DeletePermanently(["@a/src/tree/one.txt", "@a/src/tree/inner"]),
            UndoRestores: false, Removes: ["server/a/src/tree/one.txt", "server/a/src/tree/inner"], StaysPut: true),
    ];

    private static void ServerTree(string dir)
    {
        Write(dir, "server/a/src/tree/one.txt", "one");
        Write(dir, "server/a/src/tree/two.txt", "two, a little longer");
        Write(dir, "server/a/src/tree/inner/three.txt", "three, inside");
        Write(dir, "server/a/src/tree/inner/deeper/four.txt", "four, deeper still");
    }

    private static void OneOverAnother(string dir)
    {
        Write(dir, "src/a.txt", "new alpha");
        Write(dir, "dst/a.txt", "old alpha");
    }

    private static void TreeOverAnother(string dir)
    {
        Tree(dir);
        Write(dir, "dst/tree/one.txt", "the other one");
        Write(dir, "dst/tree/only-here.txt", "only in the other folder");
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task NoStepFailingLosesAFile(Scenario scenario)
    {
        var problems = new List<string>();
        foreach (var kind in Enum.GetValues<Kind>())
        {
            var finished = false;
            for (var n = 1; n < MostCalls && !finished; n++)
                finished = await RunOnceAsync(scenario, new Fault(kind, n), problems);
            if (!finished)
                problems.Add($"{scenario.Name}, {kind}: still reaching the failing call after {MostCalls} calls; raise MostCalls.");
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems.Distinct().Take(30)));
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task NoStepOfAnUndoFailingLosesAFile(Scenario scenario)
    {
        var problems = new List<string>();
        foreach (var kind in (Kind[])[Kind.Before, Kind.After, Kind.Sticky])
        {
            var finished = false;
            for (var n = 1; n < MostCalls && !finished; n++)
                finished = await UndoOnceAsync(scenario, new Fault(kind, n), problems);
            if (!finished)
                problems.Add($"{scenario.Name}, undo, {kind}: still reaching the failing call after {MostCalls} calls.");
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems.Distinct().Take(30)));
    }

    /// <returns>True once a run got through without reaching the failing call.</returns>
    private async Task<bool> RunOnceAsync(Scenario scenario, Fault fault, List<string> problems)
    {
        var (tree, binDir, before, run) = Arrange(scenario, fault, $"{fault.Kind}-{fault.FailAt}");

        fault.Arm();
        await ActAsync(scenario, run);
        var where = $"{scenario.Name}, {fault.Kind} at call {fault.FailAt}";
        fault.Off();

        // An edit the user made meanwhile is what they have now: the old bytes of that file are
        // theirs to lose, the new ones are not. A copy made before the edit still holds the old
        // ones, and that is a whole file too.
        var expected = before.ToDictionary(f => f.Key, f => run.Edits.FirstOrDefault(e => e.Old == f.Value).New ?? f.Value);

        Check(scenario, before, expected, Snapshot(tree), Snapshot(binDir), where, problems);

        if (scenario.UndoRestores)
        {
            await UndoAllAsync(run, where, problems);
            Check(scenario, before, expected, Snapshot(tree), Snapshot(binDir), $"{where}, after undo", problems);
            // Not after a step that took effect and then reported failure: the operation believes
            // it failed and records nothing for it, so undo can't know (AUDIT.md §2 kind 17; files
            // are safe, rules 1 and 2 hold). Nor after a dropped connection: see the remarks.
            if (fault.Kind is not (Kind.Sticky or Kind.After))
                CheckRestored(expected, tree, run, where, problems);
        }

        return !fault.Fired;
    }

    /// <returns>True once an undo got through without reaching the failing call.</returns>
    private async Task<bool> UndoOnceAsync(Scenario scenario, Fault fault, List<string> problems)
    {
        var (tree, binDir, before, run) = Arrange(scenario, fault, $"undo-{fault.Kind}-{fault.FailAt}");

        await ActAsync(scenario, run);
        var where = $"{scenario.Name}, its undo, {fault.Kind} at call {fault.FailAt}";

        // An undo answers for what the operation left, not for what the user let go of through it:
        // a file replaced where there is no bin was gone before the undo began.
        var left = Snapshot(tree).Values.Concat(Snapshot(binDir).Values).ToHashSet();
        var stillThere = before.Where(f => left.Contains(f.Value)).ToDictionary();

        fault.Arm();
        await UndoAllAsync(run, where, problems, failuresExpected: true);
        fault.Off();
        Check(scenario, before, stillThere, Snapshot(tree), Snapshot(binDir), where, problems);

        return !fault.Fired;
    }

    private (string Tree, string Bin, Dictionary<string, string> Before, Run Run) Arrange(Scenario scenario, Fault fault, string label)
    {
        var dir = Path.Combine(_root, $"{scenario.Name.Replace(' ', '-').Replace(',', '-')}-{label}");
        var tree = Path.Combine(dir, "tree");
        var binDir = Path.Combine(dir, "bin");
        Directory.CreateDirectory(tree);
        Directory.CreateDirectory(binDir);
        scenario.Arrange(tree);
        return (tree, binDir, Snapshot(tree), new Run(tree, fault, new SweepBin(fault, binDir)));
    }

    private static async Task ActAsync(Scenario scenario, Run run)
    {
        try
        {
            await scenario.Act(run);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or OperationCanceledException)
        {
            // Failing is allowed; losing a file is not.
        }
    }

    private static async Task UndoAllAsync(Run run, string where, List<string> problems, bool failuresExpected = false)
    {
        while (run.Undo.CanUndo)
        {
            try
            {
                await run.Undo.UndoAsync(CancellationToken.None);
            }
            catch (CannotUndoException)
            {
            }
            catch (Exception ex) when (failuresExpected && ex is IOException or OperationCanceledException)
            {
                // The injected failure: the undo is used up, and rules 1 and 2 are checked after.
            }
            catch (Exception ex)
            {
                problems.Add($"{where}: undo threw {ex.GetType().Name}: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>
    /// Rules 1 and 2: every file is somewhere real, and nothing at a real name is half of one.
    /// Rule 4: bytes are at their own name, not merely somewhere.
    /// </summary>
    /// <param name="before">The tree as arranged.</param>
    /// <param name="expected">The same, with any edit the user made meanwhile: what must survive.</param>
    private static void Check(
        Scenario scenario, Dictionary<string, string> before, Dictionary<string, string> expected,
        Dictionary<string, string> tree, Dictionary<string, string> bin, string where, List<string> problems)
    {
        // Every version a name has had is a whole file: before an edit and after it.
        var known = before.Concat(expected).ToList();
        var whole = known.Select(f => f.Value).ToHashSet();

        // Not a .part: content that survives only there is one bad commit from gone, and nobody
        // would think to look for it. Our .dc- staging and set-aside names are told to the user.
        var everywhere = tree.Where(f => !IsPart(f.Key)).Select(f => f.Value).Concat(bin.Values).ToHashSet();
        foreach (var (path, hash) in expected)
        {
            if (IsRemoved(scenario, path))
                continue;
            // Replaced, as the user chose: allowed only where a whole other file now has its name.
            if (scenario.MayReplace && tree.TryGetValue(path, out var now) && now != hash && whole.Contains(now))
                continue;
            if (!everywhere.Contains(hash))
                problems.Add($"{where}: LOST {path}: its content is nowhere real, in the tree or the bin.");
            else if (scenario.StaysPut && (!tree.TryGetValue(path, out var there) || there != hash))
                problems.Add($"{where}: {path} was not asked about, and is no longer at its own name as it was.");
        }

        foreach (var (path, hash) in tree)
        {
            if (IsOurTemporary(path))
                continue;
            if (!whole.Contains(hash))
            {
                problems.Add($"{where}: {path} holds bytes that were never a whole file (a half-written copy at a real name).");
                continue;
            }

            // Rule 4. Two files whose bytes were swapped, or a copy landing under its neighbour's
            // name, pass rules 1 and 2: every byte is somewhere. So a name that was there before
            // holds its own bytes, or (replaced, as chosen) a same-named source's; a new name holds
            // the bytes of a file of that name, give or take Keep both's " (2)" and a rename asked for.
            var fromItsOwnName = known.Any(b => b.Value == hash && SameName(scenario, b.Key, path));
            var itsOwn = known.Any(b => b.Key == path && b.Value == hash);
            if (before.ContainsKey(path) ? !itsOwn && !(scenario.MayReplace && fromItsOwnName) : !fromItsOwnName)
                problems.Add($"{where}: {path} holds the bytes of another file, not its own.");
        }
    }

    private static bool IsRemoved(Scenario scenario, string path) =>
        scenario.Removes?.Any(r => path == r || path.StartsWith(r + "/", StringComparison.Ordinal)) == true;

    /// <summary>Whether a file at <paramref name="to"/> may carry the bytes of the one at <paramref name="from"/> by its name.</summary>
    private static bool SameName(Scenario scenario, string from, string to)
    {
        var was = Path.GetFileName(from);
        var now = Path.GetFileName(to);
        if (scenario.Renames?.TryGetValue(was, out var renamed) == true && renamed == now)
            return true;

        // "a (2).txt" is Keep both's name for a second "a.txt".
        var stem = Path.GetFileNameWithoutExtension(now);
        var bare = System.Text.RegularExpressions.Regex.Replace(stem, @" \(\d+\)$", "") + Path.GetExtension(now);
        return string.Equals(was, now, StringComparison.OrdinalIgnoreCase) || string.Equals(was, bare, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rule 3: the tree as it was, extras allowed only where the log said they are in both places.</summary>
    private static void CheckRestored(Dictionary<string, string> before, string tree, Run run, string where, List<string> problems)
    {
        var after = Snapshot(tree).Where(f => !IsOurTemporary(f.Key)).ToDictionary();
        var missing = before.Where(f => !after.TryGetValue(f.Key, out var h) || h != f.Value).Select(f => f.Key).ToList();

        // A move whose copy landed and whose original then could not go leaves the file in both
        // places: allowed, but only when the log said which, so nobody is left to find out. So is
        // a copy that undo kept because its source changed since: it may hold the only old bytes.
        var reported = run.Activity.Entries
            .Where(e => e.Detail is { } d && (d.Contains("still there too", StringComparison.Ordinal) || d.StartsWith("Kept, because", StringComparison.Ordinal)))
            .Select(e => e.Detail!).ToList();
        var extra = after.Keys.Except(before.Keys)
            .Where(path => !reported.Any(r => r.Contains(Path.GetFileName(path), StringComparison.Ordinal)))
            .ToList();

        if (missing.Count > 0 || extra.Count > 0)
        {
            problems.Add($"{where}: after undo, not as it was. Missing or changed: [{string.Join(", ", missing)}]; " +
                         $"extra: [{string.Join(", ", extra)}]");
        }
    }

    /// <summary>A local .part: "name.part" or "name.N.part".</summary>
    private static bool IsPart(string path) => path.EndsWith(".part", StringComparison.Ordinal);

    /// <summary>
    /// Our own temporary names: a .part, and the .dc-incoming- staging and .dc-old- set-aside
    /// folders or files. Debris with an obvious name is allowed; a wrong file at a real name is not.
    /// </summary>
    private static bool IsOurTemporary(string path) =>
        IsPart(path)
        || path.Split('/').Any(segment => segment.Contains(".dc-incoming-", StringComparison.Ordinal)
                                          || segment.Contains(".dc-old-", StringComparison.Ordinal));

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .ToDictionary(
                    p => Path.GetRelativePath(dir, p).Replace('\\', '/'),
                    p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))))
            : [];

    private static void Write(string dir, string relative, string content)
    {
        var path = Path.Combine(dir, relative.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Tree(string dir)
    {
        Write(dir, "src/tree/one.txt", "one");
        Write(dir, "src/tree/two.txt", "two, a little longer");
        Write(dir, "src/tree/inner/three.txt", "three, inside");
        Write(dir, "src/tree/inner/deeper/four.txt", "four, deeper still");
        Directory.CreateDirectory(Path.Combine(dir, "dst"));
    }

    /// <summary>One run's services, over a provider and a bin that fail at the chosen call.</summary>
    public sealed class Run
    {
        private readonly string _tree;
        private readonly CountingProvider _provider;
        private readonly IProviderRegistry _providers;
        private readonly SweepBin _bin;
        private readonly Fault _fault;

        public Run(string tree, Fault fault, SweepBin bin)
        {
            _tree = tree;
            _bin = bin;
            _fault = fault;
            _provider = new CountingProvider(new LocalFileSystemProvider(), fault);
            _server = new ServerLikeProvider(Path.Combine(tree, "server"), fault);
            _providers = new ProviderRegistry([_provider, _server]);
            fault.WhileAsked = EditASource;
        }

        private readonly ServerLikeProvider _server;

        public UndoStack Undo { get; } = new();

        public ActivityLog Activity { get; } = new();

        /// <summary>
        /// Edits the user made while a question waited: the file's bytes before and after, by hash.
        /// By content rather than path, as the operation may already have taken the file elsewhere.
        /// </summary>
        public List<(string Old, string New)> Edits { get; } = [];

        /// <summary>A path in the tree, or with "@host/" one on the server-like provider.</summary>
        private FsUri At(string relative)
        {
            if (!relative.StartsWith('@'))
                return FsUri.Local(Path.Combine(_tree, relative.Replace('/', '\\')));
            var slash = relative.IndexOf('/');
            return FsUri.Remote("srv", relative[1..slash], relative[slash..]);
        }

        private string LocalPathOf(string relative) =>
            relative.StartsWith('@') ? _server.Local(At(relative)) : Path.Combine(_tree, relative.Replace('/', '\\'));

        /// <summary>
        /// The user, in the program that had the file open, saves a change to the first source file
        /// still there. Not if the operation itself has it open so that no one else may write: then
        /// the program couldn't have saved either.
        /// </summary>
        private void EditASource()
        {
            // A source on the server as well: "src/…" or "server/<host>/src/…".
            var file = Directory.GetFiles(_tree, "*", SearchOption.AllDirectories)
                .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                    Path.GetRelativePath(_tree, f).Replace('\\', '/'), "^(server/[^/]+/)?src/") && !IsOurTemporary(f.Replace('\\', '/')))
                .Order(StringComparer.Ordinal).FirstOrDefault();
            if (file is null)
                return;
            try
            {
                var old = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
                File.AppendAllText(file, " (edited while the question waited)");
                Edits.Add((old, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))));
            }
            catch (IOException)
            {
            }
        }

        private SweepDecisions Decisions(string? answer) => new(_fault.Kind, answer);

        public async Task Transfer(
            string[] sources, string target, TransferMode mode, string? answer = null, bool renamesRefused = false, bool asBytes = false)
        {
            _provider.RenamesRefused = renamesRefused;
            _provider.CopiesWithin = !asBytes;
            Directory.CreateDirectory(LocalPathOf(target));
            var job = new TransferService(_providers, Undo, Activity, _bin, new JobService(), NullLogger<TransferService>.Instance)
                .Enqueue(sources.Select(At).ToList(), At(target), mode, Decisions(answer));

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!job.IsFinished && DateTime.UtcNow < deadline)
                await Task.Delay(2);
            job.IsFinished.ShouldBeTrue("the job never finished");
        }

        private FileOperationService Operations() => new(_providers, Undo, Activity, _bin);

        private async Task<FsEntry> EntryAsync(string relative) =>
            (relative.StartsWith('@') ? _server.StatUncounted(At(relative)) : await new LocalFileSystemProvider().StatAsync(At(relative), CancellationToken.None))
            ?? throw new InvalidOperationException($"{relative} is not there to start with.");

        public async Task Rename(string from, string to) =>
            await Operations().RenameAsync(await EntryAsync(from), to, CancellationToken.None, Decisions(null));

        public async Task Recycle(string[] items)
        {
            var entries = new List<FsEntry>();
            foreach (var item in items)
                entries.Add(await EntryAsync(item));
            await Operations().DeleteAsync(entries, permanent: false, Decisions(null), CancellationToken.None);
        }

        public async Task DeletePermanently(string[] items)
        {
            var entries = new List<FsEntry>();
            foreach (var item in items)
                entries.Add(await EntryAsync(item));
            await Operations().DeleteAsync(entries, permanent: true, Decisions("delete"), CancellationToken.None);
        }

        public async Task Group(string[] items, string directory, string name)
        {
            var entries = new List<FsEntry>();
            foreach (var item in items)
                entries.Add(await EntryAsync(item));
            await Operations().GroupIntoFolderAsync(entries, At(directory), name, CancellationToken.None);
        }
    }

    /// <summary>
    /// Answers each question as the run means it: "in use" by the run's kind, anything else by the
    /// scenario's answers, the first of them the question offers (",": "merge,keepBoth" answers a
    /// folder Merge and a file in it Keep both).
    /// </summary>
    private sealed class SweepDecisions(Kind kind, string? answers) : IDecisionHost
    {
        public Task<string?> AskAsync(ChoiceRequest request, CancellationToken ct)
        {
            bool Offered(string id) => request.Options.Any(o => o.Id == id);

            if (Offered("retry"))
            {
                return Task.FromResult<string?>(kind switch
                {
                    Kind.InUseSkip when Offered("skip") => "skip",
                    Kind.InUseSkip or Kind.InUseCancel => "cancel",
                    _ => "retry",
                });
            }

            return Task.FromResult((answers ?? "").Split(',').FirstOrDefault(Offered));
        }

        public Task<SecretAnswer?> AskSecretAsync(SecretRequest request, CancellationToken ct) => Task.FromResult<SecretAnswer?>(null);

        public Task<string?> AskTextAsync(TextRequest request, CancellationToken ct) => Task.FromResult<string?>(null);

        public void Report(Notice notice)
        {
        }
    }

    /// <summary>Counts every call, and makes the chosen one go wrong in the chosen way.</summary>
    public sealed class Fault(Kind kind, int failAt)
    {
        private int _calls;
        private int _armedFrom;
        private bool _armed;

        public Kind Kind { get; } = kind;

        public int FailAt { get; } = failAt;

        public bool Fired { get; private set; }

        /// <summary>What the user does while the in-use question waits, for <see cref="Kind.InUseEdited"/>.</summary>
        public Action? WhileAsked { get; set; }

        /// <summary>From now on, the FailAt-th call counted goes wrong.</summary>
        public void Arm()
        {
            _armedFrom = _calls;
            _armed = true;
        }

        public void Off() => _armed = false;

        /// <summary>Before the call does anything: counts it, and fails it unless the kind is After.</summary>
        public int Before(string what)
        {
            var call = Interlocked.Increment(ref _calls) - _armedFrom;
            if (!_armed)
                return call;

            if (call == FailAt || (Kind == Kind.Sticky && call > FailAt))
            {
                switch (Kind)
                {
                    case Kind.Before:
                    case Kind.Sticky:
                        Fired = true;
                        throw new IOException($"Injected failure at call {call}: {what}.");
                    case Kind.Cancel:
                        Fired = true;
                        throw new OperationCanceledException($"Cancelled at call {call}: {what}.");
                    case var inUse when IsInUse(inUse):
                        Fired = true;
                        // The question is asked after this throws; editing now is the same as
                        // editing while it waits, since nothing else happens in between.
                        if (inUse == Kind.InUseEdited)
                            WhileAsked?.Invoke();
                        throw new IOException($"{what}: in use (injected at call {call}).", unchecked((int)0x80070020));
                }
            }

            return call;
        }

        /// <summary>After the call has taken effect: fails it then, for the kind After.</summary>
        public void After(int call, string what)
        {
            if (_armed && Kind == Kind.After && call == FailAt)
            {
                Fired = true;
                throw new IOException($"Injected failure after call {call} took effect: {what}.");
            }
        }
    }

    /// <summary>The local provider, every call (and every read, write and commit of its streams) counted.</summary>
    private sealed class CountingProvider(LocalFileSystemProvider inner, Fault fault) : IFileSystemProvider, IFileIdentity, ICopiesWithin
    {
        /// <summary>Whether a local copy goes through Windows' own copy (one call) or as a stream of bytes.</summary>
        public bool CopiesWithin { get; set; } = true;

        public bool CanCopyWithin(FsUri source, FsUri target) => CopiesWithin && inner.CanCopyWithin(source, target);

        public async Task<long> CopyWithinAsync(
            FsUri source, FsUri target, bool replace, bool durable, Action<long> moved, CancellationToken ct, Func<bool>? mayReplace = null)
        {
            var call = fault.Before($"copy {source.Name} to {target.Name}");
            var length = await inner.CopyWithinAsync(source, target, replace, durable, moved, ct, mayReplace);
            fault.After(call, $"copy {source.Name} to {target.Name}");
            return length;
        }

        /// <summary>Renames of files are refused, as some servers do, so a move is a copy and a delete.</summary>
        public bool RenamesRefused { get; set; }

        public string Scheme => inner.Scheme;

        public ProviderCapabilities Capabilities => inner.Capabilities;

        public ColumnSet DefaultColumns => inner.DefaultColumns;

        public async IAsyncEnumerable<FsEntry> ListAsync(
            FsUri directory, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var call = fault.Before($"list {directory.Name}");
            var count = 0;
            await foreach (var entry in inner.ListAsync(directory, ct))
            {
                // Each entry after the first is a call of its own, so a listing can also break in
                // the middle, as a connection does, with some of the folder seen and the rest not.
                if (count++ > 0)
                {
                    var next = fault.Before($"list {directory.Name}, entry {count}");
                    fault.After(next, $"list {directory.Name}, entry {count}");
                }
                yield return entry;
            }
            fault.After(call, $"list {directory.Name}");
        }

        public async Task<FsEntry?> StatAsync(FsUri path, CancellationToken ct)
        {
            var call = fault.Before($"stat {path.Name}");
            var entry = await inner.StatAsync(path, ct);
            fault.After(call, $"stat {path.Name}");
            return entry;
        }

        public async Task<Stream> OpenReadAsync(FsUri file, CancellationToken ct)
        {
            var call = fault.Before($"open {file.Name} to read");
            var stream = new Counted(await inner.OpenReadAsync(file, ct), fault, file.Name);
            try
            {
                fault.After(call, $"open {file.Name} to read");
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }

            return stream;
        }

        public async Task<Stream> OpenWriteAsync(FsUri file, long? length, CancellationToken ct)
        {
            var call = fault.Before($"open {file.Name} to write");
            var stream = new Counted(await inner.OpenWriteAsync(file, length, ct), fault, file.Name);
            try
            {
                fault.After(call, $"open {file.Name} to write");
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }

            return stream;
        }

        public async Task CreateDirectoryAsync(FsUri directory, CancellationToken ct)
        {
            var call = fault.Before($"create folder {directory.Name}");
            await inner.CreateDirectoryAsync(directory, ct);
            fault.After(call, $"create folder {directory.Name}");
        }

#pragma warning disable RS0030 // a test double: passes the provider's delete through to count it
        public async Task DeleteAsync(FsUri path, bool recursive, CancellationToken ct)
        {
            var call = fault.Before($"delete {path.Name}");
            await inner.DeleteAsync(path, recursive, ct);
            fault.After(call, $"delete {path.Name}");
        }
#pragma warning restore RS0030

        public async Task MoveAsync(FsUri from, FsUri to, CancellationToken ct)
        {
            var call = fault.Before($"move {from.Name} to {to.Name}");
            if (RenamesRefused && File.Exists(from.ToLocalPath()))
                throw new IOException("This server does not rename files.");
            await inner.MoveAsync(from, to, ct);
            fault.After(call, $"move {from.Name} to {to.Name}");
        }

        public IDirectoryWatcher? Watch(FsUri directory) => null;

        public Task<bool> IsSameFileAsync(FsUri a, FsUri b, CancellationToken ct) => inner.IsSameFileAsync(a, b, ct);
    }

    /// <summary>
    /// A server, as the SFTP provider treats one: no Recycle Bin, a rename that never replaces and
    /// stays on its own host, deletes that are for good, and writes to a <c>.part</c> committed by
    /// stepping the old file aside (<c>.dc-old-</c>), renaming the new one in, and only then
    /// deleting the old one. Each of those is a call of its own, so a commit can break half way,
    /// which the local provider's one-call commit never does. <c>srv://host/path</c> is kept in
    /// <c>server/host/path</c> under the tree.
    /// </summary>
    private sealed class ServerLikeProvider(string root, Fault fault) : IFileSystemProvider
    {
        public string Scheme => "srv";

        public ProviderCapabilities Capabilities =>
            ProviderCapabilities.Read | ProviderCapabilities.Write | ProviderCapabilities.Delete
            | ProviderCapabilities.Rename | ProviderCapabilities.CreateDirectory;

        public ColumnSet DefaultColumns => new LocalFileSystemProvider().DefaultColumns;

        public string Local(FsUri uri) => Path.Combine(root, uri.Authority, uri.Path.TrimStart('/').Replace('/', '\\'));

        private FsEntry? Entry(FsUri uri)
        {
            var path = Local(uri);
            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                return new FsEntry(uri, uri.Name, IsDirectory: false, file.Length, file.LastWriteTimeUtc);
            }

            return Directory.Exists(path) ? new FsEntry(uri, uri.Name, IsDirectory: true, 0, Directory.GetLastWriteTimeUtc(path)) : null;
        }

        /// <summary>Not counted: for the test's own setting up.</summary>
        public FsEntry? StatUncounted(FsUri uri) => Entry(uri);

        private T Call<T>(string what, Func<T> body)
        {
            var call = fault.Before(what);
            var result = body();
            fault.After(call, what);
            return result;
        }

        private void Call(string what, Action body) => Call<bool>(what, () => { body(); return true; });

        /// <summary>A call that opens a stream: closed again if the call then fails, as a real one would leave nothing open.</summary>
        private T Open<T>(string what, Func<T> open) where T : Stream
        {
            var call = fault.Before(what);
            var stream = open();
            try
            {
                fault.After(call, what);
            }
            catch
            {
                stream.Dispose();
                throw;
            }

            return stream;
        }

        public async IAsyncEnumerable<FsEntry> ListAsync(
            FsUri directory, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var names = Call($"list {directory.Name} on the server",
                () => Directory.EnumerateFileSystemEntries(Local(directory)).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList());
            var count = 0;
            foreach (var name in names)
            {
                await Task.Yield();
                if (count++ > 0)
                    Call($"list {directory.Name} on the server, entry {count}", () => { });
                if (Entry(directory.Child(name!)) is { } entry)
                    yield return entry;
            }
        }

        public Task<FsEntry?> StatAsync(FsUri path, CancellationToken ct) =>
            Task.FromResult(Call($"stat {path.Name} on the server", () => Entry(path)));

        public Task<Stream> OpenReadAsync(FsUri file, CancellationToken ct) =>
            Task.FromResult<Stream>(new Counted(
                Open($"open {file.Name} on the server to read", () => new FileStream(Local(file), FileMode.Open, FileAccess.Read, FileShare.Read)),
                fault, file.Name));

        /// <summary>As the SFTP provider: <c>name.part</c>, or a numbered one, never opened over anything.</summary>
        public Task<Stream> OpenWriteAsync(FsUri file, long? length, CancellationToken ct)
        {
            var target = Local(file);
            for (var n = 0; ; n++)
            {
                var partial = n == 0 ? target + ".part" : $"{target}.{n}.part";
                try
                {
                    var stream = Open($"open {file.Name} on the server to write",
                        () => new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None));
                    return Task.FromResult<Stream>(new Counted(new ServerPart(this, stream, partial, file), fault, file.Name));
                }
                catch (IOException) when (n < 100 && File.Exists(partial))
                {
                }
            }
        }

        public Task CreateDirectoryAsync(FsUri directory, CancellationToken ct)
        {
            Call($"create folder {directory.Name} on the server", () => { Directory.CreateDirectory(Local(directory)); });
            return Task.CompletedTask;
        }

        public Task DeleteAsync(FsUri path, bool recursive, CancellationToken ct)
        {
            Call($"delete {path.Name} on the server", () =>
            {
                var local = Local(path);
                if (Directory.Exists(local))
                    Directory.Delete(local, recursive);
                else if (File.Exists(local))
                    File.Delete(local);
                else
                    throw new FileNotFoundException($"{path.Name} is not on the server.");
            });
            return Task.CompletedTask;
        }

        /// <summary>A rename on one host, never over something: SFTP's rename does not replace.</summary>
        public Task MoveAsync(FsUri from, FsUri to, CancellationToken ct)
        {
            if (from.Authority != to.Authority)
                throw new NotSupportedException("Moving between two servers goes through the transfer service.");
            Rename($"move {from.Name} to {to.Name} on the server", Local(from), Local(to));
            return Task.CompletedTask;
        }

        public void Rename(string what, string from, string to) => Call(what, () =>
        {
            if (File.Exists(to) || Directory.Exists(to))
                throw new IOException($"{Path.GetFileName(to)} is already there; the server does not rename over it.");
            if (Directory.Exists(from))
                Directory.Move(from, to);
            else
                File.Move(from, to);
        });

        public bool Exists(string what, string path) => Call(what, () => File.Exists(path) || Directory.Exists(path));

        public void Delete(string what, string path) => Call(what, () => File.Delete(path));

        public IDirectoryWatcher? Watch(FsUri directory) => null;
    }

    /// <summary>The server's write: the SFTP provider's <c>PartStream</c> and its commit, step by step.</summary>
    private sealed class ServerPart(ServerLikeProvider server, FileStream inner, string partial, FsUri file) : Stream, ICommittableStream
    {
        private bool _finished;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => inner.SetLength(value);

        public Task CommitAsync(bool replace, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_finished, this);
            _finished = true;
            var target = server.Local(file);
            try
            {
                inner.Dispose();
                if (!server.Exists($"commit {file.Name}: is it there", target))
                {
                    server.Rename($"commit {file.Name}: into its name", partial, target);
                    return Task.CompletedTask;
                }

                if (!replace)
                    throw WriteStreams.Appeared(file.Name);

                var aside = $"{target}.dc-old-{Guid.NewGuid():N}";
                server.Rename($"commit {file.Name}: the old one aside", target, aside);
                try
                {
                    server.Rename($"commit {file.Name}: into its name", partial, target);
                }
                catch (Exception ex)
                {
                    try
                    {
                        server.Rename($"commit {file.Name}: the old one back", aside, target);
                    }
                    catch (IOException)
                    {
                        throw new IOException($"{file.Name} could not be replaced, and the old one is now {aside}.", ex);
                    }
                    throw;
                }

                try
                {
                    server.Delete($"commit {file.Name}: the old one", aside);
                }
                catch (IOException)
                {
                    // The new file is in place; the old one is left under a name that says what it is.
                }

                return Task.CompletedTask;
            }
            catch
            {
                Abandon();
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing || _finished)
                return;
            _finished = true;
            inner.Dispose();
            Abandon();
        }

        private void Abandon()
        {
            try
            {
                if (server.Exists($"abandon {file.Name}: is the part there", partial))
                    server.Delete($"abandon {file.Name}: the part", partial);
            }
            catch (Exception)
            {
                // Left as a .part, which says what it is; as the SFTP provider, anything is swallowed.
            }
        }
    }

    /// <summary>A provider's stream with each read, write and commit counted.</summary>
    private sealed class Counted(Stream inner, Fault fault, string name) : Stream, ICommittableStream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var call = fault.Before($"read {name}");
            var read = inner.Read(buffer, offset, count);
            fault.After(call, $"read {name}");
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var call = fault.Before($"read {name}");
            var read = await inner.ReadAsync(buffer, ct);
            fault.After(call, $"read {name}");
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var call = fault.Before($"write {name}");
            inner.Write(buffer, offset, count);
            fault.After(call, $"write {name}");
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            var call = fault.Before($"write {name}");
            await inner.WriteAsync(buffer, ct);
            fault.After(call, $"write {name}");
        }

        public async Task CommitAsync(bool replace, CancellationToken ct)
        {
            var call = fault.Before($"commit {name}");
            await inner.CommitAsync(replace, ct);
            fault.After(call, $"commit {name}");
        }

        // Counted too: a flush is where a full disk or a dropped server is often first heard of.
        public override void Flush()
        {
            var call = fault.Before($"flush {name}");
            inner.Flush();
            fault.After(call, $"flush {name}");
        }

        public override async Task FlushAsync(CancellationToken ct)
        {
            var call = fault.Before($"flush {name}");
            await inner.FlushAsync(ct);
            fault.After(call, $"flush {name}");
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    /// <summary>A Recycle Bin in a folder, each item it takes or gives back a counted call.</summary>
    public sealed class SweepBin(Fault fault, string folder) : IRecycleBin
    {
        private readonly List<(string Original, string Kept, DateTimeOffset At)> _items = [];

        public bool CanRecycle(FsUri uri) => uri.IsLocal;

        public Task RecycleAsync(IReadOnlyList<FsUri> paths, CancellationToken ct)
        {
            foreach (var path in paths)
            {
                var call = fault.Before($"recycle {path.Name}");
                var original = path.ToLocalPath();
                var kept = Path.Combine(folder, Guid.NewGuid().ToString("N"), Path.GetFileName(original));
                Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                if (Directory.Exists(original))
                    Directory.Move(original, kept);
                else
                    File.Move(original, kept);
                _items.Add((original, kept, DateTimeOffset.Now));
                fault.After(call, $"recycle {path.Name}");
            }

            return Task.CompletedTask;
        }

        public Task RestoreAsync(IReadOnlyList<FsUri> paths, DateTimeOffset deletedSince, CancellationToken ct) =>
            RestoreAsync(paths, deletedSince, DateTimeOffset.MaxValue, ct);

        public Task RestoreAsync(IReadOnlyList<FsUri> paths, DateTimeOffset deletedSince, DateTimeOffset deletedBefore, CancellationToken ct)
        {
            foreach (var path in paths)
            {
                var call = fault.Before($"restore {path.Name}");
                var original = path.ToLocalPath();
                var item = _items.LastOrDefault(i => i.Original == original && i.At >= deletedSince.AddSeconds(-1) &&
                                                     (deletedBefore == DateTimeOffset.MaxValue || i.At <= deletedBefore.AddSeconds(1)));
                if (item.Kept is null)
                    throw new IOException($"{path.Name} is no longer in the Recycle Bin.");
                if (File.Exists(original) || Directory.Exists(original))
                    throw new IOException($"{path.Name} cannot be put back: something else has that name now.");
                if (Directory.Exists(item.Kept))
                    Directory.Move(item.Kept, original);
                else
                    File.Move(item.Kept, original);
                _items.Remove(item);
                fault.After(call, $"restore {path.Name}");
            }

            return Task.CompletedTask;
        }
    }
}
