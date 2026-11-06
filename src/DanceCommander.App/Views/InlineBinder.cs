using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using DanceCommander.App.ViewModels;

namespace DanceCommander.App.Views;

/// <summary>
/// Fills a <see cref="TextBlock"/> from a list of segments, so one line can be drawn in two
/// colours without a RichTextBox.
/// </summary>
/// <remarks>
/// <see cref="TextBlock.Inlines"/> is not a dependency property and cannot be bound, which is the
/// only reason this exists. It is attached per line rather than over the whole file so the list
/// can virtualise: a preview of a large file then builds runs for the lines on screen and no more.
/// </remarks>
internal static class InlineBinder
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments",
        typeof(IEnumerable),
        typeof(InlineBinder),
        new PropertyMetadata(null, OnSegmentsChanged));

    public static void SetSegments(DependencyObject element, IEnumerable? value) =>
        element.SetValue(SegmentsProperty, value);

    public static IEnumerable? GetSegments(DependencyObject element) =>
        (IEnumerable?)element.GetValue(SegmentsProperty);

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
            return;

        block.Inlines.Clear();

        if (e.NewValue is not IEnumerable segments)
            return;

        foreach (var item in segments)
        {
            if (item is not PreviewSegment segment)
                continue;

            // A reference, not the brush itself, so it follows a switch between dark and light.
            var run = new Run(segment.Text);
            if (segment.Kind == PreviewSegmentKind.Whitespace)
                run.SetResourceReference(TextElement.ForegroundProperty, "TextMutedBrush");

            block.Inlines.Add(run);
        }
    }
}
