using System.Net;
using DanceCommander.Core.Fs;
using DanceCommander.Providers.WebDav;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// Uploads to Nextcloud in pieces, against a connection that plays the server and writes down
/// every request. The case that started it: a 1.4 GB file sent as one PUT and refused as too large
/// only after all of it had gone.
/// </summary>
public sealed class NextcloudChunkedUploadTests
{
    private const int Piece = NextcloudChunkedUpload.PieceSize;
    private const string Folder = "/remote.php/dav/uploads/antti/dc-1";
    private const string Temporary = "/remote.php/dav/files/antti/Images/big.iso.dc-upload-1a2b3c4d";

    private static readonly DavTarget Hub = DavTarget.Parse("hub.example.com", secure: true);
    private static readonly FsUri File = FsUri.Parse("davs://hub.example.com/remote.php/dav/files/antti/Images/big.iso");

    private sealed record Sent(string Method, string Path, string? Destination, string? Total, int Body);

    private sealed class Server(Func<Sent, int, HttpStatusCode> answer) : IDavConnections
    {
        public List<Sent> Requests { get; } = [];

        /// <summary>What a refusal says in its body, as Nextcloud explains one.</summary>
        public string? ErrorBody { get; init; }

        public async Task<HttpResponseMessage> SendAsync(
            DavTarget target, Func<HttpRequestMessage> make, CancellationToken ct,
            HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, TimeSpan? limit = null)
        {
            var request = make();
            var sent = new Sent(
                request.Method.Method,
                Uri.UnescapeDataString(request.RequestUri!.AbsolutePath),
                request.Headers.TryGetValues("Destination", out var to) ? to.Single() : null,
                request.Headers.TryGetValues("OC-Total-Length", out var total) ? total.Single() : null,
                request.Content is null ? 0 : (await request.Content.ReadAsByteArrayAsync(ct)).Length);

            Requests.Add(sent);
            var status = answer(sent, Requests.Count(r => r == sent));
            var response = new HttpResponseMessage(status);
            if (ErrorBody is not null && (int)status >= 400)
                response.Content = new StringContent(ErrorBody);
            return response;
        }

        public Task<HttpClient> ClientFor(DavTarget target, CancellationToken ct) => throw new NotSupportedException();

        public void Forget(DavTarget target) { }
    }

    private static async Task<(NextcloudChunkedUpload? Upload, List<string> Calls)> StartAsync(Server server, long length)
    {
        var calls = new List<string>();
        var upload = await NextcloudChunkedUpload.StartAsync(
            server, Hub, Folder, Temporary, length, File,
            commit: (_, written, _) =>
            {
                calls.Add($"commit {written}");
                return Task.CompletedTask;
            },
            abandon: () =>
            {
                calls.Add("abandon");
                return Task.CompletedTask;
            },
            CancellationToken.None);
        return (upload, calls);
    }

    private static async Task WriteAsync(Stream upload, long length)
    {
        var buffer = new byte[1024 * 1024];
        for (long left = length; left > 0; left -= buffer.Length)
            await upload.WriteAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)));
    }

    [Theory]
    [InlineData("/remote.php/dav/files/antti.valli/Access/Linux Images/x.iso", "/remote.php/dav/uploads/antti.valli/dc-")]
    [InlineData("/cloud/remote.php/dav/files/u/x.iso", "/cloud/remote.php/dav/uploads/u/dc-")]
    public void APathInAUsersFilesHasAFolderForPieces(string path, string expected) =>
        NextcloudChunkedUpload.FolderFor(path)!.ShouldStartWith(expected);

    [Theory]
    [InlineData("/dav/share/x.iso")]
    [InlineData("/remote.php/webdav/x.iso")]
    [InlineData("/remote.php/dav/files/u")]
    public void AnythingElseGoesAsOnePut(string path) =>
        NextcloudChunkedUpload.FolderFor(path).ShouldBeNull();

    [Fact]
    public async Task AFileGoesInPiecesAndIsJoinedOntoTheTemporaryName()
    {
        var server = new Server((_, _) => HttpStatusCode.Created);
        var length = 2L * Piece + 5;
        var (upload, calls) = await StartAsync(server, length);

        await WriteAsync(upload!, length);
        await upload!.CommitAsync(replace: false, CancellationToken.None);

        var destination = Hub.UrlOf(Temporary).AbsoluteUri;
        server.Requests.ShouldBe(
        [
            new("MKCOL", Folder, destination, null, 0),
            new("PUT", $"{Folder}/00001", destination, null, Piece),
            new("PUT", $"{Folder}/00002", destination, null, Piece),
            new("PUT", $"{Folder}/00003", destination, null, 5),
            new("MOVE", $"{Folder}/.file", destination, length.ToString(), 0),
        ]);

        // The provider's commit, with its size check and its care for an old file, comes last.
        calls.ShouldBe([$"commit {length}"]);
    }

    [Fact]
    public async Task AServerWithoutAPlaceForPiecesGetsOnePut()
    {
        var (upload, _) = await StartAsync(new Server((_, _) => HttpStatusCode.NotFound), 3L * Piece);
        upload.ShouldBeNull();
    }

    [Fact]
    public async Task APieceTheServerFailedIsSentAgain()
    {
        // The first try of piece 1 meets a busy server; the second gets through.
        var server = new Server((sent, times) =>
            sent.Path.EndsWith("/00001", StringComparison.Ordinal) && times == 1
                ? HttpStatusCode.ServiceUnavailable
                : HttpStatusCode.Created);
        var (upload, calls) = await StartAsync(server, Piece + 1);

        await WriteAsync(upload!, Piece + 1);
        await upload!.CommitAsync(replace: false, CancellationToken.None);

        server.Requests.Count(r => r.Path.EndsWith("/00001", StringComparison.Ordinal)).ShouldBe(2);
        calls.ShouldBe([$"commit {Piece + 1}"]);
    }

    [Fact]
    public async Task ARefusedPieceEndsTheUploadAndRemovesWhatWasSent()
    {
        var server = new Server((sent, _) =>
            sent.Method == "PUT" ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.Created);
        var (upload, calls) = await StartAsync(server, 2L * Piece);

        var thrown = await Should.ThrowAsync<IOException>(() => WriteAsync(upload!, 2L * Piece));
        thrown.Message.ShouldContain("more than it takes");

        // Not sent again: a refusal would only be refused again.
        server.Requests.Count(r => r.Method == "PUT").ShouldBe(1);

        await upload!.DisposeAsync();
        server.Requests[^1].ShouldBe(new Sent("DELETE", Folder, null, null, 0));
        calls.ShouldBe(["abandon"]);
    }

    [Fact]
    public async Task AJoinAProxyGaveUpOnNamesTheFileThatMayStillAppear()
    {
        // A 504 from the proxy while the server carries on joining: the file may land after we
        // have looked, so the message says what to look for.
        var server = new Server((sent, _) =>
            sent.Method == "MOVE" ? HttpStatusCode.GatewayTimeout : HttpStatusCode.Created);
        var (upload, calls) = await StartAsync(server, Piece + 1);

        await WriteAsync(upload!, Piece + 1);
        var thrown = await Should.ThrowAsync<IOException>(() => upload!.CommitAsync(replace: false, CancellationToken.None));

        thrown.Message.ShouldContain("big.iso.dc-upload-1a2b3c4d");
        calls.ShouldBe(["abandon"]);
    }

    [Fact]
    public async Task ARefusalCarriesTheServersOwnReason()
    {
        // As Nextcloud 34 answered the hub's join: "423 Locked" and nothing else to go on, while
        // the body said what was locked.
        var server = new Server((sent, _) => sent.Method == "MOVE" ? (HttpStatusCode)423 : HttpStatusCode.Created)
        {
            ErrorBody = """
                <?xml version="1.0" encoding="utf-8"?>
                <d:error xmlns:d="DAV:" xmlns:s="http://sabredav.org/ns">
                  <s:exception>OCA\DAV\Connector\Sabre\Exception\FileLocked</s:exception>
                  <s:message>"Images/big.iso" is locked</s:message>
                </d:error>
                """,
        };
        var (upload, _) = await StartAsync(server, Piece + 1);

        await WriteAsync(upload!, Piece + 1);
        var thrown = await Should.ThrowAsync<IOException>(() => upload!.CommitAsync(replace: false, CancellationToken.None));

        thrown.Message.ShouldContain("It said: \"Images/big.iso\" is locked");
    }

    [Fact]
    public async Task AJoinThatFailsRemovesThePiecesAndCommitsNothing()
    {
        var server = new Server((sent, _) =>
            sent.Method == "MOVE" ? HttpStatusCode.BadRequest : HttpStatusCode.Created);
        var (upload, calls) = await StartAsync(server, Piece + 1);

        await WriteAsync(upload!, Piece + 1);
        await Should.ThrowAsync<IOException>(() => upload!.CommitAsync(replace: false, CancellationToken.None));

        server.Requests[^1].Method.ShouldBe("DELETE");
        calls.ShouldBe(["abandon"]);
    }
}
