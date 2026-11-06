using System.Diagnostics.CodeAnalysis;

namespace DanceCommander.Core.Fs;

/// <summary>
/// The single address type for everything the application can show: local paths, remote
/// endpoints, entries inside archives, and (later) synthetic locations such as search results.
/// </summary>
/// <remarks>
/// Archive nesting uses '!' as the boundary and composes to any depth:
/// <c>file:///C:/dl/rel.zip!/bin/tools.tar.gz!/usr/bin</c>. For an archive URI, <see cref="Inner"/>
/// identifies the container <em>as a file</em> and <see cref="Path"/> is the entry path within it.
/// A literal '!' in a file name is not supported and must be avoided or encoded.
/// </remarks>
public sealed class FsUri : IEquatable<FsUri>
{
    public const char ArchiveSeparator = '!';
    public const string FileScheme = "file";
    public const string ArchiveScheme = "archive";

    private FsUri(string scheme, string authority, string path, FsUri? inner, string parameter = "")
    {
        Scheme = scheme;
        Authority = authority;
        Path = path;
        Inner = inner;
        Parameter = parameter;
    }

    /// <summary>Lower-case scheme: file, archive, sftp, dav, davs, search, flat, collection.</summary>
    public string Scheme { get; }

    /// <summary>"user@host:port" for remote schemes; empty for local and archive URIs.</summary>
    public string Authority { get; }

    /// <summary>Normalised path with '/' separators. Drive roots keep their slash ("C:/").</summary>
    public string Path { get; }

    /// <summary>
    /// For archive entries, the containing archive file. For a view, the location being viewed.
    /// </summary>
    public FsUri? Inner { get; }

    /// <summary>
    /// A view's argument — the query of a search, empty for a flattened tree. Kept out of
    /// <see cref="Path"/> because it is not a path and must not be normalised like one.
    /// </summary>
    public string Parameter { get; }

    public bool IsLocal => Scheme == FileScheme;

    public bool IsArchiveEntry => Scheme == ArchiveScheme;

    /// <summary>
    /// A synthetic location computed from another one — a flattened tree, a search (§1.2/3).
    /// Its rows are real files that live elsewhere, which is what distinguishes it from a
    /// directory and from an archive entry.
    /// </summary>
    public bool IsView => Inner is not null && !IsArchiveEntry;

    /// <summary>
    /// The folder this location is — for a view, the folder it is a view of, through any number of
    /// views. What anything that <em>writes</em> "here" writes into: a flattened tree or a search is
    /// a way of looking at a folder, not a place a new folder or a pasted file can go, and handing a
    /// view to a provider asked it for a directory inside something that is not one.
    /// </summary>
    public FsUri Anchor => IsView ? Inner!.Anchor : this;

    // ---- construction -------------------------------------------------------------------

    public static FsUri Local(string path) => LocalFrom(Normalize(path, windows: true));

    /// <summary>
    /// Short names expanded, but not in a path Win32 would read as another: GetLongPathName takes
    /// the trailing dot off <c>notes.</c> as every call without \\?\ does, and answered with the
    /// long name of <c>notes</c> (review, 2026-10-28).
    /// </summary>
    private static FsUri LocalFrom(string normalized) =>
        new(FileScheme, string.Empty, HasNameWin32Trims(normalized) ? normalized : ShortNames.Expand(normalized), null);

    /// <summary>
    /// A full path read from a program, a drop or a listing, every name kept as written: no trim,
    /// and no Win32 normalising, which takes a trailing dot or space off <c>notes.</c> and names
    /// the folder <c>notes</c> beside it (AUDIT.md §2 kind 45). Typed text goes to <see cref="Local"/>.
    /// </summary>
    public static FsUri LocalExact(string fullyQualified) =>
        System.IO.Path.IsPathFullyQualified(fullyQualified)
            ? LocalFrom(Normalize(fullyQualified, windows: true, trim: false))
            : Local(fullyQualified);

    /// <remarks>
    /// The path as the server has it: on a Unix host a backslash and a last space are part of a
    /// name. Read as a separator and trimmed, our own address for <c>a\b.txt</c>, copied or dragged
    /// and parsed back, was <c>/a/b.txt</c>, and Cut and Paste moved that (AUDIT.md §2 kind 45).
    /// Typed text is trimmed where it is typed.
    /// </remarks>
    public static FsUri Remote(string scheme, string authority, string path) =>
        new(Require(scheme), authority ?? string.Empty, Normalize(path, windows: false, trim: false, backslashes: false), null);

    public static FsUri ArchiveEntry(FsUri container, string entryPath) =>
        new(ArchiveScheme, string.Empty, Normalize(entryPath, windows: false), container);

    /// <summary>A view over <paramref name="root"/>, e.g. <c>flat:</c> or <c>search:</c>.</summary>
    /// <param name="path">
    /// Where inside the view: the crumbs of a script location (§6.16), <c>/12345/build-job</c>.
    /// Views without depth are at their root, <c>/</c>.
    /// </param>
    public static FsUri View(string scheme, FsUri root, string parameter = "", string path = "/") =>
        new(Require(scheme), string.Empty, Normalize(path, windows: false), root, parameter ?? string.Empty);

    /// <summary>
    /// The view scheme whose parameter carries depth: <c>from:inner?id/crumb/crumb</c>. A script
    /// location's crumbs live in <see cref="Path"/>, so they walk, compare and render like a path;
    /// only the listing id stays in <see cref="Parameter"/>.
    /// </summary>
    public const string FromScheme = "from";

    /// <summary>
    /// A search of the whole computer, starting where the pane stands: <c>everywhere:C:/work?report</c>
    /// looks in C:/work first, then in the places a person's files usually are.
    /// </summary>
    public const string EverywhereScheme = "everywhere";

    /// <summary>
    /// The application's own places (ARCHITECTURE.md §6.19): <c>about:computer</c>, and in time
    /// <c>about:bookmarks</c>, <c>about:history</c>, <c>about:collections/…</c>. The page is the
    /// path: <c>about:collections/release</c> is scheme <c>about</c>, path <c>/collections/release</c>.
    /// </summary>
    public const string AboutScheme = "about";

    /// <summary>The top of everything: drives and known folders. Where Up from a drive root goes.</summary>
    public static FsUri Computer { get; } = About("computer");

    /// <summary>
    /// An <c>about:</c> page; case-insensitive in what it names, like the browser's. The kind of
    /// page is lower-cased; what follows keeps the case it was given, so a collection called
    /// "Release" is shown as that while still matching "release".
    /// </summary>
    public static FsUri About(string page)
    {
        var path = Normalize(string.IsNullOrWhiteSpace(page) ? "computer" : page, windows: false);
        // The shelf's page was about:marked until 2026-10-29; tabs, history and bookmarks saved
        // before then still name it so.
        if (string.Equals(path, "/marked", StringComparison.OrdinalIgnoreCase))
            path = "/shelf";
        var end = path.IndexOf('/', 1);
        return new(AboutScheme, string.Empty, end < 0 ? path.ToLowerInvariant() : path[..end].ToLowerInvariant() + path[end..], null);
    }

    public bool IsAbout => Scheme == AboutScheme;

    private static string Require(string scheme) =>
        string.IsNullOrWhiteSpace(scheme)
            ? throw new FormatException("Scheme must not be empty.")
            : scheme.ToLowerInvariant();

    // ---- parsing ------------------------------------------------------------------------

    public static FsUri Parse(string text) =>
        TryParse(text, out var uri, out var error) ? uri : throw new FormatException(error);

    public static bool TryParse(string? text, [NotNullWhen(true)] out FsUri? uri, out string? error)
    {
        uri = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The location is empty.";
            return false;
        }

        try
        {
            // "about:computer" -- checked first: it has no "://" and is not a view of anything.
            if (text.StartsWith(AboutScheme + ":", StringComparison.OrdinalIgnoreCase))
            {
                uri = About(text[(AboutScheme.Length + 1)..]);
                return true;
            }

            var segments = SplitAtArchives(text);
            var current = ParseSingle(segments[0]);
            for (var i = 1; i < segments.Count; i++)
                current = ArchiveEntry(current, segments[i]);
            uri = current;
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Schemes that describe a way of looking at another location rather than a place.</summary>
    private static readonly string[] ViewSchemes = ["flat", "search", "from", EverywhereScheme];

    private static FsUri ParseSingle(string text)
    {
        // "flat:C:/work" or "search:C:/work?*.cs" -- a scheme wrapping a whole other address.
        // Checked before "://" so that "flat:file:///C:/work" reads as a view rather than as a
        // remote location whose host happens to be "file".
        foreach (var view in ViewSchemes)
        {
            if (!text.StartsWith(view + ":", StringComparison.OrdinalIgnoreCase))
                continue;

            var body = text[(view.Length + 1)..];

            // A flat view has no parameter, so any '?' belongs to what it is a view of:
            // "flat:from:C:/src?gitlab/4711" flattens a script location, and taking "gitlab/4711"
            // as the flat view's own parameter left the inner address without its listing.
            if (view == "flat")
            {
                if (body.Length == 0)
                    throw new FormatException($"'{text}' does not say what it is a view of.");
                return View(view, ParseSingle(body));
            }

            // The parameter is always last, so the final '?' delimits it; a remote path may
            // legitimately contain one earlier.
            var query = body.LastIndexOf('?');
            var parameter = query >= 0 ? body[(query + 1)..] : string.Empty;
            var inner = query >= 0 ? body[..query] : body;

            if (inner.Length == 0)
                throw new FormatException($"'{text}' does not say what it is a view of.");

            // "from:C:/src?gitlab/12345/build-job": the listing id, then where inside it. Split at
            // the first '/' so the id is always what the listing lookup reads (§6.16).
            if (view == FromScheme && parameter.IndexOf('/') is var depthAt and >= 0)
                return View(view, ParseSingle(inner), parameter[..depthAt], parameter[depthAt..]);

            return View(view, ParseSingle(inner), parameter);
        }

        var mark = text.IndexOf("://", StringComparison.Ordinal);
        if (mark < 0)
        {
            // A word and a colon before any separator names a kind of address, and not one known
            // here: a drive is one letter, and no Windows name holds a colon. "abou:bookmarks", a
            // typo for about:, read as the path \abou:bookmarks on the current drive (2026-10-17).
            var colon = text.IndexOf(':');
            var separator = text.IndexOfAny(['\\', '/']);
            if (colon > 1 && (separator < 0 || colon < separator))
                throw new FormatException($"'{text[..(colon + 1)]}' is not a kind of address Dance Commander knows, as about:, sftp:// or a drive letter are.");

            return Local(text); // a bare Windows path, e.g. C:\src or \\?\C:\src
        }

        var scheme = text[..mark].ToLowerInvariant();
        var rest = text[(mark + 3)..];

        // "file:///C:/src" -> rest is "/C:/src". Exact: it is our own written form (the clipboard, a
        // drag, a collection, history), and a name's last space trimmed was its neighbour's name.
        if (scheme == FileScheme)
            return LocalFrom(Normalize(rest, windows: true, trim: false));

        var slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? "/" : rest[slash..];

        // "sftp://bob:hunter2@host/", as other clients accept it: read as the user "bob:hunter2",
        // the password went into the log, history, the activity log, the credential's name and the
        // server's own log of login names. Neither an SSH account nor an HTTP Basic user may hold
        // a colon, so nothing real is refused. The message leaves the text out, since it is shown.
        var at = authority.LastIndexOf('@');
        if (at > 0 && authority.AsSpan(0, at).Contains(':'))
            throw new FormatException($"A {scheme}:// address cannot carry a password. Leave it out; it is asked for when the server wants it.");

        // A port is digits. Anything else there is the front of a password that a '/' or a '!' in
        // it cut off before the check above could see its '@': "sftp://bob:pa/ss@host/" has the
        // authority "bob:pa", and navigating to it logged the whole address.
        var hostPort = authority[(at + 1)..];
        var portAt = hostPort.LastIndexOf(':');
        var port = portAt > hostPort.LastIndexOf(']') ? hostPort.AsSpan(portAt + 1) : "0";
        if (port.IsEmpty || port.ContainsAnyExceptInRange('0', '9'))
            throw new FormatException($"The port in this {scheme}:// address is not a number. A password goes in when asked, not in the address.");

        return Remote(scheme, authority, path);
    }

    /// <summary>
    /// Collapses separators, resolves "." and "..", and canonicalises drive letters. Windows
    /// mode additionally strips a "\\?\" prefix and preserves a leading "//" for UNC paths.
    /// </summary>
    private static string Normalize(string raw, bool windows, bool trim = true, bool backslashes = true)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new FormatException("The path is empty.");

        // A path behind \\?\ is exact: a space at its end is part of the last name, and trimmed
        // it was the neighbour without one. \\?\UNC\server\share is the share, not a folder UNC.
        string s;
        if (windows && raw.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            s = raw[4..];
            if (s.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
                s = @"\\" + s[4..];
        }
        else
        {
            s = trim ? raw.Trim() : raw;
        }

        // A separator on Windows and in a typed archive path; a character in a server's name.
        if (backslashes)
            s = s.Replace('\\', '/');

        var isUnc = windows && s.StartsWith("//", StringComparison.Ordinal);
        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);

        string? drive = null;
        var start = 0;
        if (windows && parts.Length > 0 && IsDriveToken(parts[0]))
        {
            drive = char.ToUpperInvariant(parts[0][0]) + ":";
            start = 1;
        }

        var stack = new List<string>(parts.Length);
        for (var i = start; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(part);
        }

        var body = string.Join('/', stack);
        if (drive is not null)
            return stack.Count == 0 ? drive + "/" : drive + "/" + body;
        if (isUnc)
            return "//" + body;
        return "/" + body;
    }

    private static bool IsDriveToken(string token) =>
        token.Length == 2 && token[1] == ':' && char.IsAsciiLetter(token[0]);

    private static bool IsDriveRootPath(string path) =>
        path.Length == 3 && path[1] == ':' && path[2] == '/' && char.IsAsciiLetter(path[0]);

    // ---- navigation ---------------------------------------------------------------------

    public bool IsRoot => Path == "/" || IsDriveRootPath(Path);

    /// <summary>
    /// The containing location, or null at the top. Leaving the root of an archive lands in the
    /// directory that holds the archive file, which is what a user expects from "up".
    /// </summary>
    /// <summary>
    /// Where Up goes: the parent, and from a drive root on to the computer page — rather than
    /// stopping with no way to see what other drives there are. Not <see cref="Parent"/> itself:
    /// a drive is not <em>in</em> the computer page, and copies and drops ask what contains what.
    /// </summary>
    public FsUri? Up => Parent ?? (IsLocal && IsRoot ? Computer : null);

    public FsUri? Parent
    {
        get
        {
            // Leaving a view puts you in the folder it was a view of. Leaving an archive puts you
            // beside the archive file, because the archive *is* a file in that folder -- a view is
            // not a thing in its root, it is a way of looking at it. A view with depth -- a script
            // location's crumbs -- climbs those first, and leaves only from its root.
            if (IsView && IsRoot)
                return Inner;

            // An about: page below another climbs to it; a page at the top level climbs to the
            // computer page; the computer page is the top.
            if (IsAbout)
            {
                var above = Path.LastIndexOf('/');
                if (above > 0)
                    return About(Path[..above]);
                return this == Computer ? null : Computer;
            }

            if (IsRoot)
                return Inner?.Parent;

            var cut = Path.LastIndexOf('/');
            if (cut < 0)
                return null;

            var parentPath = cut == 0 ? "/" : Path[..cut];
            if (parentPath.Length == 2 && parentPath[1] == ':')
                parentPath += "/";
            return new FsUri(Scheme, Authority, parentPath, Inner, Parameter);
        }
    }

    /// <summary>The last path segment; for a drive root, the drive ("C:").</summary>
    public string Name
    {
        get
        {
            if (this == Computer)
                return "This computer";
            if (this == AboutPages.Collections)
                return "Collections";
            if (this == AboutPages.Bookmarks)
                return "Bookmarks";
            if (this == AboutPages.History)
                return "History";
            if (this == AboutPages.Shelf)
                return "Shelf";
            if (IsAbout)
                return Path[(Path.LastIndexOf('/') + 1)..];

            if (IsView && !IsRoot)
            {
                var last = Path.LastIndexOf('/');
                return Path[(last + 1)..];
            }

            if (IsView)
            {
                // Distinguishable from the folder itself, so a tab strip showing both is readable.
                var root = Inner!.Name;
                return Parameter.Length == 0 ? $"{Scheme}: {root}" : $"{Scheme}: {Parameter}";
            }

            if (IsDriveRootPath(Path))
                return Path[..2];
            if (Path == "/")
                return Inner?.Name ?? "/";
            var cut = Path.LastIndexOf('/');
            return cut >= 0 ? Path[(cut + 1)..] : Path;
        }
    }

    public FsUri Child(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Name must not be empty.", nameof(name));

        // One name, never a path. Names come from servers, archives and scripts, where "..\..\x"
        // or "a/../b" is ordinary text; appended as it stands it climbed out of the folder it was
        // meant to be in, and a "replace" or a copy then landed on the user's own folders (§1.6).
        // On Windows a backslash or a colon (an alternate stream) is structure as well.
        if (name is "." or ".." || name.Contains('/') || name.Contains('\0') ||
            (IsLocal && (name.Contains('\\') || name.Contains(':'))))
        {
            throw new ArgumentException($"\"{name}\" is not a single name here.", nameof(name));
        }

        var path = Path.EndsWith('/') ? Path + name : Path + "/" + name;
        // The parameter too: a child of a view is still in that view, and a "from:" address that
        // lost its listing id would name nothing at all (§6.16).
        return new FsUri(Scheme, Authority, path, Inner, Parameter);
    }

    // ---- rendering ----------------------------------------------------------------------

    /// <summary>Round-trippable form, used for bookmarks, history and session state.</summary>
    /// <summary>
    /// The application's own form, which <see cref="Parse"/> reads back: what history, bookmarks,
    /// the session and its own drags keep. A '!' in a name is doubled here (see
    /// <see cref="Escape"/>), so this is not what to give another program: that is <see cref="Text"/>.
    /// </summary>
    public override string ToString() => Write(escaped: true);

    /// <summary>
    /// The address as people and other programs write it, a '!' in a name as it is: for Copy path,
    /// a tool's <c>${uri}</c>, a line run on a server. The doubled form there named a different
    /// file to whatever read it (review, 2026-10-15).
    /// </summary>
    public string Text => Write(escaped: false);

    private string Write(bool escaped)
    {
        string E(string text) => escaped ? Escape(text) : text;

        if (IsView)
            return (Parameter.Length == 0 ? $"{Scheme}:{Inner!.Write(escaped)}" : $"{Scheme}:{Inner!.Write(escaped)}?{E(Parameter)}") + E(Depth);
        if (IsAbout)
            return $"{AboutScheme}:{Path.TrimStart('/')}";
        if (Inner is not null)
            return Inner.Write(escaped) + ArchiveSeparator.ToString() + E(Path);
        if (IsLocal)
            return "file://" + E(Path.StartsWith("//", StringComparison.Ordinal) ? Path : "/" + Path);
        return $"{Scheme}://{Authority}{E(Path)}";
    }

    /// <summary>
    /// One address from text that came from outside — a server's listing, a script's output —
    /// where a '!' is part of a name and never a step into an archive. Reading those as the
    /// application's own form turned a name with "!!" in it into another file's address.
    /// </summary>
    public static bool TryParseWhole(string? text, [NotNullWhen(true)] out FsUri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith(AboutScheme + ":", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            uri = ParseSingle(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// A '!' that is part of a name, written twice so that it is not read as the step into an
    /// archive: a folder called <c>a!b</c> came back from history and bookmarks as entry <c>b</c> of
    /// an archive <c>a</c>, and could not be reached (§4, audit round 3). Doubled because two in a
    /// row mean nothing as separators: there is no entry with an empty name.
    /// </summary>
    private static string Escape(string text) =>
        text.Contains(ArchiveSeparator) ? text.Replace("!", "!!", StringComparison.Ordinal) : text;

    /// <summary>The address split at each archive step, a doubled '!' read as one in a name.</summary>
    private static List<string> SplitAtArchives(string text)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != ArchiveSeparator)
            {
                current.Append(text[i]);
            }
            else if (i + 1 < text.Length && text[i + 1] == ArchiveSeparator)
            {
                current.Append(ArchiveSeparator);
                i++;
            }
            else
            {
                parts.Add(current.ToString());
                current.Clear();
            }
        }

        parts.Add(current.ToString());
        return parts;
    }

    /// <summary>
    /// For text a person typed or pasted, or a program passed on the command line: as
    /// <see cref="TryParse"/>, except that a native path whose '!' is part of a name, not the step
    /// into an archive, is taken as that path. Told so by the disk: what comes before the '!' is
    /// not a file, and the whole of it exists. A real archive's entries read as before.
    /// </summary>
    public static bool TryParseTyped(
        string? text, Func<string, bool> isFile, Func<string, bool> exists,
        [NotNullWhen(true)] out FsUri? uri, out string? error)
    {
        var parsed = TryParse(text, out uri, out error);
        if (string.IsNullOrWhiteSpace(text) || !text.Contains(ArchiveSeparator) || text.Contains("://", StringComparison.Ordinal))
            return parsed && uri is not null;

        // A real archive's entry: what comes before the '!' is a file. That reading stands.
        var outer = uri;
        while (outer?.Inner is not null)
            outer = outer.Inner;
        if (parsed && uri is not null && !ReferenceEquals(outer, uri) && outer is { IsLocal: true } && isFile(outer.ToLocalPath()))
            return true;

        // Otherwise the path as typed, '!' and "!!" and all, when the disk has it: the address bar
        // shows a folder called "a!!b" as that, and Enter on it went to "a!b" (review, 2026-10-15).
        try
        {
            var whole = Local(text.Trim());
            if (exists(whole.ToLocalPath()))
            {
                uri = whole;
                error = null;
                return true;
            }
        }
        catch (FormatException)
        {
            // Not a path as a whole either: the first reading stands.
        }

        return parsed && uri is not null;
    }

    /// <summary>What the address bar shows: native separators for local paths.</summary>
    public string Display =>
        IsView ? (Parameter.Length == 0 ? $"{Scheme}:{Inner!.Display}" : $"{Scheme}:{Inner!.Display}?{Parameter}") + Depth
        : IsAbout ? ToString()
        : Inner is not null ? Inner.Display + ArchiveSeparator + Path
        : IsLocal ? Path.Replace('/', '\\')
        : $"{Scheme}://{Authority}{Path}";

    /// <summary>
    /// A <c>from:</c> view's crumbs, as they follow its id; empty at the root and for other views,
    /// whose parameter is free text (a search query may contain '/') and has no depth to render.
    /// </summary>
    private string Depth => Scheme == FromScheme && !IsRoot && Parameter.Length > 0 ? Path : string.Empty;

    /// <summary>
    /// The Win32 path for a local URI, prefixed with "\\?\" when long enough to need it, or when a
    /// name in it ends in a dot or a space.
    /// </summary>
    /// <remarks>
    /// Win32 takes a trailing dot or space off every name in a path it is given without \\?\, so
    /// <c>notes.</c> (made by WSL or robocopy) was the folder <c>notes</c> beside it: Shift+Del on
    /// one deleted the other (AUDIT.md §2 kind 45).
    /// </remarks>
    public string ToLocalPath()
    {
        if (!IsLocal)
            throw new InvalidOperationException($"'{Display}' is not a local path.");
        var win = Path.Replace('/', '\\');
        if (win.StartsWith(@"\\?\", StringComparison.Ordinal) || (win.Length < 240 && !HasNameWin32Trims(win)))
            return win;
        return win.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + win[2..]
            : @"\\?\" + win;
    }

    /// <summary>Whether a name in the path ends in a dot or a space, other than . and ..</summary>
    public static bool HasNameWin32Trims(string path)
    {
        foreach (var name in path.Split('\\', '/'))
        {
            if (name is "." or ".." || name.Length == 0)
                continue;
            if (name[^1] is '.' or ' ')
                return true;
        }

        return false;
    }

    // ---- equality -----------------------------------------------------------------------

    // Local paths are case-insensitive; archive entries and remote paths are not.
    private StringComparison PathComparison => IsLocal || IsAbout ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public bool Equals(FsUri? other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;
        return string.Equals(Scheme, other.Scheme, StringComparison.Ordinal)
            && string.Equals(Authority, other.Authority, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path, other.Path, PathComparison)
            && Equals(Inner, other.Inner)
            && string.Equals(Parameter, other.Parameter, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj) => Equals(obj as FsUri);

    public override int GetHashCode() => HashCode.Combine(
        Scheme,
        Authority.ToLowerInvariant(),
        IsLocal || IsAbout ? Path.ToLowerInvariant() : Path,
        Inner,
        Parameter.ToLowerInvariant());

    public static bool operator ==(FsUri? left, FsUri? right) => Equals(left, right);

    public static bool operator !=(FsUri? left, FsUri? right) => !Equals(left, right);
}
