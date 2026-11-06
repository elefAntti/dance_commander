using DanceCommander.Extensibility;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>The line Run puts on the command line, per shell.</summary>
public class RunLineTests
{
    private const string Here = @"C:\work\proj";
    private const string GitBash = @"C:\Git\bin\bash.exe";

    private static string? Find(string program) =>
        program is "pwsh" or "py" ? $@"C:\bin\{program}.exe" : null;

    private static string? Line(string name, ShellQuoting shell, string? folder = Here, string? gitBash = GitBash) =>
        RunLine.For(Path.Combine(folder!, name), Here, shell, Find, gitBash);

    [Theory]
    [InlineData("build.sh", ShellQuoting.Posix, "C:/Git/bin/bash.exe ./build.sh")]
    [InlineData("build.bat", ShellQuoting.Posix, "./build.bat")]
    [InlineData("tool.py", ShellQuoting.Posix, "C:/bin/py.exe ./tool.py")]
    [InlineData("deploy.ps1", ShellQuoting.Posix, "C:/bin/pwsh.exe -NoProfile -File ./deploy.ps1")]
    [InlineData("app.exe", ShellQuoting.Cmd, @".\app.exe")]
    [InlineData("deploy.ps1", ShellQuoting.Cmd, @"C:\bin\pwsh.exe -NoProfile -File .\deploy.ps1")]
    [InlineData("app.exe", ShellQuoting.PowerShell, @"& '.\app.exe'")]
    [InlineData("tool.py", ShellQuoting.PowerShell, @"& 'C:\bin\py.exe' '.\tool.py'")]
    public void RunsAFileInThisFolderByItsName(string name, ShellQuoting shell, string expected) =>
        Line(name, shell).ShouldBe(expected);

    [Fact]
    public void ANameWithSpacesAndOperatorsStaysOneWord()
    {
        Line("my build & test.sh", ShellQuoting.Posix).ShouldBe("C:/Git/bin/bash.exe './my build & test.sh'");
        Line("a b&c.exe", ShellQuoting.Cmd).ShouldBe(@""".\a b&c.exe""");
        Line("it's.ps1", ShellQuoting.PowerShell).ShouldBe(@"& 'C:\bin\pwsh.exe' -NoProfile -File '.\it''s.ps1'");
    }

    [Fact]
    public void ANameStartingWithADashIsNeverAnOption() =>
        Line("-rf.sh", ShellQuoting.Posix).ShouldBe("C:/Git/bin/bash.exe ./-rf.sh");

    [Theory]
    // A search or flattened view's row: the program starts in its own folder, not the search's root,
    // and a cd that fails stops the line before a file of the same name there could run.
    [InlineData("x.sh", ShellQuoting.Posix, "cd 'C:/work/proj/sub dir' && C:/Git/bin/bash.exe ./x.sh")]
    [InlineData("app.exe", ShellQuoting.Posix, "cd 'C:/work/proj/sub dir' && ./app.exe")]
    [InlineData("app.exe", ShellQuoting.Cmd, @"cd /d ""C:\work\proj\sub dir"" && .\app.exe")]
    [InlineData("tool.py", ShellQuoting.Cmd, @"cd /d ""C:\work\proj\sub dir"" && C:\bin\py.exe .\tool.py")]
    [InlineData("app.exe", ShellQuoting.PowerShell, @"try { Set-Location -LiteralPath 'C:\work\proj\sub dir' -ErrorAction Stop } catch { throw }; & '.\app.exe'")]
    public void AFileElsewhereRunsInItsOwnFolder(string name, ShellQuoting shell, string expected) =>
        Line(name, shell, folder: @"C:\work\proj\sub dir").ShouldBe(expected);

    [Fact]
    public void AFolderTheShellWouldReadIntoIsRefused()
    {
        // The folder goes on the line in the cd, so what cmd or PowerShell would read in it counts.
        Line("app.exe", ShellQuoting.Cmd, folder: @"C:\100%\bin").ShouldBeNull();
        Line("app.exe", ShellQuoting.PowerShell, folder: @"C:\a[1]\bin").ShouldBeNull();
    }

    [Theory]
    // Windows drops the dot or space: tools.\x.cmd is tools\x.cmd, a neighbour.
    [InlineData(@"C:\work\proj\tools.", ShellQuoting.Posix)]
    [InlineData(@"C:\work\proj\tools ", ShellQuoting.Cmd)]
    // cmd has no current folder on a share or behind \\?\, and pwsh 7 found no .\x.exe behind it.
    [InlineData(@"\\server\share\bin", ShellQuoting.Cmd)]
    [InlineData(@"\\?\C:\work\deep", ShellQuoting.Cmd)]
    [InlineData(@"\\?\C:\work\deep", ShellQuoting.PowerShell)]
    public void RefusesAFolderTheLineCannotGoTo(string folder, ShellQuoting shell, string name = "x.exe") =>
        RunLine.For(Path.Combine(folder, name), Here, shell, Find, GitBash).ShouldBeNull();

    [Fact]
    public void ALongPathInThisFolderIsStillHere() =>
        // The file's path has \\?\ from 240 characters, the pane's shorter folder doesn't.
        RunLine.For(@"\\?\" + Here + @"\app.exe", Here, ShellQuoting.Cmd, Find, null).ShouldBe(@".\app.exe");

    [Fact]
    public void BashAndPowerShellGoToAShare() =>
        Line("app.exe", ShellQuoting.PowerShell, folder: @"\\server\share\bin")
            .ShouldBe(@"try { Set-Location -LiteralPath '\\server\share\bin' -ErrorAction Stop } catch { throw }; & '.\app.exe'");

    /// <summary>
    /// The line in the real shells, as CommandShells starts them, from a pane folder holding an x.cmd
    /// of its own: the row's x.cmd runs, and when its folder is gone by the time the line runs,
    /// nothing does. Strings alone had passed a PowerShell cd that went on to run the pane's file.
    /// </summary>
    [Fact]
    public void InTheRealShellsTheRowsFileRunsOrNothingDoes()
    {
        var root = Path.Combine(Path.GetTempPath(), "dc-runline-" + Guid.NewGuid().ToString("N"));
        var pane = Directory.CreateDirectory(Path.Combine(root, "pane")).FullName;
        var sub = Directory.CreateDirectory(Path.Combine(pane, "sub")).FullName;
        File.WriteAllText(Path.Combine(pane, "x.cmd"), "@echo WRONG-pane\r\n");
        // Read by a relative name, as a program reads files beside itself: only found from sub.
        File.WriteAllText(Path.Combine(sub, "x.cmd"), "@type beside.txt\r\n");
        File.WriteAllText(Path.Combine(sub, "beside.txt"), "RIGHT-folder\r\n");

        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        (ShellQuoting Shell, string Exec, Func<string, string> Args)[] shells =
        [
            (ShellQuoting.Cmd, Path.Combine(system, "cmd.exe"), line => $"/v:off /s /c \"{line}\""),
            (ShellQuoting.Posix, Path.Combine(programs, "Git", "bin", "bash.exe"), line => "-c " + ProcessArguments.ForMsys(line)),
            (ShellQuoting.PowerShell, Path.Combine(programs, "PowerShell", "7", "pwsh.exe"),
                line => "-NoLogo -NoProfile -Command " + ProcessArguments.ForWindows(line)),
            (ShellQuoting.PowerShell, Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"),
                line => "-NoLogo -NoProfile -Command " + ProcessArguments.ForWindows(line)),
        ];

        try
        {
            foreach (var (shell, exec, args) in shells.Where(s => File.Exists(s.Exec)))
            {
                var line = RunLine.For(Path.Combine(sub, "x.cmd"), pane, shell, _ => null, null);
                line.ShouldNotBeNull();
                Run(exec, args(line), pane).ShouldContain("RIGHT-folder", customMessage: $"{exec}: {line}");

                // The folder renamed away between building the line and running it.
                Directory.Move(sub, sub + "-gone");
                try
                {
                    Run(exec, args(line), pane).ShouldNotContain("WRONG", customMessage: $"{exec}: {line}");
                }
                finally
                {
                    Directory.Move(sub + "-gone", sub);
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Run(string exec, string arguments, string workingDirectory)
    {
        var info = new System.Diagnostics.ProcessStartInfo(exec, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        _ = error.Result;
        return output;
    }

    [Fact]
    public void AShellScriptFromAnotherShellUsesGitBashWithSlashes()
    {
        // Git Bash's runtime reads \ as an escape in a word it doesn't find quoted.
        Line("x.sh", ShellQuoting.Cmd, gitBash: @"C:\Program Files\Git\bin\bash.exe")
            .ShouldBe(@"""C:\Program Files\Git\bin\bash.exe"" ./x.sh");
        Line("x.sh", ShellQuoting.Cmd, gitBash: null).ShouldBeNull();
    }

    [Theory]
    [InlineData("${env:USERPROFILE}.bat", ShellQuoting.Posix)]
    [InlineData("100%done.exe", ShellQuoting.Cmd)]
    // A batch file is started by cmd /c whichever shell asks, and cmd reads these again.
    [InlineData("a&b.bat", ShellQuoting.Posix)]
    [InlineData("%OS%.bat", ShellQuoting.PowerShell)]
    [InlineData("x(1).cmd", ShellQuoting.Posix)]
    // Git Bash leaves these unquoted for cmd, which splits on them: x;y.bat ran x.bat.
    [InlineData("x;y.bat", ShellQuoting.Posix)]
    [InlineData("x,y.bat", ShellQuoting.Posix)]
    [InlineData("x=y.bat", ShellQuoting.Posix)]
    [InlineData("x y.bat", ShellQuoting.Posix)]
    // Delayed expansion, where the registry turns it on.
    [InlineData("a!b.exe", ShellQuoting.Cmd)]
    // Windows PowerShell's wildcards, single quotes or not: a[1].exe ran a1.exe.
    [InlineData("a[1].exe", ShellQuoting.PowerShell)]
    // Git Bash's runtime globs these when they reach it from another shell.
    [InlineData("it's.sh", ShellQuoting.Cmd)]
    [InlineData("a{b}.sh", ShellQuoting.PowerShell)]
    [InlineData("a[1].sh", ShellQuoting.Cmd)]
    [InlineData("notes.txt", ShellQuoting.Posix)]
    [InlineData("setup.msi", ShellQuoting.Cmd)]
    public void RefusesWhatItCannotPutOnALineSafely(string name, ShellQuoting shell) =>
        Line(name, shell).ShouldBeNull();

    [Fact]
    public void BashTakesWhatOtherShellsWouldGlob() =>
        Line("a[1].exe", ShellQuoting.Posix).ShouldBe("'./a[1].exe'");

    [Fact]
    public void FallsBackToTheOtherInterpreterAndNeedsOne()
    {
        static string? OnlyWindowsPowerShell(string program) =>
            program == "powershell" ? @"C:\ps\powershell.exe" : null;

        RunLine.For(@"C:\work\proj\a.ps1", Here, ShellQuoting.Posix, OnlyWindowsPowerShell, null)
            .ShouldBe("C:/ps/powershell.exe -NoProfile -File ./a.ps1");
        // Windows PowerShell reads [ ] in a -File path as a wildcard.
        RunLine.For(@"C:\work\proj\a[1].ps1", Here, ShellQuoting.Posix, OnlyWindowsPowerShell, null).ShouldBeNull();
        // Not on PATH, not run: a bare name could be found in the folder being browsed.
        RunLine.For(@"C:\work\proj\a.ps1", Here, ShellQuoting.Posix, _ => null, null).ShouldBeNull();
    }

    [Fact]
    public void AFolderOnTheWayIsJudgedToo() =>
        // A search result's folder goes on the line, in the cd.
        Line("x.sh", ShellQuoting.Posix, folder: @"C:\work\proj\${env:X}").ShouldBeNull();

    [Fact]
    public void TheInterpreterGoesByItsFullPath()
    {
        // cmd would find a py.cmd in the folder being browsed before the real one.
        Line("tool.py", ShellQuoting.Cmd).ShouldBe(@"C:\bin\py.exe .\tool.py");
        RunLine.For(@"C:\work\proj\tool.py", Here, ShellQuoting.Cmd, _ => null, null).ShouldBeNull();
    }

    [Fact]
    public void AnInterpreterThatIsABatchFileTakesTheBatchRule()
    {
        // pyenv's python.bat hands the script's name to cmd /c.
        static string? Shim(string program) => program == "py" ? @"C:\pyenv\py.bat" : null;

        RunLine.For(@"C:\work\proj\a&b.py", Here, ShellQuoting.PowerShell, Shim, null).ShouldBeNull();
        RunLine.For(@"C:\work\proj\ab.py", Here, ShellQuoting.PowerShell, Shim, null).ShouldBe(@"& 'C:\pyenv\py.bat' '.\ab.py'");
    }

    [Fact]
    public void PowerShellsCurlyQuotesAreDoubledLikeItsOwn() =>
        Line("q’;calc;’.exe", ShellQuoting.PowerShell).ShouldBe(@"& '.\q’’;calc;’’.exe'");
}
