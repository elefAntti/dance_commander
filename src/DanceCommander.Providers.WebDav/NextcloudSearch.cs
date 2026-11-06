using System.Text;
using System.Xml.Linq;
using DanceCommander.Core.Matching;

namespace DanceCommander.Providers.WebDav;

/// <summary>
/// A search of a Nextcloud user's files, asked of the server (RFC 5323 <c>SEARCH</c>, the way
/// Nextcloud's own clients search) rather than walked a folder at a time.
/// </summary>
/// <remarks>
/// A walk over WebDAV is one PROPFIND per folder, one after another, so a tree of a few thousand
/// folders is minutes; the server answers from its file index in one request. Only Nextcloud is
/// asked: it is the server we use, and the generic form of the request is not one any server we
/// can test answers. Anything else, or a Nextcloud that refuses, is walked as before.
///
/// The index is the server's view of its files. Something put on its disk behind its back, or on
/// an external storage it has not scanned, is not in it until it is scanned.
/// </remarks>
internal sealed record NextcloudSearch(string Endpoint, string Scope)
{
    private const string FilesArea = "/remote.php/dav/files/";

    private static readonly XNamespace Dav = "DAV:";
    /// <summary>Where Nextcloud's number for a file lives, which pages are asked in order of.</summary>
    public static readonly XName FileId = (XNamespace)"http://owncloud.org/ns" + "fileid";

    /// <summary>
    /// How many to ask for at once. Nextcloud answers 100 when not told, and may cap what it is
    /// told, so the end is when a page comes back empty, never when one comes back short.
    /// </summary>
    public const int PageSize = 500;

    /// <summary>
    /// The search of <paramref name="path"/> if it is in a Nextcloud user's files,
    /// <c>/remote.php/dav/files/&lt;user&gt;/…</c> under whatever prefix the server is installed
    /// at, or null for anything else.
    /// </summary>
    public static NextcloudSearch? For(string path)
    {
        var at = path.IndexOf(FilesArea, StringComparison.Ordinal);
        if (at < 0)
            return null;

        var rest = path[(at + FilesArea.Length)..].TrimEnd('/');
        if (rest.Length == 0 || rest.StartsWith('/'))
            return null;

        // The scope is named from the DAV root, which is where the request goes.
        return new NextcloudSearch($"{path[..at]}/remote.php/dav/", $"/files/{rest}");
    }

    /// <summary>
    /// The request body: one page of everything under the scope whose name is like each of the
    /// patterns, in order of the file's number, the numbers after <paramref name="after"/> only.
    /// </summary>
    /// <remarks>
    /// Paged by the number rather than by an offset. An offset counts rows, so a file removed
    /// between two pages moves the next one down and the row at the edge is never asked for; and
    /// paging needs an order without ties, which a name is not. A number is the file's own and
    /// never shared, and is selected so that the order can be checked, not trusted.
    /// </remarks>
    public string Body(IReadOnlyList<string> terms, long? after)
    {
        var conditions = Patterns(terms)
            .Select(pattern => new XElement(Dav + "like",
                new XElement(Dav + "prop", new XElement(Dav + "displayname")),
                new XElement(Dav + "literal", pattern)))
            .ToList();

        if (after is { } last)
        {
            conditions.Add(new XElement(Dav + "gt",
                new XElement(Dav + "prop", new XElement(FileId)),
                new XElement(Dav + "literal", last)));
        }

        var where = conditions.Count switch
        {
            // Nextcloud wants a condition; this one is everything.
            0 => new XElement(Dav + "like",
                new XElement(Dav + "prop", new XElement(Dav + "displayname")),
                new XElement(Dav + "literal", "%")),
            1 => conditions[0],
            _ => new XElement(Dav + "and", conditions),
        };

        var request = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Dav + "searchrequest",
                new XAttribute(XNamespace.Xmlns + "d", Dav),
                new XAttribute(XNamespace.Xmlns + "oc", FileId.Namespace),
                new XElement(Dav + "basicsearch",
                    new XElement(Dav + "select",
                        new XElement(Dav + "prop",
                            new XElement(Dav + "displayname"),
                            new XElement(Dav + "getcontentlength"),
                            new XElement(Dav + "getlastmodified"),
                            new XElement(Dav + "resourcetype"),
                            new XElement(Dav + "getetag"),
                            new XElement(FileId))),
                    new XElement(Dav + "from",
                        new XElement(Dav + "scope",
                            new XElement(Dav + "href", Scope),
                            new XElement(Dav + "depth", "infinity"))),
                    new XElement(Dav + "where", where),
                    new XElement(Dav + "orderby",
                        new XElement(Dav + "order",
                            new XElement(Dav + "prop", new XElement(FileId)),
                            new XElement(Dav + "ascending"))),
                    new XElement(Dav + "limit",
                        new XElement(Dav + "nresults", PageSize)))));

        return request.Declaration + Environment.NewLine + request.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Each path in a page with its file's number, where the server sent one.</summary>
    internal static Dictionary<string, long?> Numbers(string xml)
    {
        var numbers = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var response in XDocument.Parse(xml).Descendants(Dav + "response"))
        {
            if (response.Element(Dav + "href")?.Value is not { Length: > 0 } href)
                continue;

            numbers[DavResponse.PathOf(href)] =
                long.TryParse(response.Descendants(FileId).FirstOrDefault()?.Value.Trim(),
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                    out var number)
                    ? number
                    : null;
        }

        return numbers;
    }

    /// <summary>
    /// SQL <c>LIKE</c> patterns, one for each piece of text a matching name must contain.
    /// </summary>
    /// <remarks>
    /// Looser than the query, never stricter, because the answer is matched again by our own rule
    /// and a name the server leaves out is a name nobody sees. So only the plain runs of text are
    /// sent: a glob's wildcards, its <c>[…]</c> classes and its <c>{a,b}</c> alternatives are dropped
    /// rather than translated, and so is a backslash, which is an escape in the database's LIKE. A
    /// <c>%</c> or <c>_</c> left in a name is a wildcard to the server, which only widens it.
    /// Nextcloud's <c>like</c> ignores case, as our rule does.
    /// </remarks>
    internal static IReadOnlyList<string> Patterns(IEnumerable<string> terms) =>
        terms.SelectMany(Fragments)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(fragment => $"%{fragment}%")
            .ToList();

    private static IEnumerable<string> Fragments(string term)
    {
        var glob = NameQuery.IsGlob(term);
        var run = new StringBuilder();
        var depth = 0;

        foreach (var c in term)
        {
            if (glob && c is '{' or '[')
                depth++;
            else if (glob && c is '}' or ']')
                depth = Math.Max(0, depth - 1);
            else if (depth == 0 && c is not ('*' or '?' or '\\'))
            {
                run.Append(c);
                continue;
            }

            if (run.Length > 0)
                yield return run.ToString();
            run.Clear();
        }

        if (run.Length > 0)
            yield return run.ToString();
    }
}
