using System.Windows;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Config;

namespace DanceCommander.App.Services;

/// <summary>
/// Puts what settings.json says into effect, at startup and on every change, live: the keys chosen
/// over the commands' own, the row density and the text size, and where OneDrive's state is drawn.
/// The colour scheme has its own
/// (<see cref="ColourScheme"/>), and how a new tab sorts is read where tabs are made.
/// </summary>
/// <remarks>
/// Density and text size work by overriding tokens at the application's top level: everything draws
/// its sizes from DynamicResources, so the new values reach every window without a rebuild, and the
/// tokens in Tokens.xaml stay what the design is measured from.
/// </remarks>
public sealed class AppliedSettings
{
    private static readonly string[] TypeSizes = ["FontSizeSmall", "FontSizeBody", "FontSizeTitle"];

    private readonly IConfigStore<SettingsFile> _settings;
    private readonly ICommandRegistry _commands;
    private readonly Dictionary<string, double> _tokens = [];

    public AppliedSettings(IConfigStore<SettingsFile> settings, ICommandRegistry commands)
    {
        _settings = settings;
        _commands = commands;
        settings.Changed += (_, _) => Application.Current?.Dispatcher.InvokeAsync(Apply);
    }

    /// <summary>Keys in settings.json that could not be applied, by command id, in words.</summary>
    public IReadOnlyDictionary<string, string> KeyProblems { get; private set; } = new Dictionary<string, string>();

    /// <summary>After each application of the settings, on the UI thread.</summary>
    public event EventHandler? Applied;

    public void Apply()
    {
        var file = _settings.Current;
        ApplySizes(file);

        // What a cloud glyph is when nothing says otherwise. The cursor row shows its own whatever
        // this is (the trigger in the Name column's template), so Collapsed means "cursor only".
        Application.Current.Resources["CloudStatusVisibility"] =
            SettingsCatalog.CloudStatusOnEveryRow(file) ? Visibility.Visible : Visibility.Collapsed;
        KeyProblems = _commands.ApplyKeys(file.Keys);
        Applied?.Invoke(this, EventArgs.Empty);
    }

    private void ApplySizes(SettingsFile file)
    {
        // Every design value read before the first is overridden: the override has the same name,
        // and compact on the first run would otherwise leave 18 as what "comfortable" reads back.
        foreach (var key in TypeSizes.Concat(["RowHeight", "RowHeightCompact", "HeaderHeight"]))
            Token(key);

        var resources = Application.Current.Resources;
        var scale = SettingsCatalog.TextScale(file);

        foreach (var key in TypeSizes)
            resources[key] = Math.Round(Token(key) * scale, 1);

        // Rows grow with their text, or larger text would crowd them; whole pixels, so a row edge
        // never lands between two.
        var row = Token(SettingsCatalog.IsCompact(file) ? "RowHeightCompact" : "RowHeight");
        resources["RowHeight"] = Math.Round(row * scale);
        resources["HeaderHeight"] = Math.Round(Token("HeaderHeight") * scale);
    }

    /// <summary>The design's own value, read once before anything overrides it.</summary>
    private double Token(string key)
    {
        if (!_tokens.TryGetValue(key, out var value))
            _tokens[key] = value = (double)Application.Current.FindResource(key);
        return value;
    }
}
