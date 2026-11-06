using System.Windows;
using System.Windows.Controls;
using DanceCommander.App.ViewModels;

namespace DanceCommander.App.Views;

/// <summary>
/// The legend's items panel: lays the slots out in lines as a wrap panel would, then shows one
/// line at a time. The legend row never grows, so nothing above it moves while a key is held, and a
/// layer with more keys than fit is shown a line at a time instead (LegendBar turns the pages).
/// </summary>
/// <remarks>
/// Kinds stay together: a group that would not fit on what is left of a line, but fits on a line of
/// its own, starts the next line rather than being split across two. Between kinds there is a little
/// more space than between keys, which is all the grouping needs to read as grouping (§1.3: space,
/// not borders).
/// </remarks>
public sealed class LegendPagePanel : Panel
{
    private const double GroupGap = 12;

    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(double), typeof(LegendPagePanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    private readonly List<int> _lineOf = [];
    private readonly List<double> _xOf = [];
    private double _lineHeight;

    public LegendPagePanel() => ClipToBounds = true;

    /// <summary>The line on show. Fractional while sliding: 0.5 is halfway from the first to the second.</summary>
    public double Page
    {
        get => (double)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public int PageCount { get; private set; } = 1;

    /// <summary>After a layout that changed <see cref="PageCount"/>; raised outside the layout pass.</summary>
    public event EventHandler? PagesChanged;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        var children = InternalChildren;

        _lineHeight = 0;
        foreach (UIElement child in children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _lineHeight = Math.Max(_lineHeight, child.DesiredSize.Height);
        }

        _lineOf.Clear();
        _xOf.Clear();
        double x = 0, widest = 0;
        var line = 0;
        for (var i = 0; i < children.Count; i++)
        {
            var itemWidth = children[i].DesiredSize.Width;
            var startsKind = i > 0 && GroupOf(children[i]) != GroupOf(children[i - 1]);
            var gap = startsKind && x > 0 ? GroupGap : 0;

            if (startsKind && x > 0)
            {
                var kindWidth = KindWidth(children, i);
                if (x + gap + kindWidth > width && kindWidth <= width)
                    (line, x, gap) = (line + 1, 0, 0);
            }

            if (x > 0 && (x + gap + itemWidth > width || StartsLine(children[i])))
                (line, x, gap) = (line + 1, 0, 0);

            _lineOf.Add(line);
            _xOf.Add(x + gap);
            x += gap + itemWidth;
            widest = Math.Max(widest, x);
        }

        var pages = line + 1;
        if (pages != PageCount)
        {
            PageCount = pages;
            Dispatcher.BeginInvoke(() => PagesChanged?.Invoke(this, EventArgs.Empty));
        }

        return new Size(Math.Min(widest, width), _lineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        for (var i = 0; i < children.Count && i < _lineOf.Count; i++)
        {
            var y = (_lineOf[i] - Page) * _lineHeight;
            children[i].Arrange(new Rect(_xOf[i], y, children[i].DesiredSize.Width, _lineHeight));
        }

        return finalSize;
    }

    private static bool StartsLine(UIElement child) =>
        (child as FrameworkElement)?.DataContext is LegendSlot { StartsLine: true };

    private static string? GroupOf(UIElement child) =>
        (child as FrameworkElement)?.DataContext is LegendSlot slot ? slot.Group : null;

    /// <summary>The width of the run of same-kind slots starting at <paramref name="start"/>.</summary>
    private static double KindWidth(UIElementCollection children, int start)
    {
        var kind = GroupOf(children[start]);
        double total = 0;
        for (var i = start; i < children.Count && GroupOf(children[i]) == kind; i++)
            total += children[i].DesiredSize.Width;
        return total;
    }
}
