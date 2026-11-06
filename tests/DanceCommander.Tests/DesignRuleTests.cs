using System.Text.RegularExpressions;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// The structural commitments of DESIGN.md §1.2 and the visual rules of §1.3 that can be checked
/// mechanically, so they hold because the build says so rather than because someone remembered.
/// The app is a WPF project this one doesn't reference, so these read its source. The modal-dialog
/// rule is not here: the compiler enforces it (BannedSymbols.txt).
/// </summary>
public class DesignRuleTests
{
    private static readonly string AppDir = Path.Combine(RepoRoot(), "src", "DanceCommander.App");
    private static readonly string ThemeDir = Path.Combine(AppDir, "Theme");

    // Views build some templates as XAML inside C# strings, so both kinds of file are read.
    private static IEnumerable<string> SourceFiles(params string[] patterns) =>
        patterns
            .SelectMany(p => Directory.EnumerateFiles(AppDir, p, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static List<string> Matches(IEnumerable<string> files, Regex pattern) =>
        files
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, line, i)))
            .Where(x => pattern.IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(AppDir, x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();

    [Fact]
    public void FontSizesComeFromTokens()
    {
        // §1.2/8 and §1.3: a literal size is how a fourth type size gets in unnoticed.
        var literal = new Regex("""FontSize"?\s*(=|Value=)\s*"[0-9]""");
        Matches(SourceFiles("*.xaml", "*.cs"), literal).ShouldBeEmpty();
    }

    [Fact]
    public void AtMostThreeTypeSizes()
    {
        var sizes = Regex.Matches(File.ReadAllText(Path.Combine(ThemeDir, "Tokens.xaml")),
            """x:Key="FontSize\w+">([0-9.]+)<""");
        sizes.Select(m => m.Groups[1].Value).Distinct().Count().ShouldBeLessThanOrEqualTo(3);
    }

    [Fact]
    public void ColoursLiveInTheTheme()
    {
        // §1.3: the chrome is near-monochrome and the user owns colour. A hex colour outside
        // the palette is a colour no theme can change.
        var hex = new Regex("\"#[0-9A-Fa-f]{3,8}\"");
        var outsideTheme = SourceFiles("*.xaml", "*.cs").Where(f => !f.StartsWith(ThemeDir));
        Matches(outsideTheme, hex).ShouldBeEmpty();
    }

    [Fact]
    public void ActionsGoThroughTheCommandRegistry()
    {
        // §1.2/1: an ad-hoc command on a viewmodel is an action the F-key legend, context menu,
        // keybindings and palette cannot see. The one exception answers an inline decision
        // (§1.2/2): its buttons are the reply to a question, not an action of the app's own.
        var adHoc = new Regex(@"\[RelayCommand|new\s+(Async)?RelayCommand|DelegateCommand");
        var allowed = Path.Combine(AppDir, "ViewModels", "DecisionViewModel.cs");
        Matches(SourceFiles("*.cs").Where(f => f != allowed), adHoc).ShouldBeEmpty();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DanceCommander.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DanceCommander.sln not found above the test output.");
    }
}
