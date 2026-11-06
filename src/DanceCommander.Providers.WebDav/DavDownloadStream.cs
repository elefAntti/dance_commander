using DanceCommander.Core.Fs;

namespace DanceCommander.Providers.WebDav;

/// <summary>
/// The body of a GET, read as it arrives, failing if the server goes quiet.
/// </summary>
/// <remarks>
/// Once the headers are in, nothing else bounds the body: a connection that stops delivering
/// without closing would leave the reader waiting for ever, which on screen is a copy that stops
/// moving and never says why. So each read is given <see cref="DavConnections.Silence"/>, and
/// running out of it is an IOException with a reason rather than a cancellation, which the
/// transfer would report as the user's own and say nothing. A connection that breaks is said the
/// way an upload's is, and not as .NET's "Unable to read data from the transport connection".
/// </remarks>
internal sealed class DavDownloadStream(HttpResponseMessage response, Stream body, FsUri file) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(DavConnections.Silence);
        try
        {
            var read = await body.ReadAsync(buffer, limit.Token).ConfigureAwait(false);
            _read += read;
            return read;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && limit.IsCancellationRequested)
        {
            throw new IOException(
                $"{file.Display} stopped arriving: the server sent nothing for {DavConnections.Silence.TotalSeconds:0} seconds.", ex);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new IOException(
                $"The download of {file.Display} stopped after about {_read:N0} bytes: {ex.GetBaseException().Message}", ex);
        }
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            body.Dispose();
            response.Dispose();
        }

        base.Dispose(disposing);
    }
}
