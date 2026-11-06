using DanceCommander.Core.Jobs;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>Captured output loses the escape sequences a terminal would have drawn as colour.</summary>
public class TerminalTextTests
{
    private const char Esc = '\u001B';

    [Fact]
    public void PowerShellTableHeadersComeOutPlain()
    {
        // As PowerShell 7 printed it into the panel on 2026-10-02.
        var header = $"{Esc}[32;1mMode   {Esc}[0m{Esc}[32;1m              LastWriteTime{Esc}[0m";
        TerminalText.Plain(header).ShouldBe("Mode                 LastWriteTime");
    }

    [Fact]
    public void TitlesAndLinksGoToo()
    {
        TerminalText.Plain($"{Esc}]0;window title\u0007done").ShouldBe("done");
        TerminalText.Plain($"{Esc}]8;;https://example.com{Esc}\\link{Esc}]8;;{Esc}\\").ShouldBe("link");
    }

    [Fact]
    public void TextWithoutEscapesIsLeftAlone()
    {
        const string text = "-a---  01/10/2026  19.12  1204 bookmarks.json [x] 100%";
        TerminalText.Plain(text).ShouldBeSameAs(text);
    }
}
