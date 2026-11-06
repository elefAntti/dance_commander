using System.Text;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Deflate;
using SharpCompress.Compressors.Xz;

namespace DanceCommander.Providers.Archive;

public enum ArchiveKind
{
    /// <summary>Not an archive, or not one we can read.</summary>
    None,
    Zip,
    SevenZip,
    Rar,
    Tar,
    GZip,
    BZip2,
    Xz,
}

/// <summary>
/// What a file actually is, decided by reading it rather than by trusting its name.
/// </summary>
/// <param name="ContainsTar">
/// True when a single-stream compressor turns out to be wrapping a tar — the common
/// <c>.tar.gz</c> shipped as <c>.gz</c>. It changes what browsing the file should show: a tarball
/// is a directory tree, a plain <c>.gz</c> is exactly one file.
/// </param>
/// <param name="InnerName">
/// The original filename recorded inside a gzip header, when it has one. Misnamed files usually
/// still carry the truth here — <c>backup.gz</c> whose header says <c>backup.tar</c>.
/// </param>
public sealed record ArchiveFormat(ArchiveKind Kind, bool ContainsTar = false, string? InnerName = null)
{
    /// <summary>A single compressed stream, which may or may not have a tar inside it.</summary>
    public bool IsSingleStream => Kind is ArchiveKind.GZip or ArchiveKind.BZip2 or ArchiveKind.Xz;

    /// <summary>True when the contents form a directory tree rather than one file.</summary>
    public bool IsTree => !IsSingleStream || ContainsTar;

    public string Label => Kind switch
    {
        ArchiveKind.Zip => "zip",
        ArchiveKind.SevenZip => "7z",
        ArchiveKind.Rar => "rar",
        ArchiveKind.Tar => "tar",
        ArchiveKind.GZip => ContainsTar ? "tar.gz" : "gzip",
        ArchiveKind.BZip2 => ContainsTar ? "tar.bz2" : "bzip2",
        ArchiveKind.Xz => ContainsTar ? "tar.xz" : "xz",
        _ => "",
    };
}

/// <summary>
/// Identifies archives by their content (§6.10). Extensions are a hint people get wrong — a great
/// many <c>.gz</c> files in the wild are tarballs, and some archives have no extension at all —
/// so nothing here looks at the name except to report what the name *should* have been.
/// </summary>
public static class ArchiveSniffer
{
    /// <summary>A tar header block is 512 bytes; everything else we need is in the first few.</summary>
    private const int TarBlock = 512;

    private static ReadOnlySpan<byte> ZipMagic => "PK"u8;
    private static ReadOnlySpan<byte> SevenZipMagic => [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    private static ReadOnlySpan<byte> RarMagic => "Rar!"u8;
    private static ReadOnlySpan<byte> XzMagic => [0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0x00];
    private static ReadOnlySpan<byte> BZip2Magic => "BZh"u8;
    private static ReadOnlySpan<byte> GZipMagic => [0x1F, 0x8B];

    /// <summary>
    /// Reads enough of <paramref name="stream"/> to say what it is, then rewinds it. The stream
    /// must be seekable; a caller with a network stream should materialise it first.
    /// </summary>
    public static async Task<ArchiveFormat> DetectAsync(Stream stream, CancellationToken ct = default)
    {
        if (!stream.CanSeek)
            return new ArchiveFormat(ArchiveKind.None);

        var origin = stream.Position;
        try
        {
            var header = new byte[TarBlock];
            var read = await ReadAtLeastAsync(stream, header, ct).ConfigureAwait(false);

            // All span work happens inside helpers: a Span local may not live across an await.
            var kind = Classify(header.AsSpan(0, read));
            if (kind is ArchiveKind.None or ArchiveKind.Zip or ArchiveKind.SevenZip
                or ArchiveKind.Rar or ArchiveKind.Tar)
            {
                return new ArchiveFormat(kind);
            }

            var innerName = kind == ArchiveKind.GZip ? ReadGZipName(header.AsSpan(0, read)) : null;

            // The whole point: decompress just the first block and look for a tar inside it.
            stream.Position = origin;
            var containsTar = await PeekForTarAsync(stream, kind, ct).ConfigureAwait(false);
            return new ArchiveFormat(kind, containsTar, innerName);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            // A truncated or corrupt file is simply not an archive as far as browsing is concerned.
            return new ArchiveFormat(ArchiveKind.None);
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = origin;
        }
    }

    /// <summary>What the leading bytes alone can tell us.</summary>
    private static ArchiveKind Classify(ReadOnlySpan<byte> span)
    {
        if (StartsWith(span, ZipMagic))
            return ArchiveKind.Zip;
        if (StartsWith(span, SevenZipMagic))
            return ArchiveKind.SevenZip;
        if (StartsWith(span, RarMagic))
            return ArchiveKind.Rar;
        if (StartsWith(span, GZipMagic))
            return ArchiveKind.GZip;
        if (StartsWith(span, BZip2Magic))
            return ArchiveKind.BZip2;
        if (StartsWith(span, XzMagic))
            return ArchiveKind.Xz;

        // An uncompressed tar is recognised by its own header block, not by a leading magic.
        return LooksLikeTar(span) ? ArchiveKind.Tar : ArchiveKind.None;
    }

    /// <summary>Convenience for a path on disk.</summary>
    public static async Task<ArchiveFormat> DetectAsync(string path, CancellationToken ct = default)
    {
        try
        {
            await using var file = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: TarBlock * 2, useAsync: true);
            return await DetectAsync(file, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ArchiveFormat(ArchiveKind.None);
        }
    }

    /// <summary>
    /// Decompresses the first block of a single-stream compressor and asks whether it is a tar
    /// header. Only one block is ever read, so this costs the same on a 50 MB file as on a small
    /// one — it must, because it runs before we know whether the user wants the file opened.
    /// </summary>
    private static async Task<bool> PeekForTarAsync(Stream stream, ArchiveKind kind, CancellationToken ct)
    {
        try
        {
            await using Stream decompressed = kind switch
            {
                ArchiveKind.GZip => new QuietDisposeStream(new GZipStream(new NonClosingStream(stream), CompressionMode.Decompress)),
                ArchiveKind.BZip2 => new QuietDisposeStream(BZip2Stream.Create(
                    new NonClosingStream(stream), CompressionMode.Decompress, decompressConcatenated: false)),
                ArchiveKind.Xz => new QuietDisposeStream(new XZStream(new NonClosingStream(stream))),
                _ => Stream.Null,
            };

            var block = new byte[TarBlock];
            var read = await ReadAtLeastAsync(decompressed, block, ct).ConfigureAwait(false);
            return LooksLikeTar(block.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
                                       or NotSupportedException or ArgumentException)
        {
            // Compressed, but we could not read the payload. Treat it as a single stream; the
            // worst case is one opaque entry rather than a tree, which is still honest.
            return false;
        }
    }

    /// <summary>
    /// A tar has no magic at offset zero. POSIX tars carry "ustar" at 257; older ones carry
    /// nothing, so the header checksum is verified instead — which is what tar itself does.
    /// </summary>
    internal static bool LooksLikeTar(ReadOnlySpan<byte> block)
    {
        if (block.Length < TarBlock)
            return false;

        if (block[257..262].SequenceEqual("ustar"u8))
            return true;

        // Octal checksum at 148..156 over the header with that field read as spaces.
        var stored = ParseOctal(block[148..156]);
        if (stored is null)
            return false;

        var signed = 0;
        for (var i = 0; i < TarBlock; i++)
            signed += i is >= 148 and < 156 ? ' ' : block[i];

        // A name of all-NULs is the end-of-archive block, not a header: reject an empty file.
        return signed == stored && block[0] != 0;
    }

    /// <summary>The original filename a gzip header may carry, which misnamed files still hold.</summary>
    internal static string? ReadGZipName(ReadOnlySpan<byte> header)
    {
        const byte FlagExtra = 0x04, FlagName = 0x08;
        if (header.Length < 10 || (header[3] & FlagName) == 0)
            return null;

        var at = 10;
        if ((header[3] & FlagExtra) != 0)
        {
            if (header.Length < 12)
                return null;
            at += 2 + (header[10] | (header[11] << 8));
        }

        if (at >= header.Length)
            return null;

        var end = header[at..].IndexOf((byte)0);
        if (end <= 0)
            return null;

        var name = Encoding.Latin1.GetString(header.Slice(at, end));
        return string.IsNullOrWhiteSpace(name) ? null : Path.GetFileName(name);
    }

    private static long? ParseOctal(ReadOnlySpan<byte> field)
    {
        long value = 0;
        var digits = 0;
        foreach (var b in field)
        {
            if (b is 0 or (byte)' ')
            {
                if (digits > 0)
                    break;
                continue;
            }
            if (b is < (byte)'0' or > (byte)'7')
                return null;
            value = (value * 8) + (b - '0');
            digits++;
        }
        return digits > 0 ? value : null;
    }

    private static bool StartsWith(ReadOnlySpan<byte> span, ReadOnlySpan<byte> magic) =>
        span.Length >= magic.Length && span[..magic.Length].SequenceEqual(magic);

    /// <summary>Fills the buffer as far as the stream allows; short reads are normal on pipes.</summary>
    private static async Task<int> ReadAtLeastAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }
}
