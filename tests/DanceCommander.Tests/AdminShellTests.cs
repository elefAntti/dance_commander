using DanceCommander.Extensibility;
using Shouldly;

namespace DanceCommander.Tests;

public sealed class AdminShellTests
{
    private static Func<string, string?> Installed(params string[] names) =>
        name => names.Contains(name) ? @"C:\Programs\" + name : null;

    [Fact]
    public void WindowsTerminalFirstWithTheFolderAsItsOwnArgument()
    {
        var start = AdminShell.For(@"C:\a b;c", Installed("wt.exe", "pwsh.exe", "powershell.exe"))!;

        start.Exec.ShouldBe(@"C:\Programs\wt.exe");
        start.Args.ShouldBe(["-d", @"C:\a b\;c"]);
    }

    [Fact]
    public void ThenPowerShell7()
    {
        var start = AdminShell.For(@"C:\x & calc", Installed("pwsh.exe", "powershell.exe"))!;

        start.Exec.ShouldBe(@"C:\Programs\pwsh.exe");
        start.Args.ShouldBe(["-NoExit", "-WorkingDirectory", @"C:\x & calc"]);
    }

    [Fact]
    public void ThenWindowsPowerShellWithTheFolderAsALiteral()
    {
        var start = AdminShell.For(@"C:\it's $(calc)", Installed("powershell.exe"))!;

        start.Args.ShouldBe(["-NoExit", "-Command", @"Set-Location -LiteralPath 'C:\it''s $(calc)'"]);
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
