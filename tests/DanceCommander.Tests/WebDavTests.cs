using DanceCommander.Core.Fs;
using DanceCommander.Providers.WebDav;
using Shouldly;

namespace DanceCommander.Tests;

public sealed class DavTargetTests
{
    [Theory]
    [InlineData("host", "host", 80, "")]
    [InlineData("host:8127", "host", 8127, "")]
    [InlineData("dance@host", "host", 80, "dance")]
    [InlineData("dance@host:8127", "host", 8127, "dance")]
    [InlineData("[::1]:8127", "::1", 8127, "")]
    public void ParsesAnAuthority(string authority, string host, int port, string user)
    {
        var target = DavTarget.Parse(authority, secure: false);

        target.Host.ShouldBe(host);
        target.Port.ShouldBe(port);
        target.User.ShouldBe(user);
    }

    [Fact]
    public void AUserNameMayContainAnAtSign() =>
        // A Nextcloud login is often an email address; splitting on the first '@' halves it.
        DavTarget.Parse("me@example.com@cloud.host", secure: false).User.ShouldBe("me@example.com");

    [Fact]
    public void TheSchemeChoosesTheDefaultPortAndTheTransport()
    {
        DavTarget.Parse("host", secure: false).BaseAddress.ShouldBe(new Uri("http://host:80/"));
        DavTarget.Parse("host", secure: true).BaseAddress.ShouldBe(new Uri("https://host:443/"));
    }

    [Fact]
    public void EachSegmentIsEscapedButTheSeparatorsSurvive()
    {
        // Escaping a whole path turns every '/' into %2F and asks for one file with slashes in
        // its name. Spaces and non-ASCII characters are the common case, not the exotic one.
        var target = DavTarget.Parse("host:8127", secure: false);

        target.UrlOf("/documents/a file.txt").AbsoluteUri
            .ShouldBe("http://host:8127/documents/a%20file.txt");
        target.UrlOf("/documents/ääkköset.md").AbsoluteUri
            .ShouldBe("http://host:8127/documents/%C3%A4%C3%A4kk%C3%B6set.md");
    }

    [Fact]
    public void ARefusedPortIsRefusedClearly() =>
        Should.Throw<FormatException>(() => DavTarget.Parse("host:not-a-port", secure: false));

    [Fact]
    public void AnAddressWithNoHostIsRefused() =>
        Should.Throw<FormatException>(() => DavTarget.Parse("   ", secure: false));
}

/// <summary>
/// Reading a 207 Multi-Status. The fixtures are shaped like the bodies real servers send, because
/// every interoperability problem this code can have is in the shape rather than in the logic.
/// </summary>
public sealed class DavResponseTests
{
    /// <summary>Verbatim from wsgidav, which prefixes the DAV namespace "ns0" and not "D".</summary>
    private const string WsgidavListing = """
        <?xml version="1.0" encoding="utf-8" ?>
        <ns0:multistatus xmlns:ns0="DAV:">
          <ns0:response>
            <ns0:href>/</ns0:href>
            <ns0:propstat><ns0:prop>
              <ns0:resourcetype><ns0:collection /></ns0:resourcetype>
              <ns0:getlastmodified>Fri, 18 Sep 2026 20:33:49 GMT</ns0:getlastmodified>
            </ns0:prop><ns0:status>HTTP/1.1 200 OK</ns0:status></ns0:propstat>
          </ns0:response>
          <ns0:response>
            <ns0:href>/a%20file%20with%20spaces.txt</ns0:href>
            <ns0:propstat><ns0:prop>
              <ns0:resourcetype />
              <ns0:getcontentlength>19</ns0:getcontentlength>
              <ns0:getlastmodified>Fri, 18 Sep 2026 20:33:49 GMT</ns0:getlastmodified>
              <ns0:getetag>"abc123"</ns0:getetag>
            </ns0:prop><ns0:status>HTTP/1.1 200 OK</ns0:status></ns0:propstat>
          </ns0:response>
          <ns0:response>
            <ns0:href>/documents/</ns0:href>
            <ns0:propstat><ns0:prop>
              <ns0:resourcetype><ns0:collection /></ns0:resourcetype>
            </ns0:prop><ns0:status>HTTP/1.1 200 OK</ns0:status></ns0:propstat>
          </ns0:response>
        </ns0:multistatus>
        """;

    [Fact]
    public void ThePrefixIsNotTheNamespace()
    {
        // The specification's examples say "D:"; real servers say "D:", "ns0:" or nothing. A
        // parser keyed on the prefix works against the documentation and fails against the server.
        var resources = DavResponse.Parse(WsgidavListing, "/");

        resources.Count.ShouldBe(2);
    }

    [Fact]
    public void TheCollectionItselfIsNotOneOfItsOwnEntries() =>
        // PROPFIND Depth 1 describes the folder and then its children; the folder is not in it.
        DavResponse.Parse(WsgidavListing, "/").ShouldNotContain(r => r.Path == "/");

    [Fact]
    public void PercentEncodingIsUndone() =>
        DavResponse.Parse(WsgidavListing, "/")
            .ShouldContain(r => r.Path == "/a file with spaces.txt");

    [Fact]
    public void CollectionsAreDistinguishedFromFiles()
    {
        var resources = DavResponse.Parse(WsgidavListing, "/");

        resources.Single(r => r.Path == "/documents").IsCollection.ShouldBeTrue();
        resources.Single(r => r.Path.EndsWith(".txt")).IsCollection.ShouldBeFalse();
    }

    [Fact]
    public void SizeAndTimeAndTagAreRead()
    {
        var file = DavResponse.Parse(WsgidavListing, "/").Single(r => !r.IsCollection);

        file.Length.ShouldBe(19);
        file.Modified!.Value.UtcDateTime.ShouldBe(new DateTime(2026, 9, 18, 20, 33, 49, DateTimeKind.Utc));
        file.ETag.ShouldBe("\"abc123\"");
    }

    [Fact]
    public void AnAbsoluteHrefIsAcceptedToo() =>
        // Both forms are legal answers to the same question, and servers differ.
        DavResponse.PathOf("http://host:8127/documents/deep.txt").ShouldBe("/documents/deep.txt");

    [Fact]
    public void ATrailingSlashDoesNotMakeADifferentPath()
    {
        // A collection's href usually ends in '/', a file's does not, and they have to compare.
        DavResponse.PathOf("/documents/").ShouldBe("/documents");
        DavResponse.PathOf("/documents").ShouldBe("/documents");
        DavResponse.PathOf("/").ShouldBe("/");
    }

    [Fact]
    public void TheFailedHalfOfASplitPropstatIsIgnored()
    {
        // A server may answer with the properties it has and a 404 for the ones it does not; the
        // second half is noise and must not blank out the first.
        const string Split = """
            <?xml version="1.0" encoding="utf-8" ?>
            <D:multistatus xmlns:D="DAV:">
              <D:response>
                <D:href>/file.txt</D:href>
                <D:propstat>
                  <D:prop><D:getcontentlength>42</D:getcontentlength><D:resourcetype /></D:prop>
                  <D:status>HTTP/1.1 200 OK</D:status>
                </D:propstat>
                <D:propstat>
                  <D:prop><D:getetag /></D:prop>
                  <D:status>HTTP/1.1 404 Not Found</D:status>
                </D:propstat>
              </D:response>
            </D:multistatus>
            """;

        var file = DavResponse.Parse(Split, "/").ShouldHaveSingleItem();
        file.Length.ShouldBe(42);
    }

    [Fact]
    public void AnEmptyMultistatusIsAnEmptyFolderRatherThanAnError() =>
        DavResponse.Parse("""<D:multistatus xmlns:D="DAV:" />""", "/").ShouldBeEmpty();

    [Theory]
    // A Linux server's names may hold any of these; each is one name, not structure, and the
    // whitespace that goes is the XML's around the href.
    [InlineData("/dir/%5C", "\\")]
    [InlineData("/dir/..%5C", "..\\")]
    [InlineData("/dir/report.txt%20", "report.txt ")]
    [InlineData("  /dir/a%2Fb  ", "a/b")]
    [InlineData("http://host:8127/dir/a%5Cb.txt", "a\\b.txt")]
    [InlineData("http://host/dir/a\\b.txt", "a\\b.txt")]
    public void ANamesOwnCharactersStayInTheName(string href, string name) =>
        DavResponse.SegmentsOf(href).ShouldBe(["dir", name]);
}

/// <summary>
/// Each row's address is the file the row shows. Built from the server's path as text, a file
/// named "\" had the listed folder's own address, and Del on it deleted the folder.
/// </summary>
public sealed class DavListingAddressTests
{
    private sealed class Server(params string[] hrefs) : IDavConnections
    {
        public Task<HttpResponseMessage> SendAsync(
            DavTarget target, Func<HttpRequestMessage> make, CancellationToken ct,
            HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, TimeSpan? limit = null)
        {
            var body = $"""<d:multistatus xmlns:d="DAV:">{string.Concat(hrefs.Select(href =>
                $"<d:response><d:href>{href}</d:href><d:propstat><d:prop><d:resourcetype/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"))}</d:multistatus>""";
            return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)207) { Content = new StringContent(body) });
        }

        public Task<HttpClient> ClientFor(DavTarget target, CancellationToken ct) => throw new NotSupportedException();

        public void Forget(DavTarget target) { }
    }

    private static readonly FsUri Dir = FsUri.Parse("dav://host/share/dir");

    private static async Task<List<FsEntry>> ListAsync(params string[] hrefs)
    {
        var rows = new List<FsEntry>();
        await foreach (var entry in new WebDavFileSystemProvider(new Server(hrefs)).ListAsync(Dir, CancellationToken.None))
            rows.Add(entry);
        return rows;
    }

    [Theory]
    [InlineData("%5C", "\\")]
    [InlineData("..%5C", "..\\")]
    [InlineData("a%5Cb.txt", "a\\b.txt")]
    [InlineData("report.txt%20", "report.txt ")]
    public async Task ARowsAddressIsItsOwnName(string href, string name)
    {
        var row = (await ListAsync("/share/dir/", $"/share/dir/{href}")).ShouldHaveSingleItem();

        row.Name.ShouldBe(name);
        row.Uri.ShouldBe(Dir.Child(name));
        row.Uri.Parent.ShouldBe(Dir);
    }

    [Fact]
    public async Task ANameAndTheSameNameWithATrailingSpaceAreTwoFiles()
    {
        var rows = await ListAsync("/share/dir/report.txt", "/share/dir/report.txt%20");

        rows.Select(r => r.Uri).Distinct().Count().ShouldBe(2);
    }

    [Theory]
    // Deeper than asked, and the folder's own entry under a prefix a proxy rewrote: each would
    // have been a row with another file's address.
    [InlineData("/share/dir/deeper/d.txt")]
    [InlineData("/proxy/share/dir/")]
    public async Task OnlyTheFoldersOwnChildrenAreRows(string href) =>
        (await ListAsync("/share/dir/ok.txt", href))
            .ShouldHaveSingleItem().Uri.ShouldBe(Dir.Child("ok.txt"));

    [Fact]
    public async Task AServerGivingTheFolderInAnotherCaseStillListsIt() =>
        (await ListAsync("/share/DIR/ok.txt"))
            .ShouldHaveSingleItem().Uri.ShouldBe(Dir.Child("ok.txt"));

    [Theory]
    // Neither is a name; a row for it would act on the path it reads as.
    [InlineData("%2E%2E")]
    [InlineData("a%2Fb.txt")]
    public async Task ANameWithNoAddressIsNotARow(string href) =>
        (await ListAsync("/share/dir/ok.txt", $"/share/dir/{href}"))
            .ShouldHaveSingleItem().Uri.ShouldBe(Dir.Child("ok.txt"));
}

/// <summary>
/// The http-to-https redirect rule. Worth its own test because the failure it prevents is silent:
/// the request succeeds as a redirect, .NET strips the Authorization header across the change of
/// scheme, and the server refuses forever with nothing on screen to say why.
/// </summary>
public sealed class DavRedirectTests
{
    private static HttpResponseMessage Landed(string finalUrl) =>
        new(System.Net.HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUrl),
        };

    [Fact]
    public void AnInsecureRequestThatEndedUpOnHttpsIsRefused()
    {
        var target = DavTarget.Parse("hub.example.com", secure: false);

        var thrown = Should.Throw<NotSupportedException>(() =>
            DavConnections.RefuseSilentDowngrade(target, Landed("https://hub.example.com/dav/")));

        // The message has to carry the address that would have worked; "redirected" alone leaves
        // the user with nothing to do.
        thrown.Message.ShouldContain("davs://");
    }

    [Fact]
    public void AnInsecureRequestThatStayedOnHttpIsFine() =>
        Should.NotThrow(() => DavConnections.RefuseSilentDowngrade(
            DavTarget.Parse("127.0.0.1:8127", secure: false),
            Landed("http://127.0.0.1:8127/documents/")));

    [Fact]
    public void ASecureRequestIsNeverAffected() =>
        // Already on TLS: a redirect within https carries the header and is nobody's problem.
        Should.NotThrow(() => DavConnections.RefuseSilentDowngrade(
            DavTarget.Parse("hub.example.com", secure: true),
            Landed("https://hub.example.com/elsewhere/")));
}

public sealed class DavDownloadStreamTests
{
    /// <summary>A body that gives so many bytes and then breaks, as a connection the server dropped.</summary>
    private sealed class BreaksAfter(int bytes) : MemoryStream(new byte[bytes])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Position < Length
                ? base.ReadAsync(buffer, ct)
                : throw new IOException("Unable to read data from the transport connection.",
                    new System.Net.Sockets.SocketException(10054));
    }

    [Fact]
    public async Task ABrokenDownloadIsSaidAsAnUploadIsWithHowFarItGot()
    {
        var file = DanceCommander.Core.Fs.FsUri.Remote(DavTarget.Scheme, "127.0.0.1:8127", "/big.bin");
        await using var stream = new DavDownloadStream(new System.Net.Http.HttpResponseMessage(), new BreaksAfter(1000), file);
        var buffer = new byte[4096];

        (await stream.ReadAsync(buffer)).ShouldBe(1000);
        var thrown = await Should.ThrowAsync<IOException>(async () => await stream.ReadAtLeastAsync(buffer, 1));

        thrown.Message.ShouldStartWith($"The download of {file.Display} stopped after about 1");
        thrown.Message.ShouldContain("forcibly closed");
    }
}

/// <summary>The Windows account answers only a server on the organisation's own network.</summary>
public sealed class DavIntegratedAuthTests
{
    [Theory]
    [InlineData("fileserver", "corp.example", true)]
    [InlineData("sp.corp.example", "corp.example", true)]
    [InlineData("SP.Corp.Example.", "corp.example", true)]
    [InlineData("cloud.example.org", "corp.example", false)]
    [InlineData("corp.example.evil.org", "corp.example", false)]
    [InlineData("evilcorp.example", "corp.example", false)]
    [InlineData("cloud.example.org", "", false)]
    [InlineData("10.0.0.5", "corp.example", false)]
    [InlineData("::1", "corp.example", false)]
    public void OnlyAnIntranetNameGetsTheWindowsAccount(string host, string domain, bool intranet) =>
        DavConnections.IsIntranet(host, domain).ShouldBe(intranet);

    [Fact]
    public void ThePublicServerGetsNoCredentialsAndTheIntranetOneOnlyForItself()
    {
        DavConnections.IntegratedAuthFor(new DavTarget("cloud.example.org", 443, "", Secure: true)).ShouldBeNull();

        var cache = DavConnections.IntegratedAuthFor(new DavTarget("fileserver", 80, "", Secure: false)).ShouldNotBeNull();
        cache.GetCredential(new Uri("http://fileserver/a/b"), "NTLM").ShouldNotBeNull();
        cache.GetCredential(new Uri("http://elsewhere/a/b"), "NTLM").ShouldBeNull();
    }

    [Theory]
    // Over TLS the server proved its name; nothing else did (BUGS.md 54).
    [InlineData("fileserver", true, false, true, true)]
    [InlineData("cloud.example.org", true, false, true, true)]
    [InlineData("fileserver", false, false, false, false)]
    [InlineData("cloud.example.org", false, false, false, false)]
    [InlineData("10.0.0.5", false, false, false, false)]
    // The user said plain HTTP is fine here.
    [InlineData("fileserver", false, true, true, true)]
    // This machine: a password for a local test server, but not the Windows account, since a
    // local port can be a tunnel to someone else's machine.
    [InlineData("localhost", false, false, true, false)]
    [InlineData("LOCALHOST", false, false, true, false)]
    [InlineData("127.0.0.1", false, false, true, false)]
    [InlineData("127.0.0.2", false, false, true, false)]
    [InlineData("::1", false, false, true, false)]
    // Names DNS answers, so whoever is asked.
    [InlineData("localhost.", false, false, false, false)]
    [InlineData("127.0.0.1.", false, false, false, false)]
    [InlineData("127.0.0.1.nip.io", false, false, false, false)]
    [InlineData("localhost.evil.org", false, false, false, false)]
    public void APasswordOrTheWindowsAccountGoesOnlyWhereItCannotBeRead(
        string host, bool secure, bool allowed, bool password, bool windows) =>
        DavConnections.MayUse(new DavTarget(host, secure ? 443 : 80, "", secure), allowed)
            .ShouldBe(new DavConnections.SignInPolicy(password, windows));
}

/// <summary>
/// WebDAV has no shallow delete: DELETE on a folder takes everything under it. Looked at and then
/// deleted where it stood, a file uploaded between the two went too.
/// </summary>
public sealed class DavShallowDeleteTests
{
    private sealed class Server(Func<string, string, string?> children) : IDavConnections
    {
        public List<string> Requests { get; } = [];

        public Task<HttpResponseMessage> SendAsync(
            DavTarget target, Func<HttpRequestMessage> make, CancellationToken ct,
            HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, TimeSpan? limit = null)
        {
            var request = make();
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).TrimEnd('/');
            Requests.Add($"{request.Method} {path}");
            if (request.Method.Method != "PROPFIND")
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Created));

            var child = children(path, string.Join('|', Requests));
            var body = $"""<d:multistatus xmlns:d="DAV:"><d:response><d:href>{path}/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>{(child is null ? "" : $"<d:response><d:href>{path}/{child}</d:href><d:propstat><d:prop><d:resourcetype/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>")}</d:multistatus>""";
            return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)207) { Content = new StringContent(body) });
        }

        public Task<HttpClient> ClientFor(DavTarget target, CancellationToken ct) => throw new NotSupportedException();

        public void Forget(DavTarget target) { }
    }

    private static readonly FsUri Folder = FsUri.Parse("dav://host/share/emptied");

    [Fact]
    public async Task AnEmptyFolderIsDeletedWhereNoOneElseCanWrite()
    {
        var server = new Server((_, _) => null);
        await new WebDavFileSystemProvider(server).DeleteAsync(Folder, recursive: false, CancellationToken.None);

        server.Requests[1].ShouldStartWith("MOVE /share/emptied");
        server.Requests[^1].ShouldStartWith("DELETE /share/.dc-delete-");
        server.Requests.ShouldNotContain("DELETE /share/emptied");
    }

    [Fact]
    public async Task AFileThatArrivedMeanwhileKeepsItsFolder()
    {
        // Empty when asked, then an upload lands as it is moved aside.
        var server = new Server((path, _) => path.Contains(".dc-delete-", StringComparison.Ordinal) ? "photo.jpg" : null);
        await Should.ThrowAsync<IOException>(() =>
            new WebDavFileSystemProvider(server).DeleteAsync(Folder, recursive: false, CancellationToken.None));

        server.Requests.ShouldNotContain(r => r.StartsWith("DELETE", StringComparison.Ordinal));
        server.Requests[^1].ShouldStartWith("MOVE /share/.dc-delete-"); // back where it was
    }
}
