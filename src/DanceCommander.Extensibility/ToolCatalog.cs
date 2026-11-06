using DanceCommander.Core.Commands;
using DanceCommander.Core.Config;
using DanceCommander.Core.Listing;
using DanceCommander.Core.Matching;
using Microsoft.Extensions.Logging;

namespace DanceCommander.Extensibility;

/// <summary>
/// Turns tools.json into commands. They enter the same registry as the built-ins (§1.2/1), so the
/// context menu, keybindings and legend treat a user's tool and a built-in identically — and a
/// tool can claim a key a built-in was using, because later registration wins.
/// </summary>
public sealed class ToolCatalog
{
    private readonly IConfigStore<ToolsFile> _store;
    private readonly ICommandRegistry _registry;
    private readonly IToolRunner _runner;
    private readonly ICommandListingCatalog _listings;
    private readonly ILogger<ToolCatalog> _log;
    private readonly List<string> _registered = [];
    private readonly HashSet<string> _fromConfig = new(StringComparer.OrdinalIgnoreCase);
    private readonly PackDetector? _packs;
    private readonly IConfigStore<SettingsFile>? _settings;
    private readonly object _reloading = new();
    private string _packsOn = string.Empty;
    private IReadOnlyList<CommandDefinition> _editors = [];

    public ToolCatalog(
        IConfigStore<ToolsFile> store,
        ICommandRegistry registry,
        IToolRunner runner,
        ICommandListingCatalog listings,
        ILogger<ToolCatalog> log,
        PackDetector? packs = null,
        IConfigStore<SettingsFile>? settings = null)
    {
        _store = store;
        _registry = registry;
        _runner = runner;
        _listings = listings;
        _log = log;
        _packs = packs;
        _settings = settings;
        _store.Changed += (_, _) => Reload();
        if (_packs is not null)
            _packs.Changed += (_, _) => Reload();

        // Only when a pack was switched: every other setting would rebuild the tools for nothing.
        if (_settings is not null)
            _settings.Changed += (_, _) =>
            {
                if (PacksOn() != _packsOn)
                    Reload();
            };
    }

    /// <summary>The packs in force, as one string to tell whether that changed.</summary>
    private string PacksOn() =>
        _packs is null || _settings is null
            ? string.Empty
            : string.Join(",", ToolPacks.All.Where(p => p.IsOn(_settings.Current)).Select(p => p.Id));

    /// <summary>Raised after a reload, so the UI can rebuild anything that lists commands.</summary>
    public event EventHandler? Changed;

    /// <summary>The tools marked <c>edits</c>, in file order: what F4 asks first.</summary>
    public IReadOnlyList<CommandDefinition> Editors => _editors;

    /// <summary>
    /// An editor is handed the file as an argument. One whose exec is the file itself would run it,
    /// and F4 is the key people press on a script precisely so that it does not run; one that is
    /// never given the file would open something other than what F4 was pressed on.
    /// </summary>
    private bool IsEditor(ToolDefinition tool)
    {
        if (ToolPlaceholders.ReadsEntries([tool.Exec]))
        {
            _log.LogError("Tool {Id} is marked edits, but its exec is the file itself, which would run it", tool.Id);
            return false;
        }

        if (!ToolPlaceholders.ReadsEntries(tool.Args))
        {
            _log.LogError("Tool {Id} is marked edits, but its args never name the file", tool.Id);
            return false;
        }

        return true;
    }

    public void Reload()
    {
        // From the file watchers and the pack detector, each on a thread of its own: two reloads at
        // once would each take out the other's commands, or register one twice.
        lock (_reloading)
        {
            foreach (var id in _registered)
                _registry.Remove(id);
            _registered.Clear();
            var editors = new List<CommandDefinition>();

            // Config-defined listings are replaced wholesale on reload; built-ins are registered by
            // the shell and are not in this file, so they are left alone.
            _listings.RemoveWhere(l => _fromConfig.Contains(l.Id));
            _fromConfig.Clear();

            foreach (var listing in _store.Current.Listings.Where(l => l.Enabled))
            {
                if (string.IsNullOrWhiteSpace(listing.Id) || string.IsNullOrWhiteSpace(listing.Exec))
                {
                    _log.LogWarning("Skipping a listing with no id or exec");
                    continue;
                }

                try
                {
                    // Compiled here so a bad pattern is a log line and a skipped entry rather than an
                    // exception thrown at whoever eventually navigates to it.
                    _ = new System.Text.RegularExpressions.Regex(listing.Extract);
                }
                catch (ArgumentException ex)
                {
                    _log.LogError(ex, "Listing {Id} has an extract pattern that will not compile", listing.Id);
                    continue;
                }

                _listings.Register(new CommandListing(
                    listing.Id,
                    string.IsNullOrWhiteSpace(listing.Title) ? listing.Id : listing.Title,
                    listing.Exec,
                    listing.Args,
                    // A script location's default is its own row format, not "one path per line".
                    listing.Script && listing.Extract == new ListingDefinition().Extract
                        ? ScriptProvider.DefaultExtract
                        : listing.Extract,
                    listing.Columns.Select(c => new CommandListingColumn(c.Group, c.Header, c.Width, c.Flexible)).ToList(),
                    listing.InRepository,
                    listing.ShowModified,
                    listing.Script,
                    listing.Secrets,
                    listing.Sort));

                _fromConfig.Add(listing.Id);

                // The command is what puts it in the menus and the palette; without one it could only
                // be typed. One already there is a built-in of the same id, which goes to the same
                // address and so serves this one as well.
                var command = CommandListingCommands.For(_listings.Find(listing.Id)!);
                if (_registry.Find(command.Id) is null)
                {
                    _registry.Register(command);
                    _registered.Add(command.Id);
                }
            }

            // Packs first, so that a key a tool of yours has wins over a pack's. A pack's tool whose id
            // tools.json has, on or off, is left out: that entry is its replacement.
            var yours = _store.Current.Tools.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tools = PackTools().Where(t => !yours.Contains(t.Id))
                .Concat(_store.Current.Tools.Where(t => t.Enabled));

            foreach (var tool in tools)
            {
                if (string.IsNullOrWhiteSpace(tool.Id) || string.IsNullOrWhiteSpace(tool.Exec))
                {
                    _log.LogWarning("Skipping a tool with no id or exec");
                    continue;
                }

                var id = "tool." + tool.Id;
                if (_registry.Find(id) is not null)
                {
                    _log.LogWarning("Skipping duplicate tool {Id}", tool.Id);
                    continue;
                }

                if (ToolPlaceholders.UnknownFilter(tool.Templates()) is { } filter)
                {
                    _log.LogError("Tool {Id} uses a placeholder filter that does not exist: {Filter}", tool.Id, filter);
                    continue;
                }

                try
                {
                    var command = Build(id, tool);
                    _registry.Register(command);
                    _registered.Add(id);
                    if (tool.Edits && IsEditor(tool))
                        editors.Add(command);
                }
                catch (Exception ex)
                {
                    // One broken tool must not take the rest of the file down with it.
                    _log.LogError(ex, "Tool {Id} could not be registered", tool.Id);
                }
            }

            // Swapped whole: a reload comes from the file watcher, and F4 may be reading the old list.
            _editors = editors;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The tools of every pack found on this computer and not switched off.</summary>
    private IEnumerable<ToolDefinition> PackTools()
    {
        _packsOn = PacksOn();
        if (_packs is null || _settings is null)
            return [];

        var found = _packs.Found;
        var settings = _settings.Current;
        return ToolPacks.All
            .Where(p => p.IsOn(settings) && found.ContainsKey(p.Id))
            .SelectMany(p => p.Tools(found[p.Id]));
    }

    private CommandDefinition Build(string id, ToolDefinition tool)
    {
        var matcher = CompiledMatcher.Compile(tool.When);
        var scope = tool.ParsedScope();
        var schemes = tool.ParsedSchemes();
        var templates = tool.Templates().ToList();
        var subject = tool.ParsedSubject();
        var remote = string.IsNullOrWhiteSpace(tool.RemoteMatches) ? null : TextPattern.Compile(tool.RemoteMatches);
        var readsOtherPane = ToolPlaceholders.ReadsOtherPane(templates);

        return new CommandDefinition
        {
            Id = id,
            Title = tool.Title,
            Group = tool.Group ?? "Tools",
            Subject = subject,
            // "background" means the folder, which the subject now says; for a tool about the rows
            // it would only ever have meant running with nothing to run on.
            Scope = ToCommandScope(scope & ~ToolScope.Background) is var rows and not CommandScope.None
                ? rows
                : CommandScope.File | CommandScope.Directory | CommandScope.Multiple,
            Binding = string.IsNullOrWhiteSpace(tool.Keybinding) ? null : KeyStroke.Parse(tool.Keybinding),
            CanExecute = context =>
            {
                // A tool that only understands local paths must not be offered on an SFTP listing,
                // where ${path} would mean nothing to the program being launched.
                var uri = subject == CommandSubject.Selection && context.Selection.Count > 0
                    ? context.Selection[0].Uri
                    : context.Directory;
                if (!schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
                    return false;

                if ((tool.InRepository || tool.InConflict) && !context.InRepository)
                    return false;

                if (tool.InConflict && !(subject == CommandSubject.Selection
                        ? context.Selection.Count > 0 && context.Selection.All(e => context.Repository!.IsConflicted(e.Uri))
                        : context.Repository!.HasConflicts))
                    return false;

                if (tool.MaxSelected is { } most && subject == CommandSubject.Selection && context.Selection.Count > most)
                    return false;

                // The same question as the scheme check above, asked of the other pane.
                if (readsOtherPane && (context.OtherPaneDirectory is not { } other ||
                        !schemes.Contains(other.Scheme, StringComparer.OrdinalIgnoreCase)))
                    return false;

                if (remote is not null && !remote.Matches(context.Repository?.RemoteUrl))
                    return false;

                // ${branch} outside a repository, or with HEAD detached, has no value; the tool
                // is absent rather than run with an empty argument (§6.6).
                if (!ToolPlaceholders.CanResolve(templates, context.Repository))
                    return false;

                if (tool.When is null || subject != CommandSubject.Selection)
                    return true;

                return context.Selection.Count > 0
                    ? context.Selection.All(matcher.Matches)
                    : true;
            },
            ExecuteAsync = (context, ct) => _runner.RunAsync(tool, context, ct),
        };
    }

    private static CommandScope ToCommandScope(ToolScope scope)
    {
        var result = CommandScope.None;
        if (scope.HasFlag(ToolScope.Background)) result |= CommandScope.Background;
        if (scope.HasFlag(ToolScope.File)) result |= CommandScope.File;
        if (scope.HasFlag(ToolScope.Directory)) result |= CommandScope.Directory;
        if (scope.HasFlag(ToolScope.Multi)) result |= CommandScope.Multiple;
        return result;
    }

    /// <summary>
    /// The file people will read first, written only when there is none. The tools for what is
    /// installed come from the packs and are not copied in (SETTINGS_PLAN.md §4); what is here is
    /// an example of the one kind no pack can know: a job that is this project's business.
    /// </summary>
    public static ToolsFile Defaults() => new()
    {
        Tools =
        [
            // A background job (§6.17): streamed into the pane, stoppable from the strip, and
            // announced when it lands, with the output folder one click away. Shipped disabled,
            // because what "build" means is the project's business: enable it and replace "make"
            // with whatever yours runs.
            new ToolDefinition
            {
                Id = "devcontainer-build",
                Title = "Build in devcontainer",
                Group = "Build",
                Exec = "devcontainer",
                Args = ["exec", "--workspace-folder", "${repoRoot}", "make"],
                WorkingDirectory = "${repoRoot}",
                OutputDirectory = "${repoRoot}/build",
                Launch = "job",
                Enabled = false,
            },
        ],
    };
}
