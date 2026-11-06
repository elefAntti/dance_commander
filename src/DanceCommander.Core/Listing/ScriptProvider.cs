using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Watching;

namespace DanceCommander.Core.Listing;

/// <summary>Supplies the secrets a script declares, asking for one that is not stored yet.</summary>
public interface IScriptSecrets
{
    /// <summary>The secret, or null when there is none and the user declined to give one.</summary>
    Task<string?> GetAsync(string name, string listingTitle, CancellationToken ct);
}

/// <summary>
/// A location served by a script (§6.16): GitLab pipelines → jobs → artifacts, a JIRA ticket's
/// attachments. The application knows neither service. The script answers two verbs, and the
/// provider contract does the rest — transfer, preview, bookmarks, history and archive nesting all
/// work because this is an ordinary read-only provider.
/// </summary>
/// <remarks>
/// <para><c>list &lt;crumbs&gt;</c> prints the rows one level down; <c>read &lt;crumbs&gt;</c>
/// writes a file's bytes to stdout. The crumbs are whatever the script printed for each level,
/// joined with '/', handed back verbatim: the script owns the hierarchy, and this only remembers the
/// way down. The root is an empty crumb path.</para>
///
/// <para>Rows are read with the listing's <c>extract</c> pattern. Named groups: <c>name</c>
/// (required), <c>kind</c> (<c>d</c>/<c>dir</c> makes a folder), <c>crumb</c> (defaults to the
/// name), <c>size</c>, <c>modified</c> (ISO 8601), and any declared column. The default pattern is
/// one tab-separated line per row: kind, name, size, modified, crumb.</para>
///
/// <para>Crumbs are restricted to letters, digits and <c>. _ ~ - %</c>, because they travel in a
/// path that is normalised like one; a script needing anything else percent-encodes it. A row whose
/// crumb breaks the rule is shown with a note rather than dropped or mangled.</para>
///
/// <para>Deliberately not <see cref="ProviderCapabilities.Write"/>: attaching a file to a ticket is
/// a command, not a path, and declaring Write would be a claim the transfer engine believes.</para>
/// </remarks>
public sealed class ScriptProvider : IFileSystemProvider, IDisposable
{
    /// <summary>One tab-separated line per row: kind, name, size, modified, crumb; all but name optional.</summary>
    public const string DefaultExtract =
        @"^(?:(?<kind>[dfDF])\t)?(?<name>[^\t\r\n]+)(?:\t(?<size>\d*))?(?:\t(?<modified>[^\t\r\n]*))?(?:\t(?<crumb>[^\t\r\n]*))?\r?$";

    /// <summary>A service answering a listing is slower than a disk, but not this slow.</summary>
    private static readonly TimeSpan ListPatience = TimeSpan.FromSeconds(60);

    /// <summary>A read is a download; it may take as long as it takes, and is cancellable.</summary>
    private static readonly TimeSpan ReadPatience = TimeSpan.FromMinutes(30);

    /// <summary>How long a level's rows answer stat without running the script again.</summary>
    private static readonly TimeSpan RowsStayFresh = TimeSpan.FromSeconds(30);

    private static readonly Regex SafeCrumb = new(@"^[A-Za-z0-9._~%-]+$", RegexOptions.CultureInvariant);

    private readonly ICommandListingCatalog _listings;
    private readonly IScriptSecrets? _secrets;
    private readonly Func<FsUri, RepositoryInfo?>? _repository;
    private readonly Dictionary<FsUri, (DateTime At, List<FsEntry> Rows)> _recent = [];
    private readonly Lock _gate = new();

    public ScriptProvider(
        ICommandListingCatalog listings,
        IScriptSecrets? secrets = null,
        Func<FsUri, RepositoryInfo?>? repository = null)
    {
        _listings = listings;
        _secrets = secrets;
        _repository = repository;
    }

    public string Scheme => FsUri.FromScheme;

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read;

    public ColumnSet DefaultColumns => ColumnSet.Standard;

    /// <summary>The columns of a script location: name, size, modified, and what the listing declares.</summary>
    public static ColumnSet ColumnsFor(CommandListing listing)
    {
        var extras = listing.Columns ?? [];
        var ownFlexible = extras.FirstOrDefault(c => c.Flexible);

        var columns = new List<ColumnDefinition>
        {
            new(ColumnIds.Name, "Name", ColumnKind.Name, 220, Flexible: ownFlexible is null),
            new(ColumnIds.Size, "Size", ColumnKind.Size, 78, ColumnAlignment.Right),
        };
        if (listing.ShowModified)
            columns.Add(new ColumnDefinition(ColumnIds.Modified, "Modified", ColumnKind.Date, 148));
        foreach (var extra in extras)
            columns.Add(new ColumnDefinition(
                extra.Group, extra.Header, ColumnKind.Text, extra.Width, Flexible: ReferenceEquals(extra, ownFlexible)));

        var sortBy = listing.Sort?.ToLowerInvariant() switch
        {
            "modified" when listing.ShowModified => ColumnIds.Modified,
            "size" => ColumnIds.Size,
            _ => ColumnIds.Name,
        };

        return new ColumnSet(columns, sortBy, DefaultDescending: sortBy != ColumnIds.Name);
    }

    public async IAsyncEnumerable<FsEntry> ListAsync(FsUri directory, [EnumeratorCancellation] CancellationToken ct)
    {
        var rows = await ListLevelAsync(directory, ct).ConfigureAwait(false);
        foreach (var row in rows)
            yield return row;
    }

    public async Task<FsEntry?> StatAsync(FsUri path, CancellationToken ct)
    {
        var listing = Listing(path);
        if (path.IsRoot)
            return new FsEntry(path, listing.Title, IsDirectory: true, 0, null);

        // No stat verb to ask: the parent's rows already said what this is. Cached, because the
        // transfer engine stats every item it copies and each would otherwise run the script.
        var parent = path.Parent!;
        List<FsEntry>? rows;
        lock (_gate)
            rows = _recent.TryGetValue(parent, out var hit) && DateTime.UtcNow - hit.At < RowsStayFresh ? hit.Rows : null;

        rows ??= await ListLevelAsync(parent, ct).ConfigureAwait(false);
        return rows.FirstOrDefault(r => r.Uri == path);
    }

    /// <summary>
    /// The name the parent level gave this row ("#1284 success"), if that level has been listed;
    /// the crumb in the address ("1284") is the script's to read back, not a title. Stale is fine:
    /// it only names a tab, and never runs the script to do it.
    /// </summary>
    public string? NameOf(FsUri path)
    {
        if (path.IsRoot || path.Parent is not { } parent)
            return null;
        lock (_gate)
            return _recent.TryGetValue(parent, out var hit) ? hit.Rows.FirstOrDefault(r => r.Uri == path)?.Name : null;
    }

    public async Task<Stream> OpenReadAsync(FsUri file, CancellationToken ct)
    {
        var listing = Listing(file);
        ForgetStaleDownloads();

        // Opening an artifacts.zip reads it twice in quick succession — once to recognise it as an
        // archive, once for the archive reader — and each read is a download. The second is served
        // from the first, for as long as a download stays fresh.
        lock (_gate)
        {
            if (_downloads.TryGetValue(file, out var kept) && File.Exists(kept.Path))
                return new FileStream(kept.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        // To a temporary file rather than straight from stdout: the stream can then seek, which is
        // what lets an artifacts.zip a script serves open as an archive, and the process is done
        // with before anyone reads — its exit code is known before its bytes are trusted.
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dc-script-{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                created = true;
                var (exit, error) = await RunAsync(
                    listing, file, "read", ReadPatience, stdout => stdout.CopyToAsync(output, ct), ct).ConfigureAwait(false);

                if (exit != 0)
                    throw new IOException(Complaint(listing, exit, error));
            }

            lock (_gate)
                _downloads[file] = (temp, DateTime.UtcNow);

            return new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch
        {
            // Only a temp this call made: if CreateNew failed, the name was someone else's.
            if (created)
                TryDelete(temp);
            throw;
        }
    }

    /// <summary>How long a download answers repeat reads before the script is asked again.</summary>
    private static readonly TimeSpan DownloadsStayFresh = TimeSpan.FromMinutes(2);

    private readonly Dictionary<FsUri, (string Path, DateTime At)> _downloads = [];

    /// <summary>
    /// Deletes downloads past their freshness. A reader still holding one keeps reading: the files
    /// are opened with delete sharing, so Windows removes the name now and the bytes when the last
    /// handle closes.
    /// </summary>
    private void ForgetStaleDownloads()
    {
        List<string> stale;
        lock (_gate)
        {
            stale = _downloads.Where(d => DateTime.UtcNow - d.Value.At >= DownloadsStayFresh).Select(d => d.Value.Path).ToList();
            foreach (var key in _downloads.Where(d => DateTime.UtcNow - d.Value.At >= DownloadsStayFresh).Select(d => d.Key).ToList())
                _downloads.Remove(key);
        }

        foreach (var path in stale)
            TryDelete(path);
    }

    /// <summary>Downloads kept for repeat reads go with the application, not with the temp folder's next sweep.</summary>
    public void Dispose()
    {
        List<string> kept;
        lock (_gate)
        {
            kept = _downloads.Values.Select(d => d.Path).ToList();
            _downloads.Clear();
        }

        foreach (var path in kept)
            TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
#pragma warning disable RS0030 // §1.6: a download this provider made in the temp folder, never the file it came from.
            File.Delete(path);
#pragma warning restore RS0030
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // In use or already gone; the temp folder is the OS's to sweep.
        }
    }

    public Task<Stream> OpenWriteAsync(FsUri file, long? length, CancellationToken ct) => throw ReadOnly();

    public Task CreateDirectoryAsync(FsUri directory, CancellationToken ct) => throw ReadOnly();

    public Task DeleteAsync(FsUri path, bool recursive, CancellationToken ct) => throw ReadOnly();

    public Task MoveAsync(FsUri from, FsUri to, CancellationToken ct) => throw ReadOnly();

    /// <summary>A service has no change notifications to offer; Ctrl+R lists again.</summary>
    public IDirectoryWatcher? Watch(FsUri directory) => null;

    private static NotSupportedException ReadOnly() =>
        new("A script location is read-only. To send a file to the service, use a tool (§6.16).");

    private CommandListing Listing(FsUri uri) =>
        _listings.Find(uri.Parameter) is { IsScript: true } listing
            ? listing
            : throw new InvalidOperationException($"There is no script location called '{uri.Parameter}'.");

    private async Task<List<FsEntry>> ListLevelAsync(FsUri directory, CancellationToken ct)
    {
        var listing = Listing(directory);
        var text = new StringBuilder();

        var (exit, error) = await RunAsync(
            listing, directory, "list", ListPatience,
            async stdout =>
            {
                using var reader = new StreamReader(stdout, Encoding.UTF8);
                text.Append(await reader.ReadToEndAsync(ct).ConfigureAwait(false));
            },
            ct).ConfigureAwait(false);

        // The script's own words, not ours: it knows why it failed and we do not.
        if (exit != 0)
            throw new IOException(Complaint(listing, exit, error));

        var extract = new Regex(listing.Extract, RegexOptions.Multiline | RegexOptions.CultureInvariant);
        var rows = new List<FsEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in extract.Matches(text.ToString()))
        {
            if (Row(listing, directory, match) is { } row && seen.Add(row.Uri.Path))
                rows.Add(row);
        }

        lock (_gate)
            _recent[directory] = (DateTime.UtcNow, rows);

        return rows;
    }

    private static FsEntry? Row(CommandListing listing, FsUri directory, Match match)
    {
        var name = Group(match, "name") ?? (match.Groups.Count > 1 ? match.Groups[1].Value.Trim() : null);
        if (string.IsNullOrEmpty(name))
            return null;

        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in listing.Columns ?? [])
        {
            if (Group(match, column.Group) is { } value)
                extra[column.Group] = value;
        }

        var kind = Group(match, "kind");
        var isDirectory = kind is not null && (kind.StartsWith('d') || kind.StartsWith('D'));
        var sized = long.TryParse(Group(match, "size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size);
        var modified = DateTimeOffset.TryParse(
            Group(match, "modified"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
            ? when
            : (DateTimeOffset?)null;

        var crumb = Group(match, "crumb") ?? name;
        // "." and ".." pass the pattern but are not names; as a crumb they would climb the path.
        if (!SafeCrumb.IsMatch(crumb) || crumb is "." or "..")
        {
            // Shown, with the reason, where the script's author will see it -- not dropped, and not
            // quietly rewritten into a crumb the script would not recognise when it came back.
            extra[ColumnIds.Note] = "crumb needs percent-encoding";
            return new FsEntry(directory, name, IsDirectory: false, size, modified) { Extra = extra, SizeUnknown = !sized };
        }

        return new FsEntry(directory.Child(crumb), name, isDirectory, size, modified)
        {
            Extra = extra.Count == 0 ? null : extra,
            SizeUnknown = !sized,
        };
    }

    private static string? Group(Match match, string name) =>
        match.Groups[name] is { Success: true } group && group.Value.Trim() is { Length: > 0 } value ? value : null;

    /// <summary>Runs <c>exec args… verb crumbs</c> where the location is anchored, secrets in the environment.</summary>
    private async Task<(int ExitCode, string StdErr)> RunAsync(
        CommandListing listing,
        FsUri at,
        string verb,
        TimeSpan patience,
        Func<Stream, Task> readOutput,
        CancellationToken ct)
    {
        var anchor = at.Anchor;
        var directory = anchor.IsLocal ? anchor.ToLocalPath() : null;
        var repository = _repository?.Invoke(anchor);

        var info = new ProcessStartInfo(listing.Exec)
        {
            WorkingDirectory = directory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in listing.Args)
            info.ArgumentList.Add(ExpandTokens(argument, anchor, repository));
        info.ArgumentList.Add(verb);
        info.ArgumentList.Add(at.IsRoot ? string.Empty : at.Path.TrimStart('/'));

        // In the environment, never in argv: argv is readable by any process on the machine, an
        // environment block is not. Different threat, different channel from §6.6's argv rule.
        foreach (var secret in listing.Secrets ?? [])
        {
            var value = _secrets is null ? null : await _secrets.GetAsync(secret, listing.Title, ct).ConfigureAwait(false);
            if (value is null)
                throw new IOException($"{listing.Title} needs the secret \"{secret}\", and none was given.");
            info.Environment[SecretVariable(secret)] = value;
        }

        using var process = Process.Start(info) ?? throw new IOException($"{listing.Exec} could not be run.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(patience);

        try
        {
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await readOutput(process.StandardOutput.BaseStream).ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return (process.ExitCode, await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            if (ct.IsCancellationRequested)
                throw;
            throw new IOException($"{listing.Title} did not answer within {patience.TotalSeconds:N0} seconds.");
        }
    }

    /// <summary><c>gitlab-token</c> → <c>DC_SECRET_GITLAB_TOKEN</c>.</summary>
    public static string SecretVariable(string name) =>
        "DC_SECRET_" + new string(name.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_').ToArray());

    /// <summary>
    /// The location tokens (§6.6) in a script's arguments: which repository and branch the location
    /// is anchored in, so a script can find "the ticket for this branch" without being told.
    /// </summary>
    private static string ExpandTokens(string argument, FsUri anchor, RepositoryInfo? repository) =>
        argument.Contains("${", StringComparison.Ordinal)
            ? argument
                .Replace("${repoRoot}", repository?.Root is { IsLocal: true } root ? root.ToLocalPath() : string.Empty, StringComparison.Ordinal)
                .Replace("${branch}", repository?.Branch ?? string.Empty, StringComparison.Ordinal)
                .Replace("${remoteUrl}", repository?.RemoteUrl ?? string.Empty, StringComparison.Ordinal)
                .Replace("${dir}", anchor.IsLocal ? anchor.ToLocalPath() : anchor.Display, StringComparison.Ordinal)
            : argument;

    private static string Complaint(CommandListing listing, int exit, string stderr)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(line)
            ? $"{listing.Title}: the script exited with code {exit}."
            : $"{listing.Title}: {(line.Length <= 160 ? line : line[..157] + "…")}";
    }
}

/// <summary>A script location as a pane sees it: the provider's rows under the listing's columns.</summary>
public sealed class ScriptListingSource(IFileSystemProvider provider, FsUri uri, CommandListing listing) : IListingSource
{
    public FsUri Uri { get; } = uri;

    public string Title => Uri.IsRoot ? listing.Title : (provider as ScriptProvider)?.NameOf(Uri) ?? Uri.Name;

    public ColumnSet Columns { get; } = ScriptProvider.ColumnsFor(listing);

    public bool SupportsNavigation => true;

    public IAsyncEnumerable<FsEntry> ListAsync(CancellationToken ct) => provider.ListAsync(Uri, ct);

    public IDirectoryWatcher? Watch() => null;
}
