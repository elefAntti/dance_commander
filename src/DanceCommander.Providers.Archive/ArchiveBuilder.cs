using DanceCommander.Core.Fs;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;

namespace DanceCommander.Providers.Archive;

public interface IArchiveBuilder
{
    /// <summary>
    /// Packs <paramref name="sources"/> into a zip at <paramref name="target"/>, keeping the names
    /// they have now and the shape of any directories among them.
    /// </summary>
    /// <returns>Each file packed, as it was when packed.</returns>
    Task<IReadOnlyList<PackedFile>> CreateZipAsync(
        IReadOnlyList<FsUri> sources, FsUri target, IProgress<ZipProgress>? progress, CancellationToken ct);
}

/// <summary>
/// A file a zip was made from, and its size and time then: what an undo looks for before it takes
/// away a zip that may since have become the only copy.
/// </summary>
public sealed record PackedFile(FsUri Source, long Size, DateTimeOffset? Modified)
{
    /// <summary>
    /// The first of <paramref name="files"/> that is no longer where it was packed from as it was
    /// (gone, a folder now, or another size or time), or null when every one is.
    /// </summary>
    public static async Task<PackedFile?> FirstChangedAsync(
        IProviderRegistry providers, IReadOnlyList<PackedFile> files, CancellationToken ct)
    {
        foreach (var file in files)
        {
            var now = await providers.Resolve(file.Source).StatAsync(file.Source, ct).ConfigureAwait(false);
            if (now is null || now.IsDirectory || now.Size != file.Size || now.Modified != file.Modified)
                return file;
        }

        return null;
    }
}

/// <summary>
/// How far a zip is, 0..1, or null while it is still finding out what goes in; and what it is on.
/// </summary>
/// <remarks>
/// The fraction runs twice: once over the bytes read into the zip, then over the zip written to
/// where it goes, which on a server is an upload long enough to want its own bar.
/// </remarks>
public readonly record struct ZipProgress(double? Fraction, string Detail);

/// <summary>
/// Writes zips (§6.10).
/// </summary>
/// <remarks>
/// Zip only, and deliberately. It is the format every recipient can open without installing
/// anything, and the one SharpCompress writes best; offering a menu of seven formats would mostly
/// be offering six ways to hand someone a file they cannot read. Sources are read through the
/// registry, so packing a folder that lives on a server works the same as packing a local one.
/// </remarks>
public sealed class ArchiveBuilder(Func<IProviderRegistry> providers) : IArchiveBuilder
{
    // Off the caller's thread: the zip writer is synchronous, and a local source can answer every
    // await without yielding, so a large folder packed from a command held the window until done.
    public Task<IReadOnlyList<PackedFile>> CreateZipAsync(
        IReadOnlyList<FsUri> sources, FsUri target, IProgress<ZipProgress>? progress, CancellationToken ct) =>
        Task.Run(() => BuildAsync(sources, target, progress, ct), ct);

    private async Task<IReadOnlyList<PackedFile>> BuildAsync(
        IReadOnlyList<FsUri> sources, FsUri target, IProgress<ZipProgress>? progress, CancellationToken ct)
    {
        var registry = providers();
        var packed = new Tally(progress);
        var files = new List<PackedFile>();

        // Built in a temp file first. Writing straight to the destination would leave a valid-
        // looking half-archive behind if anything failed part way, and the destination may be a
        // server where a partial upload is worse still.
        var temp = Path.Combine(Path.GetTempPath(), $"dc-zip-{Guid.NewGuid():N}.zip");
        var created = false;

        // The library reads each entry's stream while saving. Each opens when it is first read
        // (LazySourceStream), and they are closed together once the archive is written.
        var open = new List<Stream>();

        try
        {
            using (var archive = ZipArchive.CreateArchive())
            {
                foreach (var source in sources)
                {
                    ct.ThrowIfCancellationRequested();
                    await AddAsync(registry, archive, source, prefix: string.Empty, open, packed, files, progress, ct)
                        .ConfigureAwait(false);
                }

                await using var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write);
                created = true;
                archive.SaveTo(file, new ZipWriterOptions(CompressionType.Deflate));
            }

            var provider = registry.Resolve(target);
            await using var input = File.OpenRead(temp);
            var written = new Tally(progress) { Total = input.Length, Detail = $"Writing {target.Name}" };
            written.Add(0);
            await using var output = await provider.OpenWriteAsync(target, input.Length, ct).ConfigureAwait(false);

            // By hand rather than CopyToAsync, to count: to a server this is the upload, and the
            // part of a compress most worth a bar.
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written.Add(read);
            }

            await output.CommitAsync(replace: false, ct).ConfigureAwait(false);
            return files;
        }
        finally
        {
            foreach (var stream in open)
            {
                try
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Closing a source we have already read from cannot change the result.
                }
            }

            try
            {
                // Only a temp this call made: if CreateNew failed, the name was someone else's.
                if (created)
                {
#pragma warning disable RS0030 // §1.6: our own temp zip (a GUID name in the temp folder, made with CreateNew), once uploaded or given up on.
                    File.Delete(temp);
#pragma warning restore RS0030
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task AddAsync(
        IProviderRegistry registry,
        IWritableArchive archive,
        FsUri source,
        string prefix,
        List<Stream> open,
        Tally packed,
        List<PackedFile> files,
        IProgress<ZipProgress>? progress,
        CancellationToken ct)
    {
        var provider = registry.Resolve(source);
        var entry = await provider.StatAsync(source, ct).ConfigureAwait(false);
        if (entry is null)
            return;

        var key = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";

        if (!entry.IsDirectory)
        {
            // Named when the writer reaches it, not now: everything is gathered before anything is
            // packed, and naming files as they were found said "packing" over a folder walk.
            packed.Total += entry.Size;
            var stream = new LazySourceStream(
                token =>
                {
                    packed.Detail = entry.Name;
                    packed.Add(0);
                    return provider.OpenReadAsync(source, token);
                },
                entry.SizeUnknown ? 0 : entry.Size,
                ct,
                packed.Add,
                entry.Name);
            open.Add(stream);
            archive.AddEntry(key, stream, entry.Size, entry.Modified?.UtcDateTime);
            files.Add(new PackedFile(source, entry.Size, entry.Modified));
            return;
        }

        progress?.Report(new ZipProgress(null, $"Looking in {entry.Name}"));

        // An empty folder is not recorded, and cannot be: the library's writer strips the trailing
        // separator that marks a zip entry as a directory, so the entry would come back -- to us
        // and to Explorer alike -- as a zero-byte *file* wearing the folder's name. An omission is
        // recoverable; a file where a directory should be is not.
        await foreach (var child in provider.ListAsync(source, ct).ConfigureAwait(false))
            await AddAsync(registry, archive, child.Uri, key, open, packed, files, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Bytes done of a total, passed on as a fraction only when it has moved a step: the writer
    /// reads in small buffers, and a report per buffer was thousands of repaints for one file.
    /// </summary>
    private sealed class Tally(IProgress<ZipProgress>? progress)
    {
        private const int Steps = 200;

        private long _done;
        private int _step = -1;
        private string? _reported;

        public long Total { get; set; }

        public string Detail { get; set; } = string.Empty;

        public void Add(int bytes)
        {
            if (progress is null)
                return;

            _done += bytes;

            // Past the total too: a writer that seeks back and reads again counts twice.
            var fraction = Total <= 0 ? 1 : Math.Min(1, (double)_done / Total);
            var step = (int)(fraction * Steps);
            if (step == _step && ReferenceEquals(Detail, _reported))
                return;

            _step = step;
            _reported = Detail;
            progress.Report(new ZipProgress(fraction, Detail));
        }
    }
}
