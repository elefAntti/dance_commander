using System.Collections.Concurrent;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Watching;

namespace DanceCommander.Providers.Archive;

/// <summary>
/// Browsing an archive as if it were a directory (§6.10). The provider never touches a disk path
/// itself: it asks the registry for the container's bytes, so a zip on an SFTP server or inside
/// another zip works with no special case — the container is just another URI.
/// </summary>
/// <param name="providers">
/// Resolved on use rather than injected directly: this provider is itself in the registry, so
/// taking the registry as a constructor argument would be a cycle. By the time any of these
/// methods runs, the registry is fully built.
/// </param>
public sealed class ArchiveProvider(Func<IProviderRegistry> providers, IArchiveCache cache) : IFileSystemProvider
{
    public string Scheme => FsUri.ArchiveScheme;

    /// <summary>
    /// Read-only in v1. Writing into an archive means repacking it, and a file manager that
    /// silently rewrites a 2 GB archive to change one byte is not doing the user a favour.
    /// </summary>
    public ProviderCapabilities Capabilities => ProviderCapabilities.Read | ProviderCapabilities.RandomAccessRead;

    public ColumnSet DefaultColumns => ColumnSet.Standard;

    public async IAsyncEnumerable<FsEntry> ListAsync(
        FsUri directory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var (archive, container) = await OpenAsync(directory, ct).ConfigureAwait(false);
        try
        {
            foreach (var node in archive.List(directory.Path))
            {
                ct.ThrowIfCancellationRequested();
                yield return ToEntry(node, container);
            }
        }
        finally
        {
            archive.Release();
        }
    }

    public async Task<FsEntry?> StatAsync(FsUri path, CancellationToken ct)
    {
        var (archive, container) = await OpenAsync(path, ct).ConfigureAwait(false);
        try
        {
            return archive.Stat(path.Path) is { } node ? ToEntry(node, container) : null;
        }
        finally
        {
            archive.Release();
        }
    }

    public async Task<Stream> OpenReadAsync(FsUri file, CancellationToken ct)
    {
        var (archive, _) = await OpenAsync(file, ct).ConfigureAwait(false);
        try
        {
            // Borrowed until the stream is closed, not just until it is handed over.
            return new LeasedStream(archive.OpenRead(file.Path), archive.Release);
        }
        catch
        {
            archive.Release();
            throw;
        }
    }

    /// <summary>
    /// The format of the archive holding this URI, for callers that want to label it.
    /// </summary>
    public async Task<ArchiveFormat> DescribeAsync(FsUri inside, CancellationToken ct)
    {
        var (archive, _) = await OpenAsync(inside, ct).ConfigureAwait(false);
        try
        {
            return archive.Format;
        }
        finally
        {
            archive.Release();
        }
    }

    /// <summary>The archive, borrowed: every caller releases it when done with it.</summary>
    private async Task<(OpenArchive Archive, FsUri Container)> OpenAsync(FsUri uri, CancellationToken ct)
    {
        if (uri.Inner is not { } container)
            throw new InvalidOperationException("An archive URI must name the file that contains it.");

        var archive = await cache.GetAsync(
            container,
            async token =>
            {
                var provider = providers().Resolve(container);
                var stream = await provider.OpenReadAsync(container, token).ConfigureAwait(false);
                // Asked only of a stream that will be spooled, which is where a short one hides.
                var expected = stream.CanSeek ? null : (await provider.StatAsync(container, token).ConfigureAwait(false))?.Size;
                return await OpenArchive.OpenAsync(stream, container.Name, token, expected).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        return (archive, container);
    }

    private static FsEntry ToEntry(ArchiveNode node, FsUri container) =>
        new(FsUri.ArchiveEntry(container, node.Path),
            node.Name,
            node.IsDirectory,
            node.Size < 0 ? 0 : node.Size,
            node.Modified,
            FsEntryAttributes.None)
        {
            // The file inside a bare .gz or .bz2 has no size until it is decompressed: blank, not "0 B".
            SizeUnknown = node.Size < 0 && !node.IsDirectory,
        };

    // ---- unsupported, and honestly so ---------------------------------------------------

    public Task<Stream> OpenWriteAsync(FsUri file, long? length, CancellationToken ct) =>
        throw Unsupported("write to");

    public Task CreateDirectoryAsync(FsUri directory, CancellationToken ct) =>
        throw Unsupported("create folders in");

    public Task DeleteAsync(FsUri path, bool recursive, CancellationToken ct) =>
        throw Unsupported("delete from");

    public Task MoveAsync(FsUri from, FsUri to, CancellationToken ct) =>
        throw Unsupported("rename inside");

    private static NotSupportedException Unsupported(string verb) =>
        new($"Dance Commander cannot {verb} an archive yet — extract it first.");

    /// <summary>Nothing inside an archive changes unless the archive file itself does (§6.5).</summary>
    public IDirectoryWatcher? Watch(FsUri directory) => null;
}

public interface IArchiveCache
{
    /// <summary>The archive, borrowed for the caller (<see cref="OpenArchive.TryAcquire"/>), who releases it.</summary>
    Task<OpenArchive> GetAsync(FsUri container, Func<CancellationToken, Task<OpenArchive>> open, CancellationToken ct);

    /// <summary>Drops a cached archive, e.g. because the file behind it changed.</summary>
    void Invalidate(FsUri container);
}

/// <summary>
/// Keeps opened archives around so that stepping in and out of folders — or going back — does not
/// re-read the container. Keyed on identity *and* on size and timestamp, so an archive rebuilt on
/// disk is re-read rather than served stale.
/// </summary>
public sealed class ArchiveCache(Func<IProviderRegistry> providers) : IArchiveCache, IDisposable
{
    private const int Capacity = 4;

    private readonly ConcurrentDictionary<string, Entry> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed record Entry(OpenArchive Archive, string Stamp, long Used);

    public async Task<OpenArchive> GetAsync(
        FsUri container, Func<CancellationToken, Task<OpenArchive>> open, CancellationToken ct)
    {
        var key = container.ToString();
        var stamp = await StampAsync(container, ct).ConfigureAwait(false);

        // Borrowed before it is handed out; one evicted meanwhile can't be, and is opened again.
        if (_open.TryGetValue(key, out var cached) && cached.Stamp == stamp && cached.Archive.TryAcquire())
        {
            // Only if it is still the one there: setting it outright put back one just evicted.
            _open.TryUpdate(key, cached with { Used = Stopwatch() }, cached);
            return cached.Archive;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check: another pane may have opened the same archive while we waited.
            if (_open.TryGetValue(key, out cached) && cached.Stamp == stamp && cached.Archive.TryAcquire())
                return cached.Archive;

            // Retired, not disposed under whoever is still reading it: the last of them does that.
            if (cached is not null && _open.TryRemove(key, out var stale))
                stale.Archive.Dispose();

            var archive = await open(ct).ConfigureAwait(false);
            archive.TryAcquire();
            _open[key] = new Entry(archive, stamp, Stopwatch());
            Trim();
            return archive;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate(FsUri container)
    {
        if (_open.TryRemove(container.ToString(), out var entry))
            entry.Archive.Dispose();
    }

    /// <summary>Size and modified time: an archive rewritten in place keeps its name and URI.</summary>
    private async Task<string> StampAsync(FsUri container, CancellationToken ct)
    {
        try
        {
            if (!providers().TryResolve(container, out var provider))
                return string.Empty;
            var entry = await provider.StatAsync(container, ct).ConfigureAwait(false);
            return entry is null ? string.Empty : $"{entry.Size}:{entry.Modified?.UtcTicks ?? 0}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private void Trim()
    {
        while (_open.Count > Capacity)
        {
            var oldest = _open.OrderBy(pair => pair.Value.Used).FirstOrDefault();
            if (oldest.Key is null || !_open.TryRemove(oldest.Key, out var entry))
                return;
            entry.Archive.Dispose();
        }
    }

    private static long Stopwatch() => DateTime.UtcNow.Ticks;

    public void Dispose()
    {
        foreach (var entry in _open.Values)
            entry.Archive.Dispose();
        _open.Clear();
        _gate.Dispose();
    }
}
