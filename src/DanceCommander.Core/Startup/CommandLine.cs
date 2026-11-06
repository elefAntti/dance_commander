using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Startup;

/// <summary>How many panes an invocation asked for, or nothing if it did not say.</summary>
public enum PaneRequest
{
    Unspecified,
    One,
    Two,
}

/// <summary>
/// What a command line asked Dance Commander to open.
/// </summary>
/// <param name="Left">Where the left (or only) pane should start, if the line said.</param>
/// <param name="Right">Where the right pane should start, if the line said.</param>
/// <param name="Select">
/// A file to put the cursor on. <see cref="Left"/> is then its containing folder, so that "show me
/// where this went" is one argument rather than two.
/// </param>
/// <param name="NewWindow">Force a second window rather than reusing the one already running.</param>
/// <param name="Help">Print usage and exit.</param>
/// <param name="Errors">Arguments that could not be understood. Reported, never silently dropped.</param>
/// <param name="Placed">
/// The line said which pane (<c>--left</c>, <c>--right</c>). Otherwise <see cref="Left"/> is only
/// the first location, and a window already open puts it in the pane in use.
/// </param>
public sealed record LaunchRequest(
    FsUri? Left = null,
    FsUri? Right = null,
    string? Select = null,
    PaneRequest Panes = PaneRequest.Unspecified,
    bool NewWindow = false,
    bool Help = false,
    IReadOnlyList<string>? Errors = null,
    bool Placed = false)
{
    public bool HasWork => Left is not null || Right is not null || Panes != PaneRequest.Unspecified;

    public IReadOnlyList<string> Problems => Errors ?? [];
}

/// <summary>
/// Parses the arguments another program used to launch us (§6.11).
/// </summary>
/// <remarks>
/// Explorer's syntax is accepted verbatim, and that is the whole design goal rather than a nicety.
/// Every installer, browser and chat client that offers "show in folder" already shells out to
/// <c>explorer.exe /select,"C:\path\file.zip"</c>; a tool that understands that exact string can be
/// dropped into those integrations with no change at the other end, and the ones that cannot be
/// changed at all — a shipped binary, someone else's script — start working by themselves.
///
/// So: <c>/select,PATH</c> opens the containing folder with the file under the cursor,
/// <c>/n,PATH</c> and <c>/e,PATH</c> are Explorer's one-pane and explore forms, and a bare path
/// just opens. The comma is not a typo and there is no space after it; that is how Explorer spells
/// it, quirk and all, so that is how this reads it.
///
/// Longer spellings exist alongside for anyone typing by hand, because <c>/e,</c> is not something
/// to have to remember.
/// </remarks>
public static class CommandLine
{
    public const string Usage = """
        Dance Commander

          DanceCommander                       open where you left off
          DanceCommander PATH                  open PATH in the active pane
          DanceCommander LEFT RIGHT            open one location in each pane

        Explorer-compatible, so existing "show in folder" integrations work unchanged:

          DanceCommander /select,PATH          open PATH's folder, cursor on PATH
          DanceCommander /n,PATH               open PATH, one pane
          DanceCommander /e,PATH               open PATH, two panes

        Spelled out:

          --select PATH                        same as /select,PATH
          --left PATH, --right PATH            place a pane explicitly
          --one-pane, --two-panes              how many panes
          --new-window                         a second window, rather than reusing the first
          --help                               this text

        A PATH may be any location this program understands, not only a local one:
        sftp://user@host/var/log and flat:C:\work are both valid.
        """;

    public static LaunchRequest Parse(IReadOnlyList<string> args)
    {
        FsUri? left = null;
        FsUri? right = null;
        string? select = null;
        var panes = PaneRequest.Unspecified;
        var newWindow = false;
        var help = false;
        var placed = false;
        var errors = new List<string>();
        var bare = new List<FsUri>();

        for (var i = 0; i < args.Count; i++)
        {
            var argument = args[i];
            if (string.IsNullOrWhiteSpace(argument))
                continue;

            // Explorer's own forms: a switch, a comma, and the path with no space between.
            if (Split(argument) is var (verb, tail) && verb is not null)
            {
                // "/select" with the path as the next argument happens when a caller builds its
                // command line by joining an array; accepting it costs nothing and fixes them.
                if (tail.Length == 0 && i + 1 < args.Count && !IsSwitch(args[i + 1]))
                    tail = args[++i];

                switch (verb)
                {
                    case "select":
                        if (tail.Length == 0)
                            errors.Add("/select needs a path.");
                        else
                            select = tail;
                        break;

                    case "n":
                        panes = PaneRequest.One;
                        Place(tail);
                        break;

                    case "e" or "root":
                        panes = PaneRequest.Two;
                        Place(tail);
                        break;

                    default:
                        errors.Add($"Not a known option: {argument}");
                        break;
                }

                continue;
            }

            switch (argument.ToLowerInvariant())
            {
                case "--help" or "-h" or "/?" or "-?":
                    help = true;
                    continue;

                case "--new-window":
                    newWindow = true;
                    continue;

                case "--one-pane":
                    panes = PaneRequest.One;
                    continue;

                case "--two-panes":
                    panes = PaneRequest.Two;
                    continue;

                case "--select" or "--left" or "--right":
                    var option = argument.ToLowerInvariant();
                    if (i + 1 >= args.Count)
                    {
                        errors.Add($"{option} needs a path.");
                        continue;
                    }

                    var value = args[++i];
                    if (option == "--select")
                    {
                        select = value;
                    }
                    else if (Resolve(value) is { } location)
                    {
                        placed = true;
                        if (option == "--left")
                            left = location;
                        else
                            right = location;
                    }
                    else
                    {
                        errors.Add(NotALocation(value));
                    }

                    continue;
            }

            if (argument.StartsWith('-'))
            {
                errors.Add($"Not a known option: {argument}");
                continue;
            }

            Place(argument);
        }

        // Bare paths fill the panes left to right, which is what "dc a b" plainly means.
        if (bare.Count > 0 && left is null)
            left = bare[0];
        if (bare.Count > 1 && right is null)
            right = bare[1];
        if (bare.Count > 2)
            errors.Add("More than two locations given; the extras were ignored.");

        if (select is not null)
        {
            if (Containing(select) is var (folder, name) && folder is not null)
            {
                left ??= folder;
                select = name;
            }
            else
            {
                errors.Add(NotALocation(select));
                select = null;
            }
        }

        // Two locations mean two panes without having to say so; asking for one and giving two
        // would be a contradiction, so an explicit flag still wins.
        if (panes == PaneRequest.Unspecified && right is not null)
            panes = PaneRequest.Two;

        return new LaunchRequest(left, right, select, panes, newWindow, help, errors, placed);

        void Place(string text)
        {
            if (text.Length == 0)
                return;
            if (Resolve(text) is { } uri)
                bare.Add(uri);
            else
                errors.Add(NotALocation(text));
        }
    }

    private static bool IsSwitch(string argument) =>
        argument.StartsWith('-') || Split(argument).Verb is not null;

    /// <summary>Splits Explorer's <c>/verb,tail</c>, returning a null verb when it is not one.</summary>
    private static (string? Verb, string Tail) Split(string argument)
    {
        if (argument.Length < 2 || argument[0] != '/')
            return (null, string.Empty);

        // "/" is also how a UNC-ish or posix-ish path starts, and "/select" is not a directory.
        var comma = argument.IndexOf(',');
        var verb = (comma < 0 ? argument[1..] : argument[1..comma]).ToLowerInvariant();

        if (verb is not ("select" or "n" or "e" or "root"))
            return (null, string.Empty);

        return (verb, comma < 0 ? string.Empty : argument[(comma + 1)..].Trim().Trim('"'));
    }

    /// <summary>
    /// Why an argument is no place. For an address, the parser's own reason and not the text: the
    /// text may carry a password, which is why it was refused, and the notice is on screen.
    /// </summary>
    private static string NotALocation(string text) =>
        text.Contains("://", StringComparison.Ordinal) && !FsUri.TryParse(text.Trim().Trim('"'), out _, out var error)
            ? $"Not a location: {error}"
            : $"Not a location: {text}";

    /// <summary>
    /// A path or a URI, as the user typed it. Relative paths resolve against the working directory,
    /// because a program launched from a shell inherits one and "dc ." should mean what it says.
    /// </summary>
    internal static FsUri? Resolve(string text)
    {
        text = text.Trim().Trim('"');
        if (text.Length == 0)
            return null;

        // A bare drive is its root, as in Explorer. To Windows "C:" is the current directory on C,
        // and it is what Explorer's "C:\" arrives as: the quoted "%V" of a drive ends in \", which
        // the argument rules read as a quote, and the quote is trimmed above (2026-10-16).
        if (text.Length == 2 && text[1] == ':' && char.IsAsciiLetter(text[0]))
            text += '\\';

        // A URI if it names a scheme, a path otherwise. This tests for a scheme rather than for a
        // colon because "C:\work" has a colon and is not a URI: a drive letter is one character,
        // and a scheme is more, which is the only difference there is to go on.
        var colon = text.IndexOf(':');
        var separator = text.IndexOfAny(['\\', '/']);
        var namesAScheme = colon > 1 && (separator < 0 || colon < separator);

        // Refused as an address, it is no path either: GetFullPath takes "sftp://bob:pw@host/" for
        // a relative one, and the password became a folder name in the session and the log.
        if (namesAScheme)
            return FsUri.TryParse(text, out var parsed, out _) ? parsed : null;

        try
        {
            return FsUri.Local(Path.GetFullPath(text));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The folder holding a path, and the name to put the cursor on. A directory selects itself
    /// from its parent, which is what Explorer does and is usually what was meant.
    /// </summary>
    internal static (FsUri? Folder, string Name) Containing(string text)
    {
        if (Resolve(text) is not { } uri)
            return (null, string.Empty);

        return uri.Parent is { } parent ? (parent, uri.Name) : (uri, string.Empty);
    }
}
