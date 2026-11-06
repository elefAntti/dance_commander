using System.Globalization;

namespace DanceCommander.Core.Config;

/// <summary>A colour as three bytes; the accent arithmetic needs nothing from WPF.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// The one accent (DESIGN.md §1.3) and what the palette derives from it (SETTINGS_PLAN.md §6). The
/// user picks a colour, or takes Windows'; any colour is then made to work on the palette it lands
/// in, because a pure yellow is the accent somebody asked for and also invisible on white.
/// </summary>
public static class AccentColour
{
    /// <summary>The floor for the accent against any surface it is drawn on: WCAG AA for text.</summary>
    public const double MinContrast = 4.5;

    /// <summary>The setting's value for "whatever Windows is set to".</summary>
    public const string FromWindows = "system";

    /// <summary>The named accents; anything else is a hand-written <c>#RRGGBB</c> in the file.</summary>
    public static IReadOnlyList<(string Name, string Label, Rgb Colour)> Named { get; } =
    [
        ("blue", "Blue", new(0x3B, 0x82, 0xF6)),
        ("teal", "Teal", new(0x14, 0xB8, 0xA6)),
        ("green", "Green", new(0x22, 0xA5, 0x5E)),
        ("amber", "Amber", new(0xE0, 0x9B, 0x1A)),
        ("red", "Red", new(0xE5, 0x48, 0x4D)),
        ("pink", "Pink", new(0xDB, 0x4F, 0x9C)),
        ("violet", "Violet", new(0x8B, 0x5C, 0xF6)),
    ];

    /// <summary>A name from <see cref="Named"/> or <c>#RRGGBB</c>; false for anything else, including "system".</summary>
    public static bool TryParse(string? text, out Rgb colour)
    {
        colour = default;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (var named in Named)
        {
            if (string.Equals(named.Name, text, StringComparison.OrdinalIgnoreCase))
            {
                colour = named.Colour;
                return true;
            }
        }

        if (text.Length == 7 && text[0] == '#' &&
            int.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            colour = new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The accent, held to a little less than full saturation (§1.3: "desaturated") and moved
    /// along lightness, away from the surface, until it clears <see cref="MinContrast"/> against
    /// every surface given. The hue is the one asked for; only its weight changes.
    /// </summary>
    public static Rgb FitTo(Rgb accent, params Rgb[] surfaces)
    {
        var (h, s, l) = ToHsl(accent);
        s = Math.Min(s, 0.78);

        // Darker on a light surface, lighter on a dark one: away from where the surface is.
        var lighter = surfaces.Average(Luminance) < 0.4;
        var step = lighter ? 0.01 : -0.01;

        var fitted = FromHsl(h, s, l);
        // At most the whole range: lightness 0 and 1 are valid starting points, and a surface that
        // no lightness can clear (it cannot happen with a real palette) ends the walk at the edge.
        for (var steps = 0; steps < 100 && surfaces.Any(surface => Contrast(fitted, surface) < MinContrast); steps++)
        {
            l = Math.Clamp(l + step, 0, 1);
            fitted = FromHsl(h, s, l);
        }

        return fitted;
    }

    /// <summary>The way along from one colour to another, 0 being the first.</summary>
    public static Rgb Mix(Rgb from, Rgb to, double amount) =>
        new(Channel(from.R, to.R, amount), Channel(from.G, to.G, amount), Channel(from.B, to.B, amount));

    /// <summary>
    /// How far a fill can go from <paramref name="surface"/> toward <paramref name="text"/>, at
    /// most <paramref name="most"/> of the way, and still keep that text at <paramref name="keep"/>:1
    /// on it. A contrast theme's surface and text are the one pair promised to read; a fill mixed
    /// halfway between them is where that promise breaks (2026-10-17: Windows' Dusk theme,
    /// selected rows at 3:1).
    /// </summary>
    public static double Reach(Rgb surface, Rgb text, double most, double keep)
    {
        var amount = most;
        while (amount > 0 && Contrast(Mix(surface, text, amount), text) < keep)
            amount -= 0.005;
        return Math.Max(amount, 0);
    }

    /// <summary>WCAG 2's contrast ratio.</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static double Luminance(Rgb colour)
    {
        static double Linear(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(colour.R) + 0.7152 * Linear(colour.G) + 0.0722 * Linear(colour.B);
    }

    private static (double H, double S, double L) ToHsl(Rgb colour)
    {
        double r = colour.R / 255.0, g = colour.G / 255.0, b = colour.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min)
            return (0, 0, l);

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0)
              : max == g ? (b - r) / d + 2
              : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static Rgb FromHsl(double h, double s, double l)
    {
        if (s == 0)
            return new Rgb(Byte(l), Byte(l), Byte(l));

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return new Rgb(Byte(Hue(p, q, h + 1.0 / 3)), Byte(Hue(p, q, h)), Byte(Hue(p, q, h - 1.0 / 3)));
    }

    private static double Hue(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        return t < 1.0 / 6 ? p + (q - p) * 6 * t
             : t < 0.5 ? q
             : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6
             : p;
    }

    private static byte Byte(double unit) => (byte)Math.Round(Math.Clamp(unit, 0, 1) * 255);

    private static byte Channel(byte from, byte to, double amount) =>
        (byte)Math.Round(from + (to - from) * Math.Clamp(amount, 0, 1));
}
