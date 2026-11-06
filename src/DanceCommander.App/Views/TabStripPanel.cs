using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DanceCommander.App.Views;

/// <summary>
/// The tab strip's items panel: tabs side by side, narrowed before they overflow, as a browser's
/// are. The widest give way first, down to a floor, so a session of a handful of tabs never scrolls
/// and the faded edges (PaneView.TabStrip.cs) are left for the rare strip that really is too long.
/// </summary>
/// <remarks>
/// It sits in a ScrollViewer, which measures its content with unlimited width, so the room to fit
/// into is the scroller's own width, read from the last layout and re-measured when it changes. The
/// scroller has no bar of either kind, so its width is the viewport's.
/// </remarks>
public sealed class TabStripPanel : Panel
{
    /// <summary>Narrow enough for a dozen tabs in a half-screen pane, wide enough to recognise a title by its first word.</summary>
    private const double MinItemWidth = 80;

    private ScrollViewer? _scroller;

    public TabStripPanel()
    {
        Loaded += (_, _) =>
        {
            _scroller = FindScroller();
            if (_scroller is not null)
                _scroller.SizeChanged += OnScrollerSizeChanged;
            InvalidateMeasure();
        };
        Unloaded += (_, _) =>
        {
            if (_scroller is not null)
                _scroller.SizeChanged -= OnScrollerSizeChanged;
            _scroller = null;
        };
    }

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            InvalidateMeasure();
    }

    private ScrollViewer? FindScroller()
    {
        for (var at = VisualTreeHelper.GetParent(this); at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is ScrollViewer scroller)
                return scroller;
        }
        return null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var room = _scroller is { ActualWidth: > 0 } scroller ? scroller.ActualWidth : availableSize.Width;
        var children = InternalChildren;
        var natural = new double[children.Count];
        double total = 0, height = 0;

        for (var i = 0; i < children.Count; i++)
        {
            children[i].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            natural[i] = children[i].DesiredSize.Width;
            total += natural[i];
            height = Math.Max(height, children[i].DesiredSize.Height);
        }

        if (total <= room)
            return new Size(total, height);

        // One cap for all, the largest that fits: tabs narrower than it keep their width, and the
        // room they leave is shared by the rest. Never below the floor; past that the strip scrolls.
        var cap = Math.Max(MinItemWidth, Cap(natural, room));
        total = 0;
        foreach (UIElement child in children)
        {
            if (child.DesiredSize.Width > cap)
                child.Measure(new Size(cap, double.PositiveInfinity));
            total += child.DesiredSize.Width;
        }

        return new Size(total, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width;
        }
        return finalSize;
    }

    /// <summary>The width that, given to every item wider than it, makes them all fit in <paramref name="room"/>.</summary>
    private static double Cap(double[] widths, double room)
    {
        var sorted = widths.Order().ToArray();
        var left = room;
        for (var i = 0; i < sorted.Length; i++)
        {
            var share = left / (sorted.Length - i);
            if (sorted[i] > share)
                return share;
            left -= sorted[i];
        }
        return double.PositiveInfinity;
    }
}
