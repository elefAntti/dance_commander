using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DanceCommander.App.ViewModels;
using DanceCommander.Core.Fs;

namespace DanceCommander.App.Views;

/// <summary>
/// The bookmark star at the right of the address bar and the New tab button beside the bookmarks:
/// what a browser's toolbar offers for the mouse, so neither needs a key to be known first.
/// </summary>
public partial class PaneView
{
    private static readonly string StarOutline = char.ConvertFromUtf32(0xE734); // FavoriteStar, system icon font
    private static readonly string StarFilled = char.ConvertFromUtf32(0xE735); // FavoriteStarFill

    private IEnumerable<BookmarkItemViewModel> _bookmarks = [];

    /// <summary>
    /// A command the pane's own buttons ask for by id. The window runs it from the registry, on this
    /// pane, so the button and the key it names can never do different things.
    /// </summary>
    public event EventHandler<string>? CommandRequested;

    public IEnumerable<BookmarkItemViewModel> Bookmarks
    {
        get => _bookmarks;
        set
        {
            if (_bookmarks is INotifyCollectionChanged old)
                old.CollectionChanged -= OnBookmarksChanged;
            _bookmarks = value;
            if (_bookmarks is INotifyCollectionChanged added)
                added.CollectionChanged += OnBookmarksChanged;
            ShowStar();
        }
    }

    private bool IsBookmarkedHere =>
        Tab?.Uri is { } here && _bookmarks.Any(b => b.Uri == here);

    private void OnBookmarksChanged(object? sender, NotifyCollectionChangedEventArgs e) => ShowStar();

    private void OnAddressAreaHover(object sender, MouseEventArgs e) => ShowStar();

    private void OnAddressFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e) => ShowStar();

    /// <summary>
    /// Filled and always there on a bookmarked place; outlined only under the pointer otherwise.
    /// Hidden rather than collapsed, so the room it takes never changes and the text never moves.
    /// </summary>
    private void ShowStar()
    {
        if (StarButton is null)
            return;

        var bookmarked = IsBookmarkedHere;
        StarButton.Content = bookmarked ? StarFilled : StarOutline;
        StarButton.ToolTip = bookmarked
            ? "Bookmarked · click to remove or manage"
            : "Bookmark this location (Ctrl+D)";

        var shown = Tab?.Uri is not null
            && !AddressBox.IsKeyboardFocusWithin
            && (bookmarked || AddressArea.IsMouseOver);
        StarButton.Visibility = shown ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>
    /// Outlined: bookmark this place, which asks for the name inline as Ctrl+D does. Filled: a menu
    /// to remove the bookmark or go to the page where bookmarks are renamed and grouped, which is
    /// what a browser's filled star offers.
    /// </summary>
    private void OnStarClick(object sender, RoutedEventArgs e)
    {
        if (!IsBookmarkedHere)
        {
            CommandRequested?.Invoke(this, "dc.bookmark");
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = StarButton,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 4,
        };

        var remove = new MenuItem { Header = "Remove bookmark" };
        remove.Click += (_, _) => CommandRequested?.Invoke(this, "dc.removeLocationBookmark");
        menu.Items.Add(remove);

        var manage = new MenuItem { Header = "Manage bookmarks" };
        manage.Click += async (_, _) =>
        {
            if (Tab is { } tab)
            {
                await tab.NavigateAsync(AboutPages.Bookmarks);
                FocusGrid();
            }
        };
        menu.Items.Add(manage);

        menu.IsOpen = true;
    }

    private void OnNewTabClick(object sender, RoutedEventArgs e) =>
        CommandRequested?.Invoke(this, "dc.newTab");
}
