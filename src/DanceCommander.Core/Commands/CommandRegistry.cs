using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Commands;

public interface ICommandRegistry
{
    IReadOnlyList<CommandDefinition> All { get; }

    void Register(CommandDefinition command);

    /// <summary>Removes a command and its binding. Config-defined tools are replaced this way on reload.</summary>
    bool Remove(string id);

    CommandDefinition? Find(string id);

    /// <summary>
    /// The command a key runs. At a location, a command that belongs there (<see
    /// cref="CommandDefinition.OnlyAt"/>) takes its key from the general one: Del in a collection.
    /// </summary>
    CommandDefinition? ForKey(KeyStroke stroke, FsUri? at = null);

    /// <summary>
    /// Commands bound to F1..F{count} with exactly these modifiers held, indexed by key number
    /// minus one. Nulls are blank slots. This is what the legend bar renders, and why holding
    /// Shift re-labels it rather than showing a fixed row.
    /// </summary>
    IReadOnlyList<CommandDefinition?> Legend(KeyModifiers modifiers, int count = 12, FsUri? at = null);

    /// <summary>
    /// Every command bound with exactly these modifiers, function keys first and then the rest in
    /// key order. Holding a modifier turns the legend into a view of that whole layer of the
    /// keymap, not just its function keys.
    /// </summary>
    IReadOnlyList<(KeyStroke Stroke, CommandDefinition Command)> BoundWith(KeyModifiers modifiers, FsUri? at = null);

    IEnumerable<CommandDefinition> ApplicableTo(ICommandContext context);

    /// <summary>Bindings that were taken over by a later registration, oldest first.</summary>
    IReadOnlyList<(KeyStroke Stroke, string Lost, string Won)> Displaced { get; }

    /// <summary>
    /// Keys chosen in settings.json, by command id, over the commands' own; null takes a key away.
    /// A chosen key wins over every declared one, and the command that had it is left without
    /// (SETTINGS_PLAN.md §7: conflicts are shown, not refused). Kept for commands registered later,
    /// so a tool that reloads gets its chosen key again.
    /// </summary>
    /// <returns>Keys that could not be read, in words, by command id.</returns>
    IReadOnlyDictionary<string, string> ApplyKeys(IReadOnlyDictionary<string, string?> keys);

    /// <summary>Commands whose key was taken by a key chosen for another: the key, who lost it, who has it.</summary>
    IReadOnlyList<(KeyStroke Stroke, string Lost, string Won)> Taken { get; }

    /// <summary>After <see cref="ApplyKeys"/>. Registering and removing do not raise it.</summary>
    event EventHandler? BindingsChanged;
}

public sealed class CommandRegistry : ICommandRegistry
{
    private readonly List<CommandDefinition> _commands = [];
    private readonly Dictionary<string, CommandDefinition> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<KeyStroke, CommandDefinition> _byKey = [];
    private readonly List<(KeyStroke Stroke, string Lost, string Won)> _displaced = [];
    private readonly List<(KeyStroke Stroke, string Lost, string Won)> _taken = [];

    /// <summary>Bound commands that exist only at some locations; they overlay <see cref="_byKey"/> there.</summary>
    private readonly List<CommandDefinition> _placed = [];

    /// <summary>Keys chosen in settings.json, parsed; null is "no key".</summary>
    private Dictionary<string, KeyStroke?> _chosen = new(StringComparer.Ordinal);

    public IReadOnlyList<CommandDefinition> All => _commands;

    public event EventHandler? BindingsChanged;

    public void Register(CommandDefinition command)
    {
        if (!_byId.TryAdd(command.Id, command))
            throw new InvalidOperationException($"A command with id '{command.Id}' is already registered.");
        _commands.Add(command);

        // From the start rather than added on: a command arriving with a key someone chose for
        // another must not take it back, and one arriving with a chosen key must take it from
        // whoever has it. A couple of hundred commands, so well under a millisecond.
        Rebuild();
    }

    private void Index(CommandDefinition command)
    {
        if (command.Binding is not null && command.OnlyAt is not null)
        {
            // Sharing a key with a general command is the point of a placed one, so it displaces
            // nothing: the general command keeps the key everywhere else.
            _placed.Add(command);
        }
        else if (command.Binding is { } stroke)
        {
            // Last registration wins, so a tool from config can deliberately take a built-in's key.
            // It is recorded because the other way this happens is two built-ins colliding by
            // accident, which is silent, survives the tests, and is only ever found by pressing
            // the key and getting the wrong command.
            if (_byKey.TryGetValue(stroke, out var displaced))
                _displaced.Add((stroke, displaced.Id, command.Id));

            _byKey[stroke] = command;
        }
    }

    public bool Remove(string id)
    {
        if (!_byId.Remove(id, out var command))
            return false;

        _commands.Remove(command);

        // A command that had lost its key to this one gets it back.
        Rebuild();
        return true;
    }

    public IReadOnlyDictionary<string, string> ApplyKeys(IReadOnlyDictionary<string, string?> keys)
    {
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        var chosen = new Dictionary<string, KeyStroke?>(StringComparer.Ordinal);
        foreach (var (id, text) in keys)
        {
            if (string.IsNullOrWhiteSpace(text))
                chosen[id] = null;
            else if (KeyStroke.TryParseTyped(text, out var stroke))
                chosen[id] = stroke;
            else
                problems[id] = $"\"{text}\" is not a key this application can tell apart.";

            // An id with no command yet is kept, not reported: a tool that is off, or one whose
            // tools.json has not loaded, gets its key when it arrives. Whoever shows the keys can
            // say which ids match nothing at the time they are looked at.
        }

        _chosen = chosen;
        Rebuild();

        // Only here: registering and removing happen in batches (a tools.json reload), whose owner
        // says once when it is done.
        BindingsChanged?.Invoke(this, EventArgs.Empty);
        return problems;
    }

    /// <summary>
    /// Every binding from the start: the declared keys in registration order, as they would have
    /// been indexed, then the chosen ones over them.
    /// </summary>
    private void Rebuild()
    {
        _byKey.Clear();
        _placed.Clear();
        _displaced.Clear();
        _taken.Clear();

        foreach (var command in _commands)
        {
            command.ResetBinding();
            if (!_chosen.ContainsKey(command.Id))
                Index(command);
        }

        foreach (var command in _commands)
        {
            if (!_chosen.TryGetValue(command.Id, out var key))
                continue;
            command.Rebind(key);
            if (key is not { } stroke)
                continue;

            if (command.OnlyAt is null && _byKey.TryGetValue(stroke, out var holder))
            {
                // Its key is gone, so it shows none rather than one that runs something else.
                holder.Rebind(null);
                _taken.Add((stroke, holder.Id, command.Id));
            }

            if (command.OnlyAt is not null)
                _placed.Add(command);
            else
                _byKey[stroke] = command;
        }
    }

    public IReadOnlyList<(KeyStroke Stroke, string Lost, string Won)> Taken => _taken;

    public CommandDefinition? Find(string id) => _byId.GetValueOrDefault(id);

    public CommandDefinition? ForKey(KeyStroke stroke, FsUri? at = null) =>
        Placed(at).LastOrDefault(c => c.Binding == stroke) ?? _byKey.GetValueOrDefault(stroke);

    public IReadOnlyList<CommandDefinition?> Legend(KeyModifiers modifiers, int count = 12, FsUri? at = null)
    {
        var slots = new CommandDefinition?[count];
        foreach (var (stroke, command) in Bindings(at))
        {
            if (stroke.Modifiers != modifiers)
                continue;
            if (stroke.FunctionKeyNumber is { } n && n <= count)
                slots[n - 1] = command;
        }
        return slots;
    }

    public IReadOnlyList<(KeyStroke Stroke, CommandDefinition Command)> BoundWith(KeyModifiers modifiers, FsUri? at = null) =>
        Bindings(at)
            .Where(pair => pair.Key.Modifiers == modifiers)
            .OrderBy(pair => pair.Key.FunctionKeyNumber is null) // function keys lead
            .ThenBy(pair => pair.Key.FunctionKeyNumber ?? 0)
            .ThenBy(pair => pair.Key.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => (pair.Key, pair.Value))
            .ToList();

    private IEnumerable<CommandDefinition> Placed(FsUri? at) =>
        at is null ? [] : _placed.Where(c => c.OnlyAt!(at));

    /// <summary>Every binding in force at a location: the general ones, with the placed ones over them.</summary>
    private Dictionary<KeyStroke, CommandDefinition> Bindings(FsUri? at)
    {
        var bindings = new Dictionary<KeyStroke, CommandDefinition>(_byKey);
        foreach (var command in Placed(at))
            bindings[command.Binding!.Value] = command;
        return bindings;
    }

    public IEnumerable<CommandDefinition> ApplicableTo(ICommandContext context) =>
        _commands.Where(c => c.IsApplicable(context));

    public IReadOnlyList<(KeyStroke Stroke, string Lost, string Won)> Displaced => _displaced;
}
