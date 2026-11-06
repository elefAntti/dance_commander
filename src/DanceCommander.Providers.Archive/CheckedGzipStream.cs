namespace DanceCommander.Providers.Archive;

/// <summary>
/// A gzip's contents, checked at their end against the file's own trailer: the CRC-32 and the
/// length of everything decompressed.
/// </summary>
/// <remarks>
/// .NET's decompressor checks a member's trailer only when it reaches one: a file cut in half just
/// ends, and copying its entry out made half a file with no error. SharpCompress's caught that but
/// read only the first member of a joined gzip, quietly, and refused one padded with zeros (review,
/// 2026-10-28). So .NET decompresses and this checks: the last trailer must describe all of what
/// was read, allowing for zero padding after it. A joined gzip's last trailer describes only its
/// last member, so one fails here out loud rather than going anywhere short.
/// </remarks>
internal sealed class CheckedGzipStream(Stream source, Stream decompressed) : Stream
{
    private uint _crc = 0xFFFFFFFF;
    private long _length;
    private bool _checked;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var got = decompressed.Read(buffer);
        return Count(buffer[..got], buffer.Length);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var got = await decompressed.ReadAsync(buffer, ct).ConfigureAwait(false);
        return Count(buffer.Span[..got], buffer.Length);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    private int Count(ReadOnlySpan<byte> got, int asked)
    {
        foreach (var b in got)
            _crc = Table[(_crc ^ b) & 0xFF] ^ (_crc >> 8);
        _length += got.Length;

        if (got.Length == 0 && asked > 0 && !_checked)
        {
            _checked = true;
            if (!TrailerMatches(~_crc, (uint)_length))
                throw new InvalidDataException("The compressed file is damaged or cut short: its contents did not check out.");
        }

        return got.Length;
    }

    /// <summary>
    /// Whether the eight bytes ending where the file's zero padding begins, or anywhere inside that
    /// padding (a length's high bytes can be zeros too), are this CRC and length.
    /// </summary>
    private bool TrailerMatches(uint crc, uint length)
    {
        var tail = (int)Math.Min(source.Length, 64 * 1024);
        var bytes = new byte[tail];
        source.Position = source.Length - tail;
        source.ReadExactly(bytes);

        var end = tail;
        while (end > 0 && bytes[end - 1] == 0)
            end--;
        for (var at = Math.Min(tail, end + 8); at >= 8 && at >= end; at--)
        {
            if (BitConverter.ToUInt32(bytes, at - 8) == crc && BitConverter.ToUInt32(bytes, at - 4) == length)
                return true;
        }

        return false;
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            decompressed.Dispose();
        base.Dispose(disposing);
    }
}
