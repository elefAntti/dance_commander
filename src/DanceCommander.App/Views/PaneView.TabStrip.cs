using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DanceCommander.App.ViewModels;

namespace DanceCommander.App.Views;

/// <summary>
/// The tab strip once it overflows. There is no scroll bar: the side with more tabs fades out, and
/// that faded edge is the control for every kind of input there is. A click pages along, a press
/// held keeps paging, a drag held there scrolls so a tab or files can reach a tab out of sight, and
/// the wheel works anywhere on the strip, sideways tilt and touchpad swipes included.
/// </summary>
public partial class PaneView
{
    /// <summary>WM_MOUSEHWHEEL: a tilt wheel or a sideways touchpad swipe. WPF raises no event for it.</summary>
    private const int WmMouseHWheel = 0x020E;

    /// <summary>Within half a pixel counts as at the end, against layout rounding.</summary>
    private const double EdgeSlack = 0.5;

    private HwndSource? _wheelSource;
    private DispatcherTimer? _tabEdgeDragScroll;
    private int _tabEdgeDragDirection;

    /// <summary>
    /// Called from the constructor. The hook goes on the window's handle, which a pane only has
    /// once it is in a window; both panes hook it, and only the one under the pointer answers.
    /// </summary>
    private void HookSidewaysWheel()
    {
        Loaded += (_, _) =>
        {
            _wheelSource = PresentationSource.FromVisual(this) as HwndSource;
            _wheelSource?.AddHook(OnWindowMessage);
        };
        Unloaded += (_, _) =>
        {
            _wheelSource?.RemoveHook(OnWindowMessage);
            _wheelSource = null;
        };
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmMouseHWheel && TabStripArea.IsVisible && TabStripArea.IsMouseOver)
        {
            // The delta is the high word, positive to the right.
            var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset + delta / 2.0);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// An edge is there only on a side with more tabs. Its fade is not drawn while the current tab
    /// reaches that edge: that tab is the pane's own surface, joined to it, and fading it would
    /// fade the join. Its clipped title says there is more just as well.
    /// </summary>
    private void ShowTabEdges(bool currentReachesLeft, bool currentReachesRight)
    {
        var shown = ViewModel?.ShowTabStrip == true;
        var moreLeft = shown && TabScroller.HorizontalOffset > EdgeSlack;
        var moreRight = shown && TabScroller.HorizontalOffset < TabScroller.ScrollableWidth - EdgeSlack;
        TabEdgeLeft.Visibility = moreLeft ? Visibility.Visible : Visibility.Collapsed;
        TabEdgeRight.Visibility = moreRight ? Visibility.Visible : Visibility.Collapsed;

        var fadeLeft = moreLeft && !currentReachesLeft;
        var fadeRight = moreRight && !currentReachesRight;
        TabScroller.OpacityMask = fadeLeft || fadeRight ? EdgeMask(fadeLeft, fadeRight) : null;
    }

    /// <summary>Opaque across the strip, clear at the outer side of each faded edge.</summary>
    private LinearGradientBrush EdgeMask(bool left, bool right)
    {
        var fraction = TabScroller.ActualWidth > 0 ? Math.Min(0.5, TabEdgeRight.Width / TabScroller.ActualWidth) : 0;
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        mask.GradientStops.Add(new GradientStop(left ? Colors.Transparent : Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, left ? fraction : 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, right ? 1 - fraction : 1));
        mask.GradientStops.Add(new GradientStop(right ? Colors.Transparent : Colors.Black, 1));
        mask.Freeze();
        return mask;
    }

    private void OnTabEdgeClick(object sender, RoutedEventArgs e) =>
        PageTabs(ReferenceEquals(sender, TabEdgeLeft) ? -1 : 1);

    /// <summary>
    /// Pages the strip one way. The tab that was half under the edge being paged towards ends up
    /// whole, just inside the opposite edge, so the place is kept the way a page of text keeps its
    /// last line. A tab wider than the whole strip would never move that way; it pages a viewport.
    /// </summary>
    private void PageTabs(int direction)
    {
        var edge = TabEdgeRight.Width;
        var width = TabScroller.ViewportWidth;
        var offset = TabScroller.HorizontalOffset;
        double? target = null;

        for (var i = 0; i < TabStrip.Items.Count; i++)
        {
            if (TabStrip.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement chip)
                continue;

            var left = chip.TranslatePoint(new Point(0, 0), TabScroller).X;
            var right = left + chip.ActualWidth;
            if (direction > 0 && right > width - edge + EdgeSlack)
            {
                target = offset + left - edge;
                break;
            }
            if (direction < 0 && left < edge - EdgeSlack)
                target = offset + right - (width - edge);
        }

        var stuck = target is not { } to || (direction > 0 ? to <= offset + EdgeSlack : to >= offset - EdgeSlack);
        if (stuck)
            target = offset + direction * Math.Max(edge, width - 2 * edge);
        TabScroller.ScrollToHorizontalOffset(target!.Value);
    }

    /// <summary>-1 or 1 if a drag is over the left or right edge, else 0.</summary>
    private int TabEdgeUnder(DragEventArgs e)
    {
        foreach (var (edge, direction) in new[] { (TabEdgeLeft, -1), (TabEdgeRight, 1) })
        {
            if (!edge.IsVisible)
                continue;
            // The left edge is mirrored, but onto itself, so its own bounds still hold.
            var point = e.GetPosition(edge);
            if (point.X >= 0 && point.Y >= 0 && point.X <= edge.ActualWidth && point.Y <= edge.ActualHeight)
                return direction;
        }
        return 0;
    }

    /// <summary>
    /// A drag held over an edge pages the strip at a pace that leaves time to aim, so a tab can be
    /// dragged to a place out of sight and files to a tab out of sight. Holding it there is the
    /// deliberate part; merely crossing the edge on the way somewhere else is over before the
    /// first page turns. Hovering without a drag never scrolls: that would move the strip under a
    /// pointer that was only passing.
    /// </summary>
    private void ScrollTabsUnderDrag(DragEventArgs e)
    {
        var direction = TabEdgeUnder(e);
        if (direction == 0)
        {
            StopTabEdgeDragScroll();
            return;
        }

        // DragOver comes with every move, and restarting the timer each time would never let it tick.
        if (_tabEdgeDragScroll is { IsEnabled: true } && _tabEdgeDragDirection == direction)
            return;

        _tabEdgeDragDirection = direction;
        if (_tabEdgeDragScroll is null)
        {
            _tabEdgeDragScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _tabEdgeDragScroll.Tick += (_, _) => PageTabs(_tabEdgeDragDirection);
        }
        _tabEdgeDragScroll.Start();
    }

    private void StopTabEdgeDragScroll()
    {
        _tabEdgeDragScroll?.Stop();
        _tabEdgeDragDirection = 0;
    }

    /// <summary>
    /// Where the tab is, in full: a folder's name alone doesn't tell two "src" tabs apart, or a
    /// narrowed tab from its neighbour. Read as it opens, since the tab may have moved on since.
    /// </summary>
    private void OnTabToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabViewModel tab } chip && tab.Location is { } location)
            chip.ToolTip = location.Display;
    }
}
