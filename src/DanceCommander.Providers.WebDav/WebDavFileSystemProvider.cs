using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Watching;

namespace DanceCommander.Providers.WebDav;

/// <summary>
/// WebDAV over HTTP (§6.9). Registered for <c>dav://</c>; <c>davs://</c> is the same thing
/// over TLS.
/// </summary>
/// <remarks>
/// <see cref="ProviderCapabilities.RandomAccessRead"/> is deliberately *not* claimed yet. The test
/// server answers Range requests, so a seekable stream is possible and is worth having — browsing a
/// zip without downloading it is what it buys — but it is not written, and a capability is a promise
/// the transfer service and the archive provider act on. Until the stream exists, an archive here
/// is materialised to temp first, which is slower and correct.
/// </remarks>
public sealed class WebDavFileSystemProvider(IDavConnections connections)
    : IFileSystemProvider, IWebLinks, ISearchingProvider
{
    public string Scheme => DavTarget.Scheme;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Read |
        ProviderCapabilities.Write |
        ProviderCapabilities.Delete |
        ProviderCapabilities.Rename |
        ProviderCapabilities.CreateDirectory |
        ProviderCapabilities.Remote;

    /// <remarks>
    /// No owner or permissions column, unlike SFTP: WebDAV has no notion of either. There is an
    /// ETag, which is the thing this protocol knows that others do not, but it is a cache token
    /// rather than something a person reads, so it stays out of the listing.
    /// </remarks>
    public ColumnSet DefaultColumns => ColumnSet.Standard.Without(ColumnIds.Attributes);

    private const string PropertyRequest =
        """
        <?xml version="1.0" encoding="utf-8" ?>
        <D:propfind xmlns:D="DAV:">
          <D:prop>
            <D:displayname/>
            <D:getcontentlength/>
            <D:getlastmodified/>
            <D:creationdate/>
            <D:resourcetype/>
            <D:getetag/>
          </D:prop>
        </D:propfind>
        """;

    public async IAsyncEnumerable<FsEntry> ListAsync(
        FsUri directory, [EnumeratorCancellation] CancellationToken ct)
    {
        var target = DavTarget.From(directory);
        var xml = await PropfindAsync(target, directory.Path, "1", ct).ConfigureAwait(false);

        foreach (var resource in DavResponse.Parse(xml, directory.Path))
        {
            // A name that is no name ("..", or one holding an escaped '/') has no address of its
            // own to give a row: anything done to it would be done to the path it reads as.
            if (ChildOf(directory, resource) is { } uri)
                yield return ToEntry(uri, resource);
        }
    }

    public async Task<FsEntry?> StatAsync(FsUri path, CancellationToken ct) =>
        await ResourceAsync(path, ct).ConfigureAwait(false) is { } resource
            ? ToEntry(path, resource)
            : null;

    /// <summary>What the server says about one path, or null if there is nothing there.</summary>
    private async Task<DavResource?> ResourceAsync(FsUri path, CancellationToken ct)
    {
        var target = DavTarget.From(path);

        string xml;
        try
        {
            xml = await PropfindAsync(target, path.Path, "0", ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        // Depth 0 describes the resource itself, so unlike a listing it is not filtered out.
        var resources = DavResponse.Parse(xml, "\0never matches");
        return resources.Count == 0 ? null : resources[0];
    }

    public async Task<Stream> OpenReadAsync(FsUri file, CancellationToken ct)
    {
        var target = DavTarget.From(file);

        // Headers only: reading the whole answer first held the file in memory, failed past 2 GB,
        // and ran a large file into the per-request time limit however fast it was arriving.
        var response = await connections
            .SendAsync(target, () => new HttpRequestMessage(HttpMethod.Get, target.UrlOf(file.Path)), ct,
                HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);

        try
        {
            await ThrowIfFailedAsync(response, file, ct).ConfigureAwait(false);
            return new DavDownloadStream(
                response, await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), file);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task<Stream> OpenWriteAsync(FsUri file, long? length, CancellationToken ct)
    {
        var target = DavTarget.From(file);

        // One empty request first, purely so that a challenge is answered before a body is
        // streamed: a PUT that is refused after the bytes have gone is a whole upload wasted, and
        // the body cannot be replayed from a pipe.
        using (var greeting = await connections
                   .SendAsync(target, () => Propfind(target, file.Parent?.Path ?? "/", "0"), ct)
                   .ConfigureAwait(false))
        {
        }

        // To a name of our own: a PUT straight onto the old name left keeping the old file, if the
        // upload failed, up to the server.
        var partial = await FreeUploadNameAsync(file, ct).ConfigureAwait(false);
        Task Commit(bool replace, long written, CancellationToken token) =>
            PutInPlaceAsync(partial, file, replace, written, token);
        Task Abandon() => DiscardAsync(partial);

        // Nextcloud, and more than one piece: in pieces, because one request that size may be
        // refused, and only after all of it has been sent.
        if (length > NextcloudChunkedUpload.PieceSize
            && NextcloudChunkedUpload.FolderFor(file.Path) is { } folder
            && await NextcloudChunkedUpload.StartAsync(
                    connections, target, folder, partial.Path, length.Value, file, Commit, Abandon, ct)
                .ConfigureAwait(false) is { } pieces)
        {
            return pieces;
        }

        // A pipe, so the caller writes into a stream while the PUT body is read from the other
        // end: a large upload never has to be held in memory, which is the whole reason the
        // interface hands back a stream rather than taking a byte array.
        return new DavUploadStream(
            await connections.ClientFor(target, ct).ConfigureAwait(false),
            target.UrlOf(partial.Path), length, file, Commit, Abandon);
    }

    /// <summary>
    /// <c>name.dc-upload-1a2b3c4d</c>, checked to be free: a PUT would write over whatever is
    /// there, which may be someone else's.
    /// </summary>
    /// <remarks>
    /// Not <c>name.part</c>, which SFTP uses. Nextcloud keeps <c>.part</c> names for its own
    /// unfinished uploads: it leaves them out of every listing, and answers a PROPFIND for any of
    /// them, real or not, with "exists, 0 bytes". Every name looked taken, so every upload failed,
    /// and a leftover could be neither seen nor told apart from nothing. A name of our own is
    /// answered honestly and shows in the listing, so a leftover after a crash is there to see,
    /// and says what it is. The random part makes a clash with an existing file all but impossible
    /// and two uploads of one name never share it.
    /// </remarks>
    private async Task<FsUri> FreeUploadNameAsync(FsUri file, CancellationToken ct)
    {
        var parent = file.Parent ?? file;

        // Servers limit a name to about 250 UTF-8 bytes, and the suffix adds 19: a name that fits
        // must not fail only because its temporary name does not. The name is only a hint to
        // whoever finds a leftover, so a shortened one serves as well.
        var stem = TemporaryName.Shortened(file.Name, maxBytes: 220);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var partial = parent.Child($"{stem}.dc-upload-{Guid.NewGuid().ToString("N")[..8]}");
            if (await StatAsync(partial, ct).ConfigureAwait(false) is null)
                return partial;
        }

        // Five random names all taken means the server is answering for names that are not there.
        throw new IOException($"{parent.Display} says every name tried for uploading {file.Name} is already taken.");
    }


    /// <summary>
    /// Moves a finished upload onto its name. A file already there steps aside first and is
    /// removed only once the new one is in place, so no failure in between can lose both; MOVE
    /// with Overwrite would leave that order to the server.
    /// </summary>
    /// <param name="written">What we sent. The server's success says it took the request, not
    /// that it kept every byte, so what it holds is compared before it gets the real name.</param>
    private async Task PutInPlaceAsync(FsUri partial, FsUri file, bool replace, long written, CancellationToken ct)
    {
        // A size the server does not report is taken on trust: refusing it would make every upload
        // to such a server fail, blaming it for losing bytes it never said it lost.
        var kept = await ResourceAsync(partial, ct).ConfigureAwait(false);
        if (kept is null || kept.IsCollection || kept.Length is { } size && size != written)
        {
            throw new IOException(
                $"The server kept {(kept?.Length is { } n ? $"{n:N0} bytes" : "nothing")} of the {written:N0} sent for {file.Name}, so it was not put in place.");
        }

        if (await StatAsync(file, ct).ConfigureAwait(false) is null)
        {
            await MoveAsync(partial, file, ct).ConfigureAwait(false);
            return;
        }

        if (!replace)
            throw WriteStreams.Appeared(file.Name);

        // No cancelling from here on: a cancel that landed after the server had moved the old file
        // aside left nothing at the name, and the abandon then deleted the finished upload.
        var aside = (file.Parent ?? file).Child(TemporaryName.Beside(file.Name, $".dc-old-{Guid.NewGuid():N}"));
        await MoveAsync(file, aside, CancellationToken.None).ConfigureAwait(false);
        try
        {
            await MoveAsync(partial, file, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await MoveAsync(aside, file, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Nothing is lost, but the user has to be told where the old file is now.
                throw new IOException($"{file.Name} could not be replaced, and the old one is now {aside.Name}.", ex);
            }

            throw;
        }

        try
        {
#pragma warning disable RS0030 // §1.6: the file the user chose to replace, set aside, and only once its replacement is in place.
            await DeleteAsync(aside, recursive: false, CancellationToken.None).ConfigureAwait(false);
#pragma warning restore RS0030
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The new file is in place; the old one is left under a name that says what it is.
        }
    }

    private async Task DiscardAsync(FsUri partial)
    {
        try
        {
            // §1.6: our own unfinished upload, never the real name. Never a folder either: a PUT
            // makes a file, so a folder under that name is someone else's.
            if (await StatAsync(partial, CancellationToken.None).ConfigureAwait(false) is { IsDirectory: false })
            {
#pragma warning disable RS0030 // §1.6: our own temporary upload (a random name checked free, PUT with If-None-Match).
                await DeleteAsync(partial, recursive: false, CancellationToken.None).ConfigureAwait(false);
#pragma warning restore RS0030
            }
        }
        catch (Exception)
        {
            // Debris with an obvious name; the caller is already reporting what went wrong.
        }
    }

    public async Task CreateDirectoryAsync(FsUri directory, CancellationToken ct)
    {
        var target = DavTarget.From(directory);

        using var response = await connections
            .SendAsync(target, () => new HttpRequestMessage(new HttpMethod("MKCOL"), target.UrlOf(directory.Path)), ct)
            .ConfigureAwait(false);

        await ThrowIfFailedAsync(response, directory, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(FsUri path, bool recursive, CancellationToken ct)
    {
        var target = DavTarget.From(path);

        // DELETE on a collection is always recursive in WebDAV; there is no shallow form. Ignoring
        // the flag meant "remove this folder if the move emptied it" removed a folder still holding
        // the files the move had skipped, so a shallow delete asks first whether anything is in it.
        if (!recursive)
        {
            var listing = await PropfindAsync(target, path.Path, "1", ct).ConfigureAwait(false);
            if (DavResponse.Parse(listing, path.Path).Count > 0)
                throw new IOException($"{path.Display} is not empty.");
            if (DavResponse.Self(listing, path.Path) is { IsCollection: true } && path.Parent is { } parent
                && await DeleteEmptyAsync(target, path, parent, ct).ConfigureAwait(false))
            {
                return;
            }
        }

        using var response = await connections
            .SendAsync(target, () => new HttpRequestMessage(HttpMethod.Delete, target.UrlOf(path.Path)), ct)
            .ConfigureAwait(false);

        await ThrowIfFailedAsync(response, path, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// An empty folder deleted with nothing able to arrive in it first: it is moved aside to a name
    /// no one else knows, looked at there, and deleted only if still empty. Looked at and deleted
    /// where it stood, a file uploaded between the look and the DELETE, which takes everything
    /// under it, went too (BUGS.md). False where the server won't move it, for the plain DELETE.
    /// </summary>
    private async Task<bool> DeleteEmptyAsync(DavTarget target, FsUri folder, FsUri parent, CancellationToken ct)
    {
        var aside = parent.Child($".dc-delete-{Guid.NewGuid():N}");
        try
        {
            await MoveAsync(folder, aside, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or HttpRequestException)
        {
            return false;
        }

        var listing = await PropfindAsync(target, aside.Path, "1", CancellationToken.None).ConfigureAwait(false);
        if (DavResponse.Parse(listing, aside.Path).Count > 0)
        {
            try
            {
                await MoveAsync(aside, folder, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
            {
                throw new IOException($"{folder.Display} was not empty after all, so it was kept, as {aside.Name}.", ex);
            }
            throw new IOException($"{folder.Display} is not empty.");
        }

        using var response = await connections
            .SendAsync(target, () => new HttpRequestMessage(HttpMethod.Delete, target.UrlOf(aside.Path)), CancellationToken.None)
            .ConfigureAwait(false);
        await ThrowIfFailedAsync(response, aside, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    public async Task MoveAsync(FsUri from, FsUri to, CancellationToken ct)
    {
        var target = DavTarget.From(from);

        // One provider serves every dav:// address, and the Destination is built on the source's
        // server: a "move" to another server, port or account renamed the file on the first one
        // and reported it moved. Across them it is a copy and a delete, which the transfer does.
        if (target != DavTarget.From(to))
            throw new NotSupportedException("Moving between two WebDAV servers goes through the transfer service.");

        // Server-side, which is the point: renaming a gigabyte costs one request rather than a
        // download and an upload.
        using var response = await connections.SendAsync(
            target,
            () =>
            {
                var request = new HttpRequestMessage(new HttpMethod("MOVE"), target.UrlOf(from.Path));
                request.Headers.Add("Destination", target.UrlOf(to.Path).AbsoluteUri);
                request.Headers.Add("Overwrite", "F");
                return request;
            },
            ct).ConfigureAwait(false);

        await ThrowIfFailedAsync(response, from, ct).ConfigureAwait(false);
    }

    /// <summary>A user's files on Nextcloud (or ownCloud): the only WebDAV with a web page per file.</summary>
    public bool MayHaveWebLink(FsUri file) =>
        file.Path.Contains("/remote.php/dav/files/", StringComparison.Ordinal);

    /// <summary>
    /// The server's own link to the file in its web interface (<c>oc:privatelink</c>), or one
    /// made from the file's number (<c>oc:fileid</c>) where the server gives only that.
    /// </summary>
    /// <remarks>
    /// The link is the server's text going onto the clipboard, so only an http(s) address on this
    /// same server is taken: a link elsewhere is not the file's page, whatever the server says.
    /// </remarks>
    public async Task<Uri?> WebLinkAsync(FsUri file, CancellationToken ct)
    {
        var target = DavTarget.From(file);

        using var response = await connections.SendAsync(
            target,
            () =>
            {
                var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), target.UrlOf(file.Path))
                {
                    Content = new StringContent(WebLinkRequest, Encoding.UTF8, "application/xml"),
                };
                request.Headers.Add("Depth", "0");
                return request;
            },
            ct).ConfigureAwait(false);

        await ThrowIfFailedAsync(response, file, ct).ConfigureAwait(false);

        var xml = System.Xml.Linq.XDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        System.Xml.Linq.XNamespace oc = "http://owncloud.org/ns";

        if (xml.Descendants(oc + "privatelink").FirstOrDefault()?.Value.Trim() is { Length: > 0 } link
            && Uri.TryCreate(link, UriKind.Absolute, out var given)
            && given.Scheme is "https" or "http"
            && string.Equals(given.Host, target.Host, StringComparison.OrdinalIgnoreCase))
        {
            return given;
        }

        // Nextcloud opens /f/<number> in its Files app, under whatever prefix it is installed at.
        var installedAt = file.Path.IndexOf("/remote.php/", StringComparison.Ordinal);
        if (installedAt >= 0
            && xml.Descendants(oc + "fileid").FirstOrDefault()?.Value.Trim() is { Length: > 0 } id
            && id.All(char.IsAsciiDigit))
        {
            return target.UrlOf($"{file.Path[..installedAt]}/f/{id}");
        }

        return null;
    }

    private const string WebLinkRequest =
        """
        <?xml version="1.0" encoding="utf-8" ?>
        <D:propfind xmlns:D="DAV:" xmlns:oc="http://owncloud.org/ns">
          <D:prop>
            <oc:privatelink/>
            <oc:fileid/>
          </D:prop>
        </D:propfind>
        """;

    /// <summary>
    /// Nextcloud's own search (<see cref="NextcloudSearch"/>), or null for the caller to walk:
    /// on any other server, and whenever Nextcloud answers with anything but its results.
    /// </summary>
    /// <remarks>
    /// Only a refused password is an error, and the connection raises that itself. A server that
    /// turns down SEARCH, a proxy that does not know the method, a body that is not a Multi-Status:
    /// each still lists folders, so the walk gets the search done more slowly. A 403 is among them,
    /// and does not forget the password, because it can be the method that is refused rather than
    /// the user.
    /// <para>The answer comes a page at a time (<see cref="NextcloudSearch.PageSize"/>), each page
    /// the file numbers after the last one seen, until a page comes back empty. The numbers are
    /// read back: one missing, or not rising, means the pages cannot be trusted to add up to the
    /// whole, and so does a page refused part way; the walk does it then.</para>
    /// </remarks>
    public async Task<IReadOnlyList<FsEntry>?> SearchAsync(FsUri root, IReadOnlyList<string> terms, CancellationToken ct)
    {
        if (NextcloudSearch.For(root.Path) is not { } search)
            return null;

        var target = DavTarget.From(root);
        var found = new List<DavResource>();
        long? last = null;

        while (true)
        {
            if (await SearchPageAsync(target, search, terms, last, ct).ConfigureAwait(false) is not { } page)
                return null;

            if (page.Count == 0)
                break;

            foreach (var (resource, number) in page)
            {
                if (number is not { } n || n <= last)
                    return null;

                found.Add(resource);
                last = n;
            }
        }

        // Depth infinity may name the folder searched; it is not one of its own finds. One that
        // is not under it name by name, or has a name with no address, is an answer that cannot
        // be the folder's, and the walk does it.
        var self = DavResponse.Normalise(root.Path);
        var entries = new List<FsEntry>();
        foreach (var resource in found.Where(resource => resource.Path != self))
        {
            if (DescendantOf(root, resource) is not { } uri)
                return null;
            entries.Add(ToEntry(uri, resource));
        }

        return entries;
    }

    /// <summary>
    /// One page, everything the server sent for it with each file's number, or null when it sent
    /// anything but results.
    /// </summary>
    private async Task<IReadOnlyList<(DavResource Resource, long? Number)>?> SearchPageAsync(
        DavTarget target, NextcloudSearch search, IReadOnlyList<string> terms, long? after, CancellationToken ct)
    {
        var body = search.Body(terms, after);

        using var response = await connections.SendAsync(
            target,
            () => new HttpRequestMessage(new HttpMethod("SEARCH"), target.UrlOf(search.Endpoint))
            {
                Content = new StringContent(body, Encoding.UTF8, "text/xml"),
            },
            ct).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.MultiStatus)
            return null;

        var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // Not filtered by the folder's own path: its number is in the order like any other.
        try
        {
            var numbers = NextcloudSearch.Numbers(xml);
            return DavResponse.Parse(xml, "\0never matches")
                .Select(resource => (resource, numbers.GetValueOrDefault(resource.Path)))
                .ToList();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Null: HTTP has no change notification, so the pane falls back to Ctrl+R.</summary>
    public IDirectoryWatcher? Watch(FsUri directory) => null;

    private static HttpRequestMessage Propfind(DavTarget target, string path, string depth)
    {
        var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), target.UrlOf(path))
        {
            Content = new StringContent(PropertyRequest, Encoding.UTF8, "application/xml"),
        };
        request.Headers.Add("Depth", depth);
        return request;
    }

    private async Task<string> PropfindAsync(
        DavTarget target, string path, string depth, CancellationToken ct)
    {
        using var response = await connections
            .SendAsync(target, () => Propfind(target, path, depth), ct)
            .ConfigureAwait(false);

        await ThrowIfFailedAsync(response, FsUri.Parse($"{Scheme}://{target.Display}{path}"), ct)
            .ConfigureAwait(false);

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The address of a resource in a Depth 1 listing of <paramref name="directory"/>, or null when
    /// its name cannot be one.
    /// </summary>
    /// <remarks>
    /// Built from the parent rather than from the href, so that the authority (and therefore the
    /// user and port) stays exactly as the user typed it: the server's own hrefs are path-only and
    /// would lose it. Made with <see cref="FsUri.Child"/>, never parsed: a '!' in the server's name
    /// is part of it (review, 2026-10-15), and parsing also turned a name's backslash into a
    /// separator, collapsed "..\" and trimmed a trailing space, so a file called "\" was given the
    /// folder's own address (review, 2026-10-22).
    /// <para>Only a resource whose parent is the folder listed, by its last name: a server that
    /// ignores Depth 1 sends deeper paths, and a folder's own entry that escaped the filter (a
    /// proxy that rewrites the prefix) would be a row named after it, each with the address of
    /// another file (review, 2026-10-22). The prefix is not compared, as a proxy may rewrite it,
    /// and the case is not, as a server may give its own.</para>
    /// </remarks>
    private static FsUri? ChildOf(FsUri directory, DavResource resource)
    {
        var segments = resource.Segments;
        if (segments.Count == 0)
            return null;

        if (!directory.IsRoot &&
            (segments.Count < 2 || !string.Equals(segments[^2], directory.Name, StringComparison.OrdinalIgnoreCase)))
            return null;

        return TryChild(directory, segments[^1]);
    }

    /// <summary>
    /// The address of a search's find under <paramref name="root"/>, built name by name, or null
    /// when the find is not under it or one of its names cannot be one.
    /// </summary>
    private static FsUri? DescendantOf(FsUri root, DavResource resource)
    {
        var above = root.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (resource.Segments.Count <= above.Length || !resource.Segments.Take(above.Length).SequenceEqual(above))
            return null;

        FsUri? uri = root;
        foreach (var name in resource.Segments.Skip(above.Length))
            uri = uri is null ? null : TryChild(uri, name);
        return uri;
    }

    private static FsUri? TryChild(FsUri parent, string name)
    {
        try
        {
            return parent.Child(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static FsEntry ToEntry(FsUri uri, DavResource resource) =>
        new(uri, uri.Name, resource.IsCollection, resource.Length ?? 0, resource.Modified);

    /// <summary>
    /// Turns an HTTP status into the exception the rest of the application already understands.
    /// </summary>
    /// <remarks>
    /// Without this every failure reaches the pane as "Response status code does not indicate
    /// success: 507", which is an accurate way of telling somebody nothing. The transfer service
    /// and the notice bar both speak in terms of these exception types.
    /// </remarks>
    private Task ThrowIfFailedAsync(HttpResponseMessage response, FsUri uri, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return Task.CompletedTask;

        var where = uri.Display;

        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound =>
                new FileNotFoundException($"{where} is not on the server.", where),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                Refused(uri, where),
            HttpStatusCode.Conflict =>
                new IOException($"{where} cannot be created because its parent folder does not exist."),
            HttpStatusCode.PreconditionFailed =>
                new IOException($"{where} already exists."),
            HttpStatusCode.MethodNotAllowed =>
                new NotSupportedException($"The server does not allow that on {where}."),
            (HttpStatusCode)423 =>
                new IOException($"{where} is locked by someone else."),
            (HttpStatusCode)507 =>
                new IOException($"The server has no room for {where}."),
            _ => new IOException(
                $"The server answered {(int)response.StatusCode} {response.ReasonPhrase} for {where}."),
        };
    }

    private UnauthorizedAccessException Refused(FsUri uri, string where)
    {
        // So that the next attempt asks for a password instead of failing the same way again. Not
        // for work nobody asked for (a folder listed ahead of being opened): a folder the account
        // may not read, passed over by the cursor, would otherwise throw away the saved password.
        if (DecisionScope.Current is not NoOneAsks)
            connections.Forget(DavTarget.From(uri));
        return new UnauthorizedAccessException($"Not allowed to read {where}.");
    }
}

/// <summary>
/// The same provider, registered a second time for <c>davs://</c>.
/// </summary>
/// <remarks>
/// The registry resolves by scheme and a provider declares one, so TLS needs its own registration.
/// It is a subclass rather than a flag because everything else about it is identical, and
/// <see cref="DavTarget"/> already reads the scheme off the URI.
/// </remarks>
public sealed class WebDavSecureFileSystemProvider(IDavConnections connections)
    : IFileSystemProvider, IWebLinks, ISearchingProvider
{
    private readonly WebDavFileSystemProvider _inner = new(connections);

    public string Scheme => DavTarget.SecureScheme;

    public ProviderCapabilities Capabilities => _inner.Capabilities;

    public ColumnSet DefaultColumns => _inner.DefaultColumns;

    public IAsyncEnumerable<FsEntry> ListAsync(FsUri directory, CancellationToken ct) =>
        _inner.ListAsync(directory, ct);

    public Task<FsEntry?> StatAsync(FsUri path, CancellationToken ct) => _inner.StatAsync(path, ct);

    public Task<Stream> OpenReadAsync(FsUri file, CancellationToken ct) => _inner.OpenReadAsync(file, ct);

    public Task<Stream> OpenWriteAsync(FsUri file, long? length, CancellationToken ct) =>
        _inner.OpenWriteAsync(file, length, ct);

    public Task CreateDirectoryAsync(FsUri directory, CancellationToken ct) =>
        _inner.CreateDirectoryAsync(directory, ct);

#pragma warning disable RS0030 // §1.6: davs:// passes the provider's own delete through; its callers carry the reasons.
    public Task DeleteAsync(FsUri path, bool recursive, CancellationToken ct) =>
        _inner.DeleteAsync(path, recursive, ct);
#pragma warning restore RS0030

    public Task MoveAsync(FsUri from, FsUri to, CancellationToken ct) => _inner.MoveAsync(from, to, ct);

    public IDirectoryWatcher? Watch(FsUri directory) => null;

    public bool MayHaveWebLink(FsUri file) => _inner.MayHaveWebLink(file);

    public Task<Uri?> WebLinkAsync(FsUri file, CancellationToken ct) => _inner.WebLinkAsync(file, ct);

    public Task<IReadOnlyList<FsEntry>?> SearchAsync(FsUri root, IReadOnlyList<string> terms, CancellationToken ct) =>
        _inner.SearchAsync(root, terms, ct);
}
