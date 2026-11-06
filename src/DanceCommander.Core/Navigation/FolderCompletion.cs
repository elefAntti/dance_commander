using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Navigation;

/// <summary>
/// The folders on disk that carry on from a path being typed into the address bar: "C:\Users\An"
/// offers the folders in C:\Users whose names start with "An". History only knows places already
/// visited; this knows the ones that exist.
/// </summary>
/// <remarks>
/// Drives and Windows shares only. Listing an SFTP or WebDAV folder to complete a name would
/// connect to it, and maybe ask for a password, in the middle of typing an address that may never
/// be visited. A share is listed as Windows reaches it, which may take a while; it never makes
/// typing wait, only the folders arrive later.
/// </remarks>
public sealed class FolderCompletion(IProviderRegistry providers)
{
    /// <summary>Enough to scroll through; a folder of thousands is a listing, not a suggestion.</summary>
    public const int Max = 20;

    private readonly Lock _gate = new();

    /// <summary>
    /// The folder last listed and the folders in it, kept while the address is being typed so that
    /// each further letter is a filter, not another trip to the disk.
    /// </summary>
    private (FsUri Folder, IReadOnlyList<FsEntry> Folders)? _listed;

    /// <summary>
    /// The folder the text is typing a name in, and the start of that name; null when the text is
    /// not a rooted local path ending in a separator and a partial name.
    /// </summary>
    public static (FsUri Folder, string Prefix)? Split(string text)
    {
        // Expanded as Enter expands it, so "%USERPROFILE%\Do" completes too.
        text = Environment.ExpandEnvironmentVariables(text.TrimStart());

        var cut = text.LastIndexOfAny(['\\', '/']);
        if (cut < 0 || text.Contains("://", StringComparison.Ordinal))
            return null;

        var prefix = text[(cut + 1)..];
        if (prefix is "." or ".." || prefix.IndexOfAny([':', '?', '*', FsUri.ArchiveSeparator]) >= 0)
            return null;

        if (!FsUri.TryParse(text[..(cut + 1)], out var folder, out _)
            || !folder.IsLocal
            || folder.Inner is not null
            || !IsListable(folder.Path))
            return null;

        return (folder, prefix);
    }

    /// <summary>
    /// A drive or a share: "C:/..." or "//server/share/...". Anything else is relative, which means
    /// nothing in an address bar, or a bare server, whose shares are not a directory listing.
    /// </summary>
    private static bool IsListable(string path) =>
        (path.Length >= 3 && path[1] == ':' && path[2] == '/' && char.IsAsciiLetter(path[0]))
        || (path.StartsWith("//", StringComparison.Ordinal) && path.IndexOf('/', 2) > 2);

    /// <summary>
    /// The matches from the folder already listed, or null when it has not been: then
    /// <see cref="ListAsync"/> has to go to the disk first.
    /// </summary>
    public IReadOnlyList<FsUri>? Cached(FsUri folder, string prefix)
    {
        lock (_gate)
            return _listed is { } listed && listed.Folder == folder ? Match(listed.Folders, prefix) : null;
    }

    /// <summary>Lists the folder, keeps it for the letters that follow, and returns the matches.</summary>
    public async Task<IReadOnlyList<FsUri>> ListAsync(FsUri folder, string prefix, CancellationToken ct)
    {
        if (Cached(folder, prefix) is { } cached)
            return cached;

        if (!providers.TryResolve(folder, out var provider))
            return [];

        // On the pool, whatever thread asks: called from the address bar, a provider's own yield
        // would post straight back to the window's thread and list the folder there.
        var folders = await Task.Run(() => FoldersIn(provider, folder, ct), ct).ConfigureAwait(false);

        // A read that failed is not kept: the share that was slow to answer this time may answer
        // the next, and a cached "nothing here" would hide its folders until the address bar is
        // left. Nor is one cancelled meanwhile, which may land after a newer folder's listing.
        if (folders is null)
            return [];
        ct.ThrowIfCancellationRequested();

        folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        lock (_gate)
            _listed = (folder, folders);
        return Match(folders, prefix);
    }

    /// <summary>The folders in the folder, or null when it could not be read.</summary>
    private static async Task<List<FsEntry>?> FoldersIn(IFileSystemProvider provider, FsUri folder, CancellationToken ct)
    {
        var folders = new List<FsEntry>();
        try
        {
            await foreach (var entry in provider.ListAsync(folder, ct).ConfigureAwait(false))
            {
                if (entry.IsDirectory)
                    folders.Add(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A folder that is not there, not ours to read, or holding a name that is no single
            // name here (Child refuses it) has nothing to offer: the address is still being typed,
            // and saying what is wrong with it is Enter's job.
            return null;
        }

        return folders;
    }

    /// <summary>Lets go of the last listing, so the next address typed sees the disk as it is now.</summary>
    public void Forget()
    {
        lock (_gate)
            _listed = null;
    }

    /// <summary>
    /// Folders whose names start with the prefix. Hidden and system ones only once their name has
    /// been begun: "C:\" lists what Explorer would, "C:\$R" still finds "$Recycle.Bin".
    /// </summary>
    private static List<FsUri> Match(IReadOnlyList<FsEntry> folders, string prefix) =>
        folders
            .Where(entry => entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(entry => prefix.Length > 0
                || (entry.Attributes & (FsEntryAttributes.Hidden | FsEntryAttributes.System)) == 0)
            .Take(Max)
            .Select(entry => entry.Uri)
            .ToList();
}
