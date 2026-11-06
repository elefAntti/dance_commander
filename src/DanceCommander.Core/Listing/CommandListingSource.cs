using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Watching;

namespace DanceCommander.Core.Listing;

/// <summary>
/// A listing whose rows are the paths a command printed (§6.12).
/// </summary>
/// <remarks>
/// The motivating case is git worktrees: with several agents working in parallel checkouts of one
/// repository, the set of places you care about changes under you, and a bookmark list is one you
/// maintain while a generated one maintains itself. But nothing here is about git. Anything that
/// can print paths — a worktree list, a build manifest, paths grepped out of a document — becomes
/// a location you can sort, select in, transfer from and bookmark, which is §1.2/3 taken one step
/// further than the app's own schemes.
///
/// The command is never in the URI. A <c>from:</c> address names an entry in <c>tools.json</c>,
/// so what actually runs is always something the user wrote in their own configuration; a
/// bookmark that carried an arbitrary command line would be a very different kind of object.
/// </remarks>
public sealed class CommandListingSource : IListingSource
{
    /// <summary>Long enough for a slow repository, short enough not to hang the pane.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly IProviderRegistry _providers;
    private readonly CommandListing _listing;
    private readonly FsUri _workingDirectory;
    private readonly Regex _extract;
    private string? _omission;

    public CommandListingSource(IProviderRegistry providers, FsUri uri, CommandListing listing)
    {
        _providers = providers;
        _listing = listing;
        _workingDirectory = uri.Inner
            ?? throw new InvalidOperationException("A command listing must say where it runs.");
        Uri = uri;

        // Matched against the whole output rather than line by line, which is what lets one rule
        // cover both shapes of output there are: "^(.+)$" with Multiline for a list of paths, and
        // a pattern spanning several lines for a stanza format like git's --porcelain.
        _extract = new Regex(listing.Extract, RegexOptions.Multiline | RegexOptions.CultureInvariant);
    }

    public FsUri Uri { get; }

    public string Title => _listing.Title;

    public ColumnSet Columns { get; private set; } = ColumnSet.Standard;

    public bool SupportsNavigation => true;

    public string? Omission => _omission;

    /// <summary>Nothing to watch: the rows come from a program, not from a directory.</summary>
    public IDirectoryWatcher? Watch() => null;

    public async IAsyncEnumerable<FsEntry> ListAsync([EnumeratorCancellation] CancellationToken ct)
    {
        Columns = CommandListingColumns.For(_listing);
        _omission = null;

        var directory = _workingDirectory.IsLocal ? _workingDirectory.ToLocalPath() : null;
        var result = await ProcessOutput
            .RunAsync(_listing.Exec, _listing.Args, directory, Patience, ct)
            .ConfigureAwait(false);

        if (result is not { } run)
        {
            _omission = $"{_listing.Exec} could not be run";
            yield break;
        }

        if (run.ExitCode != 0)
        {
            // The program's own words, not ours. It knows why it failed and we do not.
            var reason = run.StdErr.Trim();
            _omission = reason.Length > 0
                ? Summarise(reason)
                : $"{_listing.Exec} exited with code {run.ExitCode}";
            yield break;
        }

        var missing = 0;
        // The same line twice is described once; rows are told apart by the file they ended up
        // as. By the trimmed text, `report` and `report ` were one key, and whichever came second
        // was dropped with nothing said, though both are files (review, 2026-10-30).
        var lines = new HashSet<string>(StringComparer.Ordinal);
        // By address, compared as its provider compares names: a server's File and file are two.
        var seen = new HashSet<FsUri>();
        var badPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in _extract.Matches(run.StdOut))
        {
            ct.ThrowIfCancellationRequested();

            var asWritten = AsWritten(match);
            if (Path(match) is not { } path || !lines.Add(asWritten))
                continue;

            var entry = await DescribeAsync(path, asWritten, match, ct).ConfigureAwait(false);
            // A bad path's row carries the listing's own address; those stay apart by their text.
            if (entry.Uri == Uri ? !badPaths.Add(path) : !seen.Add(entry.Uri))
                continue;
            if (entry.Extra?.ContainsKey(ColumnIds.Note) == true)
                missing++;

            yield return entry;
        }

        if (missing > 0)
            _omission = missing == 1 ? "1 path no longer exists" : $"{missing:N0} paths no longer exist";
    }

    /// <summary>The captured path: a group called "path", or the first group, or the whole match.</summary>
    private static string? Path(Match match)
    {
        var group = match.Groups["path"];
        if (!group.Success)
            group = match.Groups.Count > 1 ? match.Groups[1] : match.Groups[0];

        var value = group.Value.Trim().Trim('"');
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// The captured path with only its line break and quotes off: a name's own first or last
    /// space is part of it, and trimmed it was the neighbour's name, for Del to act on (BUGS.md).
    /// Taken only when such a file is there; a space a script left at a line's edge is far likelier.
    /// </summary>
    private static string AsWritten(Match match)
    {
        var group = match.Groups["path"];
        if (!group.Success)
            group = match.Groups.Count > 1 ? match.Groups[1] : match.Groups[0];
        return group.Value.TrimEnd('\r', '\n').Trim('"');
    }

    private async Task<FsEntry> DescribeAsync(string path, string asWritten, Match match, CancellationToken ct)
    {
        // Only a reading in the same folder as the trimmed one, so the same name with its own
        // edges: `git status -s`'s ` ../README.md`, kept as printed, became `<here>\ ..\README.md`,
        // whose `..` took the space folder off by text, and the row was the README here, another
        // file. Trimming the start for that lost ` notes`, a name of its own (reviews, 2026-10-30).
        if (asWritten != path && Resolve(asWritten) is { } exact && Resolve(path) is { } trimmed
            && exact.Parent == trimmed.Parent
            && await StatAsync(exact, ct).ConfigureAwait(false) is not null)
        {
            path = asWritten;
        }

        var extra = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var column in _listing.Columns ?? [])
        {
            if (match.Groups[column.Group] is { Success: true } value)
                extra[column.Group] = value.Value.Trim();
        }

        var uri = Resolve(path);
        if (uri is null)
        {
            extra[ColumnIds.Note] = "bad path";
            extra[ColumnIds.RelativePath] = path;
            // The listing's own address, which nothing can delete, rename or move. This row used
            // to carry the folder the listing runs in, so Del on a line the program printed badly
            // (a NUL from -z output is enough) sent the whole repository to the Recycle Bin.
            return new FsEntry(Uri, path, IsDirectory: false, 0, null) { Extra = extra };
        }

        extra[ColumnIds.RelativePath] = uri.Parent?.Display ?? uri.Display;

        var stat = await StatAsync(uri, ct).ConfigureAwait(false);
        if (stat is null)
        {
            // Shown rather than dropped: a list of worktrees that silently omits the one somebody
            // deleted is a list that has stopped telling you the truth about your machine.
            extra[ColumnIds.Note] = "not found";
            return new FsEntry(uri, uri.Name, IsDirectory: false, 0, null) { Extra = extra };
        }

        // The real entry, so that opening, copying and previewing it behave exactly as they would
        // in the folder it actually lives in -- these rows are files, not references to files.
        var merged = stat.Extra is null
            ? extra
            : new Dictionary<string, string>(stat.Extra, StringComparer.Ordinal)
                .Concat(extra).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        return stat with { Extra = merged };
    }

    private FsUri? Resolve(string path)
    {
        try
        {
            // A script's output names a place; a '!' in it is part of a name, never a step into
            // an archive, so it is read whole rather than as the application's own form.
            if (FsUri.TryParseWhole(path, out var parsed) && !parsed.IsLocal)
                return parsed;

            // An address refused (one carrying a password, say) is no path either: joined to the
            // working folder, "sftp://u:pw@h/x" became a local row with the password in it. No
            // Windows name holds "://", so nothing local is lost.
            if (path.Contains("://", StringComparison.Ordinal) && parsed is null)
                return null;

            var rooted = System.IO.Path.IsPathRooted(path)
                ? path
                : System.IO.Path.Combine(_workingDirectory.ToLocalPath(), path);

            // GetFullPath only where the path needs it (C:x, \x): it takes a trailing dot or space
            // off, and the row for notes. was then the folder notes, for Del to act on.
            return System.IO.Path.IsPathFullyQualified(rooted)
                ? FsUri.LocalExact(rooted)
                : FsUri.Local(System.IO.Path.GetFullPath(rooted));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private async Task<FsEntry?> StatAsync(FsUri uri, CancellationToken ct)
    {
        try
        {
            return await _providers.Resolve(uri).StatAsync(uri, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>One line of a program's complaint, which is where it always puts the useful part.</summary>
    private static string Summarise(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? text;
        return line.Length <= 120 ? line : line[..117] + "…";
    }
}
