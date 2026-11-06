using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Transfer;

namespace DanceCommander.App.Views;

/// <summary>
/// The Up button as the parent folder you can point at — what the ".." row used to be. A click goes
/// there, a drop lands there, a right-click opens its menu. The location icon in the address bar is
/// the same for the folder you are in: the icon is here, the arrow is the parent.
/// </summary>
public partial class PaneView
{
    /// <summary>Where Up goes, as a folder things can be put into; null at the top, or where nothing can be.</summary>
    private FsUri? ParentTarget => Tab?.Uri?.Up is { } parent && !parent.IsAbout && parent.Scheme != FsUri.FromScheme ? parent.Anchor : null;

    private void OnUpRightClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (Tab?.Uri?.Up is not { } parent)
            return;

        var menu = new ContextMenu
        {
            PlacementTarget = UpButton,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 4,
        };

        BuildingContextMenu?.Invoke(this, new ContextMenuRequest(menu, MenuTarget.Parent));
        if (menu.Items.Count > 0)
            menu.IsOpen = true;
    }

    private void OnUpDragOver(object sender, DragEventArgs e)
    {
        // A tab is not files: over Up it is over the pane, like anywhere else in the toolbar.
        if (DraggedTab(e) is not null)
        {
            OnPaneDragOver(sender, e);
            return;
        }

        NoteRoute(e.Data);
        e.Effects = ParentTarget is { } parent
            ? Evaluate(e, out _, onto: parent) switch
            {
                DropAction.Copy => DragDropEffects.Copy,
                DropAction.Move => DragDropEffects.Move,
                _ => DragDropEffects.None,
            }
            : DragDropEffects.None;

        // The same mark a folder row wears when it is the target.
        if (e.Effects == DragDropEffects.None)
            UpButton.ClearValue(BackgroundProperty);
        else
            UpButton.Background = (Brush)FindResource("SelectionActiveBrush");

        // Held here, the drag goes up, whether or not the files could be dropped in the parent:
        // somewhere above may take them. The pane's own DragOver never sees this, as it is handled.
        SpringLoad(!CarriesFiles(e.Data) || Tab?.Uri?.Up is null ? null : UpButton);

        e.Handled = true;
    }

    private void OnUpDragLeave(object sender, DragEventArgs e)
    {
        _pathsAtDrop = false;
        UpButton.ClearValue(BackgroundProperty);
    }

    private void OnUpDrop(object sender, DragEventArgs e)
    {
        try
        {
            UpDrop(sender, e);
        }
        finally
        {
            _pathsAtDrop = false;
        }
    }

    private void UpDrop(object sender, DragEventArgs e)
    {
        UpButton.ClearValue(BackgroundProperty);
        StopSpring();
        if (DraggedTab(e) is not null)
        {
            OnPaneDrop(sender, e);
            return;
        }

        e.Handled = true;
        e.Effects = DragDropEffects.None;

        if (ParentTarget is not { } parent)
            return;

        var action = Evaluate(e, out _, onto: parent);
        IReadOnlyList<FsUri> sources = _pathsAtDrop ? [] : SourcesFrom(e.Data);
        if (action == DropAction.None)
            return;

        if (sources.Count == 0)
        {
            DropContents(e, parent);
            return;
        }

        e.Effects = Reported(action);
        FilesDropped?.Invoke(this, new DropRequest(sources, parent, action));
    }
}
