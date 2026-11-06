using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DanceCommander.App.Views;

/// <summary>
/// Shows a path in a <see cref="TextBlock"/> too narrow for it, dropping leading segments rather
/// than trailing characters: <c>…/obj/Debug/net9.0-windows</c>.
/// </summary>
/// <remarks>
/// WPF only trims at the end, which is the wrong end for a path. Every row in a flattened
/// repository begins <c>DanceCommander.</c>-something, so an ordinary ellipsis leaves a column of
/// identical stubs that says nothing at all — the part that distinguishes one row from another is
/// the part that got cut. Dropping whole segments keeps the result readable as a path instead of
/// ending mid-word.
/// </remarks>
public static class PathTrim
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached(
            "Source", typeof(string), typeof(PathTrim),
            new PropertyMetadata(null, OnSourceChanged));

    public static void SetSource(DependencyObject element, string? value) =>
        element.SetValue(SourceProperty, value);

    public static string? GetSource(DependencyObject element) =>
        (string?)element.GetValue(SourceProperty);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
            return;

        // Rows are virtualised and recycled, so the same TextBlock gets a new value many times;
        // unhooking first keeps it to one handler however often that happens.
        block.SizeChanged -= OnSizeChanged;
        block.SizeChanged += OnSizeChanged;
        Apply(block);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && sender is TextBlock block)
            Apply(block);
    }

    private static void Apply(TextBlock block)
    {
        var path = GetSource(block) ?? string.Empty;
        var available = block.ActualWidth;

        // Before the first layout pass there is nothing to fit to; SizeChanged brings us back.
        if (available <= 0)
        {
            block.Text = path;
            return;
        }

        if (Measure(block, path) <= available)
        {
            block.Text = path;
            return;
        }

        // A local path is written the Windows way and a remote or relative one with slashes; split
        // on the one it uses, or a backslashed path is a single segment that only ever tail-trims.
        var separator = path.Contains('\\') ? '\\' : '/';
        var segments = path.Split(separator);
        for (var skip = 1; skip < segments.Length; skip++)
        {
            var candidate = "…" + separator + string.Join(separator, segments.Skip(skip));
            if (Measure(block, candidate) <= available)
            {
                block.Text = candidate;
                return;
            }
        }

        // Not even the last segment fits. Show it anyway and let the TextBlock trim the tail:
        // there is no arrangement of an ellipsis that makes this column informative, and a
        // half-word beats an empty cell. No leading marker when there was only ever one segment --
        // it would claim a folder was dropped and spend width saying so.
        block.Text = segments.Length == 1 ? path : "…" + separator + segments[^1];
    }

    private static double Measure(TextBlock block, string text) =>
        new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
            block.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(block).PixelsPerDip).WidthIncludingTrailingWhitespace;
}
