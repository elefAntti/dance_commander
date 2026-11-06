using System.Globalization;
using System.Xml.Linq;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// DESIGN.md §1.3: light and dark are both first-class, both checked for WCAG AA contrast on text.
/// Measured here from the palettes themselves, so neither can drift below it unnoticed, and the
/// two keep the same keys, so switching between them never leaves something undrawn.
/// </summary>
public sealed class PaletteContrastTests
{
    private const double AA = 4.5;

    private static readonly string ThemeDir = Path.Combine(RepoRoot(), "src", "DanceCommander.App", "Theme");

    /// <summary>Text drawn on panes, and the surfaces it is drawn on.</summary>
    private static readonly string[] Texts =
        ["TextPrimaryBrush", "TextSecondaryBrush", "TextMutedBrush", "DangerBrush", "WarningBrush",
         "GitModifiedBrush", "GitAddedBrush", "GitDeletedBrush", "GitUntrackedBrush", "GitConflictBrush"];

    /// <summary>
    /// Where each palette promises AA for every text colour. Dark's promise is the panes; on a
    /// hovered or selected row its muted grey and conflict red fall short (PLAN.md §4). Light was
    /// made to clear AA everywhere a row can be.
    /// </summary>
    private static readonly string[] Panes = ["PaneBrush", "PaneInactiveBrush"];

    private static readonly string[] EveryRow = ["PaneBrush", "PaneInactiveBrush", "HoverBrush", "SelectionActiveBrush", "SelectionInactiveBrush"];

    public static TheoryData<string> Palettes() => ["Dark.xaml", "Light.xaml"];

    [Theory]
    [MemberData(nameof(Palettes))]
    public void TextClearsAAOnEveryPane(string palette)
    {
        var colours = Read(palette);
        var failing = (from text in Texts
                       from pane in Panes
                       let ratio = Contrast(colours[text], colours[pane])
                       where ratio < AA
                       select $"{text} on {pane}: {ratio:0.00}").ToList();

        failing.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void NamesStayReadableOnASelectedRow(string palette)
    {
        var colours = Read(palette);
        foreach (var text in (string[])["TextPrimaryBrush", "TextSecondaryBrush"])
            Contrast(colours[text], colours["SelectionActiveBrush"]).ShouldBeGreaterThanOrEqualTo(AA, $"{text} on a selected row");
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void MessagesClearAAOnTheirSurfaces(string palette)
    {
        var colours = Read(palette);
        Contrast(colours["DangerBrush"], colours["DangerSurfaceBrush"]).ShouldBeGreaterThanOrEqualTo(AA);
        Contrast(colours["WarningBrush"], colours["WarningSurfaceBrush"]).ShouldBeGreaterThanOrEqualTo(AA);
        Contrast(colours["TextPrimaryBrush"], colours["InfoSurfaceBrush"]).ShouldBeGreaterThanOrEqualTo(AA);
    }

    [Fact]
    public void TheLightPaletteClearsAAOnEveryKindOfRow()
    {
        var colours = Read("Light.xaml");
        var failing = (from text in Texts.Append("AccentBrush")
                       from row in EveryRow
                       let ratio = Contrast(colours[text == "AccentBrush" ? "AccentColor" : text], colours[row])
                       where ratio < AA
                       select $"{text} on {row}: {ratio:0.00}").ToList();

        failing.ShouldBeEmpty();
    }

    [Fact]
    public void BothPalettesHaveTheSameKeys() =>
        Read("Light.xaml").Keys.Order().ShouldBe(Read("Dark.xaml").Keys.Order());

    [Fact]
    public void HighContrastDeclaresEveryKeyTheOthersDo()
    {
        // Its colours are system ones, not literals, so the key check reads the keys alone.
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string[] Keys(string palette) => XDocument.Load(Path.Combine(ThemeDir, palette)).Root!.Elements()
            .Select(e => e.Attribute(x + "Key")?.Value).OfType<string>().Order().ToArray();

        Keys("HighContrast.xaml").ShouldBe(Keys("Dark.xaml"));
    }

    /// <summary>Every keyed colour in a palette: brushes by their colour, and the accent colour itself.</summary>
    private static Dictionary<string, string> Read(string palette)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(ThemeDir, palette));
        var colours = new Dictionary<string, string>();
        foreach (var element in document.Root!.Elements())
        {
            var key = element.Attribute(x + "Key")?.Value;
            if (key is null)
                continue;

            var value = element.Name.LocalName == "Color" ? element.Value.Trim() : element.Attribute("Color")?.Value;
            if (value is not null && value.StartsWith('#'))
                colours[key] = value;
        }

        return colours;
    }

    /// <summary>WCAG 2's contrast ratio between two #RRGGBB colours.</summary>
    private static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int at)
        {
            var c = int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DanceCommander.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DanceCommander.sln not found above the test output.");
    }
}
