using DanceCommander.Core.Config;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>The accent is whatever was asked for, made to be legible where it lands (SETTINGS_PLAN.md §6).</summary>
public sealed class AccentColourTests
{
    private static readonly Rgb DarkPane = new(0x1F, 0x22, 0x25);
    private static readonly Rgb LightPane = new(0xFA, 0xFB, 0xFC);

    [Theory]
    [InlineData("blue")]
    [InlineData("Teal")]
    [InlineData("#C2410C")]
    [InlineData(" #c2410c ")]
    public void NamesAndHexAreRead(string text) => AccentColour.TryParse(text, out _).ShouldBeTrue();

    [Theory]
    [InlineData("system")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#12345")]
    [InlineData("#GG0000")]
    [InlineData("chartreuse")]
    public void AnythingElseFallsBackToWindows(string? text) => AccentColour.TryParse(text, out _).ShouldBeFalse();

    [Fact]
    public void HexReadsBackAsWritten()
    {
        AccentColour.TryParse("#C2410C", out var colour).ShouldBeTrue();
        colour.ToString().ShouldBe("#C2410C");
    }

    [Theory]
    [InlineData("#FFFF00")] // pure yellow: invisible on white
    [InlineData("#000000")] // black: invisible on dark
    [InlineData("#808080")]
    [InlineData("#3B82F6")]
    [InlineData("#E09B1A")]
    public void AnyAccentClearsAAOnBothPalettes(string hex)
    {
        AccentColour.TryParse(hex, out var wanted).ShouldBeTrue();

        AccentColour.Contrast(AccentColour.FitTo(wanted, DarkPane), DarkPane).ShouldBeGreaterThanOrEqualTo(AccentColour.MinContrast);
        AccentColour.Contrast(AccentColour.FitTo(wanted, LightPane), LightPane).ShouldBeGreaterThanOrEqualTo(AccentColour.MinContrast);
    }

    [Fact]
    public void ALegibleAccentKeepsItsHue()
    {
        // Already fine on dark: only the saturation cap may touch it, so it stays blue.
        var fitted = AccentColour.FitTo(new Rgb(0x7A, 0xAA, 0xFF), DarkPane);
        fitted.B.ShouldBeGreaterThan(fitted.R);
    }

    [Fact]
    public void EveryNamedAccentSurvivesBothPalettes()
    {
        foreach (var (_, _, colour) in AccentColour.Named)
        {
            AccentColour.Contrast(AccentColour.FitTo(colour, DarkPane), DarkPane).ShouldBeGreaterThanOrEqualTo(AccentColour.MinContrast);
            AccentColour.Contrast(AccentColour.FitTo(colour, LightPane), LightPane).ShouldBeGreaterThanOrEqualTo(AccentColour.MinContrast);
        }
    }

    [Fact]
    public void MixGoesFromOneColourToTheOther()
    {
        AccentColour.Mix(new Rgb(0, 0, 0), new Rgb(200, 100, 50), 0).ShouldBe(new Rgb(0, 0, 0));
        AccentColour.Mix(new Rgb(0, 0, 0), new Rgb(200, 100, 50), 1).ShouldBe(new Rgb(200, 100, 50));
        AccentColour.Mix(new Rgb(0, 0, 0), new Rgb(200, 100, 50), 0.5).ShouldBe(new Rgb(100, 50, 25));
    }

    /// <summary>Windows 11's contrast themes, window and text colour: Aquatic, Desert, Dusk, Night sky.</summary>
    public static TheoryData<string, string> ContrastThemes => new()
    {
        { "#202020", "#FFFFFF" },
        { "#FFFAEF", "#3D3D3D" },
        { "#2D3236", "#FFFFFF" },
        { "#000000", "#FFFFFF" },
    };

    [Theory]
    [MemberData(nameof(ContrastThemes))]
    public void ASelectionInAContrastThemeKeepsItsTextAtAAA(string window, string text)
    {
        AccentColour.TryParse(window, out var surface).ShouldBeTrue();
        AccentColour.TryParse(text, out var ink).ShouldBeTrue();

        var reach = AccentColour.Reach(surface, ink, most: 0.38, keep: 7);
        var fill = AccentColour.Mix(surface, ink, reach);

        AccentColour.Contrast(fill, ink).ShouldBeGreaterThanOrEqualTo(7);
        // Still a fill, not the window colour: a selection has to be seen.
        reach.ShouldBeGreaterThan(0.1);
    }

    [Fact]
    public void ReachStopsAtWhatWasAskedWhenTheTextKeepsUpAnyway() =>
        // Black on white keeps 7:1 well past a 0.1 step toward black.
        AccentColour.Reach(new Rgb(255, 255, 255), new Rgb(0, 0, 0), most: 0.1, keep: 7).ShouldBe(0.1);
}
