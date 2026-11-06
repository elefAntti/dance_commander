using System.Net;
using System.Net.Http.Headers;
using DanceCommander.Core.Fs;

namespace DanceCommander.Providers.WebDav;

/// <summary>
/// A stream whose bytes become the body of a PUT.
/// </summary>
/// <remarks>
/// The provider contract hands back a stream for the caller to write into, and HTTP wants a
/// request whose content it can read. A pipe joins the two, so a file of any size streams through
/// without being held anywhere: the PUT starts as soon as the first bytes arrive and finishes when
/// the stream is disposed.
///
/// The bytes go to a temporary name (<c>name.dc-upload-…</c>), and <see cref="CommitAsync"/> is
/// where the result of the upload appears: the last byte written is not the end of the transfer, the server's answer is,
/// and a Write that returned before the server had spoken would report success for uploads that
/// were refused. Only an upload the server accepted is then moved onto the real name (§1.6);
/// disposing without committing cuts the upload off and removes the temporary file.
///
/// The request can end before the body does: the server refuses early, the connection drops, or
/// the server stops taking data. The writer was then left waiting for room in a pipe nobody would
/// read again, for ever, and a 1.4 GB upload sat at 80% with nothing said. Now the end of the
/// request wakes the writer, and the writer reports why the request ended.
/// </remarks>
internal sealed class DavUploadStream : Stream, ICommittableStream
{
    /// <summary>
    /// How long the server may take to answer once it has the whole body. Longer than
    /// <see cref="DavConnections.Silence"/>, because a server may store or check a large file
    /// before it answers, and running out of it throws away an upload that may have been fine.
    /// </summary>
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(10);

    private readonly Task<HttpResponseMessage> _request;
    private readonly AnonymousPipeContent _content;
    private readonly FsUri _uri;
    private readonly Func<bool, long, CancellationToken, Task> _commit;
    private readonly Func<Task> _abandon;

    /// <summary>
    /// Ends the request: armed by the content while the server is expected to do something, and
    /// cancelled outright to cut the upload off. Never disposed: the content may still touch it
    /// after the request has answered, and it holds nothing a collection doesn't release.
    /// </summary>
    private readonly CancellationTokenSource _abort = new();

    private bool _finished;

    /// <param name="uri">The real name, which is what the messages talk about.</param>
    /// <param name="commit">Moves the accepted upload onto the real name, given how many bytes were sent.</param>
    /// <param name="abandon">Removes whatever the server kept of an upload that was not committed.</param>
    public DavUploadStream(
        HttpClient client, Uri url, long? length, FsUri uri, Func<bool, long, CancellationToken, Task> commit, Func<Task> abandon)
    {
        _uri = uri;
        _commit = commit;
        _abandon = abandon;
        _content = new AnonymousPipeContent(length, _abort);

        // Until the content starts being sent, the limit is on connecting and sending the headers.
        _abort.CancelAfter(DavConnections.Silence);

        var message = new HttpRequestMessage(HttpMethod.Put, url) { Content = _content };

        // Only if nothing is there. The name was checked to be free, but in a separate request;
        // this makes "free" part of the write itself, and covers a server that says "absent"
        // about a file it has. A refusal is a 412, not an overwrite.
        message.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);

        // Asks before sending the body, so a server that will refuse it (too large, not allowed)
        // says so first: a 1.4 GB upload was sent whole only to be answered 413. A server that
        // ignores the question gets the body after a second anyway.
        message.Headers.ExpectContinue = true;
        _request = client.SendAsync(message, HttpCompletionOption.ResponseContentRead, _abort.Token);
        _ = _request.ContinueWith(
            _ =>
            {
                // Over, whichever way: a limit still running would only make a later reason wrong.
                _abort.CancelAfter(Timeout.InfiniteTimeSpan);
                _content.RequestEnded();
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _content.Written;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (_request.IsCompleted || !await _content.Write(buffer, ct).ConfigureAwait(false))
            await ThrowCutOffAsync().ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), ct).AsTask();

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public async Task CommitAsync(bool replace, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _finished = true;

        try
        {
            _content.Complete();
            using (var response = await AnswerAsync(ct).ConfigureAwait(false))
                ThrowIfRefused(response);
            _abort.CancelAfter(Timeout.InfiniteTimeSpan);

            // HTTP/1.1 sends the whole body before it reads an answer, so a success means the
            // server had all of it. Checked anyway, because what goes onto the real name next
            // must never be an upload that is short, whatever the protocol does one day.
            if (!_content.Sent)
                throw new IOException($"The server answered before all of {_uri.Display} had been sent.");

            await _commit(replace, _content.Written, ct).ConfigureAwait(false);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            await AbandonIfOursAsync().ConfigureAwait(false);
            throw;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_finished)
        {
            _finished = true;
            await AbandonAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_finished)
        {
            _finished = true;
            AbandonAsync().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Ends the body with an error rather than completing it, so the server never sees a short
    /// upload as a whole one, then waits for the request to give up and removes the temporary
    /// file.
    /// </summary>
    private async Task AbandonAsync()
    {
        _content.Fail();
        await StopAsync().ConfigureAwait(false);
        await AbandonIfOursAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Cuts the request off and waits until it has stopped. Removing the temporary file while the
    /// PUT was still going could leave the server to write it again; and a server that stopped
    /// taking data would otherwise hold this up until the silence ran out.
    /// </summary>
    /// <summary>
    /// Removes the temporary file, unless the server refused the PUT because something was
    /// already there: then it is someone else's file, and removing it is exactly what the
    /// refusal prevented.
    /// </summary>
    private Task AbandonIfOursAsync() =>
        _request.IsCompletedSuccessfully && _request.Result.StatusCode == HttpStatusCode.PreconditionFailed
            ? Task.CompletedTask
            : _abandon();

    private async Task StopAsync()
    {
        _abort.Cancel();
        try
        {
            (await _request.ConfigureAwait(false)).Dispose();
        }
        catch (Exception)
        {
            // Expected: the request was cut off on purpose.
        }
    }

    /// <summary>The server's answer to the whole body, or why there was none.</summary>
    private async Task<HttpResponseMessage> AnswerAsync(CancellationToken ct)
    {
        try
        {
            return await _request.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new IOException($"The upload of {_uri.Display} did not finish: {WhyEnded(ex)}", ex);
        }
    }

    /// <summary>
    /// For a write that found the request already over: says why, which is never a success,
    /// because the server cannot have accepted a body it has not had all of.
    /// </summary>
    private async Task ThrowCutOffAsync()
    {
        HttpResponseMessage response;
        try
        {
            response = await _request.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"The upload of {_uri.Display} stopped after about {_content.Written:N0} bytes: {WhyEnded(ex)}", ex);
        }

        using (response)
            ThrowIfRefused(response);

        throw new IOException($"The server answered before all of {_uri.Display} had been sent.");
    }

    /// <summary>
    /// The reason in words. Our own limit running out shows up as a cancellation, which says
    /// nothing, and a transfer would take it for the user's; anything else is the innermost cause,
    /// which is the one that names the network's complaint.
    /// </summary>
    private string WhyEnded(Exception ex) =>
        !_abort.IsCancellationRequested ? ex.GetBaseException().Message
        : _content.Sent ? $"the server had not answered {AnswerTimeout.TotalMinutes:0} minutes after receiving all of it."
        : $"the server took nothing for {DavConnections.Silence.TotalSeconds:0} seconds.";

    private void ThrowIfRefused(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    new UnauthorizedAccessException($"Not allowed to write {_uri.Display}."),
                HttpStatusCode.Conflict =>
                    new IOException($"{_uri.Display} cannot be written because its folder does not exist."),
                HttpStatusCode.PreconditionFailed =>
                    new IOException($"Something appeared under the temporary name while uploading {_uri.Display}; nothing was overwritten."),
                HttpStatusCode.ExpectationFailed =>
                    new IOException($"The server, or a proxy before it, refuses uploads that ask first whether they are wanted, so {_uri.Display} was not sent."),
                HttpStatusCode.RequestEntityTooLarge =>
                    new IOException($"The server does not take files as large as {_uri.Display}."),
                (HttpStatusCode)423 =>
                    new IOException($"{_uri.Display} is locked by someone else."),
                (HttpStatusCode)507 =>
                    new IOException($"The server has no room for {_uri.Display}."),
                _ => new IOException(
                    $"The server answered {(int)response.StatusCode} {response.ReasonPhrase} for {_uri.Display}."),
            };
        }
    }

    /// <summary>Request content fed a piece at a time by whoever is writing to the stream.</summary>
    /// <param name="abort">
    /// Armed while the server is the one expected to act, and disarmed while the pipe waits for
    /// our own source: a slow disk is not the server going quiet.
    /// </param>
    private sealed class AnonymousPipeContent(long? length, CancellationTokenSource abort) : HttpContent
    {
        private readonly System.IO.Pipelines.Pipe _pipe = new();
        private volatile bool _over;

        public long Written { get; private set; }

        /// <summary>Whether the whole body has gone to the server.</summary>
        public bool Sent { get; private set; }

        /// <returns>False when the request has ended and nothing will read the pipe again.</returns>
        public async ValueTask<bool> Write(ReadOnlyMemory<byte> buffer, CancellationToken ct)
        {
            var result = await _pipe.Writer.WriteAsync(buffer, ct).ConfigureAwait(false);
            if (result.IsCanceled || result.IsCompleted)
                return false;

            Written += buffer.Length;
            return true;
        }

        /// <summary>
        /// Wakes a writer waiting for room. Safe from any thread, and if no write is waiting, the
        /// next one returns at once instead.
        /// </summary>
        public void RequestEnded()
        {
            _over = true;
            _pipe.Writer.CancelPendingFlush();
        }

        /// <summary>
        /// Starts a limit, unless the request is already over: the handler can finish sending
        /// after the answer has come, and a limit started then would fire on nothing and leave a
        /// later reason saying "timed out".
        /// </summary>
        private void Arm(TimeSpan limit)
        {
            if (!_over)
                abort.CancelAfter(limit);
        }

        public void Complete() => _pipe.Writer.Complete();

        public void Fail() =>
            _pipe.Writer.Complete(new OperationCanceledException("The upload was abandoned before it finished."));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            var reader = _pipe.Reader;
            try
            {
                while (true)
                {
                    abort.CancelAfter(Timeout.InfiniteTimeSpan);
                    var result = await reader.ReadAsync(ct).ConfigureAwait(false);

                    // The handler's write streams ignore their token; the limit acts by ending
                    // the whole request, which is what the abort source is for.
                    foreach (var piece in result.Buffer)
                    {
                        Arm(DavConnections.Silence);
                        await stream.WriteAsync(piece, ct).ConfigureAwait(false);
                    }

                    reader.AdvanceTo(result.Buffer.End);
                    if (result.IsCompleted)
                        break;
                }

                Sent = true;
            }
            finally
            {
                // Sent whole, the server has its answer to give. Not, the handler may still try
                // again on a new connection, and that wait needs a limit as much as the first.
                Arm(Sent ? AnswerTimeout : DavConnections.Silence);

                // Completed rather than failed, so a writer finds the pipe closed and asks the
                // request why; and so a retry that tried to send the body again, which a pipe
                // cannot replay, fails instead of sending only what was left.
                reader.Complete();
            }
        }

        protected override bool TryComputeLength(out long computed)
        {
            // A known length lets the server pre-allocate and lets a proxy avoid chunking; an
            // unknown one is legal and falls back to chunked transfer encoding.
            computed = length ?? 0;
            return length.HasValue;
        }
    }
}
