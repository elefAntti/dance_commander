using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using DanceCommander.Core.Fs;

namespace DanceCommander.App.Views;

/// <summary>
/// What a browser's Back and Forward do when held or right-clicked: list where they would go, so
/// that three steps are one pick, not three clicks. Up does the same for the folders above, on
/// hold only; its right-click is the parent's own menu (PaneView.UpButton.cs).
/// </summary>
public partial class PaneView
{
    /// <summary>Long enough that a click is never mistaken for a hold, short enough to feel like one.</summary>
    private static readonly TimeSpan HoldFor = TimeSpan.FromMilliseconds(450);

    /// <summary>How many entries a menu lists; the history holds a hundred, and a menu taller than the screen helps nobody.</summary>
    private const int MenuEntries = 15;

    private DispatcherTimer? _holdTimer;
    private Button? _held;
    private bool _heldMenuOpened;

    private void OnHistoryButtonPressed(object sender, MouseButtonEventArgs e)
    {
        _heldMenuOpened = false;
        _held = (Button)sender;

        if (_holdTimer is null)
        {
            _holdTimer = new DispatcherTimer { Interval = HoldFor };
            _holdTimer.Tick += OnHoldElapsed;
        }

        _holdTimer.Stop();
        _holdTimer.Start();
    }

    private void OnHistoryButtonReleased(object sender, MouseButtonEventArgs e) => _holdTimer?.Stop();

    private void OnHoldElapsed(object? sender, EventArgs e)
    {
        _holdTimer?.Stop();
        if (_held is not { IsMouseOver: true, IsEnabled: true } button)
            return;

        if (OpenHistoryMenu(button))
        {
            // The menu has the mouse now, so the button never sees its release: let go of it by
            // hand, and have the click that may still come do nothing.
            _heldMenuOpened = true;
            button.ReleaseMouseCapture();
        }
    }

    /// <summary>True once, after a hold opened a menu: the release that follows is not a click.</summary>
    private bool TakeHeldMenu()
    {
        var was = _heldMenuOpened;
        _heldMenuOpened = false;
        return was;
    }

    private void OnHistoryButtonRightClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenHistoryMenu((Button)sender);
    }

    /// <summary>Lists what the button would go to; false when there is nothing to list.</summary>
    private bool OpenHistoryMenu(Button button)
    {
        if (Tab is not { } tab)
            return false;

        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 4,
        };

        var at = tab.History.Position;
        if (ReferenceEquals(button, BackButton))
        {
            // Nearest first, as in a browser: the place you just left is at the top.
            for (var index = at - 1; index >= 0 && at - index <= MenuEntries; index--)
            {
                var entry = index;
                menu.Items.Add(PlaceItem(tab.History.Entries[entry], () => tab.GoToHistoryAsync(entry)));
            }
        }
        else if (ReferenceEquals(button, ForwardButton))
        {
            for (var index = at + 1; index < tab.History.Entries.Count && index - at <= MenuEntries; index++)
            {
                var entry = index;
                menu.Items.Add(PlaceItem(tab.History.Entries[entry], () => tab.GoToHistoryAsync(entry)));
            }
        }
        else
        {
            for (var above = tab.Uri?.Up; above is not null && menu.Items.Count < MenuEntries; above = above.Up)
            {
                var target = above;
                menu.Items.Add(PlaceItem(target, () =>
                {
                    tab.LeaveAddress();
                    return tab.NavigateAsync(target);
                }));
            }
        }

        if (menu.Items.Count == 0)
            return false;

        // Back to the listing when the menu goes without a pick, as the location menu does.
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(
            () =>
            {
                if (!IsKeyboardFocusWithin)
                    FocusGrid();
            },
            DispatcherPriority.Input);

        menu.IsOpen = true;
        return true;
    }

    /// <summary>A place: its name, with where it is muted beside it, since two folders can both be called "src".</summary>
    private static MenuItem PlaceItem(FsUri uri, Func<Task> go)
    {
        var item = new MenuItem
        {
            Header = uri.Name.Length > 0 ? uri.Name : uri.Display,
            InputGestureText = uri.Parent?.Display ?? string.Empty,
        };
        item.Click += (_, _) => _ = go();
        return item;
    }
}
