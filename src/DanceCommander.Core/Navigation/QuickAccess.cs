using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Navigation;

/// <summary>A folder pinned in Explorer's Quick access, in Explorer's order.</summary>
public sealed record PinnedFolder(string Name, string Path);

/// <summary>Reads the folders pinned in Explorer's Quick access (SETTINGS_PLAN.md §8).</summary>
public interface IQuickAccess
{
    /// <summary>The pinned folders only, never the ones Windows adds as frequent; empty when it can't tell.</summary>
    Task<IReadOnlyList<PinnedFolder>> PinnedAsync(CancellationToken ct);
}

/// <summary>
/// Turns Quick access into bookmarks: on a first run, as the whole list; afterwards, as what is
/// pinned and not yet bookmarked. Never removes or renames anything already there.
/// </summary>
public static class QuickAccessBookmarks
{
    public const string Group = "Quick access";

    /// <summary>
    /// The first list: the fixed places, always, as the defaults have always been; then the pinned
    /// folders that are not one of them, as the group "Quick access" in Explorer's order. The fixed
    /// ones come first so that every machine starts from the same few, whatever Explorer pins
    /// (the user, 2026-10-14).
    /// </summary>
    public static List<Bookmark> FirstRun(IReadOnlyList<PinnedFolder> pinned, IReadOnlyList<Bookmark> always) =>
        [.. always, .. NotYetBookmarked(pinned, always)];

    /// <summary>
    /// The pinned folders that no bookmark goes to yet, named so as not to take an existing name:
    /// adding under a taken name would replace that bookmark. Two pinned folders called
    /// "Documents" (OneDrive's and the local one) are told apart by the folder they are in.
    /// </summary>
    public static List<Bookmark> NotYetBookmarked(IReadOnlyList<PinnedFolder> pinned, IReadOnlyList<Bookmark> existing)
    {
        var added = new List<Bookmark>();
        foreach (var folder in pinned)
        {
            if (string.IsNullOrWhiteSpace(folder.Path))
                continue;

            string uri;
            try
            {
                uri = FsUri.Local(folder.Path).ToString();
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (existing.Concat(added).Any(b => SamePlace(b.Uri, uri)))
                continue;

            var name = string.IsNullOrWhiteSpace(folder.Name) ? Path.GetFileName(folder.Path.TrimEnd('\\')) : folder.Name.Trim();
            // Where it is: the folder above, or for a folder at a drive's root, the drive.
            var above = (Path.GetDirectoryName(folder.Path.TrimEnd('\\')) ?? string.Empty).TrimEnd('\\');
            var parent = Path.GetFileName(above) is { Length: > 0 } named ? named : above;
            added.Add(new Bookmark(Unique(name, parent, [.. existing, .. added]), uri, Group));
        }

        return added;
    }

    private static string Unique(string name, string? parent, IReadOnlyList<Bookmark> taken)
    {
        bool Taken(string candidate) => taken.Any(b => string.Equals(b.Name, candidate, StringComparison.OrdinalIgnoreCase));

        if (!Taken(name))
            return name;
        if (!string.IsNullOrEmpty(parent) && !Taken($"{name} ({parent})"))
            return $"{name} ({parent})";

        var n = 2;
        while (Taken($"{name} ({n})"))
            n++;
        return $"{name} ({n})";
    }

    /// <summary>Local paths are one place whatever their case.</summary>
    private static bool SamePlace(string a, string b) =>
        FsUri.TryParse(a, out var x, out _) && FsUri.TryParse(b, out var y, out _)
            ? x.IsLocal && y.IsLocal ? string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase) : x == y
            : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
