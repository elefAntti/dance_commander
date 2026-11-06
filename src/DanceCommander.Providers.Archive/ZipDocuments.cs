namespace DanceCommander.Providers.Archive;

/// <summary>
/// Formats that are zip archives inside and documents to the person holding them. Enter opens
/// these in their application rather than browsing into them (PLAN.md §4, 2026-10-01).
/// </summary>
/// <remarks>
/// Content alone cannot tell: a .docx is a zip, and so is a .nupkg. The difference is what you
/// want from Enter. Nobody pressing it on a report wants its XML parts; plenty of people pressing
/// it on a .jar or a .vsix want to see what is in it, so packages are not here. A list rather than
/// the file associations, because those would send .7z to 7-Zip instead of opening it as a folder.
/// Extract still takes any of these apart, since that one is asked for explicitly.
/// </remarks>
public static class ZipDocuments
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Office Open XML: Word, Excel, PowerPoint, Visio, themes.
        ".docx", ".docm", ".dotx", ".dotm",
        ".xlsx", ".xlsm", ".xlsb", ".xltx", ".xltm",
        ".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm",
        ".vsdx", ".vsdm", ".vstx", ".vstm", ".thmx",

        // OpenDocument (LibreOffice and friends), documents and templates.
        ".odt", ".ods", ".odp", ".odg", ".odf", ".ott", ".ots", ".otp", ".otg",

        // Other documents that happen to be zips.
        ".epub", ".xps", ".oxps", ".3mf", ".kmz", ".pbix", ".pages", ".numbers", ".key",
    };

    /// <summary>True when Enter should open this file in its application, whatever its bytes say.</summary>
    public static bool IsDocument(string fileName) => Extensions.Contains(Path.GetExtension(fileName));
}
