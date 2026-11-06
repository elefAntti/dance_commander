using System.Text.RegularExpressions;

namespace DanceCommander.Core.Jobs;

/// <summary>
/// Output meant for a terminal, made fit for a panel that is not one.
/// </summary>
/// <remarks>
/// Programs colour their output with ANSI escape sequences, and some do it even when their output
/// is captured rather than shown in a console: PowerShell 7's table headers came through as
/// <c>[32;1mMode   [0m</c> (2026-10-02), and git or ls with colour forced on do the same. The
/// panel is text in the chrome's palette (DESIGN.md §1.3: the chrome is near-monochrome), so the
/// sequences are removed rather than drawn.
/// </remarks>
public static partial class TerminalText
{
    /// <summary>The text without its escape sequences; unchanged, and unallocated, when it has none.</summary>
    public static string Plain(string text) =>
        text.Contains('\u001B') ? Escapes().Replace(text, string.Empty) : text;

    // CSI (colours, cursor moves: ESC [ … final byte), OSC (window titles, links: ESC ] … BEL or
    // ESC \), and the two-character ones (ESC and one byte).
    [GeneratedRegex(@"\u001B\[[0-?]*[ -/]*[@-~]|\u001B\][^\u0007\u001B]*(\u0007|\u001B\\)|\u001B[@-Z\\-_]")]
    private static partial Regex Escapes();
}
