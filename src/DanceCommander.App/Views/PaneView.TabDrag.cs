using System.Windows;
using System.Windows.Input;
using DanceCommander.App.ViewModels;

namespace DanceCommander.App.Views;

/// <param name="Before">Where in the strip it goes, as <see cref="PaneViewModel.Adopt"/>; null for the end.</param>
public sealed record TabDropRequest(TabViewModel Tab, int? Before);

/// <summary>
/// Dragging a tab: along its own strip to rearrange, or onto the other pane to move it there
/// (PLAN.md §3, decided 2026-10-10). It is not a drag of files and must never be taken for one.
/// </summary>
public partial class PaneView
{
    /// <summary>Marks a drag of a tab. It carries nothing else: no paths, no URIs.</summary>
    private const string TabFormat = "DanceCommander.Tab";

    /// <summary>
    /// The tab being dragged and the pane it comes from. Kept here rather than in the drag's data,
    /// so the data is only a marker: a folder row, Explorer or the shelf finds no files in it and
    /// refuses it, where a tab carrying its folder would have been a folder copied by a slip.
    /// </summary>
    private static (TabViewModel Tab, PaneViewModel From)? s_draggedTab;

    private Point _tabPressedAt;
    private TabViewModel? _pressedTab;

    /// <summary>Raised when a tab is dropped on this pane; the window moves it.</summary>
    public event EventHandler<TabDropRequest>? TabDropped;

    private void OnTabClicked(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null || sender is not FrameworkElement { DataContext: TabViewModel tab })
            return;

        var changed = !ReferenceEquals(ViewModel.ActiveTab, tab);
        ViewModel.ActiveTab = tab;
        _pressedTab = tab;
        _tabPressedAt = e.GetPosition(this);

        // The keyboard to the tab's list, as Ctrl+PageUp gives it. Left to the press, the strip's
        // scroller took it, and the arrows went nowhere (2026-10-15). Handled so the scroller
        // doesn't take it back; a drag starts from MouseMove and is unaffected. Not for the tab
        // already in front whose list has the keyboard: FocusGrid goes to the first mark, which
        // moved the cursor F2 and Enter act on (review, 2026-10-16).
        e.Handled = true;
        if (changed || !FileGrid.IsKeyboardFocusWithin)
            FocusGrid();
    }

    private void OnTabMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedTab is not { } tab || e.LeftButton != MouseButtonState.Pressed)
        {
            _pressedTab = null;
            return;
        }

        var moved = e.GetPosition(this) - _tabPressedAt;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressedTab = null;
        if (ViewModel is not { } pane || sender is not DependencyObject chip)
            return;

        s_draggedTab = (tab, pane);
        try
        {
            DragDrop.DoDragDrop(chip, new DataObject(TabFormat, "tab"), DragDropEffects.Move);
        }
        finally
        {
            s_draggedTab = null;
            HideTabDropMark();
        }
    }

    private static (TabViewModel Tab, PaneViewModel From)? DraggedTab(DragEventArgs e) =>
        e.Data.GetDataPresent(TabFormat) ? s_draggedTab : null;

    /// <summary>
    /// Where a tab dropped here now would go, or false if nowhere. In its own pane only onto the
    /// strip, and only somewhere it isn't already. Onto the other pane anywhere: with one tab that
    /// pane has no strip to aim at, so the whole pane takes it, at the end.
    /// </summary>
    private bool TabDropPlace(DragEventArgs e, (TabViewModel Tab, PaneViewModel From) dragged, out int? before)
    {
        before = TabInsertionUnder(e);
        if (ViewModel is not { } pane)
            return false;
        if (!ReferenceEquals(dragged.From, pane))
            return true;

        var at = pane.Tabs.IndexOf(dragged.Tab);
        return before is { } index && at >= 0 && index != at && index != at + 1;
    }

    private void OnTabDragOver(DragEventArgs e, (TabViewModel Tab, PaneViewModel From) dragged)
    {
        if (TabDropPlace(e, dragged, out var before))
        {
            e.Effects = DragDropEffects.Move;
            ShowTabDropMark(before);
        }
        else
        {
            e.Effects = DragDropEffects.None;
            HideTabDropMark();
        }
        e.Handled = true;
    }

    private void OnTabDrop(DragEventArgs e, (TabViewModel Tab, PaneViewModel From) dragged)
    {
        HideTabDropMark();
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (!TabDropPlace(e, dragged, out var before))
            return;

        e.Effects = DragDropEffects.Move;
        TabDropped?.Invoke(this, new TabDropRequest(dragged.Tab, before));
    }

    /// <summary>
    /// The place in the strip under the pointer: the index of the tab whose left half it is over,
    /// or the count past the middle of the last. Null when it is not over the strip.
    /// </summary>
    private int? TabInsertionUnder(DragEventArgs e)
    {
        if (!TabScroller.IsVisible || ViewModel is not { } pane)
            return null;

        var point = e.GetPosition(TabScroller);
        if (point.X < 0 || point.Y < 0 || point.X > TabScroller.ActualWidth || point.Y > TabScroller.ActualHeight)
            return null;

        for (var i = 0; i < pane.Tabs.Count; i++)
        {
            if (TabStrip.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement chip &&
                point.X < chip.TranslatePoint(new Point(chip.ActualWidth / 2, 0), TabScroller).X)
            {
                return i;
            }
        }

        return pane.Tabs.Count;
    }

    /// <summary>
    /// Where the tab would land: a bar between two chips, or with no strip a line along the top of
    /// the pane, where the strip will appear with the tab in it.
    /// </summary>
    private void ShowTabDropMark(int? before)
    {
        const double Thickness = 2;
        TabDropMark.Visibility = Visibility.Visible;

        if (!TabScroller.IsVisible || ViewModel is not { } pane)
        {
            TabDropMark.Margin = new Thickness(0);
            TabDropMark.Width = PaneRoot.ActualWidth;
            TabDropMark.Height = Thickness;
            return;
        }

        // The left edge of the tab it goes before, or the right edge of the last one.
        var index = Math.Min(before ?? pane.Tabs.Count, pane.Tabs.Count);
        var edge = index < pane.Tabs.Count ? index : index - 1;
        if (TabStrip.ItemContainerGenerator.ContainerFromIndex(edge) is not FrameworkElement chip)
        {
            HideTabDropMark();
            return;
        }

        var at = chip.TranslatePoint(new Point(index < pane.Tabs.Count ? 0 : chip.ActualWidth, 0), PaneRoot);
        var left = TabScroller.TranslatePoint(new Point(0, 0), PaneRoot).X;
        var x = Math.Clamp(at.X - Thickness / 2, left, left + TabScroller.ActualWidth - Thickness);
        TabDropMark.Margin = new Thickness(x, at.Y, 0, 0);
        TabDropMark.Width = Thickness;
        TabDropMark.Height = chip.ActualHeight;
    }

    private void HideTabDropMark() => TabDropMark.Visibility = Visibility.Collapsed;
}
