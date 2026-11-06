using DanceCommander.Core.Fs;
using DanceCommander.Core.Startup;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// The command line other programs use to start us. Explorer's spelling is the contract here:
/// these are the exact strings already in other people's shipped binaries and scripts.
/// </summary>
public sealed class CommandLineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-cli-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string File(string name)
    {
        var path = Path.Combine(_root, name);
        System.IO.File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void NoArgumentsAsksForNothing()
    {
        var request = CommandLine.Parse([]);

        request.HasWork.ShouldBeFalse();
        request.Problems.ShouldBeEmpty();
    }

    [Fact]
    public void ABarePathOpensIt() =>
        CommandLine.Parse([_root]).Left.ShouldBe(FsUri.Local(_root));

    [Fact]
    public void TwoBarePathsFillBothPanes()
    {
        var request = CommandLine.Parse([_root, Path.GetTempPath()]);

        request.Left.ShouldBe(FsUri.Local(_root));
        request.Right.ShouldBe(FsUri.Local(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)));
        // Two locations mean two panes without having to say so.
        request.Panes.ShouldBe(PaneRequest.Two);
    }

    [Fact]
    public void ExplorerSelectOpensTheFolderWithTheFileUnderTheCursor()
    {
        // The exact string every "show in folder" integration already emits. Comma, no space.
        var file = File("downloaded.zip");

        var request = CommandLine.Parse([$"/select,{file}"]);

        request.Left.ShouldBe(FsUri.Local(_root));
        request.Select.ShouldBe("downloaded.zip");
    }

    [Fact]
    public void ExplorerSelectToleratesQuotesAroundThePath()
    {
        // A caller that quoted the path inside the argument rather than around it.
        var file = File("with space.txt");

        var request = CommandLine.Parse([$"/select,\"{file}\""]);

        request.Select.ShouldBe("with space.txt");
        request.Left.ShouldBe(FsUri.Local(_root));
    }

    [Fact]
    public void ASeparatedSelectWorksToo()
    {
        // Callers that build argv as an array tend to lose the comma. Accepting it costs nothing.
        var file = File("a.txt");

        CommandLine.Parse(["/select", file]).Select.ShouldBe("a.txt");
    }

    [Fact]
    public void SelectingAFolderShowsItFromItsParent()
    {
        var request = CommandLine.Parse([$"/select,{_root}"]);

        request.Left.ShouldBe(FsUri.Local(_root).Parent);
        request.Select.ShouldBe(Path.GetFileName(_root));
    }

    [Fact]
    public void ExplorerNIsOnePaneAndEIsTwo()
    {
        CommandLine.Parse([$"/n,{_root}"]).Panes.ShouldBe(PaneRequest.One);
        CommandLine.Parse([$"/e,{_root}"]).Panes.ShouldBe(PaneRequest.Two);
        CommandLine.Parse([$"/n,{_root}"]).Left.ShouldBe(FsUri.Local(_root));
    }

    [Fact]
    public void TheSpelledOutFormsMeanTheSame()
    {
        var file = File("b.txt");

        CommandLine.Parse(["--select", file]).Select.ShouldBe("b.txt");
        CommandLine.Parse(["--left", _root]).Left.ShouldBe(FsUri.Local(_root));
        CommandLine.Parse(["--right", _root]).Right.ShouldBe(FsUri.Local(_root));
        CommandLine.Parse(["--one-pane"]).Panes.ShouldBe(PaneRequest.One);
        CommandLine.Parse(["--two-panes"]).Panes.ShouldBe(PaneRequest.Two);
    }

    [Fact]
    public void OnlyLeftAndRightSayWhichPane()
    {
        CommandLine.Parse(["--left", _root]).Placed.ShouldBeTrue();
        CommandLine.Parse(["--right", _root]).Placed.ShouldBeTrue();
        CommandLine.Parse([_root]).Placed.ShouldBeFalse();
        CommandLine.Parse([_root, _root]).Placed.ShouldBeFalse();
        CommandLine.Parse([$"/select,{File("c.txt")}"]).Placed.ShouldBeFalse();
    }

    [Fact]
    public void AnExplicitPaneCountBeatsTheOneImpliedByTwoPaths() =>
        CommandLine.Parse(["--one-pane", _root, _root]).Panes.ShouldBe(PaneRequest.One);

    [Fact]
    public void NewWindowAndHelpAreRecognised()
    {
        CommandLine.Parse(["--new-window"]).NewWindow.ShouldBeTrue();
        CommandLine.Parse(["--help"]).Help.ShouldBeTrue();
        CommandLine.Parse(["/?"]).Help.ShouldBeTrue();
    }

    [Fact]
    public void ARemoteLocationIsAValidPlaceToStart()
    {
        // The reason this is FsUri and not a path: everything the app can open, it can be told to
        // open. A drive letter is not a scheme, which is the only thing that makes this ambiguous.
        var request = CommandLine.Parse(["sftp://claude@host/var/log"]);

        request.Left!.Scheme.ShouldBe("sftp");
        request.Left.Path.ShouldBe("/var/log");
    }

    [Fact]
    public void ARefusedAddressIsNotTakenForARelativePath() =>
        // Read as a path, the password in it became a folder name in the session and the log.
        CommandLine.Resolve("sftp://bob:hunter2@host/var/log").ShouldBeNull();

    [Fact]
    public void ARefusedAddressIsSaidWithoutRepeatingIt()
    {
        // The problems are shown in a notice; the text would put the password on screen.
        var problem = CommandLine.Parse(["--right", "sftp://bob:hunter2@host/var/log"]).Problems.ShouldHaveSingleItem();

        problem.ShouldContain("cannot carry a password");
        problem.ShouldNotContain("hunter2");
    }

    [Fact]
    public void AViewIsAValidPlaceToStart() =>
        CommandLine.Parse([@"flat:C:\work"]).Left!.IsView.ShouldBeTrue();

    [Fact]
    public void ADriveLetterIsNotAScheme() =>
        // "C:" looks like a URI scheme to anything that only tests for a colon.
        CommandLine.Parse([_root]).Left!.IsLocal.ShouldBeTrue();

    [Theory]
    // What Explorer's "Open in Dance Commander" on a drive hands over: "%V" is "C:\", and the
    // argument rules read its \" as a literal quote.
    [InlineData("C:\"")]
    [InlineData("C:")]
    [InlineData(@"C:\")]
    public void ADriveIsItsRoot(string argument) =>
        CommandLine.Parse([argument]).Left.ShouldBe(FsUri.Local(@"C:\"));

    [Fact]
    public void ARelativePathResolvesAgainstTheWorkingDirectory()
    {
        var request = CommandLine.Parse(["."]);

        request.Left.ShouldBe(FsUri.Local(Directory.GetCurrentDirectory()));
    }

    [Fact]
    public void AnUnreadableArgumentIsReportedRatherThanDropped()
    {
        // A launcher passing the wrong thing is a bug in the launcher; opening the home folder in
        // silence hides it from the only person who can fix it.
        var request = CommandLine.Parse(["--nonsense"]);

        request.Problems.ShouldNotBeEmpty();
    }

    [Fact]
    public void AnOptionMissingItsValueSaysSo() =>
        CommandLine.Parse(["--select"]).Problems.ShouldNotBeEmpty();

    [Fact]
    public void AThirdLocationIsReportedRatherThanSilentlyIgnored()
    {
        var request = CommandLine.Parse([_root, _root, _root]);

        request.Problems.ShouldNotBeEmpty();
        request.Left.ShouldNotBeNull();
        request.Right.ShouldNotBeNull();
    }

    [Fact]
    public void AGoodLineHasNothingToComplainAbout()
    {
        var file = File("c.txt");

        CommandLine.Parse([$"/select,{file}"]).Problems.ShouldBeEmpty();
        CommandLine.Parse([_root, "--two-panes"]).Problems.ShouldBeEmpty();
    }
}
