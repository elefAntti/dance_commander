namespace DanceCommander.Extensibility;

/// <summary>
/// What a tool's arguments become when its program is <c>wsl.exe</c>. Its own options (<c>-d</c>,
/// <c>--cd</c>, <c>-u</c>) it reads itself. After <c>--exec</c> (<c>-e</c>) the rest is the
/// program and its arguments, each passed on as it is. But a command given without it, or after
/// <c>--</c>, goes to the distribution's shell as a line to read, so a file called
/// <c>$(rm -rf ~).sh</c> in <c>wsl bash ${path|wsl}</c> would run as a command: the argument
/// vector's promise (§6.6) broken the way cmd breaks it for a batch file.
/// </summary>
public static class WslArguments
{
    /// <summary>
    /// wsl.exe, or wslg.exe with the same grammar, or System32's bash.exe, which is WSL's too. As
    /// it will run: %windir% expanded and a bare name found, as the launch will.
    /// </summary>
    public static bool IsWsl(string exec)
    {
        var expanded = Environment.ExpandEnvironmentVariables(exec);
        var name = Path.GetFileNameWithoutExtension(expanded);
        if (name.Equals("wsl", StringComparison.OrdinalIgnoreCase) || name.Equals("wslg", StringComparison.OrdinalIgnoreCase))
            return true;

        return name.Equals("bash", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Path.GetDirectoryName(ExecutableSearch.Resolve(expanded)), Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The first value from outside that the Linux shell would read as more than a word, or null
    /// when there is none: the arguments go to a program after --exec, or every one that reaches
    /// the shell is the user's own text or a plain word.
    /// </summary>
    /// <param name="fromOutside">For each argument, whether it holds a name, a path or the like.</param>
    public static WslRefusal? UnsafeForShell(IReadOnlyList<string> arguments, IReadOnlyList<bool> fromOutside)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            var outside = i < fromOutside.Count && fromOutside[i];

            // A value where wsl.exe looks for its options would be read as one: "-e" from a name
            // would turn what follows into a program to run. Values never start with a dash
            // (${name} is ./-name), so this is only a guard.
            if (outside && argument.StartsWith('-'))
                return new WslRefusal(argument, InShellLine: false);

            if (argument is "-e" or "--exec")
                return ShellScript(arguments, fromOutside, i + 1);

            if (argument == "--")
                return FirstUnsafe(arguments, fromOutside, i + 1) is { } after ? new WslRefusal(after, false) : null;

            if (TakesValue(argument))
            {
                i++;
                continue;
            }

            if (argument.StartsWith('-'))
                continue;

            // The first word that isn't an option: it and everything after it are the shell's line.
            return FirstUnsafe(arguments, fromOutside, i) is { } word ? new WslRefusal(word, false) : null;
        }

        return null;
    }

    /// <summary>
    /// After --exec the program gets its arguments as they are, unless the program is itself a
    /// shell given a line with -c: <c>--exec bash -c "cat ${path|wsl}"</c> is the same hole one
    /// step on, and the notice that sends people to --exec must not send them there (review,
    /// 2026-11-12). No value from outside goes in the line at all: a line is never one word, and
    /// what follows it reaches the shell as $0, $1…, values it doesn't read as commands.
    /// </summary>
    private static WslRefusal? ShellScript(IReadOnlyList<string> arguments, IReadOnlyList<bool> fromOutside, int program)
    {
        // env runs the program after its NAME=value words and options.
        var i = program;
        if (i < arguments.Count && Path.GetFileName(arguments[i]) == "env")
        {
            i++;
            while (i < arguments.Count && (arguments[i].StartsWith('-') || arguments[i].Contains('=')))
                i++;
        }

        if (i >= arguments.Count || !Shells.Contains(Path.GetFileName(arguments[i])))
            return null;

        // Options up to the line: -c, or bundled as -lc, -ec.
        for (i++; i < arguments.Count && arguments[i].StartsWith('-'); i++)
        {
            if (arguments[i].Length > 1 && arguments[i][1] != '-' && arguments[i].Contains('c'))
            {
                var line = i + 1;
                return line < arguments.Count && line < fromOutside.Count && fromOutside[line]
                    ? new WslRefusal(arguments[line], InShellLine: true)
                    : null;
            }
        }

        return null;
    }

    private static readonly HashSet<string> Shells = ["sh", "bash", "dash", "zsh", "ksh", "mksh", "fish", "ash", "busybox"];

    private static bool TakesValue(string option) =>
        option is "--cd" or "-d" or "--distribution" or "--distribution-id" or "-u" or "--user" or "--shell-type";

    private static string? FirstUnsafe(IReadOnlyList<string> arguments, IReadOnlyList<bool> fromOutside, int from)
    {
        for (var i = from; i < arguments.Count; i++)
        {
            if (i < fromOutside.Count && fromOutside[i] && !IsPlainWord(arguments[i]))
                return arguments[i];
        }

        return null;
    }

    /// <summary>
    /// What any POSIX shell reads as one word and nothing else. Letters beyond ASCII are letters to
    /// a shell; a space is refused too, since a line split there is two arguments. Not a leading
    /// dash, which the command would take for an option, nor a leading = or %, which zsh reads as
    /// a program's path and fish as a process (the same as <c>ShellQuoteState.PosixSafe</c>).
    /// </summary>
    internal static bool IsPlainWord(string value) =>
        value.Length > 0 && value[0] is not ('-' or '=' or '%') &&
        value.All(c => c > 127 || char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or ':' or '@' or '%' or '+' or ',' or '=');
}

/// <param name="Value">The argument that would be read as a command.</param>
/// <param name="InShellLine">
/// It is a shell's -c line holding a value, rather than a word wsl.exe hands to the shell itself.
/// </param>
public sealed record WslRefusal(string Value, bool InShellLine);
