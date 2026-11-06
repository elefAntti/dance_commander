using SharpCompress.Archives;
using SharpCompress.Archives.Tar;
using SharpCompress.Common;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Deflate;
using SharpCompress.Compressors.Xz;

namespace DanceCommander.Providers.Archive;

/// <summary>One entry in an archive, already placed in a directory tree.</summary>
public sealed record ArchiveNode(
    string Name, string Path, bool IsDirectory, long Size, DateTimeOffset? Modified);

/// <summary>
/// An archive opened once and indexed into a directory tree, so that navigating inside it costs a
/// dictionary lookup rather than a re-scan (§6.10). Archives are flat lists of full paths; the
/// intermediate directories mostly do not exist as entries and are synthesised here.
/// </summary>
public sealed class OpenArchive : IDisposable
{
    private readonly IArchive? _archive;

    /// <summary>.NET's own reader, for a zip SharpCompress could not read (see <see cref="OpenZipFallback"/>).</summary>
    private readonly System.IO.Compression.ZipArchive? _zip;

    private readonly Stream? _source;
    private readonly string? _tempFile;
    private readonly Func<Stream>? _singleStream;

    /// <summary>Children by parent directory path. "" is the root.</summary>
    private readonly Dictionary<string, List<ArchiveNode>> _children = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ArchiveNode> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public ArchiveFormat Format { get; }

    /// <summary>Entries the archive contained that we refused to show, e.g. paths escaping the root.</summary>
    public int RejectedEntries { get; private set; }

    private OpenArchive(
        ArchiveFormat format, IArchive? archive, Stream? source, string? tempFile, Func<Stream>? singleStream,
        System.IO.Compression.ZipArchive? zip = null)
    {
        Format = format;
        _archive = archive;
        _zip = zip;
        _source = source;
        _tempFile = tempFile;
        _singleStream = singleStream;
    }

    /// <summary>
    /// Opens <paramref name="stream"/>, which this instance takes ownership of.
    /// </summary>
    /// <param name="containerName">
    /// The archive file's own name, used to name the single entry of a plain (non-tar) compressed
    /// file when its header does not carry the original name.
    /// </param>
    /// <param name="expectedLength">
    /// The container's size as listed, for a stream that is spooled: a download cut short ends like
    /// a whole one, and a tar has no directory at its end to say it was cut.
    /// </param>
    public static async Task<OpenArchive> OpenAsync(
        Stream stream, string containerName, CancellationToken ct, long? expectedLength = null)
    {
        var seekable = stream;
        string? temp = null;

        // Everything below needs to seek. A non-seekable source (a stream from a remote provider,
        // or an entry inside another archive) is spooled to a temp file first.
        if (!stream.CanSeek)
        {
            temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dc-arc-{Guid.NewGuid():N}.tmp");
            var spool = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            seekable = spool;
            try
            {
                await stream.CopyToAsync(spool, ct).ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
                if (expectedLength is > 0 and var expected && spool.Length < expected)
                    throw new IOException($"{containerName} arrived as {spool.Length:N0} of its {expected:N0} bytes, so it was not opened.");
            }
            catch
            {
                // The source too: a server's stream left open holds its connection.
                await stream.DisposeAsync().ConfigureAwait(false);
                await spool.DisposeAsync().ConfigureAwait(false);
                Delete(temp);
                throw;
            }
            spool.Position = 0;
        }

        var format = await ArchiveSniffer.DetectAsync(seekable, ct).ConfigureAwait(false);

        try
        {
            if (format.Kind == ArchiveKind.None)
                throw new InvalidDataException("This file is not an archive Dance Commander can read.");

            if (format.IsSingleStream)
            {
                return format.ContainsTar
                    ? await OpenCompressedTarAsync(seekable, format, temp, ct).ConfigureAwait(false)
                    : OpenSingleFile(seekable, format, containerName, temp);
            }

            try
            {
                var archive = ArchiveFactory.OpenArchive(seekable);
                var open = new OpenArchive(format, archive, seekable, temp, null);
                open.Index(archive.Entries.Select(Describe));
                return open;
            }
            catch (Exception ex) when (format.Kind == ArchiveKind.Zip && ex is not OperationCanceledException && seekable.CanSeek)
            {
                return OpenZipFallback(seekable, format, temp);
            }
        }
        catch
        {
            await seekable.DisposeAsync().ConfigureAwait(false);
            Delete(temp);
            throw;
        }
    }

    /// <summary>
    /// A zip SharpCompress fails on, read with .NET's own zip reader instead. Seen with a zip that
    /// sets Zip64's "look in the extended record" marker even on its disk numbers (0xFFFF, with 0
    /// in the Zip64 field), as some streaming writers do: SharpCompress took it for disk 65,536 of
    /// a split archive and indexed past its list of volumes, while 7-Zip and Explorer opened it.
    /// Only a fallback: SharpCompress reads zip variants .NET does not (bzip2 or LZMA inside).
    /// </summary>
    private static OpenArchive OpenZipFallback(Stream seekable, ArchiveFormat format, string? temp)
    {
        seekable.Position = 0;
        var zip = new System.IO.Compression.ZipArchive(seekable, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
        var open = new OpenArchive(format, null, seekable, temp, null, zip);
        open.Index(zip.Entries.Select(entry =>
        {
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var path = Normalize(entry.FullName);
            var name = path.Length == 0 ? string.Empty : path[(path.LastIndexOf('/') + 1)..];
            return new ArchiveNode(name, path, isDirectory, isDirectory ? 0 : entry.Length, entry.LastWriteTime);
        }));
        return open;
    }

    /// <summary>
    /// A tar inside a compressor. The compressed stream cannot seek, and a tar has to be scanned
    /// from the front for every entry, so it is decompressed to a temp file once and read from
    /// there — which is what makes a 50 MB <c>.tar.gz</c> browsable rather than merely listable.
    /// </summary>
    private static async Task<OpenArchive> OpenCompressedTarAsync(
        Stream source, ArchiveFormat format, string? existingTemp, CancellationToken ct)
    {
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dc-tar-{Guid.NewGuid():N}.tar");
        var expanded = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);

        try
        {
            source.Position = 0;
            await using (var decompressed = Decompress(source, format.Kind, partial: false))
                await decompressed.CopyToAsync(expanded, ct).ConfigureAwait(false);

            expanded.Position = 0;
            await source.DisposeAsync().ConfigureAwait(false);
            Delete(existingTemp);

            var archive = TarArchive.OpenArchive(expanded);
            var open = new OpenArchive(format, archive, expanded, temp, null);
            open.Index(archive.Entries.Select(Describe));
            return open;
        }
        catch
        {
            await expanded.DisposeAsync().ConfigureAwait(false);
            Delete(temp);
            throw;
        }
    }

    /// <summary>A compressed single file browses as an archive holding exactly one entry.</summary>
    private static OpenArchive OpenSingleFile(
        Stream source, ArchiveFormat format, string containerName, string? temp)
    {
        var name = format.InnerName ?? StripCompressionSuffix(containerName);

        var open = new OpenArchive(format, null, source, temp, () =>
        {
            source.Position = 0;
            // A caller may read only the head of a file it is previewing, so cleanup stays quiet.
            return Decompress(source, format.Kind, partial: true);
        });

        // The uncompressed size is not knowable without decompressing the whole thing, and
        // claiming a wrong number is worse than admitting we do not know.
        open.Index([new ArchiveNode(name, name, IsDirectory: false, Size: -1, Modified: null)]);
        return open;
    }

    /// <param name="partial">
    /// True when the caller may stop reading early, which makes the decompressor fail its trailer
    /// check on dispose through no fault of the archive. False for a full read, where that check is
    /// the only thing between a corrupt file and a silently truncated listing.
    /// </param>
    private static Stream Decompress(Stream source, ArchiveKind kind, bool partial)
    {
        var guarded = new NonClosingStream(source);
        Stream decompressed = kind switch
        {
            // .NET's own for gzip, checked at the end (CheckedGzipStream says why). Every stream of
            // a joined .bz2 (pbzip2) too, which went short without a word (review, 2026-10-28).
            ArchiveKind.GZip => new CheckedGzipStream(source, new System.IO.Compression.GZipStream(guarded, System.IO.Compression.CompressionMode.Decompress)),
            ArchiveKind.BZip2 => BZip2Stream.Create(guarded, CompressionMode.Decompress, decompressConcatenated: true),
            ArchiveKind.Xz => new XZStream(guarded),
            _ => throw new NotSupportedException($"{kind} is not a single-stream compressor."),
        };

        return partial ? new QuietDisposeStream(decompressed) : decompressed;
    }

    /// <summary>"backup.tar.gz" -> "backup.tar"; "notes.gz" -> "notes"; "opaque" -> "opaque.out".</summary>
    internal static string StripCompressionSuffix(string name)
    {
        foreach (var suffix in (string[])[".gz", ".bz2", ".xz", ".tgz", ".tbz2", ".txz"])
        {
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            var stem = name[..^suffix.Length];
            // .tgz is shorthand for .tar.gz, and the name should say so once unpacked.
            return suffix is ".tgz" or ".tbz2" or ".txz" ? stem + ".tar" : stem;
        }

        return name.Length == 0 ? "contents" : name + ".out";
    }

    private static ArchiveNode Describe(IArchiveEntry entry)
    {
        var key = entry.Key ?? string.Empty;

        // A trailing separator means a directory, whatever the library made of the entry. That is
        // the zip convention for recording an empty folder, and it is the only thing distinguishing
        // one from a zero-byte file.
        var isDirectory = entry.IsDirectory || key.EndsWith('/') || key.EndsWith('\\');

        var path = Normalize(key);
        var name = path.Length == 0 ? string.Empty : path[(path.LastIndexOf('/') + 1)..];
        return new ArchiveNode(name, path, isDirectory, isDirectory ? 0 : entry.Size, entry.LastModifiedTime);
    }

    /// <summary>
    /// Builds the tree, synthesising directories that have no entry of their own and dropping
    /// anything whose path escapes the archive root — a zip is untrusted input, and an entry
    /// named <c>../../autoexec.bat</c> must not be shown, let alone extracted (§11).
    /// </summary>
    private void Index(IEnumerable<ArchiveNode> entries)
    {
        foreach (var entry in entries)
        {
            // Any "..", not only one that climbs above the root: "a/../b" stays inside, but its
            // synthesised folder "a/.." was named "..", which an address resolves to the root, so
            // copying the archive walked root, a, .., root, … without end.
            if (entry.Path.Length == 0 || Escapes(entry.Path) || entry.Path.Split('/').Contains(".."))
            {
                RejectedEntries++;
                continue;
            }

            if (!entry.IsDirectory)
                Add(entry);

            // Every ancestor must exist so the tree can be walked from the root down.
            var path = entry.Path;
            var cut = path.LastIndexOf('/');
            while (cut > 0)
            {
                var directory = path[..cut];
                var name = directory[(directory.LastIndexOf('/') + 1)..];
                if (_byPath.ContainsKey(directory))
                    break;
                Add(new ArchiveNode(name, directory, IsDirectory: true, 0, null));
                path = directory;
                cut = path.LastIndexOf('/');
            }

            if (entry.IsDirectory && !_byPath.ContainsKey(entry.Path))
                Add(entry);
        }
    }

    private void Add(ArchiveNode node)
    {
        if (!_byPath.TryAdd(node.Path, node))
            return;

        var cut = node.Path.LastIndexOf('/');
        var parent = cut <= 0 ? string.Empty : node.Path[..cut];
        if (!_children.TryGetValue(parent, out var list))
            _children[parent] = list = [];
        list.Add(node);
    }

    /// <summary>Children of a directory; the root is "" or "/".</summary>
    public IReadOnlyList<ArchiveNode> List(string directory) =>
        _children.TryGetValue(Normalize(directory), out var list) ? list : [];

    public bool IsDirectory(string path) =>
        Normalize(path).Length == 0 || (_byPath.TryGetValue(Normalize(path), out var n) && n.IsDirectory);

    public ArchiveNode? Stat(string path)
    {
        var key = Normalize(path);
        if (key.Length == 0)
            return new ArchiveNode("/", string.Empty, IsDirectory: true, 0, null);
        return _byPath.GetValueOrDefault(key);
    }

    /// <summary>
    /// An entry's contents. Every entry of one archive reads through the one source stream, and
    /// opening an entry moves its position: a second entry opened while the first was still being
    /// read (F5 on an entry the preview had open) made each read the other's bytes, and the copy
    /// looked whole (AUDIT.md §2 kind 47). So one reader at a time shares the source, and any other
    /// opens the file again for itself.
    /// </summary>
    public Stream OpenRead(string path)
    {
        bool shared;
        lock (_life)
        {
            shared = !_shared;
            _shared = true;
        }

        if (!shared)
            return OpenPrivate(path);

        try
        {
            return new LeasedStream(OpenShared(path), () => { lock (_life) _shared = false; });
        }
        catch
        {
            lock (_life)
                _shared = false;
            throw;
        }
    }

    /// <summary>Whether the source stream is in use by an entry's reader.</summary>
    private bool _shared;

    private Stream OpenShared(string path)
    {
        var key = Normalize(path);

        if (_singleStream is not null)
            return _byPath.ContainsKey(key) ? _singleStream() : throw FileNotFound(path);

        if (_zip is not null)
        {
            var found = _zip.Entries.FirstOrDefault(
                e => !e.FullName.EndsWith('/') && string.Equals(Normalize(e.FullName), key, StringComparison.OrdinalIgnoreCase));
            return found?.Open() ?? throw FileNotFound(path);
        }

        var entry = _archive?.Entries.FirstOrDefault(
            e => !e.IsDirectory && string.Equals(Normalize(e.Key ?? string.Empty), key, StringComparison.OrdinalIgnoreCase));

        return entry?.OpenEntryStream() ?? throw FileNotFound(path);
    }

    /// <summary>
    /// The entry read from a handle of its own on the same file, opened again as it was the first
    /// time. Slower (a zip's directory is read again, a tar scanned), so only while another reader
    /// has the shared one. A source that is not a file (an SFTP stream, which can seek) refuses instead.
    /// </summary>
    private Stream OpenPrivate(string path)
    {
        if (_source is not FileStream { Name: var file })
            throw new IOException("Another file in this archive is being read; try again when that finishes.");

        var key = Normalize(path);
        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        IDisposable? owner = null;
        try
        {
            Stream entry;
            if (_singleStream is not null)
            {
                entry = _byPath.ContainsKey(key) ? Decompress(stream, Format.Kind, partial: true) : throw FileNotFound(path);
            }
            else if (_zip is not null)
            {
                var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
                owner = zip;
                entry = zip.Entries.FirstOrDefault(
                        e => !e.FullName.EndsWith('/') && string.Equals(Normalize(e.FullName), key, StringComparison.OrdinalIgnoreCase))
                    ?.Open() ?? throw FileNotFound(path);
            }
            else
            {
                // A compressed tar's source is its decompressed spool, a plain tar.
                var archive = Format.IsSingleStream ? TarArchive.OpenArchive(stream) : ArchiveFactory.OpenArchive(stream);
                owner = archive;
                entry = archive.Entries.FirstOrDefault(
                        e => !e.IsDirectory && string.Equals(Normalize(e.Key ?? string.Empty), key, StringComparison.OrdinalIgnoreCase))
                    ?.OpenEntryStream() ?? throw FileNotFound(path);
            }

            var held = owner;
            return new LeasedStream(entry, () =>
            {
                held?.Dispose();
                stream.Dispose();
            });
        }
        catch
        {
            owner?.Dispose();
            stream.Dispose();
            throw;
        }
    }

    private static FileNotFoundException FileNotFound(string path) =>
        new($"'{path}' is not in this archive.");

    /// <summary>
    /// Forward slashes, no leading or trailing separator, and no "." segments. That last part
    /// matters more than it looks: <c>tar -C dir .</c> writes every entry as <c>./name</c>, which
    /// is how a large share of real tarballs are built, and treating that prefix as a directory
    /// files the whole archive under a folder called "." that a root listing never shows.
    /// </summary>
    internal static string Normalize(string path)
    {
        var segments = path.Replace('\\', '/').Split('/');
        var kept = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment.Length > 0 && segment != ".")
                kept.Add(segment);
        }

        return string.Join('/', kept);
    }

    /// <summary>True when a path climbs above the archive root at any point.</summary>
    internal static bool Escapes(string path)
    {
        var depth = 0;
        foreach (var segment in path.Split('/'))
        {
            if (segment is "." or "")
                continue;
            if (segment == "..")
            {
                if (--depth < 0)
                    return true;
                continue;
            }
            depth++;
        }
        return false;
    }

    private static void Delete(string? path)
    {
        if (path is null)
            return;
        try
        {
#pragma warning disable RS0030 // §1.6: our own spool of an archive's contents in the temp folder, made with CreateNew.
            File.Delete(path);
#pragma warning restore RS0030
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---- lifetime -------------------------------------------------------------------------

    private readonly object _life = new();
    private int _users;
    private bool _retired;
    private bool _disposed;

    /// <summary>
    /// Borrows the archive for one use: a listing, a stat, a stream read to its end. False once it
    /// has been disposed, in which case it has to be opened again. The cache used to dispose an
    /// archive it evicted while another pane was still listing it, or an extraction still reading
    /// from it (§4, audit round 3).
    /// </summary>
    public bool TryAcquire()
    {
        lock (_life)
        {
            // Retired is as good as gone for a new use: the cache has let it go, and handing it out
            // again kept a file handle on the archive open after it had been replaced.
            if (_disposed || _retired)
                return false;
            _users++;
            return true;
        }
    }

    /// <summary>The end of one use. The last one out of a retired archive disposes it.</summary>
    public void Release()
    {
        lock (_life)
        {
            _users--;
            if (!_retired || _users > 0 || _disposed)
                return;
            _disposed = true;
        }

        DisposeNow();
    }

    /// <summary>
    /// No new uses: disposed now if nobody has it, otherwise when the last one releases it. What
    /// the cache does on evicting it, and what disposing it means, so a plain <c>using</c> is safe.
    /// </summary>
    public void Dispose()
    {
        lock (_life)
        {
            _retired = true;
            if (_users > 0 || _disposed)
                return;
            _disposed = true;
        }

        DisposeNow();
    }

    private void DisposeNow()
    {
        _archive?.Dispose();
        _zip?.Dispose();
        _source?.Dispose();
        Delete(_tempFile);
    }
}
