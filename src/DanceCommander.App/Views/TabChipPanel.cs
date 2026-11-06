using System.Windows;
using System.Windows.Controls;

namespace DanceCommander.App.Views;

/// <summary>
/// A tab's title and its close button (shown on hover). A tab at its natural width keeps room for
/// the button beside the title, so on a short name like "site" the button never lands on the text
/// and the title is a safe place to click. A tab the strip has narrowed (TabStripPanel) gives that
/// room to the title instead, and the button is drawn over the title's end: there every pixel of
/// title counts, and no tab changes width when the button appears either way.
/// </summary>
/// <remarks>The first child is the title, the second the button.</remarks>
public sealed class TabChipPanel : Panel
{
    private bool _reserved = true;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count < 2)
            return default;

        var (title, button) = (InternalChildren[0], InternalChildren[1]);
        var unlimited = new Size(double.PositiveInfinity, double.PositiveInfinity);
        title.Measure(unlimited);
        button.Measure(unlimited);
        var height = Math.Max(title.DesiredSize.Height, button.DesiredSize.Height);
        var whole = title.DesiredSize.Width + button.DesiredSize.Width;

        _reserved = availableSize.Width >= whole;
        if (_reserved)
            return new Size(whole, height);

        title.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return new Size(availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count < 2)
            return finalSize;

        var (title, button) = (InternalChildren[0], InternalChildren[1]);
        var buttonWidth = Math.Min(button.DesiredSize.Width, finalSize.Width);
        var titleWidth = _reserved ? finalSize.Width - buttonWidth : finalSize.Width;
        title.Arrange(new Rect(0, 0, Math.Max(0, titleWidth), finalSize.Height));
        button.Arrange(new Rect(finalSize.Width - buttonWidth, 0, buttonWidth, finalSize.Height));
        return finalSize;
    }
}
