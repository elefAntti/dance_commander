using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using DanceCommander.App.Commands;
using DanceCommander.App.Services;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Config;
using DanceCommander.Extensibility;

namespace DanceCommander.App.ViewModels;

/// <summary>
/// The settings window (SETTINGS_PLAN.md §2): a renderer over the setting catalog and the command
/// registry, as the palette is. Nothing is held back for an Apply: a choice is written to
/// settings.json at once, and what the window shows is read back from the file, so a hand edit made
/// while it is open shows up here the same way.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    public const string General = "General";
    public const string Appearance = "Appearance";
    public const string Keyboard = "Keyboard";
    public const string Tools = "Tools";

    private readonly IConfigStore<SettingsFile> _store;
    private readonly SettingsWriter _writer;
    private readonly ICommandRegistry _commands;
    private readonly AppliedSettings _applied;
    private readonly PackDetector? _packs;
    private readonly IConfigStore<ToolsFile>? _tools;

    public SettingsViewModel(
        IConfigStore<SettingsFile> store, SettingsWriter writer, ICommandRegistry commands, AppliedSettings applied,
        PackDetector? packs = null, IConfigStore<ToolsFile>? tools = null)
    {
        _store = store;
        _writer = writer;
        _commands = commands;
        _applied = applied;
        _packs = packs;
        _tools = tools;

        Settings = SettingsCatalog.All.Concat(packs?.Settings ?? [])
            .Select(s => new SettingRowViewModel(s)).ToList();
        _applied.Applied += OnApplied;

        // What is found, and what tools.json replaces, change outside settings.json.
        if (_packs is not null)
            _packs.Changed += OnElsewhere;
        if (_tools is not null)
            _tools.Changed += OnElsewhere;

        // A mistake made by hand while the window is open: no reload follows it, so no Applied.
        if (store is IConfigFile file)
            file.ProblemFound += OnProblemFound;
        Refresh();
    }

    public IReadOnlyList<string> Sections { get; } = [General, Appearance, Tools, Keyboard];

    public bool ShowsTools => !IsSearching && Section == Tools;

    [ObservableProperty]
    private string _section = General;

    [ObservableProperty]
    private string _search = string.Empty;

    /// <summary>On the Keyboard page: every command, only the ones given a key of your own, or only clashes.</summary>
    [ObservableProperty]
    private KeyFilter _keyFilter = KeyFilter.All;

    /// <summary>What went wrong with the last change, in words.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    private string? _writeProblem;

    /// <summary>What is wrong with settings.json as it is on disk.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    private string? _fileProblem;

    /// <summary>What to say at the foot of the window; null when all is well.</summary>
    public string? Problem => WriteProblem ?? FileProblem;

    /// <summary>The command whose new key is being waited for, if any.</summary>
    [ObservableProperty]
    private KeyRowViewModel? _recording;

    public IReadOnlyList<SettingRowViewModel> Settings { get; }

    public ObservableCollection<SettingRowViewModel> VisibleSettings { get; } = [];

    public ObservableCollection<KeyRowViewModel> VisibleKeys { get; } = [];

    private List<KeyRowViewModel> _keys = [];

    public bool IsSearching => Search.Trim().Length > 0;

    public bool ShowsKeys => IsSearching || Section == Keyboard;

    /// <summary>The heading over what is shown: the page, or that these are search results.</summary>
    public string Heading => IsSearching ? $"Settings and keys matching “{Search.Trim()}”" : Section;

    public bool NothingFound => IsSearching && VisibleSettings.Count == 0 && VisibleKeys.Count == 0;

    /// <summary>Keys chosen for ids that match no command, said once under the list.</summary>
    public string? OrphanKeys { get; private set; }

    partial void OnSectionChanged(string value)
    {
        Filter();

        // Opening the page is a moment to look again: someone checking whether a program they just
        // installed was noticed should not have to restart to find out.
        if (value == Tools && _packs is not null)
            _ = _packs.DetectAsync();
    }

    partial void OnSearchChanged(string value) => Filter();

    partial void OnKeyFilterChanged(KeyFilter value) => Filter();

    public void StepSection(int step)
    {
        var index = Sections.ToList().IndexOf(Section);
        Section = Sections[(index + step + Sections.Count) % Sections.Count];
    }

    public Task ChooseAsync(SettingRowViewModel row, string value) =>
        WriteAsync(() => SettingsCommands.SetAsync(_writer, row.Definition, value));

    public Task ResetAsync(SettingRowViewModel row) => ChooseAsync(row, row.Definition.Default);

    public void StartRecording(KeyRowViewModel row) => Recording = row;

    public void StopRecording() => Recording = null;

    /// <summary>The key pressed while recording becomes the command's.</summary>
    public Task RecordAsync(KeyStroke stroke)
    {
        if (Recording is not { } row)
            return Task.CompletedTask;
        Recording = null;

        // The command's own key back is not a choice to write down.
        return stroke == row.Command.DefaultBinding
            ? WriteAsync(() => _writer.ResetKeyAsync(row.Command.Id))
            : WriteAsync(() => _writer.SetKeyAsync(row.Command.Id, stroke.ToString()));
    }

    public Task RemoveKeyAsync(KeyRowViewModel row) =>
        row.Command.DefaultBinding is null
            ? WriteAsync(() => _writer.ResetKeyAsync(row.Command.Id))
            : WriteAsync(() => _writer.SetKeyAsync(row.Command.Id, null));

    public Task ResetKeyAsync(KeyRowViewModel row) => WriteAsync(() => _writer.ResetKeyAsync(row.Command.Id));

    private async Task WriteAsync(Func<Task> write)
    {
        try
        {
            await write();
            WriteProblem = null;
        }
        catch (InvalidOperationException ex)
        {
            WriteProblem = ex.Message;
        }
        catch (JsonException)
        {
            WriteProblem = "settings.json has a mistake in it, so nothing was written. Fix it and try again.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteProblem = $"settings.json could not be written ({ex.Message}). Nothing in it was changed; try again.";
        }
    }

    private void OnApplied(object? sender, EventArgs e) => Refresh();

    private void OnElsewhere(object? sender, object e) =>
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(Refresh);

    private void OnProblemFound(object? sender, ConfigProblem problem) =>
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => FileProblem = problem.Message);

    /// <summary>Everything read again from the file and the registry.</summary>
    private void Refresh()
    {
        var file = _store.Current;
        foreach (var row in Settings)
            row.Update(file, Replaced(row.Definition));

        var problems = _applied.KeyProblems;
        var takenFrom = _commands.Taken.ToDictionary(t => t.Lost, t => t, StringComparer.Ordinal);
        _keys = _commands.All
            // The palette's setting entries would be a third of the list, and each is a click away.
            .Where(c => !c.Id.StartsWith("dc.setting.", StringComparison.Ordinal))
            .OrderBy(c => c.Group ?? "~", StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .Select(c => new KeyRowViewModel(c, file.Keys.ContainsKey(c.Id), Note(c, takenFrom, problems)))
            .ToList();

        var orphans = file.Keys.Keys.Where(id => _commands.Find(id) is null).ToList();
        OrphanKeys = orphans.Count == 0
            ? null
            : $"settings.json chooses keys for {string.Join(", ", orphans)}, which no command has as its id now. "
              + "A tool that is off, or no longer in tools.json, would look like this.";
        OnPropertyChanged(nameof(OrphanKeys));

        FileProblem = (_store as IConfigFile)?.Problem?.Message;
        Filter();
    }

    /// <summary>What to say under a command's key: a clash, a key that could not be read, or a habit it breaks.</summary>
    private string? Note(
        CommandDefinition command,
        Dictionary<string, (KeyStroke Stroke, string Lost, string Won)> takenFrom,
        IReadOnlyDictionary<string, string> problems)
    {
        if (problems.TryGetValue(command.Id, out var problem))
            return problem;
        if (takenFrom.TryGetValue(command.Id, out var taken))
            return $"Its key {taken.Stroke} went to {TitleOf(taken.Won)}.";

        // Only for a key someone chose: the defaults were weighed when they were given.
        if (command.Binding is { } key && key != command.DefaultBinding
            && KeyConventions.ConflictFor(key, command.Id) is { } meaning)
        {
            return $"{key} is {meaning} in most other programs.";
        }

        var lost = _commands.Taken.FirstOrDefault(t => t.Won == command.Id);
        return lost.Lost is { } loser ? $"Took {lost.Stroke} from {TitleOf(loser)}, which has no key now." : null;
    }

    private string TitleOf(string id) => _commands.Find(id)?.Title ?? id;

    /// <summary>A pack's tools that tools.json has its own of, which are used instead.</summary>
    private string? Replaced(SettingDefinition setting)
    {
        if (_tools is null || setting.Path is not ["tools", "packs", var id]
            || ToolPacks.All.FirstOrDefault(p => p.Id == id) is not { } pack)
        {
            return null;
        }

        var yours = _tools.Current.Tools.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var replaced = pack.Sample.Where(t => yours.Contains(t.Id)).Select(t => t.Title).ToList();
        return replaced.Count == 0
            ? null
            : $"tools.json has its own {string.Join(", ", replaced)}, used instead, and this switch leaves "
              + (replaced.Count == 1 ? "it" : "them") + " alone.";
    }

    private void Filter()
    {
        var query = Search.Trim();
        var searching = query.Length > 0;

        VisibleSettings.Clear();
        foreach (var row in Settings)
        {
            var onPage = searching ? row.Matches(query) : row.Definition.Section.ToString() == Section;
            if (onPage)
                VisibleSettings.Add(row);
        }

        VisibleKeys.Clear();
        if (searching || Section == Keyboard)
        {
            foreach (var row in _keys)
            {
                if (searching && !row.Matches(query))
                    continue;
                if (KeyFilter == KeyFilter.Changed && !row.IsChanged)
                    continue;
                if (KeyFilter == KeyFilter.Notes && row.Note is null)
                    continue;
                VisibleKeys.Add(row);
            }
        }

        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(ShowsKeys));
        OnPropertyChanged(nameof(ShowsTools));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(NothingFound));
    }

    public void Dispose()
    {
        _applied.Applied -= OnApplied;
        if (_packs is not null)
            _packs.Changed -= OnElsewhere;
        if (_tools is not null)
            _tools.Changed -= OnElsewhere;
        if (_store is IConfigFile file)
            file.ProblemFound -= OnProblemFound;
    }
}

public enum KeyFilter
{
    All,
    Changed,
    Notes,
}

/// <summary>One setting: its choices, which is in force, and whether that is yours or the default.</summary>
public sealed partial class SettingRowViewModel : ObservableObject
{
    public SettingRowViewModel(SettingDefinition definition)
    {
        Definition = definition;
        Choices = definition.Choices.Select(c => new ChoiceViewModel(this, c.Value, c.Label)).ToList();
    }

    public SettingDefinition Definition { get; }

    public string Title => Definition.Title;

    public string Description => Definition.Description;

    /// <summary>Where it is in settings.json, so the window teaches the file.</summary>
    public string Key => Definition.Id;

    public IReadOnlyList<ChoiceViewModel> Choices { get; }

    [ObservableProperty]
    private bool _isChanged;

    /// <summary>A value typed into the file that is none of the choices, said rather than hidden.</summary>
    [ObservableProperty]
    private string? _other;

    /// <summary>What this computer has to say about it: a tool pack's program found or not.</summary>
    [ObservableProperty]
    private string? _status;

    /// <summary>Something that makes the choice matter less than it looks, said in amber.</summary>
    [ObservableProperty]
    private string? _caveat;

    public void Update(SettingsFile file, string? caveat = null)
    {
        Status = Definition.Status?.Invoke();
        Caveat = caveat;
        var value = Definition.Read(file);
        foreach (var choice in Choices)
            choice.IsChosen = choice.Value == value;
        IsChanged = value != Definition.Default;
        Other = Choices.Any(c => c.IsChosen) ? null : $"“{value}” in settings.json";
    }

    public bool Matches(string query) =>
        Title.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Description.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Key.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Choices.Any(c => c.Label.Contains(query, StringComparison.OrdinalIgnoreCase));
}

public sealed partial class ChoiceViewModel(SettingRowViewModel setting, string value, string label) : ObservableObject
{
    public SettingRowViewModel Setting { get; } = setting;

    public string Value { get; } = value;

    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isChosen;
}

/// <summary>One command on the Keyboard page: its key, the one it comes with, and anything to know about it.</summary>
public sealed class KeyRowViewModel(CommandDefinition command, bool isChanged, string? note)
{
    public CommandDefinition Command { get; } = command;

    public string Title => Command.Title;

    public string Group => Command.Group ?? "Keys";

    public string Id => Command.Id;

    public string Key => Command.Binding?.ToString() ?? "No key";

    public bool HasKey => Command.Binding is not null;

    /// <summary>Chosen in settings.json, rather than the command's own.</summary>
    public bool IsChanged { get; } = isChanged;

    public string? Default => IsChanged
        ? Command.DefaultBinding is { } key ? $"was {key}" : "had no key"
        : null;

    public string? Note { get; } = note;

    public bool Matches(string query) =>
        Title.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Group.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Id.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (HasKey && Key.Contains(query, StringComparison.OrdinalIgnoreCase));
}
