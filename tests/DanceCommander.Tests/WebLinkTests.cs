using System.Net;
using DanceCommander.Core.Fs;
using DanceCommander.Providers.WebDav;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>Copy web link: a Nextcloud file's page in the server's own web interface.</summary>
public sealed class WebLinkTests
{
    private sealed class Server(string properties) : IDavConnections
    {
        public Task<HttpResponseMessage> SendAsync(
            DavTarget target, Func<HttpRequestMessage> make, CancellationToken ct,
            HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, TimeSpan? limit = null)
        {
            var request = make();
            var body = $"""
                <?xml version="1.0"?>
                <d:multistatus xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns">
                  <d:response>
                    <d:href>{request.RequestUri!.AbsolutePath}</d:href>
                    <d:propstat><d:prop>{properties}</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
                  </d:response>
                </d:multistatus>
                """;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)207) { Content = new StringContent(body) });
        }

        public Task<HttpClient> ClientFor(DavTarget target, CancellationToken ct) => throw new NotSupportedException();

        public void Forget(DavTarget target) { }
    }

    private static Task<Uri?> LinkAsync(string properties, string path = "/remote.php/dav/files/antti/Images/big.iso") =>
        new WebDavSecureFileSystemProvider(new Server(properties))
            .WebLinkAsync(FsUri.Parse($"davs://hub.example.com{path}"), CancellationToken.None);

    [Fact]
    public async Task TheServersOwnLinkIsUsed() =>
        (await LinkAsync("<oc:privatelink>https://hub.example.com/f/42</oc:privatelink><oc:fileid>42</oc:fileid>"))!
            .AbsoluteUri.ShouldBe("https://hub.example.com/f/42");

    [Fact]
    public async Task ALinkToAnotherServerIsNotTheFilesPage() =>
        // Whatever the server says, the clipboard gets this server's page, made from the number.
        (await LinkAsync("<oc:privatelink>https://elsewhere.example.com/f/42</oc:privatelink><oc:fileid>42</oc:fileid>"))!
            .AbsoluteUri.ShouldBe("https://hub.example.com/f/42");

    [Fact]
    public async Task TheNumberAloneMakesALinkUnderTheInstallationsPrefix() =>
        (await LinkAsync("<oc:fileid>7</oc:fileid>", "/cloud/remote.php/dav/files/u/a.txt"))!
            .AbsoluteUri.ShouldBe("https://hub.example.com/cloud/f/7");

    [Fact]
    public async Task NeitherMeansNoLink() =>
        (await LinkAsync("<oc:fileid>not-a-number</oc:fileid>")).ShouldBeNull();

    [Theory]
    [InlineData("/remote.php/dav/files/antti/a.iso", true)]
    [InlineData("/dav/share/a.iso", false)]
    public void OnlyANextcloudUsersFilesMayHaveOne(string path, bool expected) =>
        new WebDavSecureFileSystemProvider(new Server(""))
            .MayHaveWebLink(FsUri.Parse($"davs://hub.example.com{path}")).ShouldBe(expected);
}
