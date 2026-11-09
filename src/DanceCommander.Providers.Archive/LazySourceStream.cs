namespace DanceCommander.Providers.Archive;

/// <summary>
/// A file to be zipped, opened only when the zip writer first reads it.
/// </summary>
/// <remarks>
/// The writer is handed every entry's stream before it writes any of them. Opened up front, a
/// folder of a thousand files on a server was a thousand requests held open at once, each idle
/// until its turn, long enough for the server to give up on the later ones. Opened here, there is
/// one at a time.
///
/// The writer also seeks each stream to its start before reading it, which a download being read
/// as it arrives cannot do. Seeking to where the stream already is costs nothing and is allowed;
/// anything else is passed on to a source that can seek, or refused.
///
/// Every read is also where the zip can be stopped and counted: the writer that drives it is
/// synchronous and takes no token, so this is the only place a Cancel can reach it mid-file.
///
/// And where a file that ends early is caught. The writer takes the end of the stream for the end
/// of the file, so a download cut short, or a file truncated while it was packed, went into the zip
/// as a shorter file with a CRC of its own short bytes: a whole-looking archive missing the rest.
/// </remarks>
internal sealed class LazySourceStream(
    Func<CancellationToken, Task<Stream>> open, long length, CancellationToken ct, Action<int>? read = null, string? name = null) : Stream
{
    private Stream? _inner;
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _inner is { CanSeek: true } inner ? inner.Length : length;

    public override long Position
    {
        get => _inner is { CanSeek: true } inner ? inner.Position : _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ct.ThrowIfCancellationRequested();
        int got;
        try
        {
            _inner ??= open(ct).GetAwaiter().GetResult();
            got = _inner.Read(buffer, offset, count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Named(ex);
        }
        return Count(got, count);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        ct.ThrowIfCancellationRequested();
        int got;
        try
        {
            _inner ??= await open(ct).ConfigureAwait(false);
            got = await _inner.ReadAsync(buffer, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Named(ex);
        }
        return Count(got, buffer.Length);
    }

    // A connection lost mid-file said only the library's words ("closed before a valid SSH
    // identification string was received"), with no file in them (2026-11-03).
    private IOException Named(Exception ex) =>
        new($"{name ?? "A file"} could not be read after {_position:N0} of its {length:N0} bytes, so it was not packed: {ex.Message}", ex);

    private int Count(int got, int asked)
    {
        if (got == 0 && asked > 0 && _position < length)
            throw new IOException($"{name ?? "A file"} ended after {_position:N0} of its {length:N0} bytes, so it was not packed.");
        _position += got;
        read?.Invoke(got);
        return got;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
        ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (_inner is { CanSeek: true } inner)
            return inner.Seek(offset, origin);

        var to = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset,
        };

        if (to != _position)
            throw new NotSupportedException("This file can only be read from start to end.");
        return _position;
    }

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // Once only: a tar or 7z closes each file as soon as it is written, and the clean-up after
        // the whole archive closes every one again.
        if (disposing)
        {
            _inner?.Dispose();
            _inner = null;
        }

        base.Dispose(disposing);
    }
}
