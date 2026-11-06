namespace DanceCommander.Core.Config;

/// <summary>
/// <c>settings.json</c>: choices about how the application behaves, as opposed to the lists the
/// other files hold. Not written until there is something in it to change: a missing file is the
/// defaults. Sparse: it holds only what differs from them, so a better default reaches everyone who
/// never chose otherwise (SETTINGS_PLAN.md §3). Each setting is described once, for the settings
/// window and the palette, in <see cref="SettingsCatalog"/>.
/// </summary>
public sealed class SettingsFile
{
    public CommandLineSettings CommandLine { get; set; } = new();

    public AppearanceSettings Appearance { get; set; } = new();

    public ViewSettings View { get; set; } = new();

    public MouseSettings Mouse { get; set; } = new();

    public ToolsSettings Tools { get; set; } = new();

    public RemoteSettings Remote { get; set; } = new();

    /// <summary>
    /// Keys chosen over the commands' own, by command id: <c>"dc.search": "Ctrl+Shift+S"</c>. Null
    /// or empty takes a command's key away. A tool's key stays in tools.json, where the tool is.
    /// </summary>
    public Dictionary<string, string?> Keys { get; set; } = new(StringComparer.Ordinal);
}

public sealed class AppearanceSettings
{
    /// <summary>
    /// <c>"system"</c> (follow Windows' app mode, and switch when it does), <c>"dark"</c> or
    /// <c>"light"</c>. Anything else is taken as "system".
    /// </summary>
    public string Scheme { get; set; } = "system";

    /// <summary>
    /// <c>"system"</c> (Windows' accent colour, followed when it changes), a name from
    /// <see cref="AccentColour.Named"/>, or your own as <c>"#RRGGBB"</c>. Whatever it is, it is
    /// adjusted to be legible on the palette in use; anything unreadable is taken as "system".
    /// </summary>
    public string Accent { get; set; } = "system";

    /// <summary><c>"comfortable"</c> (24 px rows) or <c>"compact"</c> (18 px), DESIGN.md §1.3.</summary>
    public string Density { get; set; } = "comfortable";

    /// <summary>
    /// One factor over the three type sizes, and the row heights with them, so larger text never
    /// crowds its row. Not per element: §1.3 allows three sizes, and this keeps them three.
    /// </summary>
    public double TextSize { get; set; } = 1.0;
}

public sealed class ViewSettings
{
    /// <summary>
    /// How a tab starts out sorting: folders pinned above files, the classic commander way, or
    /// mixed by name. The command "Sort folders before files" still changes it per location.
    /// </summary>
    public bool DirectoriesFirst { get; set; }

    /// <summary>
    /// Where a OneDrive file's state (only online, always kept here) is drawn: <c>"cursor"</c>, on
    /// the cursor row only, or <c>"all"</c>, on every row as Explorer does. On the cursor by default
    /// because in a folder that is mostly online, a cloud on two rows in three is noise.
    /// </summary>
    public string CloudStatus { get; set; } = "cursor";
}

public sealed class MouseSettings
{
    /// <summary>
    /// What the middle button does on a folder or an archive: <c>"newTab"</c>, a tab of this pane
    /// as a browser's middle click opens one, or <c>"otherPane"</c>, the other pane goes there.
    /// Anything else is taken as "newTab".
    /// </summary>
    public string MiddleClick { get; set; } = "newTab";
}

public sealed class ToolsSettings
{
    /// <summary>
    /// Tool packs switched off, by pack id: <c>"vscode": false</c>. A pack is on wherever its
    /// program is found unless it is named here (SETTINGS_PLAN.md §4).
    /// </summary>
    public Dictionary<string, bool> Packs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Server folders (WebDAV, SFTP): what is fetched and kept to make them quicker to move through.</summary>
public sealed class RemoteSettings
{
    /// <summary>
    /// Lists the folder under the cursor before it is opened, so Enter finds it ready: at once on a
    /// click, after a moment's rest from the keyboard. Off if a server minds the extra requests.
    /// </summary>
    public bool Prefetch { get; set; } = true;

    /// <summary>
    /// Keeps the remembered folder listings on disk (%LOCALAPPDATA%), so a server tab restored at
    /// launch shows its rows at once while it is listed again.
    /// </summary>
    public bool KeepListings { get; set; } = true;

    /// <summary>
    /// Lets a <c>dav://</c> (plain HTTP) server have a password or the Windows sign-in. Off, because
    /// anyone on the network between can read the one and crack or relay the other, and a name
    /// without dots can be answered by any machine on a strange network (BUGS.md 54).
    /// </summary>
    public bool UnencryptedSignIn { get; set; }
}

public sealed class CommandLineSettings
{
    /// <summary>
    /// What a line typed at the command line runs in on this machine: <c>"auto"</c> (Git Bash when
    /// it is installed, cmd.exe otherwise), <c>"bash"</c>, <c>"pwsh"</c> or <c>"cmd"</c>. A server's
    /// own shell is asked, not chosen.
    /// </summary>
    public string Shell { get; set; } = "auto";
}
