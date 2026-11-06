using DanceCommander.Core.Commands;
using System.Text.Json.Serialization;
using DanceCommander.Core.Matching;

namespace DanceCommander.Extensibility;

/// <summary>Where a tool is offered. Parsed from "file|directory|background|multi".</summary>
[Flags]
public enum ToolScope
{
    None = 0,
    Background = 1,
    File = 2,
    Directory = 4,
    Multi = 8,
    Default = Background | File | Directory | Multi,
}

public enum ToolLaunch
{
    /// <summary>A windowed program. Started through the shell, so PATH and .cmd shims resolve.</summary>
    Process,

    /// <summary>A console program that should get its own window: bash, wsl, cmd.</summary>
    Console,

    /// <summary>Run it, wait, and show stdout/stderr in the pane.</summary>
    Capture,

    /// <summary>
    /// Run it in the job strip: output streamed into the pane as it arrives, stoppable, and
    /// announced when it finishes (§6.17). For anything measured in minutes — a build.
    /// </summary>
    Job,
}

/// <summary>
/// One entry from tools.json. This is the whole extensibility story for now (§6.6): declarative,
/// hot-reloaded, and incapable of destabilising the application the way an in-process plugin can.
/// </summary>
public sealed class ToolDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Group { get; set; }

    /// <summary>"file", "directory", "background", "multi", combined with "|". Default: all.</summary>
    public string? AppliesTo { get; set; }

    /// <summary>URI schemes this tool understands. Default: local files only.</summary>
    public List<string>? Schemes { get; set; }

    public string Exec { get; set; } = string.Empty;

    public List<string> Args { get; set; } = [];

    /// <summary>
    /// Join <see cref="Args"/> into the command line as they are, instead of quoting each one, and
    /// do not expand placeholders in them: they are a finished line. Only for a line the user typed
    /// as shell syntax: cmd.exe does not read the backslash-escaped quotes .NET would put around
    /// it, so quoting it as one argument mangles any quotes inside. Never set from tools.json --
    /// there, an argument must stay an argument (§6.6).
    /// </summary>
    [JsonIgnore]
    public bool VerbatimArgs { get; init; }

    /// <summary>
    /// A run that succeeds and prints nothing says so in passing (<see cref="Core.Decisions.IOutputHost.ShowDone"/>)
    /// instead of opening a panel that reads "(no output)". For the command line, where `touch foo`
    /// needs no answer (COMMAND_LINE_PLAN.md A). A tool from a menu keeps its panel: there, silence
    /// once read as a menu entry that does nothing. Never set from tools.json.
    /// </summary>
    [JsonIgnore]
    public bool QuietWhenSilent { get; init; }

    /// <summary>
    /// With <c>launch: job</c>: a job from the start, but shown in the strip only if it is still
    /// running after this long; one that finishes sooner is reported as a captured run would be.
    /// Zero shows it at once. For the command line, where `ls` should answer in place and a build
    /// should not hold the prompt (COMMAND_LINE_PLAN.md F). Never set from tools.json.
    /// </summary>
    [JsonIgnore]
    public TimeSpan? BackgroundAfter { get; set; }

    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// How the program's captured output is written, e.g. <c>"utf-8"</c>. Unset, it is read in the
    /// console's code page, which is right for cmd.exe and wrong for Git Bash, git and most of
    /// what comes from the Unix side: an em dash came out as <c>ΓÇö</c>.
    /// </summary>
    public string? OutputEncoding { get; set; }

    public string Launch { get; set; } = "process";

    /// <summary>
    /// For a job: where its result lands, e.g. <c>${repoRoot}/build</c>. Relative to the working
    /// directory. When set, a job that succeeds offers to open it in the other pane.
    /// </summary>
    public string? OutputDirectory { get; set; }

    public string? Keybinding { get; set; }

    /// <summary>Extra condition on the entry, using the same matcher as highlight rules.</summary>
    public MatchRule? When { get; set; }

    /// <summary>
    /// Only offer this tool inside a git work tree. A condition on the location, which
    /// <see cref="When"/> cannot express because it matches entries.
    /// </summary>
    public bool InRepository { get; set; }

    /// <summary>
    /// Only offer this tool in a repository whose remote URL matches, e.g. "orbit" or
    /// "*gitlab.example.com*/team/*". A bare word means "contains", as in the quick filter. Implies
    /// <see cref="InRepository"/>. It is how a tool written for one project — its build, its
    /// container — stays out of every other repository's menus, and it follows the project into its
    /// worktrees, which share the remote.
    /// </summary>
    public string? RemoteMatches { get; set; }

    /// <summary>
    /// Only offer this tool where git reports a merge conflict: for a tool about the selection, on
    /// rows that are unmerged files or folders holding one; for a tool about the folder, in a
    /// repository with one anywhere. Implies <see cref="InRepository"/>. Read from the rows' git
    /// marks, so it is as fresh as they are. A merge tool on every menu of every repository would
    /// be there for the one day in fifty it has work to do.
    /// </summary>
    public bool InConflict { get; set; }

    /// <summary>
    /// The most rows the tool takes at once. "Compare" makes sense for two or three files and is
    /// noise on forty: with <c>appliesTo: "multi"</c> and this at 3, it is on the menu for two or
    /// three marked rows only.
    /// </summary>
    public int? MaxSelected { get; set; }

    /// <summary>
    /// "selection" or "location": what the tool acts on (§6.18). Usually left out, because it can be
    /// read off the tool — see <see cref="ParsedSubject"/>.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>Run once per selected item instead of once for the whole selection.</summary>
    public bool ForEachSelected { get; set; }

    /// <summary>
    /// F4 uses this tool to edit the files its <see cref="When"/> matches, ahead of the text editor
    /// and the shell's own "edit" (BuiltInCommands' Edit). The first such tool that applies wins, in
    /// file order. Still an ordinary tool otherwise: on the menu, with its own key if it has one.
    /// </summary>
    public bool Edits { get; set; }

    public bool Enabled { get; set; } = true;

    public ToolScope ParsedScope()
    {
        if (string.IsNullOrWhiteSpace(AppliesTo))
            return ToolScope.Default;

        var scope = ToolScope.None;
        foreach (var part in AppliesTo.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            scope |= part.ToLowerInvariant() switch
            {
                "file" => ToolScope.File,
                "directory" or "dir" or "folder" => ToolScope.Directory,
                "background" => ToolScope.Background,
                "multi" or "multiple" => ToolScope.Multi,
                _ => ToolScope.None,
            };
        }

        return scope == ToolScope.None ? ToolScope.Default : scope;
    }

    /// <summary>
    /// What the tool acts on. Said outright by <see cref="Subject"/>, or else read off what it
    /// uses: a tool that reads <c>${path}</c> or <c>${selection}</c> is about the selection, and one
    /// that reads only <c>${dir}</c> and the location tokens is about the folder, whatever its
    /// appliesTo says — "Open in bash" was offered on a selected folder and opened the one you were
    /// in. With no entry tokens, an appliesTo that leaves out "background" still says "rows".
    /// </summary>
    public CommandSubject ParsedSubject()
    {
        switch (Subject?.Trim().ToLowerInvariant())
        {
            case "selection" or "entries" or "entry":
                return CommandSubject.Selection;
            case "location" or "folder" or "directory":
                return CommandSubject.Location;
        }

        if (ToolPlaceholders.ReadsEntries(Templates()))
            return CommandSubject.Selection;

        return !string.IsNullOrWhiteSpace(AppliesTo) && !ParsedScope().HasFlag(ToolScope.Background)
            ? CommandSubject.Selection
            : CommandSubject.Location;
    }

    public ToolLaunch ParsedLaunch() => Launch?.ToLowerInvariant() switch
    {
        "console" => ToolLaunch.Console,
        "capture" => ToolLaunch.Capture,
        "job" => ToolLaunch.Job,
        _ => ToolLaunch.Process,
    };

    /// <summary>Every field that placeholders expand in.</summary>
    public IEnumerable<string?> Templates() =>
        VerbatimArgs ? [Exec, WorkingDirectory, OutputDirectory] : [Exec, .. Args, WorkingDirectory, OutputDirectory];

    public IReadOnlyList<string> ParsedSchemes() =>
        Schemes is { Count: > 0 } ? Schemes : ["file"];
}

/// <summary>
/// One entry under "listings": a command whose printed paths become a browsable location (§6.12).
/// </summary>
public sealed class ListingDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Exec { get; set; } = string.Empty;

    public List<string> Args { get; set; } = [];

    /// <summary>
    /// Matched repeatedly against the whole output; each match is a row. The path comes from a
    /// group named "path", or from group 1. Other named groups can become columns.
    /// </summary>
    public string Extract { get; set; } = "^(.+)$";

    /// <summary>Extra columns, each filled from the named group it gives.</summary>
    public List<ListingColumnDefinition> Columns { get; set; } = [];

    /// <summary>Offer this listing only inside a git work tree.</summary>
    public bool InRepository { get; set; }

    /// <summary>Whether rows get a Modified column; off suits a listing of folders.</summary>
    public bool ShowModified { get; set; } = true;

    /// <summary>
    /// A script location (§6.16): the command is run as <c>exec args… list|read &lt;crumbs&gt;</c>
    /// and serves its own hierarchy. Without an <c>extract</c>, rows are tab-separated lines:
    /// kind (d/f), name, size, modified, crumb.
    /// </summary>
    public bool Script { get; set; }

    /// <summary>
    /// Secrets the script needs, by name, from the credential store; given to it as environment
    /// variables <c>DC_SECRET_NAME</c>, never as arguments. A script that prints its own secret
    /// puts it in the activity log, since its stderr is shown verbatim.
    /// </summary>
    public List<string> Secrets { get; set; } = [];

    /// <summary>
    /// What a script location starts sorted by: "name" (the default), "modified" or "size" — the
    /// last two newest or largest first.
    /// </summary>
    public string? Sort { get; set; }

    public bool Enabled { get; set; } = true;
}

public sealed class ListingColumnDefinition
{
    /// <summary>The named capture group in <see cref="ListingDefinition.Extract"/>.</summary>
    public string Group { get; set; } = string.Empty;

    public string Header { get; set; } = string.Empty;

    public double Width { get; set; } = 120;

    /// <summary>Take the width left over, instead of the path column. At most one per listing.</summary>
    public bool Flexible { get; set; }
}

public sealed class ToolsFile
{
    public List<ToolDefinition> Tools { get; set; } = [];

    /// <summary>
    /// Locations built from a command's output. Separate from <see cref="Tools"/> because a tool
    /// does something and a listing is somewhere: one ends up in a menu, the other in a pane.
    /// </summary>
    public List<ListingDefinition> Listings { get; set; } = [];
}
