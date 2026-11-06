namespace DanceCommander.Core.Fs;

/// <summary>
/// The application's own pages by what they are (ARCHITECTURE.md §6.19). The address is the one
/// fact; these say which page an address names, so commands and listings ask the same question.
/// </summary>
public static class AboutPages
{
    /// <summary>Every collection, one row each.</summary>
    public static FsUri Collections { get; } = FsUri.About("collections");

    /// <summary>One collection's members.</summary>
    public static FsUri Collection(string name) => FsUri.About($"collections/{name}");

    /// <summary>The collection an address shows, or null when it is not a collection's page.</summary>
    public static string? CollectionName(FsUri uri) =>
        uri.IsAbout && uri.Parent == Collections ? uri.Name : null;

    /// <summary>
    /// Whether the page's rows are files themselves — a collection's members — rather than places
    /// the application lists (drives, collections). On the first, what can be done to a row is
    /// what can be done to that file; on the second, nothing that reads or changes the row applies.
    /// </summary>
    public static bool HoldsFiles(FsUri uri) => uri == Shelf || CollectionName(uri) is not null;

    /// <summary>The shelf: files picked up across folders this session.</summary>
    public static FsUri Shelf { get; } = FsUri.About("shelf");

    /// <summary>Where you have been, newest first.</summary>
    public static FsUri History { get; } = FsUri.About("history");

    /// <summary>Every bookmark; groups are folders below it.</summary>
    public static FsUri Bookmarks { get; } = FsUri.About("bookmarks");
    
    /// <summary>This computer; pseudo-root of the file system containing all the disks etc.</summary>
    public static FsUri Computer { get; } = FsUri.About("computer");

    /// <summary>About the application: its version, its licences, where its settings and logs are.</summary>
    public static FsUri App { get; } = FsUri.About("app");

    /// <summary>One group of bookmarks.</summary>
    public static FsUri BookmarkGroup(string group) => FsUri.About($"bookmarks/{group}");

    /// <summary>The group a page shows, or null when it is not a group's page.</summary>
    public static string? BookmarkGroupName(FsUri uri) =>
        uri.IsAbout && uri.Parent == Bookmarks ? uri.Name : null;

    /// <summary>The bookmarks page or one of its groups: rows that are bookmarks, records pointing somewhere.</summary>
    public static bool IsBookmarks(FsUri uri) => uri == Bookmarks || BookmarkGroupName(uri) is not null;

    /// <summary>What a collection name may not contain: the address's own separators.</summary>
    public static string? CheckName(string name) =>
        string.IsNullOrWhiteSpace(name) ? "A collection needs a name."
        : name.IndexOfAny(['/', '\\', '?', '!', ':']) >= 0 ? @"A collection name cannot contain / \ ? ! or :."
        : null;
}
