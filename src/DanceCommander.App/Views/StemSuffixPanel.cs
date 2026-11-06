using System.Windows;
using System.Windows.Controls;

namespace DanceCommander.App.Views;

/// <summary>
/// Lays out a file name as a stem followed by what must stay visible — the extension, and after it
/// any glyphs (link, read-only) — so that a name too long for its column loses the middle rather
/// than the end: <c>abstract.cpython-3….pyc 🔒</c>.
/// </summary>
/// <remarks>
/// WPF trims at the end, and at the end of a file name is the extension — the one part that says
/// what the file is. In a flattened tree that is the difference between <c>abc.py</c> and
/// <c>abc.cpython-313.pyc</c>, which an ordinary ellipsis renders identically. A single
/// <see cref="TextBlock"/> cannot do better: to trim the stem it must be width-constrained, and to
/// keep the extension against it the stem must be content-sized. A panel can be both, because it
/// decides the stem's width after measuring everything that follows it.
///
/// The first child is the stem and the only one that gives way. The rest keep their widths and sit
/// right after it, which is also why the glyphs belong here rather than in a column of their own:
/// in a column beside a stretching name they stood at the far edge, away from the name they are
/// about.
/// </remarks>
public sealed class StemSuffixPanel : Panel
{
    /// <summary>What follows the stem may take at most this share of the room, so a freak extension cannot swallow the name.</summary>
    private const double MaxTrailingShare = 0.5;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count < 2)
        {
            foreach (UIElement child in InternalChildren)
                child.Measure(availableSize);
            return InternalChildren.Count == 0 ? default : InternalChildren[0].DesiredSize;
        }

        var width = availableSize.Width;
        var trailingRoom = double.IsInfinity(width) ? width : width * MaxTrailingShare;

        double trailing = 0, height = 0;
        for (var i = 1; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(Math.Max(0, trailingRoom - trailing), availableSize.Height));
            trailing += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        var stem = InternalChildren[0];
        stem.Measure(new Size(Math.Max(0, width - trailing), availableSize.Height));

        return new Size(stem.DesiredSize.Width + trailing, Math.Max(stem.DesiredSize.Height, height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count < 2)
        {
            foreach (UIElement child in InternalChildren)
                child.Arrange(new Rect(finalSize));
            return finalSize;
        }

        // The share is a cap for when the stem is short of room, not otherwise: arranged at its own
        // desired width (an Auto column), a short stem such as "Created archive" left its longer
        // suffix less than it measured, and " · Ctrl+Z undoes" was cut off.
        var room = Math.Max(finalSize.Width * MaxTrailingShare, finalSize.Width - InternalChildren[0].DesiredSize.Width);
        var widths = new double[InternalChildren.Count];
        double trailing = 0;
        for (var i = 1; i < InternalChildren.Count; i++)
        {
            widths[i] = Math.Min(InternalChildren[i].DesiredSize.Width, Math.Max(0, room - trailing));
            trailing += widths[i];
        }

        widths[0] = Math.Min(InternalChildren[0].DesiredSize.Width, Math.Max(0, finalSize.Width - trailing));

        double x = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            InternalChildren[i].Arrange(new Rect(x, 0, widths[i], finalSize.Height));
            x += widths[i];
        }

        return finalSize;
    }
}
