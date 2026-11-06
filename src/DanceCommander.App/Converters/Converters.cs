using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Decorations;

namespace DanceCommander.App.Converters;

/// <summary>
/// Absent means collapsed — and a string that is empty or all spaces is absent, not present and
/// blank. Otherwise a row reserves height for text nobody wrote.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || (value is string text && string.IsNullOrWhiteSpace(text))
            ? Visibility.Collapsed
            : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>Set to true to collapse when the value is true.</summary>
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Severity picks a surface colour from the palette; nothing else colours a bar.</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public bool Foreground { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var severity = value as NoticeSeverity? ?? NoticeSeverity.Info;
        var key = (severity, Foreground) switch
        {
            (NoticeSeverity.Error, true) => "DangerBrush",
            (NoticeSeverity.Warning, true) => "WarningBrush",
            (_, true) => "TextPrimaryBrush",
            (NoticeSeverity.Error, false) => "DangerSurfaceBrush",
            (NoticeSeverity.Warning, false) => "WarningSurfaceBrush",
            _ => "InfoSurfaceBrush",
        };
        return Application.Current?.TryFindResource(key) as Brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps a row's highlight style onto a brush.
/// </summary>
/// <remarks>
/// This binds to the whole <see cref="HighlightStyle"/> rather than to a property inside it, and
/// always returns a real brush. Returning UnsetValue for an undecorated row looked right and was
/// not: Control.Foreground then falls back to its metadata default — black — instead of inheriting
/// the theme, so every row matching no rule became unreadable on a dark background.
/// </remarks>
public sealed class HighlightBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Brush> Cache = [];

    /// <summary>True for background, where "no highlight" means transparent rather than the text colour.</summary>
    public bool Background { get; set; }

    /// <summary>
    /// Below 1, produces the same colour faded — for text that belongs to a row but should not
    /// compete with it, like the extension at the end of a name.
    /// </summary>
    /// <remarks>
    /// A fixed muted brush would have been simpler and was wrong: a row coloured by a highlight
    /// rule would keep its colour up to the dot and turn grey after it, which reads as two
    /// different things rather than one name. Fading whatever the row's own colour is keeps the
    /// rule in charge and the de-emphasis on top of it.
    /// </remarks>
    public double Fade { get; set; } = 1;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value as HighlightStyle) is { } style
            ? Background ? style.Background : style.Foreground
            : null;

        if (string.IsNullOrWhiteSpace(text))
            return Default();

        var key = Fade < 1 ? $"{text}@{Fade}" : text;

        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached))
                return cached;

            try
            {
                var brush = (Brush)new BrushConverter().ConvertFromString(text)!;
                if (Fade < 1)
                    brush.Opacity = Fade;
                brush.Freeze();
                Cache[key] = brush;
                return brush;
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException)
            {
                // A hand-edited colour that does not parse must not take the row down with it.
                return Default();
            }
        }
    }

    private object? Default()
    {
        if (Background)
            return Brushes.Transparent;

        var brush = Application.Current?.TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.White;
        if (Fade >= 1)
            return brush;

        // Through the same cache as the rest. Most rows match no highlight rule, so this is the
        // common path, and cloning a brush per row in a listing of ninety thousand is not a cost
        // worth paying for a colour that is the same every time.
        lock (Cache)
        {
            // Keyed by the colour it fades, not just the fade: after a switch from dark to light
            // the cached dark text, faded, came back on white, and every extension was unreadable.
            var key = $"default@{Fade}@{(brush as SolidColorBrush)?.Color}";
            if (Cache.TryGetValue(key, out var cached))
                return cached;

            // Cloned, because the theme's brush is shared by everything and is very likely frozen.
            var faded = brush.Clone();
            faded.Opacity = Fade;
            faded.Freeze();
            Cache[key] = faded;
            return faded;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Resolves a <see cref="RowMarker"/>'s colour, which is a theme resource key rather than a
/// literal, so the git marks change with the palette instead of being pinned to one.
/// </summary>
public sealed class MarkerBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not RowMarker marker)
            return Brushes.Transparent;

        return Application.Current?.TryFindResource(marker.Colour) as Brush
            ?? Application.Current?.TryFindResource("TextSecondaryBrush") as Brush
            ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Turns the preview's wrap setting into the two things that have to agree about it: whether the
/// text wraps, and whether there is a horizontal scroll bar for it to have needed.
/// </summary>
public sealed class WrapConverter : IValueConverter
{
    public bool ForScrollBar { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var wrap = value is true;

        if (!ForScrollBar)
            return wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;

        // Disabled rather than hidden, so the content is measured against the viewport width and
        // actually folds. Hidden would let it lay out as wide as it liked and simply not say so.
        return wrap ? System.Windows.Controls.ScrollBarVisibility.Disabled : System.Windows.Controls.ScrollBarVisibility.Auto;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class HighlightFontWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is HighlightStyle { Bold: true } ? FontWeights.SemiBold : FontWeights.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class HighlightFontStyleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is HighlightStyle { Italic: true } ? FontStyles.Italic : FontStyles.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
