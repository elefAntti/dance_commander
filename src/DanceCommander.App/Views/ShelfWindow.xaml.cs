using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DanceCommander.App.Services;
using DanceCommander.Core.Collections;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Operations;

namespace DanceCommander.App.Views;

/// <summary>One thing on the shelf: the file it points at, as a name and an icon.</summary>
public sealed record ShelfItem(FsUri Uri, string Name, string Where, ImageSource? Icon);

/// <summary>
/// The shelf in a small window that stays above the others, for handing files to a program that
/// fills the screen (PLAN.md, Brainstorming: "Tray mode"). A drop adds to the shelf, a drag takes
/// from it, Del takes out, Esc puts the window away. It opens without taking the focus, so the
/// program you are dragging from, or to, stays where it was.
/// </summary>
/// <remarks>
/// Not owned by the main window: an owned window goes when its owner is minimised, and the main
/// window being out of the way is the whole case for this one. The main window closes it instead.
/// </remarks>
public partial class ShelfWindow : Window
{
    private readonly IShelf _shelf;
    private readonly IIconService _icons;
    private readonly IUndoStack _undo;

    private bool _closing;
    private bool _placed;

    private Point _pressedAt;
    private bool _mayDrag;

    /// <summary>A press on one of several selected items: it narrows to that one only if no drag follows.</summary>
    private ShelfItem? _narrowTo;

    public ShelfWindow(IShelf shelf, IIconService icons, IUndoStack undo)
    {
        _shelf = shelf;
        _icons = icons;
        _undo = undo;
        InitializeComponent();
        TitleBar.Apply(this);

        DragOver += OnShelfDragOver;
        Drop += OnShelfDrop;
        _shelf.Changed += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (IsVisible)
                Refresh();
        });

        // Put away, not closed: the next Win+Shift+B brings it back where it was.
        Closing += (_, e) =>
        {
            if (_closing)
                return;
            e.Cancel = true;
            Hide();
        };
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        Refresh();
        if (!_placed)
        {
            // The corner nearest the taskbar's clock, where a picture-in-picture window would go.
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 16;
            Top = area.Bottom - Height - 16;
            _placed = true;
        }

        Show();
    }

    /// <summary>For the main window's closing: the shelf goes with it.</summary>
    public void CloseForGood()
    {
        _closing = true;
        Close();
    }

    private void Refresh()
    {
        var items = _shelf.Items.Select(Describe).ToList();
        Items.ItemsSource = items;
        EmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Title = items.Count == 0 ? "Shelf" : $"Shelf — {items.Count:N0}";
    }

    private ShelfItem Describe(FsUri uri)
    {
        // Folders get the folder icon when that is cheap to know. A share is not asked: a server that
        // does not answer would hold the window up, and the icon is not worth that.
        var isDirectory = false;
        if (uri.IsLocal)
        {
            var path = uri.ToLocalPath();
            isDirectory = !path.StartsWith(@"\\", StringComparison.Ordinal) && Directory.Exists(path);
        }

        var entry = new FsEntry(uri, uri.Name, isDirectory, 0, null);
        return new ShelfItem(uri, uri.Name, (uri.Parent ?? uri).Display, _icons.GetIcon(entry));
    }

    private IReadOnlyList<ShelfItem> Selected => Items.SelectedItems.OfType<ShelfItem>().ToList();

    private void OnItemsKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Delete when Selected is { Count: > 0 } selected:
                Remove(selected.Select(item => item.Uri).ToList());
                e.Handled = true;
                break;
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Takes items off the shelf — the files stay where they are — as Del on its page does.</summary>
    private void Remove(IReadOnlyList<FsUri> items)
    {
        _shelf.Remove(items);
        var what = items.Count == 1 ? items[0].Name : $"{items.Count:N0} items";
        _undo.Push(new DelegateUndo(
            $"Remove {what} from the shelf",
            "(taken off the shelf; no file was touched)",
            _ =>
            {
                _shelf.Add(items);
                return Task.CompletedTask;
            }));
    }

    // Dragging out. The list selects on the press, so a press on one of several selected items
    // would narrow the selection to it before the drag could take them all; that press is held
    // back, and only narrows on a release that did not become a drag.

    private void OnItemsPreviewLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressedAt = e.GetPosition(Items);
        _narrowTo = null;
        var item = ItemUnder(e.OriginalSource as DependencyObject);
        _mayDrag = item is not null;

        if (item is { IsSelected: true, DataContext: ShelfItem shelved }
            && Items.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _narrowTo = shelved;
            Items.Focus();
            e.Handled = true;
        }
    }

    private void OnItemsPreviewLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _mayDrag = false;
        if (_narrowTo is { } only)
        {
            _narrowTo = null;
            Items.SelectedItem = only;
        }
    }

    private void OnItemsPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_mayDrag || e.LeftButton != MouseButtonState.Pressed)
            return;

        var moved = e.GetPosition(Items) - _pressedAt;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _mayDrag = false;
        _narrowTo = null;
        if (Selected is not { Count: > 0 } selected)
            return;

        // Copy, never move: what leaves the shelf is a copy, as from any shelf. Allowing Move would
        // let Explorer choose it for a drop on the same drive, and the file would leave its folder
        // behind the shelf's back. Link is allowed only for the shelf's own page and collections.
        var data = PaneView.DragData(selected.Select(item => item.Uri).ToList());
        data.SetData(PaneView.ShelfFormat, true);
        DragDrop.DoDragDrop(Items, data, DragDropEffects.Copy | DragDropEffects.Link);
    }

    // Dropping on. Whatever is dropped goes onto the shelf as itself, as a drop on the shelf's
    // page does; nothing is copied.

    private void OnShelfDragOver(object sender, DragEventArgs e)
    {
        e.Effects = Droppable(e.Data).Count > 0 ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnShelfDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        // Link, said explicitly: left alone, WPF reports everything the source allowed, and a
        // source that hears Move deletes its files.
        e.Effects = DragDropEffects.None;
        if (Droppable(e.Data) is { Count: > 0 } sources)
        {
            e.Effects = DragDropEffects.Link;
            _shelf.Add(sources);
        }
    }

    /// <summary>What a drop would add: files and folders, not our own pages, and not the shelf's own drag.</summary>
    private static IReadOnlyList<FsUri> Droppable(IDataObject data)
    {
        if (data.GetDataPresent(PaneView.ShelfFormat))
            return [];

        var sources = PaneView.SourcesFrom(data);
        return sources.Any(uri => uri.IsAbout) ? [] : sources;
    }

    private static ListBoxItem? ItemUnder(DependencyObject? source) => TreeWalk.Ancestor<ListBoxItem>(source);
}
