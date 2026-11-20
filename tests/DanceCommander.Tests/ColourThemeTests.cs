using System.Xml.Linq;
using DanceCommander.Core.Config;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// A theme is a look, and the app stays readable in it: every built-in theme and any theme file
/// is held to what PaletteContrastTests holds the app's own palettes to.
/// </summary>
public sealed class ColourThemeTests
{
    private const double AA = 4.5;

    private static readonly string[] Texts =
        ["TextPrimaryBrush", "TextSecondaryBrush", "TextMutedBrush", "DangerBrush", "WarningBrush",
         "SyntaxKeywordBrush", "SyntaxStringBrush", "SyntaxNumberBrush", "SyntaxNameBrush",
         "GitModifiedBrush", "GitAddedBrush", "GitDeletedBrush", "GitUntrackedBrush", "GitConflictBrush"];

    public static TheoryData<string> BuiltIn() => [.. ColourTheme.BuiltIn.Select(t => t.Id)];

    [Theory]
    [MemberData(nameof(BuiltIn))]
    public void DeclaresEveryKeyTheAppsPalettesDo(string id)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dark = XDocument.Load(Path.Combine(RepoRoot(), "src", "DanceCommander.App", "Theme", "Dark.xaml")).Root!
            .Elements().Select(e => e.Attribute(x + "Key")?.Value)
            .OfType<string>().Where(key => !key.StartsWith('{')).Order().ToArray();

        ColourTheme.Find(id)!.Tokens().Keys.Order().ShouldBe(dark);
    }

    [Theory]
    [MemberData(nameof(BuiltIn))]
    public void TextClearsAAOnEveryPaneAndNamesOnASelectedRow(string id) =>
        Failing(ColourTheme.Find(id)!.Tokens()).ShouldBeEmpty();

    [Theory]
    [MemberData(nameof(BuiltIn))]
    public void AnAccentOfYourOwnStaysLegible(string id) =>
        Failing(ColourTheme.Find(id)!.Tokens(new Rgb(0xFF, 0xFF, 0x00))).ShouldBeEmpty();

    [Fact]
    public void AThemeFileNeedsOnlyBaseAndText()
    {
        var theme = ColourTheme.Parse("gruvbox", """
            // Comments are allowed, as in settings.json.
            { "name": "Gruvbox", "accent": "yellow", "colours": { "base": "#282828", "text": "#ebdbb2", "yellow": "#fabd2f", } }
            """);

        theme.Label.ShouldBe("Gruvbox");
        theme.IsDark.ShouldBeTrue();
        theme.AccentRole.ShouldBe("yellow");
        Failing(theme.Tokens()).ShouldBeEmpty();
    }

    [Fact]
    public void AThemeFileOfPoorContrastIsMadeReadable()
    {
        var theme = ColourTheme.Parse("murky", """{ "colours": { "base": "#777777", "text": "#888888" } }""");

        Failing(theme.Tokens()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("{", "valid JSON")]
    [InlineData("[]", "JSON object")]
    [InlineData("""{ "colours": { "text": "#ffffff" } }""", "“base”")]
    [InlineData("""{ "colours": { "base": "#000000", "text": "#ffffff", "bg": "#000000" } }""", "“bg”")]
    [InlineData("""{ "colours": { "base": "black", "text": "#ffffff" } }""", "#1E1E2E")]
    [InlineData("""{ "accent": "chartreuse", "colours": { "base": "#000000", "text": "#ffffff" } }""", "chartreuse")]
    public void AFileThatIsNotAThemeSaysWhy(string json, string said) =>
        Should.Throw<FormatException>(() => ColourTheme.Parse("x", json)).Message.ShouldContain(said);

    [Theory]
    [InlineData(@"..\settings")]
    [InlineData("../settings")]
    [InlineData(@"C:\Windows\x")]
    [InlineData(@"\\server\share\x")]
    [InlineData("")]
    public void ANameFromTheSettingNeverLeavesTheThemesFolder(string id)
    {
        var folder = Directory.CreateTempSubdirectory("dc-themes-").FullName;
        var previous = ColourThemeFiles.Directory;
        try
        {
            ColourThemeFiles.Directory = folder;
            ColourThemeFiles.TryLoad(id, out var theme, out var problem).ShouldBeFalse();
            theme.ShouldBeNull();
            problem.ShouldNotBeNull();
        }
        finally
        {
            ColourThemeFiles.Directory = previous;
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ThemeFilesAreOfferedByTheirOwnNames()
    {
        var folder = Directory.CreateTempSubdirectory("dc-themes-").FullName;
        var previous = ColourThemeFiles.Directory;
        try
        {
            File.WriteAllText(Path.Combine(folder, "Gruvbox.json"), """{ "name": "Gruvbox Dark", "colours": { "base": "#282828", "text": "#ebdbb2" } }""");
            File.WriteAllText(Path.Combine(folder, "broken.json"), "{");
            ColourThemeFiles.Directory = folder;

            ColourThemeFiles.Choices().ShouldBe([new SettingChoice("broken", "broken"), new SettingChoice("gruvbox", "Gruvbox Dark")]);
            ColourThemeFiles.TryLoad("gruvbox", out var theme, out _).ShouldBeTrue();
            theme!.Label.ShouldBe("Gruvbox Dark");
            ColourThemeFiles.TryLoad("broken", out _, out var problem).ShouldBeFalse();
            problem.ShouldBe("themes\\broken.json can't be used: it isn't valid JSON (line 1).");
        }
        finally
        {
            ColourThemeFiles.Directory = previous;
            Directory.Delete(folder, recursive: true);
        }
    }

    private static List<string> Failing(IReadOnlyDictionary<string, Rgb> tokens)
    {
        var failing = (from text in Texts.Append("AccentBrush")
                       from pane in (string[])["PaneBrush", "PaneInactiveBrush"]
                       let ratio = AccentColour.Contrast(tokens[text], tokens[pane])
                       where ratio < AA
                       select $"{text} on {pane}: {ratio:0.00}").ToList();

        foreach (var text in (string[])["TextPrimaryBrush", "TextSecondaryBrush"])
        {
            if (AccentColour.Contrast(tokens[text], tokens["SelectionActiveBrush"]) < AA)
                failing.Add($"{text} on a selected row");
        }

        if (AccentColour.Contrast(tokens["DangerBrush"], tokens["DangerSurfaceBrush"]) < AA)
            failing.Add("danger on its surface");
        if (AccentColour.Contrast(tokens["WarningBrush"], tokens["WarningSurfaceBrush"]) < AA)
            failing.Add("warning on its surface");
        if (AccentColour.Contrast(tokens["TextPrimaryBrush"], tokens["InfoSurfaceBrush"]) < AA)
            failing.Add("text on the info surface");

        return failing;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DanceCommander.sln")))
            dir = dir.Parent;
        return dir!.FullName;
    }
}
