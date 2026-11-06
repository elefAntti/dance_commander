using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Navigation;

/// <summary>
/// When text typed into the address bar is a search rather than an address, and where it searches.
/// </summary>
/// <remarks>
/// The address bar stays an address bar: search only takes text that could never have been an
/// address. Anything with a separator or a colon is a path or a URI, and a mistyped one should say
/// so rather than quietly searching for itself; a bare word, which used to parse as a driveless
/// path that led nowhere, is a search. A leading <c>?</c> asks for one outright, as a browser's
/// does — the way to search for a word that is also the start of a bookmark's name.
/// </remarks>
public static class AddressSearch
{
    public const char Prefix = '?';

    /// <summary>The query the text asks for, or null when it is an address (or nothing at all).</summary>
    public static string? QueryIn(string text)
    {
        text = text.Trim();
        if (text.StartsWith(Prefix))
            return text[1..].Trim() is { Length: > 0 } query ? query : null;

        return text.Length == 0 || text.IndexOfAny(['\\', '/', ':']) >= 0 ? null : text;
    }

    /// <summary>True when the text asked for a search outright, so nothing else should be offered.</summary>
    public static bool IsExplicit(string text) => text.TrimStart().StartsWith(Prefix);

    /// <summary>
    /// A search from where the pane is standing — from the folder itself rather than from a view
    /// of it, so searching again does not stack "search over search over folder".
    /// </summary>
    public static FsUri From(FsUri here, string query) =>
        FsUri.View("search", Folder(here), query.Trim());

    /// <summary>
    /// The address bar's search: the whole computer, from here outwards, as a browser's searches the
    /// web. From a server, an archive or a page of the app's own, only the folder searched before: a
    /// search that set off from a server would leave it at once for the local disks.
    /// </summary>
    public static FsUri Everywhere(FsUri here, string query) =>
        Folder(here) is { IsLocal: true, Inner: null } folder
            ? FsUri.View(FsUri.EverywhereScheme, folder, query.Trim())
            : From(here, query);

    private static FsUri Folder(FsUri here) => here.IsView ? here.Inner! : here;
}
