using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Fs;

namespace DanceCommander.App.Views;

/// <summary>
/// The location icon at the left of the address bar (§6.18): the current folder as a thing you can
/// point at. A click opens its menu, a drag carries it, a drop lands in it.
/// </summary>
public partial class PaneView
{
    private Point _chipPressedAt;
    private bool _chipPressed;

    /// <summary>
    /// Opens <em>This folder</em> under the icon — the same menu empty space opens, built by the same
    /// code, so the three ways in can never drift apart. Also what Ctrl+. does.
    /// </summary>
    public void OpenLocationMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = LocationChip,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 4,
        };

        BuildingContextMenu?.Invoke(this, new ContextMenuRequest(menu, MenuTarget.Location));
        if (menu.Items.Count == 0)
            return;

        // Back to the listing when the menu goes without running anything — pressing Escape on a
        // menu opened from the keyboard should not leave the keyboard pointing at nothing. A command
        // that raised a prompt has put focus there by now, and is left alone.
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(
            () =>
            {
                if (!IsKeyboardFocusWithin)
                    FocusGrid();
            },
            System.Windows.Threading.DispatcherPriority.Input);

        menu.IsOpen = true;
    }

    private void OnChipPressed(object sender, MouseButtonEventArgs e)
    {
        // Captured, so a press that becomes a drag keeps reporting movement once the pointer leaves
        // a 24-pixel target — which it does almost at once.
        _chipPressedAt = e.GetPosition(this);
        _chipPressed = true;
        LocationChip.CaptureMouse();
        e.Handled = true;
    }

    private void OnChipMouseMove(object sender, MouseEventArgs e)
    {
        if (!_chipPressed || e.LeftButton != MouseButtonState.Pressed)
            return;

        var moved = e.GetPosition(this) - _chipPressedAt;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return; // A click that wobbles is still a click.
        }

        _chipPressed = false;
        LocationChip.ReleaseMouseCapture();

        // A flattened tree or a search is a view of a folder, not a folder; the drag carries the
        // folder it was made from, which is the one thing that can be copied somewhere.
        if (Tab?.Uri?.Anchor is not { } here)
            return;

        var data = DragData([here]);
        data.SetData(LocationFormat, true);
        DragDrop.DoDragDrop(LocationChip, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void OnChipReleased(object sender, MouseButtonEventArgs e)
    {
        if (!_chipPressed)
            return;

        _chipPressed = false;
        LocationChip.ReleaseMouseCapture();
        e.Handled = true;
        OpenLocationMenu();
    }

    private void OnChipRightClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenLocationMenu();
    }

    // Dropping on the icon is dropping on the pane with no folder row under the pointer: the files
    // land in the folder the pane is showing. The pane's own handlers decide it; the icon only
    // wears the mark, so a full listing still has somewhere obvious to aim.

    private void OnChipDragOver(object sender, DragEventArgs e)
    {
        OnPaneDragOver(sender, e);
        // The mark says files would land in this folder; a tab dropped here lands in the pane.
        LocationChip.Tag = e.Effects == DragDropEffects.None || DraggedTab(e) is not null ? null : "drop";
    }

    private void OnChipDragLeave(object sender, DragEventArgs e) => LocationChip.Tag = null;

    private void OnChipDrop(object sender, DragEventArgs e)
    {
        LocationChip.Tag = null;
        OnPaneDrop(sender, e);
    }

    /// <summary>
    /// What a drag carries: our own format with full URIs, and file paths for everyone else when
    /// there are any. Shared by row drags, the icon and the shelf.
    /// </summary>
    internal static DataObject DragData(IReadOnlyList<FsUri> sources)
    {
        var data = new DataObject();
        data.SetData(UriFormat, sources.Select(uri => uri.ToString()).ToArray());

        // Explorer and everything else understand paths and nothing else. Offering FileDrop for a
        // remote or in-archive entry would promise a file that is not there.
        if (sources.All(uri => uri.IsLocal))
        {
            var paths = new System.Collections.Specialized.StringCollection();
            foreach (var uri in sources)
                paths.Add(uri.ToLocalPath());
            data.SetFileDropList(paths);
        }

        return data;
    }
}
