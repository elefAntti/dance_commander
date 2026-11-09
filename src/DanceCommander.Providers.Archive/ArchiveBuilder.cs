using DanceCommander.Core.Fs;
using DanceCommander.Core.Operations;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using SharpCompress.Writers.Tar;
using SharpCompress.Writers.Zip;

namespace DanceCommander.Providers.Archive;

public interface IArchiveBuilder
{
    /// <summary>
    /// Packs <paramref name="sources"/> into an archive of <paramref name="format"/> at
    /// <paramref name="target"/>, keeping the names they have now and the shape of any directories
    /// among them.
    /// </summary>
    /// <returns>Each file packed, as it was when packed.</returns>
    Task<IReadOnlyList<PackedFile>> CreateAsync(
        IReadOnlyList<FsUri> sources, FsUri target, PackFormat format, IProgress<ArchiveProgress>? progress, CancellationToken ct);
}

/// <summary>The kinds of archive Compress writes.</summary>
public enum PackFormat
{
    Zip,
    SevenZip,
    TarGz,
}

public static class PackFormats
{
    /// <summary>
    /// The extensions that name each format, the one Compress adds first. Longest first, so
    /// <c>.tar.gz</c> is found before anything shorter that ends the same way.
    /// </summary>
    public static IReadOnlyList<(string Extension, PackFormat Format)> Extensions { get; } =
    [
        (".tar.gz", PackFormat.TarGz),
        (".tgz", PackFormat.TarGz),
        (".zip", PackFormat.Zip),
        (".7z", PackFormat.SevenZip),
    ];

    /// <summary>The format <paramref name="name"/>'s extension asks for, or null when it names none.</summary>
    public static PackFormat? FromName(string name)
    {
        foreach (var (extension, format) in Extensions)
        {
            if (name.Length > extension.Length && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return format;
        }

        return null;
    }

    public static string ExtensionOf(PackFormat format) => format switch
    {
        PackFormat.SevenZip => ".7z",
        PackFormat.TarGz => ".tar.gz",
        _ => ".zip",
    };

    /// <summary>The command that makes the same archive of this folder's contents, for the log.</summary>
    public static string ShellFor(PackFormat format, string name)
    {
        // Quoted for spaces, and a name starting with a dash given as ./ so it can't be read as
        // an option if the line is run (review, 2026-11-08).
        var file = ShellText.Quote(name.StartsWith('-') ? $"./{name}" : name);
        return format switch
        {
            PackFormat.SevenZip => $"7z a {file} .",
            PackFormat.TarGz => $"tar -czf {file} .",
            _ => $"zip -r {file} .",
        };
    }
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
public readonly record struct ArchiveProgress(double? Fraction, string Detail);

/// <summary>
/// Writes zips, 7z archives and gzipped tarballs (§6.10).
/// </summary>
/// <remarks>
/// Zip is the default, since every recipient can open it without installing anything; 7z and
/// .tar.gz are there for when the user knows who gets the file (a server, a colleague with 7-Zip).
/// Sources are read through the registry, so packing a folder that lives on a server works the
/// same as packing a local one.
/// </remarks>
public sealed class ArchiveBuilder(Func<IProviderRegistry> providers) : IArchiveBuilder
{
    // Off the caller's thread: the writers are synchronous, and a local source can answer every
    // await without yielding, so a large folder packed from a command held the window until done.
    public Task<IReadOnlyList<PackedFile>> CreateAsync(
        IReadOnlyList<FsUri> sources, FsUri target, PackFormat format, IProgress<ArchiveProgress>? progress, CancellationToken ct) =>
        Task.Run(() => BuildAsync(sources, target, format, progress, ct), ct);

    private async Task<IReadOnlyList<PackedFile>> BuildAsync(
        IReadOnlyList<FsUri> sources, FsUri target, PackFormat format, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        var registry = providers();
        var packed = new Tally(progress);
        var files = new List<PackedFile>();

        // Built in a temp file first. Writing straight to the destination would leave a valid-
        // looking half-archive behind if anything failed part way, and the destination may be a
        // server where a partial upload is worse still.
        var temp = Path.Combine(Path.GetTempPath(), $"dc-pack-{Guid.NewGuid():N}.tmp");
        var created = false;

        // Each entry's stream opens when it is first read (LazySourceStream), and they are closed
        // together once the archive is written.
        var open = new List<Stream>();
        var entries = new List<Entry>();

        try
        {
            foreach (var source in sources)
            {
                ct.ThrowIfCancellationRequested();
                await AddAsync(registry, entries, source, prefix: string.Empty, open, packed, files, progress, ct)
                    .ConfigureAwait(false);
            }

            // Two files picked in a flat or search view can share a name. The zip writer refused a
            // second entry of one name, but the tar and 7z writers keep both, and then the archive
            // shows one and extracting it puts one over the other (review, 2026-11-08). Asked
            // here, before anything is written, for every format alike; without case, because
            // Windows unpacks README and readme to the same file.
            if (entries.GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
                throw new IOException($"Two files would both be {twice.Key} in the archive. Rename one, or compress them separately.");

            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                created = true;
                if (format == PackFormat.Zip)
                    WriteZip(entries, file);
                else
                    WriteStreamed(entries, file, format);
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
#pragma warning disable RS0030 // §1.6: our own temp archive (a GUID name in the temp folder, made with CreateNew), once uploaded or given up on.
                    File.Delete(temp);
#pragma warning restore RS0030
                }
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A file to pack: its name in the archive, and its contents to be read when reached.</summary>
    private sealed record Entry(string Key, LazySourceStream Stream, FsEntry Source);

    // The zip writer is handed every entry before it writes any, and reads each to its end.
    private static void WriteZip(List<Entry> entries, Stream file)
    {
        using var archive = ZipArchive.CreateArchive();
        foreach (var entry in entries)
            archive.AddEntry(entry.Key, entry.Stream, entry.Source.Size, entry.Source.Modified?.UtcDateTime);
        archive.SaveTo(file, new ZipWriterOptions(CompressionType.Deflate));
    }

    // The tar and 7z writers take one file at a time, so each is closed before the next opens.
    private static void WriteStreamed(List<Entry> entries, Stream file, PackFormat format)
    {
        using IWriter writer = format switch
        {
            PackFormat.TarGz => new TarWriter(file, new TarWriterOptions(CompressionType.GZip, finalizeArchiveOnClose: true) { LeaveStreamOpen = true }),
            _ => new SevenZipWriter(file, new SevenZipWriterOptions { LeaveStreamOpen = true }),
        };

        foreach (var entry in entries)
        {
            // A tar header gives the size before the contents, and the 7z writer takes a length of
            // nothing for an empty file and reads none of it, so both need the size first.
            if (entry.Source.SizeUnknown)
                throw new IOException($"The size of {entry.Source.Name} is not known, which a {(format == PackFormat.TarGz ? ".tar.gz" : ".7z")} needs. A .zip can hold it.");

            if (writer is TarWriter tar)
            {
                tar.Write(entry.Key, entry.Stream, entry.Source.Modified?.UtcDateTime, entry.Source.Size);
            }
            else
            {
                writer.Write(entry.Key, entry.Stream, entry.Source.Modified?.UtcDateTime);
            }

            // A tar copies exactly the size its header gave, so a file that grew while it was packed
            // went in cut at that size, with nothing to say so. Anything left to read is that.
            if (entry.Stream.ReadByte() >= 0)
                throw new IOException($"{entry.Source.Name} grew while it was packed, so it was not packed.");
            entry.Stream.Dispose();
        }
    }

    private static async Task AddAsync(
        IProviderRegistry registry,
        List<Entry> entries,
        FsUri source,
        string prefix,
        List<Stream> open,
        Tally packed,
        List<PackedFile> files,
        IProgress<ArchiveProgress>? progress,
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
            entries.Add(new Entry(key, stream, entry));
            files.Add(new PackedFile(source, entry.Size, entry.Modified));
            return;
        }

        progress?.Report(new ArchiveProgress(null, $"Looking in {entry.Name}"));

        // An empty folder is not recorded, and cannot be: the library's writer strips the trailing
        // separator that marks a zip entry as a directory, so the entry would come back -- to us
        // and to Explorer alike -- as a zero-byte *file* wearing the folder's name. An omission is
        // recoverable; a file where a directory should be is not.
        await foreach (var child in provider.ListAsync(source, ct).ConfigureAwait(false))
            await AddAsync(registry, entries, child.Uri, key, open, packed, files, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Bytes done of a total, passed on as a fraction only when it has moved a step: the writer
    /// reads in small buffers, and a report per buffer was thousands of repaints for one file.
    /// </summary>
    private sealed class Tally(IProgress<ArchiveProgress>? progress)
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
            progress.Report(new ArchiveProgress(fraction, Detail));
        }
    }
}
