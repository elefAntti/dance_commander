using DanceCommander.Extensibility;
using Shouldly;

namespace DanceCommander.Tests;

public sealed class AdminShellTests
{
    private static Func<string, string?> Installed(params string[] names) =>
        name => names.Contains(name) ? @"C:\Programs\" + name : null;

    private static string GoesTo(string literal) =>
        $"try {{ Set-Location -LiteralPath {literal} -ErrorAction Stop }} " +
        $"catch {{ Set-Location -LiteralPath $HOME; Write-Warning ('Could not open ' + {literal} + ' as administrator, so this is your home folder.') }}";

    [Fact]
    public void WindowsTerminalFirstWithTheFolderAsItsOwnArgument()
    {
        var start = AdminShell.For(@"C:\a b;c", Installed("wt.exe", "pwsh.exe", "powershell.exe"))!;

        start.Exec.ShouldBe(@"C:\Programs\wt.exe");
        start.Args.ShouldBe(["-d", @"C:\a b\;c"]);
    }

    [Theory]
    [InlineData(@"\\server\share\x")] // cmd as Windows Terminal's profile can't stand there
    [InlineData(@"\\?\C:\x\tools.")]
    [InlineData(@"C:\x\%USERNAME%")] // Windows Terminal may expand it
    public void FoldersWindowsTerminalMightMissGoToPowerShell(string folder)
    {
        var start = AdminShell.For(folder, Installed("wt.exe", "pwsh.exe"))!;

        start.Exec.ShouldBe(@"C:\Programs\pwsh.exe");
        start.Args.ShouldBe(["-NoExit", "-Command", GoesTo(AdminShell.Quote(folder))]);
    }

    [Fact]
    public void ThenPowerShell7()
    {
        var start = AdminShell.For(@"C:\x & calc", Installed("pwsh.exe", "powershell.exe"))!;

        start.Exec.ShouldBe(@"C:\Programs\pwsh.exe");
        start.Args.ShouldBe(["-NoExit", "-Command", GoesTo(@"'C:\x & calc'")]);
    }

    [Fact]
    public void ThenWindowsPowerShellWithTheFolderAsALiteral()
    {
        var start = AdminShell.For(@"C:\it's $(calc)", Installed("powershell.exe"))!;

        start.Exec.ShouldBe(@"C:\Programs\powershell.exe");
        start.Args.ShouldBe(["-NoExit", "-Command", GoesTo(@"'C:\it''s $(calc)'")]);
    }

    [Theory]
    [InlineData("a'b", "'a''b'")]
    [InlineData("a’b", "'a’’b'")]
    [InlineData("a‘b‚c‛", "'a‘‘b‚‚c‛‛'")]
    [InlineData("$env:x", "'$env:x'")]
    public void EveryQuotePowerShellKnowsIsDoubled(string text, string expected) =>
        AdminShell.Quote(text).ShouldBe(expected);

    [Fact]
    public void NothingInstalledIsNull() =>
        AdminShell.For(@"C:\", Installed()).ShouldBeNull();
}
