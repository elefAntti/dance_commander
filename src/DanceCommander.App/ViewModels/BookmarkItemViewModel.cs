using System.Windows.Media;
using DanceCommander.App.Services;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Navigation;
using DanceCommander.Providers.Local;

namespace DanceCommander.App.ViewModels;

/// <summary>A group heading in the bookmarks popup.</summary>
public sealed record BookmarkGroupHeader(string Name);

public sealed class BookmarkItemViewModel
{
    public BookmarkItemViewModel(
        Bookmark bookmark, FsUri uri, IIconService icons, IGitService? git = null)
    {
        Name = bookmark.Name;
        Group = bookmark.Group;
        Uri = uri;
        Display = uri.Display;
        _icons = icons;

        _git = git;
    }

    public string Name { get; }

    public string? Group { get; }

    public FsUri Uri { get; }

    public string Display { get; }

    /// <summary>
    /// Asked for when the popup first draws it, not when the list is built at startup: the special
    /// folders' icons come from the shell reading their desktop.ini, which cost a tenth of a second
    /// of every launch for a popup that may never be opened.
    /// </summary>
    public ImageSource? Icon => _icon ??= _icons.GetLocationIcon(Uri);

    private readonly IIconService _icons;
    private ImageSource? _icon;

    /// <summary>The branch, when this bookmark points inside a repository.</summary>
    /// <remarks>
    /// A list of bookmarked repositories that does not say which branch each is on is a list you
    /// have to visit one by one to read. The lookup is a cached read of .git/HEAD, made when the
    /// popup draws the row — the list itself is built at startup, and was reading every
    /// bookmark's .git then.
    /// </remarks>
    public string? Branch => _branchRead ? _branch : ReadBranch();

    private readonly IGitService? _git;
    private string? _branch;
    private bool _branchRead;

    private string? ReadBranch()
    {
        _branch = Uri.IsLocal ? _git?.Describe(Uri)?.Label : null;
        _branchRead = true;
        return _branch;
    }

    /// <summary>
    /// Builds the popup's contents: ungrouped bookmarks first, then one heading per group.
    /// Groups come from a "Group/Name" convention when adding, so foldering needs no extra UI.
    /// </summary>
    public static IReadOnlyList<object> Arrange(IEnumerable<BookmarkItemViewModel> bookmarks)
    {
        var items = new List<object>();
        var all = bookmarks.ToList();

        foreach (var loose in all.Where(b => string.IsNullOrWhiteSpace(b.Group)))
            items.Add(loose);

        foreach (var group in all
            .Where(b => !string.IsNullOrWhiteSpace(b.Group))
            .GroupBy(b => b.Group!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            items.Add(new BookmarkGroupHeader(group.Key));
            items.AddRange(group);
        }

        return items;
    }
}
