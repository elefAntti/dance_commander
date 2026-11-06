using System.Net;
using System.Xml.Linq;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Listing;
using DanceCommander.Providers.WebDav;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// A search of a Nextcloud folder asked of the server in one request, and the walk it falls back
/// to when the server will not answer. The server is played by a connection that answers SEARCH
/// from a fixed body and PROPFIND from a small tree.
/// </summary>
public sealed class NextcloudSearchTests
{
    private const string Files = "/remote.php/dav/files/antti";

    private static readonly FsUri Docs = FsUri.Parse($"davs://hub.example.com{Files}/Docs");

    /// <summary>Shaped like Nextcloud's answer: "d:" prefix, absolute paths, folders with a slash.</summary>
    private const string Found = $"""
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns">
          <d:response><d:href>{Files}/Docs/readme.md</d:href>
            <d:propstat><d:prop><d:displayname>readme.md</d:displayname><d:getcontentlength>12</d:getcontentlength>
              <d:resourcetype/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
          <d:response><d:href>{Files}/Docs/Old%20Notes/README.txt</d:href>
            <d:propstat><d:prop><d:displayname>README.txt</d:displayname><d:getcontentlength>3</d:getcontentlength>
              <d:resourcetype/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
          <d:response><d:href>{Files}/Docs/readme-drafts/</d:href>
            <d:propstat><d:prop><d:displayname>readme-drafts</d:displayname>
              <d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
          <d:response><d:href>{Files}/Docs/.git/readme</d:href>
            <d:propstat><d:prop><d:displayname>readme</d:displayname>
              <d:resourcetype/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
          <d:response><d:href>{Files}/Docs/unread.md</d:href>
            <d:propstat><d:prop><d:displayname>unread.md</d:displayname>
              <d:resourcetype/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
        </d:multistatus>
        """;

    private static string Answer(params string[] hrefs) =>
        $"""<d:multistatus xmlns:d="DAV:">{string.Concat(hrefs.Select(href =>
            $"<d:response><d:href>{href}</d:href><d:propstat><d:prop><d:resourcetype/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"))}</d:multistatus>""";

    private sealed class Server(HttpStatusCode searchAnswer, string found = Found) : IDavConnections
    {
        public List<(string Method, string Path, string? Body)> Requests { get; } = [];

        /// <summary>What the connection itself raises, as the real one does for a refused password.</summary>
        public Exception? Fails { get; init; }

        /// <summary>The most it sends in a page, whatever it is asked for: Nextcloud's own 100, say.</summary>
        public int Cap { get; init; } = int.MaxValue;

        /// <summary>Takes no notice of "after this number", so sends the first page every time.</summary>
        public bool IgnoresAfter { get; init; }

        /// <summary>Sends each page in the opposite order to the one asked for.</summary>
        public bool Backwards { get; init; }

        /// <summary>Answers 400 to a search that names a file's number.</summary>
        public bool WillNotSearchByNumber { get; init; }

        /// <summary>Answers 500 to this page, counting from 1.</summary>
        public int? FailsPage { get; init; }

        /// <summary>Sends no file numbers, as a server that does not know the property would.</summary>
        public bool StripsNumbers { get; init; }

        /// <summary>After the first page, the first file found is no longer there.</summary>
        public bool FirstGoesAfterOnePage { get; init; }

        private int _pages;

        /// <summary>
        /// One page of <c>found</c>, numbered in steps of ten as a server's are not consecutive, for
        /// the request's limit and the number after which it asks.
        /// </summary>
        private string Page(string request)
        {
            XNamespace dav = "DAV:";
            var query = XDocument.Parse(request);
            var size = Math.Min(Cap, (int)query.Descendants(dav + "nresults").Single());
            var after = !IgnoresAfter && query.Descendants(dav + "gt").SingleOrDefault() is { } gt
                ? (long)gt.Element(dav + "literal")!
                : 0;

            var all = XDocument.Parse(found);
            var page = all.Root!.Elements(dav + "response")
                .Select((response, i) => (Response: response, Number: (i + 1) * 10L))
                .Where(row => row.Number > after && !(FirstGoesAfterOnePage && _pages > 1 && row.Number == 10))
                .Take(size)
                .ToList();
            if (Backwards)
                page.Reverse();

            foreach (var (response, number) in page.Where(_ => !StripsNumbers))
                response.Descendants(dav + "prop").First().Add(new XElement(NextcloudSearch.FileId, number));

            all.Root.ReplaceNodes(page.Select(row => row.Response));
            return all.ToString();
        }

        public async Task<HttpResponseMessage> SendAsync(
            DavTarget target, Func<HttpRequestMessage> make, CancellationToken ct,
            HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, TimeSpan? limit = null)
        {
            var request = make();
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method.Method, path, body));

            if (request.Method.Method == "SEARCH")
            {
                if (Fails is not null)
                    throw Fails;

                if (WillNotSearchByNumber && body!.Contains("fileid", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);

                if (++_pages == FailsPage)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);

                return new HttpResponseMessage(searchAnswer)
                {
                    Content = new StringContent(searchAnswer == HttpStatusCode.MultiStatus ? Page(body!) : "no"),
                };
            }

            // The walk: Docs holds readme.md and a folder holding README.txt.
            var listing = path.TrimEnd('/') switch
            {
                $"{Files}/Docs" => $"""
                    <d:response><d:href>{Files}/Docs/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                    <d:response><d:href>{Files}/Docs/readme.md</d:href><d:propstat><d:prop><d:resourcetype/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                    <d:response><d:href>{Files}/Docs/Old%20Notes/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                    """,
                $"{Files}/Docs/Old Notes" => $"""
                    <d:response><d:href>{Files}/Docs/Old%20Notes/README.txt</d:href><d:propstat><d:prop><d:resourcetype/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                    """,
                _ => null,
            };

            return listing is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent($"""<d:multistatus xmlns:d="DAV:">{listing}</d:multistatus>"""),
                };
        }

        public Task<HttpClient> ClientFor(DavTarget target, CancellationToken ct) => throw new NotSupportedException();

        public void Forget(DavTarget target) { }
    }

    private static async Task<(List<FsEntry> Rows, IListingSource Source)> SearchAsync(
        Server server, FsUri root, string query, bool showHidden = false)
    {
        var providers = new ProviderRegistry([new WebDavSecureFileSystemProvider(server)]);
        var source = new ListingSourceFactory(providers).Create(FsUri.View("search", root, query), new ListingFilter(showHidden));

        var rows = new List<FsEntry>();
        await foreach (var entry in source.ListAsync(CancellationToken.None))
            rows.Add(entry);
        return (rows, source);
    }

    [Theory]
    [InlineData("/remote.php/dav/files/antti/Docs", "/remote.php/dav/", "/files/antti/Docs")]
    [InlineData("/remote.php/dav/files/antti", "/remote.php/dav/", "/files/antti")]
    [InlineData("/remote.php/dav/files/antti/", "/remote.php/dav/", "/files/antti")]
    [InlineData("/cloud/remote.php/dav/files/me@example.com/My Docs", "/cloud/remote.php/dav/", "/files/me@example.com/My Docs")]
    public void AUsersFilesAreSearchedFromTheDavRoot(string path, string endpoint, string scope)
    {
        var search = NextcloudSearch.For(path).ShouldNotBeNull();

        search.Endpoint.ShouldBe(endpoint);
        search.Scope.ShouldBe(scope);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/remote.php/webdav/Docs")]
    [InlineData("/remote.php/dav/files/")]
    [InlineData("/dav/share")]
    public void AnythingElseIsWalked(string path) => NextcloudSearch.For(path).ShouldBeNull();

    [Theory]
    [InlineData("readme", "%readme%")]
    [InlineData("*.cs", "%.cs%")]
    [InlineData("a?b*", "%a%|%b%")]
    // An alternative or a class is either-or; requiring its text would leave out the other half.
    [InlineData("*.{cs,md}", "%.%")]
    [InlineData("report[0-9]*.pdf", "%report%|%.pdf%")]
    [InlineData("*{a,{b,c}}x*", "%x%")]
    // A backslash escapes in a database's LIKE, so text is never sent next to one.
    [InlineData(@"a\b", "%a%|%b%")]
    // Not a glob, so a bracket is only a character.
    [InlineData("notes[1]", "%notes[1]%")]
    [InlineData("*", "")]
    public void OnlyTheTextANameMustContainIsSent(string term, string expected) =>
        string.Join('|', NextcloudSearch.Patterns([term])).ShouldBe(expected);

    [Fact]
    public void EachWordIsRequired() =>
        NextcloudSearch.Patterns(["mon", "d", "MON"]).ShouldBe(["%mon%", "%d%"]);

    [Fact]
    public void TheRequestNamesTheScopeAndEveryWord()
    {
        var body = XDocument.Parse(NextcloudSearch.For($"{Files}/A & B")!.Body(["x<y", "z"], after: null));
        XNamespace dav = "DAV:";

        body.Descendants(dav + "href").Single().Value.ShouldBe("/files/antti/A & B");
        body.Descendants(dav + "depth").Single().Value.ShouldBe("infinity");
        body.Descendants(dav + "and").Single().Elements(dav + "like")
            .Select(like => like.Element(dav + "literal")!.Value)
            .ShouldBe(["%x<y%", "%z%"]);
    }

    [Fact]
    public void AnEmptyQueryAsksForEverything()
    {
        var body = XDocument.Parse(NextcloudSearch.For(Files)!.Body([], after: null));

        body.Descendants((XNamespace)"DAV:" + "literal").Single().Value.ShouldBe("%");
    }

    [Fact]
    public async Task TheServerIsAskedUntilAPageIsEmptyAndNothingIsWalked()
    {
        var server = new Server(HttpStatusCode.MultiStatus);

        await SearchAsync(server, Docs, "readme");

        server.Requests.Count.ShouldBe(2);
        server.Requests.ShouldAllBe(r => r.Method == "SEARCH" && r.Path == "/remote.php/dav");
    }

    private static string Many(int count) =>
        Answer(Enumerable.Range(0, count).Select(i => $"{Files}/Docs/readme-{i}.md").ToArray());

    [Fact]
    public async Task EveryPageIsAskedForUntilTheLast()
    {
        var server = new Server(HttpStatusCode.MultiStatus, Many(1_234));

        var (rows, _) = await SearchAsync(server, Docs, "readme");

        rows.Count.ShouldBe(1_234);
        server.Requests.Count.ShouldBe(4);
    }

    [Fact]
    public async Task AShortPageIsNotTakenForTheLast()
    {
        // Nextcloud sends 100 when it is not told, and a server may send fewer than it is told;
        // stopping at the first short page showed the first hundred as the whole answer.
        var server = new Server(HttpStatusCode.MultiStatus, Many(250)) { Cap = 100 };

        var (rows, _) = await SearchAsync(server, Docs, "readme");

        rows.Count.ShouldBe(250);
    }

    [Fact]
    public async Task EachPageAsksForTheNumbersAfterTheLastOneSeen()
    {
        var server = new Server(HttpStatusCode.MultiStatus, Many(600));

        await SearchAsync(server, Docs, "readme");

        XNamespace dav = "DAV:";
        server.Requests
            .Select(r => XDocument.Parse(r.Body!).Descendants(dav + "gt").SingleOrDefault()?.Element(dav + "literal")?.Value)
            .ShouldBe([null, "5000", "6000"]);
    }

    [Fact]
    public async Task AFileGoneBetweenPagesDoesNotHideTheNext()
    {
        // Paged by an offset, the first file going moved every later one down a place, and the
        // one at the edge of the next page was never asked for.
        var server = new Server(HttpStatusCode.MultiStatus, Many(600)) { FirstGoesAfterOnePage = true };

        var (rows, _) = await SearchAsync(server, Docs, "readme");

        rows.Count.ShouldBe(600);
    }

    [Theory]
    [InlineData("after")]
    [InlineData("backwards")]
    [InlineData("number")]
    [InlineData("page")]
    public async Task PagesThatCannotBeShownToAddUpAreWalkedInstead(string fault)
    {
        // A server that sends the first page every time, one whose order is not the one asked
        // for, one that will not search by number, and a page refused part way: none of them
        // can be shown to have sent everything, so none of their pages is shown as if it had.
        var server = new Server(HttpStatusCode.MultiStatus)
        {
            Cap = 2,
            IgnoresAfter = fault == "after",
            Backwards = fault == "backwards",
            WillNotSearchByNumber = fault == "number",
            FailsPage = fault == "page" ? 2 : null,
        };

        var (rows, _) = await SearchAsync(server, Docs, "readme");

        rows.Select(r => r.Name).ShouldBe(["readme.md", "README.txt"], ignoreOrder: true);
        server.Requests.ShouldContain(r => r.Method == "PROPFIND");
    }

    [Fact]
    public async Task APageWithoutTheNumbersIsWalkedInstead()
    {
        // The order cannot be checked, so it is not trusted.
        var server = new Server(HttpStatusCode.MultiStatus, Answer($"{Files}/Docs/readme.md"));
        var withNumbers = await SearchAsync(server, Docs, "readme");
        withNumbers.Rows.ShouldHaveSingleItem();

        var walked = new Server(HttpStatusCode.MultiStatus, Answer($"{Files}/Docs/readme.md")) { StripsNumbers = true };
        await SearchAsync(walked, Docs, "readme");
        walked.Requests.ShouldContain(r => r.Method == "PROPFIND");
    }

    [Fact]
    public async Task TheAnswerIsMatchedAgainByOurOwnRule()
    {
        // The server was asked for names like %readme%; "unread.md" is what a looser answer looks
        // like, and it is not shown.
        var (rows, _) = await SearchAsync(new Server(HttpStatusCode.MultiStatus), Docs, "readme");

        rows.Select(r => r.Name).ShouldBe(["readme.md", "README.txt", "readme-drafts"]);
        rows.Single(r => r.Name == "readme-drafts").IsDirectory.ShouldBeTrue();
        rows.Single(r => r.Name == "README.txt").Uri.ShouldBe(FsUri.Parse($"davs://hub.example.com{Files}/Docs/Old Notes/README.txt"));
    }

    [Fact]
    public async Task EachRowSaysWhichFolderItCameFrom()
    {
        var (rows, _) = await SearchAsync(new Server(HttpStatusCode.MultiStatus), Docs, "readme");

        rows.Single(r => r.Name == "readme.md").Extra!["relpath"].ShouldBe(".");
        rows.Single(r => r.Name == "README.txt").Extra!["relpath"].ShouldBe("Old Notes");
    }

    [Fact]
    public async Task TheServersFindsAreRankedAsTheWalksAre()
    {
        // The server's order is its file numbers, which mean nothing to a person. The two at the
        // top rank alike and so read A-Z; the one a folder down comes after them.
        var (rows, source) = await SearchAsync(new Server(HttpStatusCode.MultiStatus), Docs, "readme");

        rows.Sort(new ListingOrder(source.Columns.DefaultSortColumnId));
        rows.Select(r => r.Name).ShouldBe(["readme-drafts", "readme.md", "README.txt"]);
    }

    [Fact]
    public async Task AFindInAHiddenFolderIsLeftOutAndSaidSo()
    {
        // The walk would not have gone into .git; the server did, so its find is set aside and
        // counted, and Ctrl+H brings it back.
        var (hidden, source) = await SearchAsync(new Server(HttpStatusCode.MultiStatus), Docs, "readme");
        hidden.ShouldNotContain(r => r.Name == "readme");
        source.Omission.ShouldBe("1 found in a hidden folder not shown");

        var (shown, all) = await SearchAsync(new Server(HttpStatusCode.MultiStatus), Docs, "readme", showHidden: true);
        shown.Single(r => r.Name == "readme").Extra!["relpath"].ShouldBe(".git");
        all.Omission.ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.OK)]
    public async Task AServerThatWillNotSearchIsWalkedInstead(HttpStatusCode answer)
    {
        var server = new Server(answer);

        var (rows, _) = await SearchAsync(server, Docs, "readme");

        rows.Select(r => r.Name).ShouldBe(["readme.md", "README.txt"], ignoreOrder: true);
        server.Requests.ShouldContain(r => r.Method == "PROPFIND");
    }

    [Fact]
    public async Task AnAnswerFromOutsideTheFolderIsNotTrustedAndTheFolderIsWalked()
    {
        // The server and we disagree about where the folder is, so its finds inside it may be
        // short too; showing only those would read as the whole answer.
        var server = new Server(HttpStatusCode.MultiStatus, Answer($"{Files}/Docs/readme.md", $"{Files}/Photos/readme.md"));

        var (rows, _) = await SearchAsync(server, Docs, "readme");

        rows.Select(r => r.Name).ShouldBe(["readme.md", "README.txt"], ignoreOrder: true);
        server.Requests.ShouldContain(r => r.Method == "PROPFIND");
    }

    [Fact]
    public async Task ANameThatReadsAsAPathIsARowWithItsOwnAddress()
    {
        // Parsed, "A\..\readme.md" was the address of Docs/readme.md, which Del on the row then
        // deleted. On a Linux server it is one file's name, and the row's address is that file.
        var server = new Server(HttpStatusCode.MultiStatus, Answer($"{Files}/Docs/readme.md", $"{Files}/Docs/A%5C..%5Creadme.md"));

        var (rows, _) = await SearchAsync(server, Docs, "");

        rows.Select(r => r.Uri).ShouldBe([Docs.Child("readme.md"), Docs.Child(@"A\..\readme.md")], ignoreOrder: true);
        rows.ShouldAllBe(r => r.Uri.Name == r.Name);
    }

    [Theory]
    // No name can be "..", nor hold a '/': the server's answer cannot be trusted as the folder's.
    [InlineData("A/B/%2E%2E")]
    [InlineData("A%2Freadme.md")]
    public async Task AFindWithNoAddressSendsTheSearchToTheWalk(string href)
    {
        var server = new Server(HttpStatusCode.MultiStatus, Answer($"{Files}/Docs/readme.md", $"{Files}/Docs/{href}"));

        var (rows, _) = await SearchAsync(server, Docs, "readme");

        rows.Select(r => r.Name).ShouldBe(["readme.md", "README.txt"], ignoreOrder: true);
        server.Requests.ShouldContain(r => r.Method == "PROPFIND");
    }

    [Fact]
    public async Task ARefusedPasswordIsAnErrorNotAWalk()
    {
        var server = new Server(HttpStatusCode.MultiStatus) { Fails = new UnauthorizedAccessException("refused") };

        await Should.ThrowAsync<UnauthorizedAccessException>(() => SearchAsync(server, Docs, "readme"));
        server.Requests.ShouldNotContain(r => r.Method == "PROPFIND");
    }

    [Fact]
    public async Task AServerThatIsNotNextcloudIsNeverAskedToSearch()
    {
        var server = new Server(HttpStatusCode.MultiStatus);

        await SearchAsync(server, FsUri.Parse("davs://hub.example.com/dav/share"), "readme");

        server.Requests.ShouldNotContain(r => r.Method == "SEARCH");
    }
}
