using System.Globalization;
using System.Text.Json.Nodes;

namespace DanceCommander.Core.Config;

/// <summary>Which page of the settings window a setting is on.</summary>
public enum SettingSection
{
    General,
    Appearance,
    Tools,
}

/// <summary>One value a setting can take, as the file spells it and as a person reads it.</summary>
public sealed record SettingChoice(string Value, string Label);

/// <summary>
/// One entry of <c>settings.json</c>, described once (SETTINGS_PLAN.md §3): the settings window
/// and the palette are both renderers over these, as the legend and the menus are over the
/// command registry. Every setting here is a choice among a few values; a toggle is a choice of
/// two. Values are compared as the file's own text, so a hand-edited value outside the choices
/// still reads back, and the window says it is one of yours rather than pretending it is a default.
/// </summary>
public sealed class SettingDefinition
{
    /// <summary>Where it sits in the file: <c>["appearance", "density"]</c>.</summary>
    public required IReadOnlyList<string> Path { get; init; }

    public string Id => JsoncEdit.Describe(Path);

    public required string Title { get; init; }

    /// <summary>One line on what it changes, for the window and its search.</summary>
    public required string Description { get; init; }

    public required SettingSection Section { get; init; }

    public required IReadOnlyList<SettingChoice> Choices { get; init; }

    public required string Default { get; init; }

    /// <summary>The value in force, in the same text as <see cref="SettingChoice.Value"/>.</summary>
    public required Func<SettingsFile, string> Read { get; init; }

    /// <summary>How a value is written: a string unless said otherwise.</summary>
    public Func<string, JsonNode?> ToJson { get; init; } = value => JsonValue.Create(value);

    /// <summary>
    /// A fact about this computer that bears on the setting, asked each time the window shows it:
    /// "Not found on this computer" for a tool pack. Null when there is nothing to say.
    /// </summary>
    public Func<string?>? Status { get; init; }

    public string LabelOf(string value) =>
        Choices.FirstOrDefault(c => c.Value == value)?.Label ?? value;
}

/// <summary>Every setting there is, in the order the window shows them.</summary>
/// <remarks>
/// A setting is here because people genuinely differ about it (SETTINGS_PLAN.md §9), not because a
/// choice happens to exist. Adding one: a property on <see cref="SettingsFile"/>, an entry here, and
/// whatever applies it listening to the store's <c>Changed</c>.
/// </remarks>
public static class SettingsCatalog
{
    public static SettingDefinition Scheme { get; } = new()
    {
        Path = ["appearance", "scheme"],
        Title = "Colour scheme",
        Description = "Dark or light. System follows Windows' app mode, and changes when it does.",
        Section = SettingSection.Appearance,
        Choices = [new("system", "System"), new("dark", "Dark"), new("light", "Light")],
        Default = "system",
        Read = file => Text(file.Appearance.Scheme),
    };

    public static SettingDefinition Accent { get; } = new()
    {
        Path = ["appearance", "accent"],
        Title = "Accent",
        Description = "The one colour the application has: the cursor, the active pane, a selection. Windows follows your Windows accent; a hex colour such as #C2410C can be written in the file. Always adjusted to stay legible.",
        Section = SettingSection.Appearance,
        Choices = [new(AccentColour.FromWindows, "Windows"), .. AccentColour.Named.Select(n => new SettingChoice(n.Name, n.Label))],
        Default = AccentColour.FromWindows,
        Read = file => Text(file.Appearance.Accent),
    };

    public static SettingDefinition Density { get; } = new()
    {
        Path = ["appearance", "density"],
        Title = "Rows",
        Description = "Comfortable rows are easier to read and to hit; compact ones show more of a long folder.",
        Section = SettingSection.Appearance,
        Choices = [new("comfortable", "Comfortable"), new("compact", "Compact")],
        Default = "comfortable",
        Read = file => Text(file.Appearance.Density),
    };

    public static SettingDefinition TextSize { get; } = new()
    {
        Path = ["appearance", "textSize"],
        Title = "Text size",
        Description = "All text in the application, and the rows with it.",
        Section = SettingSection.Appearance,
        Choices = [new("0.9", "90%"), new("1", "100%"), new("1.1", "110%"), new("1.25", "125%")],
        Default = "1",
        Read = file => Number(file.Appearance.TextSize),
        ToJson = value => JsonValue.Create(double.Parse(value, CultureInfo.InvariantCulture)),
    };

    public static SettingDefinition DirectoriesFirst { get; } = new()
    {
        Path = ["view", "directoriesFirst"],
        Title = "Folders before files",
        Description = "How a new tab sorts. A folder sorted another way on purpose stays that way.",
        Section = SettingSection.General,
        Choices = [new("false", "Off"), new("true", "On")],
        Default = "false",
        Read = file => file.View.DirectoriesFirst ? "true" : "false",
        ToJson = value => JsonValue.Create(value == "true"),
    };

    public static SettingDefinition CloudStatus { get; } = new()
    {
        Path = ["view", "cloudStatus"],
        Title = "OneDrive status",
        Description = "The cloud on a file that is only online, and the pin on one always kept here: on the cursor row, or on every row as Explorer has them.",
        Section = SettingSection.Appearance,
        Choices = [new("cursor", "Cursor row"), new("all", "Every row")],
        Default = "cursor",
        Read = file => Text(file.View.CloudStatus),
    };

    public static SettingDefinition Shell { get; } = new()
    {
        Path = ["commandLine", "shell"],
        Title = "Command line shell",
        Description = "What a line typed at the command line runs in. Auto is Git Bash when it is installed, cmd otherwise.",
        Section = SettingSection.General,
        Choices = [new("auto", "Auto"), new("bash", "Git Bash"), new("pwsh", "PowerShell"), new("cmd", "cmd")],
        Default = "auto",
        Read = file => Text(file.CommandLine.Shell),
    };

    public static SettingDefinition MiddleClick { get; } = new()
    {
        Path = ["mouse", "middleClick"],
        Title = "Middle click",
        Description = "What the middle button does on a folder or an archive: open it in a new tab here, as a browser does, or in the other pane.",
        Section = SettingSection.General,
        Choices = [new("newTab", "New tab"), new("otherPane", "Other pane")],
        Default = "newTab",
        // Spelled in camel case, as the file's keys are, and read whatever the case: the others are
        // single words, where lower case was the only spelling anybody would type.
        Read = file => Text(file.Mouse.MiddleClick) switch
        {
            "newtab" => "newTab",
            "otherpane" => "otherPane",
            var other => other,
        },
    };

    public static SettingDefinition Prefetch { get; } = new()
    {
        Path = ["remote", "prefetch"],
        Title = "Fetch server folders ahead",
        Description = "On a server, the folder under the cursor is listed before you open it: at once on a click, after a moment from the keyboard. Off if a server minds the extra requests.",
        Section = SettingSection.General,
        Choices = [new("false", "Off"), new("true", "On")],
        Default = "true",
        Read = file => file.Remote.Prefetch ? "true" : "false",
        ToJson = value => JsonValue.Create(value == "true"),
    };

    public static SettingDefinition KeepListings { get; } = new()
    {
        Path = ["remote", "keepListings"],
        Title = "Remember server folders between sessions",
        Description = "The server folders you were in are kept on this computer, so a server tab shows its rows at once after a restart while it is listed again. Off forgets them.",
        Section = SettingSection.General,
        Choices = [new("false", "Off"), new("true", "On")],
        Default = "true",
        Read = file => file.Remote.KeepListings ? "true" : "false",
        ToJson = value => JsonValue.Create(value == "true"),
    };

    public static SettingDefinition UnencryptedSignIn { get; } = new()
    {
        Path = ["remote", "unencryptedSignIn"],
        Title = "Sign in to unencrypted WebDAV",
        Description = "A dav:// server gets no password and no Windows sign-in, since anyone on the network between could read or reuse them; davs:// is unaffected. On sends them anyway, for a network you trust.",
        Section = SettingSection.General,
        Choices = [new("false", "Off"), new("true", "On")],
        Default = "false",
        Read = file => file.Remote.UnencryptedSignIn ? "true" : "false",
        ToJson = value => JsonValue.Create(value == "true"),
    };

    public static IReadOnlyList<SettingDefinition> All { get; } =
        [DirectoriesFirst, Shell, MiddleClick, Prefetch, KeepListings, UnencryptedSignIn, Scheme, Accent, Density, TextSize, CloudStatus];

    /// <summary>The command the middle button runs on a row; anything but "otherPane" is a new tab.</summary>
    public static string MiddleClickCommand(SettingsFile file) =>
        MiddleClick.Read(file) == "otherPane" ? "dc.openInOtherPane" : "dc.openInNewTab";

    /// <summary>The text size in force, within reason: a hand-edited 10 would make the window unusable.</summary>
    public static double TextScale(SettingsFile file) => Math.Clamp(file.Appearance.TextSize, 0.75, 1.75);

    public static bool IsCompact(SettingsFile file) => Text(file.Appearance.Density) == "compact";

    /// <summary>Anything but "all" is the cursor row, the quieter of the two.</summary>
    public static bool CloudStatusOnEveryRow(SettingsFile file) => Text(file.View.CloudStatus) == "all";

    private static string Text(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// Writes settings.json the way a person would edit it: one property at a time, the rest of the
/// file untouched, and a value set back to its default taken out rather than written down.
/// </summary>
public sealed class SettingsWriter(IConfigFile file)
{
    public Task SetAsync(SettingDefinition setting, string value) =>
        file.EditTextAsync(text => value == setting.Default
            ? JsoncEdit.Remove(text, setting.Path)
            : JsoncEdit.Set(text, setting.Path, setting.ToJson(value)));

    /// <summary>A key of your own for a command; null takes its key away.</summary>
    public Task SetKeyAsync(string commandId, string? key) =>
        file.EditTextAsync(text => JsoncEdit.Set(text, ["keys", commandId], key is null ? null : JsonValue.Create(key)));

    /// <summary>The command's own key back.</summary>
    public Task ResetKeyAsync(string commandId) =>
        file.EditTextAsync(text => JsoncEdit.Remove(text, ["keys", commandId]));
}
