using System.Globalization;
using System.Net;
using DanceCommander.Core.Fs;

namespace DanceCommander.Providers.WebDav;

/// <summary>
/// An upload to Nextcloud sent in pieces, the way Nextcloud's own clients send one.
/// </summary>
/// <remarks>
/// A server may cap the size of one request, and Nextcloud's often is: a 1.4 GB file went up
/// whole as a single PUT and was then refused as too large, after every byte had been sent. The
/// pieces go into a folder of their own under <c>/remote.php/dav/uploads/&lt;user&gt;/</c>, and a
/// MOVE of <c>.file</c> has the server join them into the destination in one step, so the
/// destination never holds part of a file.
///
/// Each piece is held in memory until the server has it. That costs a few megabytes, and buys
/// what a streamed body cannot have: a piece that fails on the way can be sent again, a password
/// challenge can be answered by sending it again, and nothing depends on the HTTP handler not
/// resending a body behind our back (AUDIT §2 kind 14). The pieces already on the server are also
/// what resuming an interrupted upload will start from.
///
/// The destination is the provider's temporary name, as for a single PUT; moving that onto the
/// real name, with the size check and the old file set aside, stays where it was.
/// </remarks>
internal sealed class NextcloudChunkedUpload : Stream, ICommittableStream
{
    /// <summary>Files up to this size go as one PUT; one piece of a larger one is at least this.</summary>
    internal const int PieceSize = 10 * 1024 * 1024;

    /// <summary>Nextcloud numbers pieces from 1 to 10 000; kept clear of the end.</summary>
    private const int MostPieces = 9000;

    private const string FilesArea = "/remote.php/dav/files/";

    /// <summary>One piece, at a slow 20 KB/s, with room to spare.</summary>
    private static readonly TimeSpan PieceLimit = TimeSpan.FromMinutes(10);

    /// <summary>Joining the pieces: the server copies the whole file, which takes a while for a large one.</summary>
    private static readonly TimeSpan JoinLimit = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan[] RetryAfter = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)];

    private readonly IDavConnections _connections;
    private readonly DavTarget _target;
    private readonly string _folder;
    private readonly Uri _destination;
    private readonly FsUri _uri;
    private readonly Func<bool, long, CancellationToken, Task> _commit;
    private readonly Func<Task> _abandon;
    private readonly byte[] _piece;
    private int _filled;
    private int _pieces;
    private long _written;
    private bool _finished;

    private NextcloudChunkedUpload(
        IDavConnections connections, DavTarget target, string folder, Uri destination, int pieceSize, FsUri uri,
        Func<bool, long, CancellationToken, Task> commit, Func<Task> abandon)
    {
        _connections = connections;
        _target = target;
        _folder = folder;
        _destination = destination;
        _uri = uri;
        _commit = commit;
        _abandon = abandon;
        _piece = new byte[pieceSize];
    }

    /// <summary>
    /// The folder for one upload's pieces, if <paramref name="path"/> is in a Nextcloud user's
    /// files: <c>/remote.php/dav/files/&lt;user&gt;/…</c>, under whatever prefix the server is
    /// installed at. Null for anything else, which then goes as one PUT.
    /// </summary>
    internal static string? FolderFor(string path)
    {
        var at = path.IndexOf(FilesArea, StringComparison.Ordinal);
        if (at < 0)
            return null;

        var rest = path[(at + FilesArea.Length)..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
            return null;

        return $"{path[..at]}/remote.php/dav/uploads/{rest[..slash]}/dc-{Guid.NewGuid():N}";
    }

    /// <summary>
    /// Makes the folder for the pieces. Null when the server has no such place (not Nextcloud
    /// after all, or uploads in pieces turned off), and the caller sends one PUT instead.
    /// </summary>
    /// <param name="destination">Where the joined file goes: the provider's temporary name.</param>
    public static async Task<NextcloudChunkedUpload?> StartAsync(
        IDavConnections connections, DavTarget target, string folder, string destination, long length, FsUri uri,
        Func<bool, long, CancellationToken, Task> commit, Func<Task> abandon, CancellationToken ct)
    {
        var destinationUrl = target.UrlOf(destination);

        using var response = await connections.SendAsync(
            target,
            () =>
            {
                var request = new HttpRequestMessage(new HttpMethod("MKCOL"), target.UrlOf(folder));

                // Nextcloud 26 and later take this as "the second version of chunking", which
                // also lets a server on object storage pass the pieces straight through.
                request.Headers.Add("Destination", destinationUrl.AbsoluteUri);
                return request;
            },
            ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
            or HttpStatusCode.Forbidden or HttpStatusCode.NotImplemented)
            return null;

        if (!response.IsSuccessStatusCode)
            throw await RefusedAsync(response, uri, "could not start an upload in pieces for").ConfigureAwait(false);

        // Larger pieces for a file that would otherwise need more than Nextcloud numbers.
        var pieceSize = (int)Math.Max(PieceSize, (length + MostPieces - 1) / MostPieces);
        return new NextcloudChunkedUpload(connections, target, folder, destinationUrl, pieceSize, uri, commit, abandon);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _written;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), ct).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_finished, this);

        while (buffer.Length > 0)
        {
            var take = Math.Min(buffer.Length, _piece.Length - _filled);
            buffer[..take].CopyTo(_piece.AsMemory(_filled));
            _filled += take;
            _written += take;
            buffer = buffer[take..];

            if (_filled == _piece.Length)
                await SendPieceAsync(ct).ConfigureAwait(false);
        }
    }

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
            if (_filled > 0)
                await SendPieceAsync(ct).ConfigureAwait(false);

            await JoinAsync(ct).ConfigureAwait(false);

            // The server removes the folder of pieces itself once they are joined.
            await _commit(replace, _written, ct).ConfigureAwait(false);
        }
        catch
        {
            await CleanUpAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Has the server join the pieces onto the temporary name.
    /// </summary>
    /// <remarks>
    /// The total says how much the server must end up with; a piece it lost makes the join fail
    /// rather than produce a shorter file. Joining copies the whole file, and a proxy in front of
    /// the server may give up waiting (a 504) while the server carries on. The temporary file can
    /// then appear after we have cleaned up and found nothing to remove, so the message names it.
    /// </remarks>
    private async Task JoinAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _connections.SendAsync(
                _target,
                () =>
                {
                    var request = new HttpRequestMessage(new HttpMethod("MOVE"), _target.UrlOf($"{_folder}/.file"));
                    request.Headers.Add("Destination", _destination.AbsoluteUri);
                    request.Headers.Add("Overwrite", "F");
                    request.Headers.Add("OC-Total-Length", _written.ToString(CultureInfo.InvariantCulture));
                    return request;
                },
                ct, limit: JoinLimit).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw await RefusedAsync(response, _uri, "did not join the pieces of").ConfigureAwait(false);
        }
        // Not a password refused over dav://: the server turned the join away, so nothing will appear.
        catch (Exception ex) when (ex is IOException or HttpRequestException
                                   && ex is not UnencryptedSignInRefusedException
                                   && (ex is not RefusedException refused || refused.Status >= 500))
        {
            var name = Uri.UnescapeDataString(_destination.Segments[^1]);
            throw new IOException(
                $"{ex.Message} The server may still finish joining it on its own: if {name} appears in the folder, it is ours and can be deleted.",
                ex);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_finished)
        {
            _finished = true;
            await CleanUpAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_finished)
        {
            _finished = true;
            CleanUpAsync().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Sends what is in the buffer as the next piece. A piece the network lost, or one a busy
    /// server failed, is sent again: the same bytes under the same number, so a second copy
    /// replaces the first. A refusal (too large, no room, not allowed) is not retried.
    /// </summary>
    private async Task SendPieceAsync(CancellationToken ct)
    {
        _pieces++;
        var name = _pieces.ToString("D5", CultureInfo.InvariantCulture);
        var url = _target.UrlOf($"{_folder}/{name}");

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await _connections.SendAsync(
                    _target,
                    () =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Put, url)
                        {
                            Content = new ByteArrayContent(_piece, 0, _filled),
                        };
                        request.Headers.Add("Destination", _destination.AbsoluteUri);
                        return request;
                    },
                    ct, limit: PieceLimit).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    break;

                if ((int)response.StatusCode < 500 || attempt >= RetryAfter.Length)
                    throw await RefusedAsync(response, _uri, $"refused piece {_pieces} of", _piece.Length).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException
                                       && ex is not RefusedException
                                       // Refused the same way every time, the piece's bytes sent again for nothing.
                                       && ex is not UnencryptedSignInRefusedException
                                       && attempt < RetryAfter.Length
                                       && !ct.IsCancellationRequested)
            {
                // Lost on the way: wait a little, then the same piece again.
            }

            await Task.Delay(RetryAfter[attempt], ct).ConfigureAwait(false);
        }

        _filled = 0;
    }

    /// <summary>
    /// Removes the pieces and anything joined under the temporary name. Never the real name: that
    /// is touched only by the commit, which moves a whole file onto it.
    /// </summary>
    private async Task CleanUpAsync()
    {
        try
        {
            using var response = await _connections.SendAsync(
                _target, () => new HttpRequestMessage(HttpMethod.Delete, _target.UrlOf(_folder)), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nextcloud removes abandoned upload folders itself after a day.
        }

        await _abandon().ConfigureAwait(false);
    }

    /// <summary>
    /// The refusal in words, with the server's own reason when it gave one. Nextcloud explains in
    /// the body (a 423 names the file that is locked), and "423 Locked" alone left nothing to go on.
    /// </summary>
    /// <param name="pieceSize">What was sent in one request, for a "too large".</param>
    private static async Task<RefusedException> RefusedAsync(
        HttpResponseMessage response, FsUri uri, string what, int pieceSize = PieceSize)
    {
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"Not allowed to write {uri.Display}.",
            HttpStatusCode.RequestEntityTooLarge =>
                $"The server {what} {uri.Display}: even a piece of {pieceSize / (1024 * 1024)} MB is more than it takes in one request.",
            (HttpStatusCode)507 => $"The server has no room for {uri.Display}.",
            _ => $"The server {what} {uri.Display}: it answered {(int)response.StatusCode} {response.ReasonPhrase}.",
        };

        if (await ServerSaysAsync(response).ConfigureAwait(false) is { } reason)
            message += $" It said: {reason}";

        return new RefusedException((int)response.StatusCode, message);
    }

    /// <summary>
    /// The <c>s:message</c> of a Sabre error body, or null if there is none to read. It is the
    /// server's text going into our messages and the log, so it is kept short and on one line,
    /// and a body that is not a small error document is not read at all.
    /// </summary>
    private static async Task<string?> ServerSaysAsync(HttpResponseMessage response)
    {
        const int LargestBody = 64 * 1024;
        const int LongestReason = 300;

        if (response.Content.Headers.ContentLength is not (> 0 and <= LargestBody))
            return null;

        try
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var reader = System.Xml.XmlReader.Create(
                new StringReader(body), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit });
            var text = System.Xml.Linq.XDocument.Load(reader).Root?
                .Element(System.Xml.Linq.XName.Get("message", "http://sabredav.org/ns"))?.Value;
            if (string.IsNullOrWhiteSpace(text))
                return null;

            // Control and formatting characters (new lines, bidi overrides) become spaces, so the
            // server cannot add lines of its own to the log or turn the text around on screen.
            var plain = string.Join(' ', new string(text.Select(c =>
                    char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? ' ' : c)
                .ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return plain.Length <= LongestReason ? plain : plain[..LongestReason] + "…";
        }
        catch (Exception)
        {
            // Not XML, or no body: the status has to do.
            return null;
        }
    }

    /// <summary>The server's own answer, which for a 4xx sending again would not change.</summary>
    private sealed class RefusedException(int status, string message) : IOException(message)
    {
        public int Status { get; } = status;
    }
}
