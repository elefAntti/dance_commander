using System.Globalization;
using System.Xml.Linq;

namespace DanceCommander.Providers.WebDav;

/// <param name="Path">The resource's path on the server, decoded, with no trailing slash.</param>
/// <param name="Segments">
/// The same path one name at a time, each decoded on its own. What an address is built from:
/// <paramref name="Path"/> cannot tell a name's own <c>%2F</c> from a separator.
/// </param>
/// <param name="Length">Null when the server did not say, which is not the same as empty.</param>
public sealed record DavResource(
    string Path,
    IReadOnlyList<string> Segments,
    bool IsCollection,
    long? Length,
    DateTimeOffset? Modified,
    string? ETag);

/// <summary>
/// Reads a <c>207 Multi-Status</c> body.
/// </summary>
/// <remarks>
/// Written by hand rather than taken from a library because this is the whole of what the provider
/// needs from WebDAV and it is where the interoperability problems live. Two of them are visible
/// in the first response any server sends:
///
/// The namespace prefix is arbitrary. The specification's examples say <c>D:</c>, real servers say
/// <c>D:</c>, <c>ns0:</c> or nothing at all, and a parser that keys on the prefix works against
/// the documentation and fails against the server. Everything here is matched on the <c>DAV:</c>
/// namespace, which is the part that is fixed.
///
/// Hrefs are percent-encoded, and only sometimes absolute. <c>/a%20file.txt</c> and
/// <c>http://host:8127/a%20file.txt</c> are both legal answers to the same question, and a file
/// called "a file.txt" has to come out of either.
/// </remarks>
public static class DavResponse
{
    private static readonly XNamespace Dav = "DAV:";

    public static IReadOnlyList<DavResource> Parse(string xml, string basePath)
    {
        // The collection asked about is in its own listing; it is the folder, not an entry in it.
        var self = Normalise(basePath);
        return ParseAll(xml).Where(r => !string.Equals(r.Path, self, StringComparison.Ordinal)).ToList();
    }

    /// <summary>The resource asked about, as its own listing describes it: a folder or a file.</summary>
    public static DavResource? Self(string xml, string basePath)
    {
        var self = Normalise(basePath);
        return ParseAll(xml).FirstOrDefault(r => string.Equals(r.Path, self, StringComparison.Ordinal));
    }

    private static List<DavResource> ParseAll(string xml)
    {
        var document = XDocument.Parse(xml);
        var resources = new List<DavResource>();

        foreach (var response in document.Descendants(Dav + "response"))
        {
            if (response.Element(Dav + "href")?.Value is not { Length: > 0 } href)
                continue;

            // The first propstat that reports success. A server may return several, splitting the
            // properties it has from the ones it does not, and the 404 half is noise.
            var properties = response.Descendants(Dav + "propstat")
                .Where(p => p.Element(Dav + "status")?.Value.Contains(" 200", StringComparison.Ordinal) != false)
                .Select(p => p.Element(Dav + "prop"))
                .FirstOrDefault(p => p is not null)
                ?? response.Descendants(Dav + "prop").FirstOrDefault();

            var segments = SegmentsOf(href);
            var isCollection = properties?.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null;

            resources.Add(new DavResource(
                Join(segments),
                segments,
                isCollection,
                Length(properties),
                Modified(properties),
                properties?.Element(Dav + "getetag")?.Value));
        }

        return resources;
    }

    /// <summary>The decoded path from an href that may be absolute or relative.</summary>
    internal static string PathOf(string href) => Join(SegmentsOf(href));

    /// <summary>
    /// The names in an href, split while still encoded and then decoded one by one.
    /// </summary>
    /// <remarks>
    /// Taken apart by hand rather than by <see cref="Uri"/>, and nothing trimmed once decoded. A
    /// Linux server's names may hold a backslash, a trailing space or a "..\": <see cref="Uri"/>
    /// turns a backslash into a separator and removes dot segments, and a trim makes
    /// "report.txt " into its neighbour "report.txt". Each of those gave the row the address of
    /// another file or of the folder itself, which Del then deleted (review, 2026-10-22). The
    /// whitespace trimmed is the XML's, around the still-encoded href.
    /// </remarks>
    internal static IReadOnlyList<string> SegmentsOf(string href)
    {
        var text = href.Trim();

        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0 && text[..scheme].All(char.IsAsciiLetter))
        {
            var path = text.IndexOf('/', scheme + 3);
            text = path < 0 ? "/" : text[path..];
        }

        var end = text.IndexOfAny(['?', '#']);
        if (end >= 0)
            text = text[..end];

        return text.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToList();
    }

    private static string Join(IReadOnlyList<string> segments) => "/" + string.Join('/', segments);

    /// <summary>Leading slash, no trailing slash — so a collection and a file compare alike.</summary>
    /// <remarks>Not trimmed: a name may end in a space.</remarks>
    internal static string Normalise(string path)
    {
        var text = path;
        if (!text.StartsWith('/'))
            text = "/" + text;

        return text.Length > 1 ? text.TrimEnd('/') : "/";
    }

    private static long? Length(XElement? properties)
    {
        var text = properties?.Element(Dav + "getcontentlength")?.Value;
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static DateTimeOffset? Modified(XElement? properties)
    {
        // getlastmodified is RFC 1123 ("Fri, 18 Sep 2026 20:33:49 GMT"); creationdate is ISO 8601.
        // Servers are inconsistent about which they fill in, so both are tried.
        if (properties?.Element(Dav + "getlastmodified")?.Value is { Length: > 0 } modified &&
            DateTimeOffset.TryParse(modified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        if (properties?.Element(Dav + "creationdate")?.Value is { Length: > 0 } created &&
            DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var iso))
        {
            return iso;
        }

        return null;
    }
}
