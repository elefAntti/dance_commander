namespace DanceCommander.Extensibility;

/// <summary>
/// What starts an administrator terminal in a folder: Windows Terminal if it is installed, else
/// PowerShell. Only a shell. The app never elevates an operation of its own (WISHLIST.md, Probably
/// not), and nothing it runs inherits the rights: what happens in that window is the user's typing.
/// </summary>
/// <remarks>
/// No cmd.exe: an elevated start ignores the working folder, so cmd would need the folder written
/// into a <c>cd</c> on its command line, where a name with <c>&amp;</c> in it is a second command.
/// Windows Terminal takes the folder as an argument of its own; PowerShell gets it as a quoted
/// literal. An elevated shell that can't get to the folder starts in System32, where a
/// <c>del *</c> meant for the folder would land with administrator rights: PowerShell is told to
/// go home instead and say why, and Windows Terminal is used only where it can't fail that way.
/// </remarks>
public static class AdminShell
{
    public sealed record Start(string Exec, IReadOnlyList<string> Args, string Name);

    /// <param name="folder">A local folder, as a full path.</param>
    /// <param name="find">The full path of a program by its name, or null where it isn't installed.</param>
    /// <returns>Null when none of them is installed.</returns>
    public static Start? For(string folder, Func<string, string?> find)
    {
        // Windows Terminal hands the folder to whatever its default profile is: cmd can't stand in
        // a UNC folder (it starts in C:\Windows), a \\?\ path is beyond most of them, and its
        // starting folder may have %VARIABLES% expanded. Those go to PowerShell, which says so
        // when it can't get there.
        var plain = !folder.StartsWith(@"\\", StringComparison.Ordinal) && !folder.Contains('%');
        if (plain && find("wt.exe") is { } terminal)
        {
            // Windows Terminal reads an unescaped ';' as "and another tab", even inside an argument.
            return new Start(terminal, ["-d", folder.Replace(";", "\\;", StringComparison.Ordinal)], "Windows Terminal");
        }

        if (find("pwsh.exe") is { } pwsh)
            return new Start(pwsh, ["-NoExit", "-Command", GoTo(folder)], "PowerShell");

        if (find("powershell.exe") is { } powershell)
            return new Start(powershell, ["-NoExit", "-Command", GoTo(folder)], "Windows PowerShell");

        return null;
    }

    /// <summary>
    /// Into the folder, or, when that fails, home with a warning that says so: never left in
    /// System32, where an elevated shell starts.
    /// </summary>
    private static string GoTo(string folder) =>
        $"try {{ Set-Location -LiteralPath {Quote(folder)} -ErrorAction Stop }} " +
        $"catch {{ Set-Location -LiteralPath $HOME; Write-Warning ('Could not open ' + {Quote(folder)} + ' as administrator, so this is your home folder.') }}";

    /// <summary>
    /// A PowerShell single-quoted literal: nothing inside is expanded, and each quote character
    /// PowerShell accepts (the typographic ones too) is doubled so it can't end the string.
    /// </summary>
    public static string Quote(string text)
    {
        var quoted = new System.Text.StringBuilder(text.Length + 2).Append('\'');
        foreach (var ch in text)
        {
            if (ch is '\'' or '‘' or '’' or '‚' or '‛')
                quoted.Append(ch);
            quoted.Append(ch);
        }

        return quoted.Append('\'').ToString();
    }
}
