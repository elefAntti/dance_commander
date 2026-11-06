using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace DanceCommander.Providers.Local;

/// <summary>
/// What git thinks of one path. Ordered by how much it wants saying: a conflict outranks a
/// modification, which outranks being untracked, which outranks being ignored.
/// </summary>
public enum GitState
{
    None = 0,
    Ignored,
    Untracked,
    Modified,
    Added,
    Deleted,
    Renamed,
    Conflicted,
}

/// <summary>
/// The status of one work tree, as a lookup by path relative to its root.
/// </summary>
/// <remarks>
/// Directories are in here as well as files, and for two different reasons. Git itself collapses a
/// wholly untracked directory into one entry — <c>new-feature/</c> rather than ninety files —
/// which is exactly what a pane listing that directory's parent wants. Everything else is
/// rolled up on the way in: a folder containing a modified file is reported as modified, because a
/// row that gives no sign anything below it changed is the reason people keep a terminal open
/// beside their file manager.
/// </remarks>
public sealed class GitStatus(IReadOnlyDictionary<string, GitState> byPath)
{
    public static readonly GitStatus Empty = new(new Dictionary<string, GitState>());

    public int Count => byPath.Count;

    /// <summary>The state of a path given relative to the work tree root, using '/' separators.</summary>
    public GitState For(string relativePath) =>
        byPath.TryGetValue(relativePath, out var state) ? state : GitState.None;

    private IReadOnlyList<string>? _conflicts;

    /// <summary>
    /// The unmerged paths. Worked out once per answer, not per ask: every command context asks,
    /// and the legend makes one on each modifier key.
    /// </summary>
    public IReadOnlyList<string> Conflicts => _conflicts ??=
        byPath.Where(p => p.Value == GitState.Conflicted).Select(p => p.Key).ToList();
}

public interface IGitStatusService
{
    /// <summary>
    /// Status for the work tree at this root, or null when git could not be asked.
    /// </summary>
    ValueTask<GitStatus?> GetAsync(string workTreeRoot, CancellationToken ct);

    /// <summary>
    /// The last answer git gave for this work tree, even one since invalidated; null if never asked.
    /// What to show while the fresh one is on its way.
    /// </summary>
    GitStatus? Last(string workTreeRoot) => null;

    /// <summary>Drops the cached answer for a work tree, e.g. because something in it changed.</summary>
    void Invalidate(string workTreeRoot);
}

/// <summary>
/// Per-file git status, by asking git (§6.3, §6.5).
/// </summary>
/// <remarks>
/// Branch decoration reads <c>.git/HEAD</c> directly because a branch name is a forty-byte read.
/// Status is the opposite: reproducing it means reading the index, applying every
/// <c>.gitignore</c> in the tree, and stat-ing the work tree — which is a reimplementation of git,
/// not a shortcut. So this shells out, and accepts the process cost in exchange for answers that
/// are right about submodules, sparse checkouts, skip-worktree and all the rest.
///
/// <c>--no-optional-locks</c> is the important flag and not an optimisation: without it
/// <c>git status</c> takes <c>index.lock</c> to write back its refreshed stat cache, and a file
/// manager that idly locks the index of every repository you browse will eventually collide with
/// the terminal you are actually working in. The flag costs a little speed on repeat calls and
/// buys never being the reason someone's commit failed.
/// </remarks>
public sealed class GitStatusService : IGitStatusService
{
    /// <summary>A repository this big is one where the answer is not worth the wait.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, Task<GitStatus?>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    // Kept through Invalidate. An editor's own git rewrites .git/index every few seconds, and each
    // rewrite invalidates; without this, every visit to a repository open in one waited the whole
    // git status (2.5 s on a large one) with no marks at all, when the old ones were nearly all right.
    private readonly ConcurrentDictionary<string, GitStatus> _last =
        new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<GitStatus?> GetAsync(string workTreeRoot, CancellationToken ct)
    {
        // The task rather than its result, so that two panes showing the same repository at the
        // same moment run one git process between them rather than one each.
        var pending = _cache.GetOrAdd(workTreeRoot, root => Task.Run(async () =>
        {
            var report = await RunAsync(root).ConfigureAwait(false);
            if (report is not null)
                _last[root] = report;
            return report;
        }, CancellationToken.None));
        return new ValueTask<GitStatus?>(pending.WaitAsync(ct));
    }

    public GitStatus? Last(string workTreeRoot) => _last.GetValueOrDefault(workTreeRoot);

    public void Invalidate(string workTreeRoot) => _cache.TryRemove(workTreeRoot, out _);

    private static async Task<GitStatus?> RunAsync(string workTreeRoot)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workTreeRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        // -z so that a filename containing a quote, a newline or a non-ASCII character arrives
        // intact; without it git escapes such paths and we would have to unescape them.
        //
        // Not --ignored: ignored rows carry no mark, and walking the ignored build trees to say so
        // was half the time on a large repository (1.2 s against 0.57 s, warm). A lens
        // that wants them (PLAN.md, the .gitignore lens) should ask for them when it is on.
        foreach (var argument in (string[])
            ["--no-optional-locks", "status", "--porcelain=v1", "-z"])
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return null;

            using var timeout = new CancellationTokenSource(Patience);

            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            return process.ExitCode == 0 ? Parse(output) : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // No git on PATH, or it died. Not having git installed is not an error condition for a
            // file manager; the rows simply carry no marks.
            return null;
        }
    }

    /// <summary>
    /// Parses <c>--porcelain=v1 -z</c>: records of <c>XY&lt;space&gt;path</c> separated by NUL,
    /// with a rename or copy followed by a second record holding where it came from.
    /// </summary>
    internal static GitStatus Parse(string output)
    {
        var byPath = new Dictionary<string, GitState>(StringComparer.Ordinal);
        var records = output.Split('\0');

        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.Length < 4)
                continue;

            var x = record[0];
            var y = record[1];
            var path = record[3..].TrimEnd('/');

            // A rename's origin arrives as the next record and is not a status of its own.
            if (x is 'R' or 'C')
                i++;

            var state = Classify(x, y);
            if (state == GitState.None)
                continue;

            Record(byPath, path, state);

            // Ignored and untracked directories are reported whole and are not rolled up: the
            // point of git collapsing them is that nothing inside needs looking at.
            if (state is GitState.Ignored or GitState.Untracked)
                continue;

            RollUp(byPath, path);
        }

        return new GitStatus(byPath);
    }

    private static GitState Classify(char x, char y)
    {
        // Both sides unmerged, or either side 'U', is a conflict and outranks everything.
        if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D'))
            return GitState.Conflicted;

        return (x, y) switch
        {
            ('!', '!') => GitState.Ignored,
            ('?', '?') => GitState.Untracked,
            ('R', _) or (_, 'R') => GitState.Renamed,
            ('A', _) => GitState.Added,
            ('D', _) or (_, 'D') => GitState.Deleted,
            ('C', _) => GitState.Added,
            (' ', ' ') => GitState.None,
            _ => GitState.Modified,
        };
    }

    /// <summary>Marks every directory above a changed path, so a collapsed tree still shows it.</summary>
    private static void RollUp(Dictionary<string, GitState> byPath, string path)
    {
        // A folder is reported as plainly "modified" whatever happened inside it: "something below
        // here differs" is the whole of what a parent row can honestly claim.
        for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/'))
        {
            path = path[..slash];
            Record(byPath, path, GitState.Modified);
        }
    }

    private static void Record(Dictionary<string, GitState> byPath, string path, GitState state)
    {
        if (path.Length == 0)
            return;

        // Loudest wins: a folder holding a conflict and a hundred clean files is a folder with a
        // conflict in it.
        if (!byPath.TryGetValue(path, out var existing) || state > existing)
            byPath[path] = state;
    }
}
