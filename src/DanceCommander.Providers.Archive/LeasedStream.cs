namespace DanceCommander.Providers.Archive;

/// <summary>
/// An entry's stream that keeps something borrowed until it is closed: its archive, so the cache
/// can't dispose it under a copy still reading from it, or the archive's one shared reader.
/// Passes everything else through.
/// </summary>
internal sealed class LeasedStream(Stream inner, Action release) : Stream
{
    private int _released;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => inner.Read(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        inner.ReadAsync(buffer, ct);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        inner.ReadAsync(buffer, offset, count, ct);

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Released even when closing fails: a lease kept would hold the archive's file and
            // spool until exit, or keep its shared reader taken.
            try
            {
                inner.Dispose();
            }
            finally
            {
                ReleaseOnce();
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            ReleaseOnce();
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Once, however it is closed: a second release would hand back someone else's use.</summary>
    private void ReleaseOnce()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            release();
    }
}
