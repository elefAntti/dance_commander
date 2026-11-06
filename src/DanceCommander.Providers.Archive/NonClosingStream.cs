namespace DanceCommander.Providers.Archive;

/// <summary>
/// Passes everything through but refuses to close what it wraps.
/// </summary>
/// <remarks>
/// The decompressors take ownership of their input and close it on dispose, which is wrong here:
/// one archive stream is opened once and read many times, so a caller disposing the decompressed
/// view of a single entry must not take the whole archive down with it. Not every SharpCompress
/// decompressor offers a leaveOpen flag, so the guarantee is made here instead of per call site.
/// </remarks>
internal sealed class NonClosingStream(Stream inner) : Stream
{
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

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

    // The point of the class: dispose stops here.
    protected override void Dispose(bool disposing)
    {
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Delegates everything and refuses to fail on dispose.
/// </summary>
/// <remarks>
/// A decompressor that is deliberately read only part-way — which is exactly what sniffing does —
/// can throw while it tries to validate a trailer it never reached. SharpCompress's GZipStream
/// throws ArgumentException there. That must not be able to change an answer already computed, and
/// it must not surface to a caller who simply stopped reading early, so disposal is swallowed
/// here rather than guarded at every call site.
/// </remarks>
internal sealed class QuietDisposeStream(Stream inner) : Stream
{
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

    // Past the end, the end again: the decompressor is gone by then.
    public override int Read(byte[] buffer, int offset, int count) => _checked ? 0 : AtEnd(inner.Read(buffer, offset, count), count);

    public override int Read(Span<byte> buffer) => _checked ? 0 : AtEnd(inner.Read(buffer), buffer.Length);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        _checked ? 0 : AtEnd(await inner.ReadAsync(buffer, ct).ConfigureAwait(false), buffer.Length);

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    private bool _checked;

    /// <summary>
    /// A read that reached the end is a full read after all, and its trailer is checked there, out
    /// loud, before the caller can take what it read for the whole file: a .gz cut in half read
    /// to a quiet end, and copying its entry out made half a file with no error (BUGS.md).
    /// </summary>
    private int AtEnd(int got, int asked)
    {
        if (got == 0 && asked > 0 && !_checked)
        {
            _checked = true;
            try
            {
                inner.Dispose();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException("The compressed file is damaged or cut short: its contents did not check out.", ex);
            }
        }

        return got;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing || _checked)
            return;

        try
        {
            inner.Dispose();
        }
        catch (Exception)
        {
            // Every exception, deliberately. A decompressor validates its trailer on dispose and
            // can fail in library-specific ways -- SharpCompress throws ZlibException for a CRC it
            // never finished computing and ArgumentException from its buffer handling -- and
            // enumerating those types would mean rediscovering them one unfamiliar file at a time.
            // Nothing here is recoverable and nothing is lost: the stream is being discarded and
            // whatever was read from it has already been used. Only wrap a stream in this when
            // reading it partially is expected; a full read that fails its CRC means real damage
            // and must not be silenced.
        }
    }
}
