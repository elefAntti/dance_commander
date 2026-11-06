using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Commands;

[Flags]
public enum CommandScope
{
    None = 0,

    /// <summary>Right-click on empty space; nothing selected.</summary>
    Background = 1,
    File = 2,
    Directory = 4,

    /// <summary>More than one item selected.</summary>
    Multiple = 8,
    Any = Background | File | Directory | Multiple,
}

/// <summary>
/// What a command acts on (§6.18). Not the same question as <see cref="CommandScope"/>, which says
/// where a command may be offered: the renderers used to guess this from that, and the guess is
/// how Worktrees came to vanish from one menu or another depending on what was selected.
/// </summary>
public enum CommandSubject
{
    /// <summary>The selected rows: Open, Rename, Copy path, Git diff.</summary>
    Selection,

    /// <summary>
    /// The folder the pane shows, or the repository it is in: Paste, New folder, Git status,
    /// Worktrees. Offered whatever is selected, because the selection is not what it is about.
    /// </summary>
    Location,

    /// <summary>The window itself — tabs, panes, the view, undo. Never in a context menu.</summary>
    Window,
}

[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
}

/// <summary>A key plus modifiers, e.g. "F7", "Ctrl+Shift+G", "Alt+Left".</summary>
public readonly record struct KeyStroke(string Key, KeyModifiers Modifiers = KeyModifiers.None)
{
    public static KeyStroke Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = KeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            modifiers |= parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => KeyModifiers.Control,
                "shift" => KeyModifiers.Shift,
                "alt" => KeyModifiers.Alt,
                var other => throw new FormatException($"Unknown modifier '{other}'."),
            };
        }
        return new KeyStroke(parts[^1], modifiers);
    }

    /// <summary>
    /// A key as a person types it into settings.json: any case, the usual short names (Esc, Del,
    /// PgDn). Comes back spelled the way the keyboard reports it, or not at all: "Ctrl+s" must be
    /// the same stroke as the S key pressed with Ctrl, and a name no key produces would be a binding
    /// that silently never fires.
    /// </summary>
    public static bool TryParseTyped(string? text, out KeyStroke stroke)
    {
        stroke = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // "Ctrl++" cannot be meant; a trailing "+" alone is not a key the mapper knows either.
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = KeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var modifier = parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => KeyModifiers.Control,
                "shift" => KeyModifiers.Shift,
                "alt" => KeyModifiers.Alt,
                _ => KeyModifiers.None,
            };
            if (modifier == KeyModifiers.None)
                return false;
            modifiers |= modifier;
        }

        if (KeyName(parts[^1]) is not { } key)
            return false;
        stroke = new KeyStroke(key, modifiers);
        return true;
    }

    /// <summary>The names the key mapper produces, matched without regard to case.</summary>
    private static readonly string[] NamedKeys =
    [
        "Enter", "Backspace", "Tab", "Escape", "Space", "Delete", "Insert",
        "PageUp", "PageDown", "Home", "End", "Left", "Right", "Up", "Down",
    ];

    private static string? KeyName(string typed)
    {
        if (typed.Length == 1)
        {
            var c = char.ToUpperInvariant(typed[0]);
            return c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or ',' ? c.ToString() : null;
        }

        if (new KeyStroke(typed).FunctionKeyNumber is { } n)
            return "F" + n;

        var alias = typed.ToLowerInvariant() switch
        {
            "esc" => "Escape",
            "del" => "Delete",
            "ins" => "Insert",
            "return" => "Enter",
            "pgup" => "PageUp",
            "pgdn" => "PageDown",
            _ => typed,
        };
        return NamedKeys.FirstOrDefault(name => name.Equals(alias, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The function key number (1-24) this stroke targets, or null.</summary>
    public int? FunctionKeyNumber =>
        Key.Length >= 2 && (Key[0] == 'F' || Key[0] == 'f') && int.TryParse(Key[1..], out var n) && n is >= 1 and <= 24
            ? n
            : null;

    public override string ToString()
    {
        var prefix = string.Empty;
        if (Modifiers.HasFlag(KeyModifiers.Control)) prefix += "Ctrl+";
        if (Modifiers.HasFlag(KeyModifiers.Shift)) prefix += "Shift+";
        if (Modifiers.HasFlag(KeyModifiers.Alt)) prefix += "Alt+";
        return prefix + Key;
    }
}

/// <summary>What a command is allowed to know about the world it runs in.</summary>
public interface ICommandContext
{
    FsUri Directory { get; }

    IReadOnlyList<FsEntry> Selection { get; }

    FsEntry? Focused { get; }

    FsUri? OtherPaneDirectory { get; }

    /// <summary>
    /// True when <see cref="Directory"/> is inside a git work tree.
    /// </summary>
    /// <remarks>
    /// A fact about the location rather than about any entry, which is why it lives here and not
    /// in the matcher the highlight rules share: no glob over a filename can answer it. It is the
    /// condition that lets git commands exist without a plugin host -- they are ordinary commands
    /// that decline to appear outside a repository.
    /// </remarks>
    bool InRepository => Repository is not null;

    /// <summary>
    /// The work tree <see cref="Directory"/> is inside, or null. What the location tokens expand
    /// from, and the same answer wherever in the tree you stand — so a command about the
    /// repository is offered from a nested folder exactly as from its root.
    /// </summary>
    RepositoryInfo? Repository { get; }

    IDecisionHost Decisions { get; }

    /// <summary>Where a tool's captured output goes.</summary>
    IOutputHost Output { get; }

    IServiceProvider Services { get; }

    /// <summary>Re-list the active pane, e.g. after a mutation the watcher will not report.</summary>
    Task RefreshAsync();

    /// <summary>
    /// Shows these rows of the active pane as on their way out until the handle is disposed: the
    /// moment a delete starts, before the disk has done anything.
    /// </summary>
    IDisposable MarkLeaving(IReadOnlyCollection<FsUri> items) => NoMarks.Instance;

    /// <param name="selectName">Row to focus once the listing arrives, if it is still there.</param>
    Task NavigateAsync(FsUri uri, string? selectName = null);

    /// <summary>
    /// Opens each location in a new tab of this pane; the first comes to the front and the rest wait
    /// behind it, the way a browser opens several links.
    /// </summary>
    Task OpenInNewTabsAsync(IReadOnlyList<FsUri> locations);
}

/// <summary>Nothing to undo, for a context with no rows to mark.</summary>
public sealed class NoMarks : IDisposable
{
    public static readonly NoMarks Instance = new();

    public void Dispose()
    {
    }
}

/// <summary>
/// One user-invocable action. Built-ins and entries from tools.json produce the same type, and
/// the F-key legend, context menu, keybindings and any later command palette are all renderers
/// over the registry rather than parallel implementations (§1.2/1).
/// </summary>
public sealed class CommandDefinition
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>Compressed label for the F-key legend. Falls back to <see cref="Title"/>.</summary>
    public string? LegendTitle { get; init; }

    /// <summary>
    /// False keeps a bound command off the legend, whose one row is the scarce thing: keys everyone
    /// already has in their fingers (Ctrl+C), or keys the screen states where they apply (Ctrl+H in
    /// the footer's hidden count, Ctrl+R beside a pane that is not live). Still bound, and still in
    /// the palette.
    /// </summary>
    public bool InLegend { get; init; } = true;

    public string? Group { get; init; }

    /// <summary>Where a <see cref="CommandSubject.Selection"/> command may be offered. Ignored for the rest.</summary>
    public CommandScope Scope { get; init; } = CommandScope.Any;

    public CommandSubject Subject { get; init; } = CommandSubject.Selection;

    /// <summary>
    /// The key in force: the one the command is declared with, unless settings.json gives it
    /// another or none, or a key chosen there for another command took it (<see
    /// cref="CommandRegistry.ApplyKeys"/>). Declaring it sets the default.
    /// </summary>
    public KeyStroke? Binding
    {
        get => _rebound ? _key : DefaultBinding;
        init => DefaultBinding = value;
    }

    /// <summary>The key the command comes with, whatever is chosen over it.</summary>
    public KeyStroke? DefaultBinding { get; private init; }

    private bool _rebound;
    private KeyStroke? _key;

    /// <summary>The registry's to call: a key other than the default, null for none.</summary>
    internal void Rebind(KeyStroke? key)
    {
        _rebound = true;
        _key = key;
    }

    internal void ResetBinding() => _rebound = false;

    public Func<ICommandContext, bool>? CanExecute { get; init; }

    public required Func<ICommandContext, CancellationToken, Task> ExecuteAsync { get; init; }

    public string Legend => LegendTitle ?? Title;

    /// <summary>
    /// What the provider must be able to do for this command to make sense at all: Rename for
    /// rename, Delete for delete and move. Asked of the provider of the entries it acts on — or of
    /// the location, for a command about the location — so a read-only place (an archive, a script
    /// location) does not offer to change what it cannot.
    /// </summary>
    public ProviderCapabilities Needs { get; init; }

    /// <summary>
    /// Puts something into the location: a new folder, a paste, a zip. A flat view or a search
    /// writes into the folder it is a view of (<see cref="FsUri.Anchor"/>); a <c>from:</c> listing
    /// has no such folder — its rows come from a command or a script — so there it does not apply.
    /// </summary>
    public bool WritesHere { get; init; }

    /// <summary>
    /// The command reads what the rows hold, or walks below the location: copy, preview, flatten,
    /// search. Not offered on pages whose rows are places rather than things — F5 on "C:" on the
    /// computer page would copy the drive.
    /// </summary>
    public bool ReadsContents { get; init; }

    /// <summary>
    /// The command deletes the selected entries themselves: Delete, Delete permanently. Not offered
    /// in a collection, where Del takes a member out of the list and the file itself is deleted
    /// where it lives (§6.19).
    /// </summary>
    public bool DeletesEntries { get; init; }

    /// <summary>
    /// Where the command exists at all, for a command that belongs to one kind of place: Remove
    /// from collection. Such a command may share a key with a general one — Del — and takes it only
    /// where this holds (<see cref="CommandRegistry.ForKey"/>). Null for commands that exist everywhere.
    /// </summary>
    public Func<FsUri, bool>? OnlyAt { get; init; }

    /// <summary>
    /// Whether the command belongs here at all, whatever is selected: what the legend shows, and the
    /// first thing <see cref="IsApplicable"/> asks.
    /// </summary>
    public bool AppliesAt(ICommandContext context)
    {
        if (OnlyAt is not null && !OnlyAt(context.Directory))
            return false;
        if (WritesHere && context.Directory.Scheme == FsUri.FromScheme)
            return false;

        // The application's own pages (§6.19) are asked what they are, not what their provider can
        // do. Nothing is written *into* one. Where the rows are places — the computer page, the list
        // of collections — nothing reads or changes a row: a row there is Documents itself. Where
        // the rows are files — a collection — a row is that file and can be renamed, copied or
        // previewed as anywhere, but Del edits the list, so deleting the file is not offered, and
        // the page is no folder to flatten or search.
        if (context.Directory.IsAbout)
        {
            if (WritesHere)
                return false;
            if (AboutPages.HoldsFiles(context.Directory))
            {
                if (DeletesEntries || (ReadsContents && Subject != CommandSubject.Selection))
                    return false;
            }
            else if (OnlyAt is null && (Needs != ProviderCapabilities.None || ReadsContents))
            {
                return false;
            }
        }

        if (Needs == ProviderCapabilities.None)
            return true;
        if (context.Services.GetService(typeof(IProviderRegistry)) is not IProviderRegistry providers)
            return true;

        var entry = (context.Focused ?? context.Selection.FirstOrDefault())?.Uri;

        // A collection's rows are files from anywhere, and the page itself can do nothing; with no
        // row to ask yet — the legend is drawn as the page opens — the rows decide when there is one.
        if (Subject == CommandSubject.Selection && entry is null && AboutPages.HoldsFiles(context.Directory))
            return true;

        var subject = Subject == CommandSubject.Selection ? entry ?? context.Directory : context.Directory.Anchor;

        return !providers.TryResolve(subject, out var provider) || (provider.Capabilities & Needs) == Needs;
    }

    public bool IsApplicable(ICommandContext context)
    {
        if (!AppliesAt(context))
            return false;

        // A command about the folder does not care what is selected in it.
        if (Subject != CommandSubject.Selection)
            return CanExecute?.Invoke(context) ?? true;

        var scope = context.Selection.Count switch
        {
            0 => CommandScope.Background,
            1 => context.Selection[0].IsDirectory ? CommandScope.Directory : CommandScope.File,
            _ => CommandScope.Multiple,
        };
        if ((Scope & scope) == 0)
            return false;
        return CanExecute?.Invoke(context) ?? true;
    }
}
