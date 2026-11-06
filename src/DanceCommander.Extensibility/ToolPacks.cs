using DanceCommander.Core.Config;
using DanceCommander.Core.Matching;

namespace DanceCommander.Extensibility;

/// <summary>
/// How a pack finds its program. The machine's own answers in use; a test gives its own, so
/// detection is checked without depending on what this computer has installed.
/// </summary>
/// <param name="OnPath">A bare name looked up the way a command prompt would (PATH, then App Paths); null when not found.</param>
/// <param name="FileExists">Whether a full path is there.</param>
/// <param name="HasWslDistribution">Whether WSL has a Linux installed, not only its launcher.</param>
public sealed record ProgramLocator(
    Func<string, string?> OnPath,
    Func<string, bool> FileExists,
    Func<bool> HasWslDistribution)
{
    public static ProgramLocator Machine { get; } = new(
        name => ExecutableSearch.Resolve(name) is var found && !ExecutableSearch.IsBare(found) ? found : null,
        File.Exists,
        WslDistributions);

    /// <summary>The first of the full paths that exists, environment variables expanded.</summary>
    public string? FirstFile(params string[] paths) =>
        paths.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(FileExists);

    // wsl.exe is in System32 on every recent Windows, Linux or no Linux; what it would open is
    // registered under Lxss, one subkey per distribution.
    private static bool WslDistributions()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            return key?.SubKeyCount > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Tools that come with a program (SETTINGS_PLAN.md §4): on when the program is found, unless
/// switched off in settings.json. They live here rather than in the user's tools.json, so a better
/// pack reaches everyone, as a better default setting does; a tool in tools.json with the same id
/// replaces the pack's, which is how one is changed.
/// </summary>
/// <param name="Id">The key under <c>tools.packs</c> in settings.json. Stable.</param>
/// <param name="Title">The program, as people call it.</param>
/// <param name="Locate">Where the program is on this computer, or null.</param>
/// <param name="Tools">The tools, given what <see cref="Locate"/> found.</param>
public sealed record ToolPack(
    string Id,
    string Title,
    Func<ProgramLocator, string?> Locate,
    Func<string, IReadOnlyList<ToolDefinition>> Tools)
{
    /// <summary>The tools as they would be, for saying what the pack holds before it is found.</summary>
    public IReadOnlyList<ToolDefinition> Sample => Tools(Id);

    /// <summary>The pack's switch, for the settings window and the palette.</summary>
    public SettingDefinition Setting(Func<string?> status) => new()
    {
        Path = ["tools", "packs", Id],
        Title = $"{Title} tools",
        Description = string.Join(", ", Sample.Select(t => t.Title)) + ".",
        Section = SettingSection.Tools,
        Choices = [new("false", "Off"), new("true", "On")],
        Default = "true",
        Read = file => IsOn(file) ? "true" : "false",
        ToJson = value => System.Text.Json.Nodes.JsonValue.Create(value == "true"),
        Status = status,
    };

    public bool IsOn(SettingsFile file) => file.Tools.Packs.GetValueOrDefault(Id, true);
}

/// <summary>The packs that ship. What each holds is curated: the tools we use, nothing for completeness.</summary>
public static class ToolPacks
{
    public static ToolPack GitBash { get; } = new(
        "gitbash", "Git Bash",
        // Not "bash" on PATH: that is often System32's, which is WSL's and opens Linux, not Git's.
        found => found.FirstFile(
            @"%ProgramFiles%\Git\bin\bash.exe",
            @"%ProgramFiles(x86)%\Git\bin\bash.exe",
            @"%LocalAppData%\Programs\Git\bin\bash.exe"),
        bash =>
        [
            new ToolDefinition
            {
                Id = "open-in-bash",
                Title = "Open in bash",
                Group = "Terminal",
                AppliesTo = "directory|background",
                Exec = bash,
                Args = ["--login", "-i"],
                WorkingDirectory = "${dir}",
                Launch = "console",
                Keybinding = "Ctrl+Shift+B",
            },
        ]);

    public static ToolPack Wsl { get; } = new(
        "wsl", "WSL",
        found => found.HasWslDistribution() ? found.OnPath("wsl.exe") : null,
        _ =>
        [
            new ToolDefinition
            {
                Id = "open-in-wsl",
                Title = "Open in WSL",
                Group = "Terminal",
                AppliesTo = "directory|background",
                Exec = "wsl.exe",
                Args = ["--cd", "${dir}"],
                Launch = "console",
            },
        ]);

    public static ToolPack Terminal { get; } = new(
        "terminal", "Windows Terminal",
        found => found.OnPath("wt.exe"),
        _ =>
        [
            new ToolDefinition
            {
                Id = "open-in-terminal",
                Title = "Open in Windows Terminal",
                Group = "Terminal",
                AppliesTo = "directory|background",
                Exec = "wt.exe",
                Args = ["-d", "${dir}"],
                Launch = "process",
            },
        ]);

    // "code" rather than the path found: it is a .cmd shim, which the runner resolves and guards
    // the same way either way, and the name is what a user copying the entry into tools.json expects.
    public static ToolPack VsCode { get; } = new(
        "vscode", "VS Code",
        found => found.OnPath("code"),
        _ =>
        [
            new ToolDefinition
            {
                Id = "vscode",
                Title = "Open with VS Code",
                Group = "Edit",
                AppliesTo = "file|directory|multi",
                Exec = "code",
                Args = ["${selection}"],
                Launch = "process",
            },
            // The folder you are in, as opposed to what is selected in it (§6.18).
            new ToolDefinition
            {
                Id = "vscode-folder",
                Title = "Open folder in VS Code",
                Group = "Edit",
                Exec = "code",
                Args = ["${dir}"],
                Launch = "process",
            },
            // The repository rather than whatever happens to be selected, from anywhere inside it.
            // No inRepository flag: ${repoRoot} has no value outside one, and that alone keeps the
            // entry off the menu there (§6.6).
            new ToolDefinition
            {
                Id = "vscode-repository",
                Title = "Open repository in VS Code",
                Group = "Edit",
                Exec = "code",
                Args = ["${repoRoot}"],
                Launch = "process",
            },
        ]);

    // The newest first, by asking for each: App Paths can hold both, one per hive, and on this
    // machine HKCU's still named 4 after 5 was installed.
    public static ToolPack BeyondCompare { get; } = new(
        "beyondcompare", "Beyond Compare",
        found => found.FirstFile(
            @"%ProgramFiles%\Beyond Compare 5\BCompare.exe",
            @"%ProgramFiles%\Beyond Compare 4\BCompare.exe",
            @"%LocalAppData%\Programs\Beyond Compare 5\BCompare.exe",
            @"%LocalAppData%\Programs\Beyond Compare 4\BCompare.exe") ?? found.OnPath("BCompare.exe"),
        BeyondCompareTools);

    private static IReadOnlyList<ToolDefinition> BeyondCompareTools(string bcompare)
    {
        // Git waits on BComp, which stays until the comparison is closed. BCompare hands its work
        // to a window already open and returns, which git takes for done, and a folder diff's
        // temporary copies are cleaned up from under the window. Given here rather than left to the
        // user's git config, which may not name Beyond Compare, or name BCompare. Forward slashes
        // because git's mergetool scripts are shell.
        var bcomp = Path.Combine(Path.GetDirectoryName(bcompare) ?? string.Empty, "BComp.exe").Replace('\\', '/');
        string[] diff = ["-c", $"difftool.bc.path={bcomp}", "difftool", "--no-prompt", "--tool=bc"];
        // A backup kept whatever the config says: BC writes its result over the file, and that
        // file may hold a resolution half done by hand. The .orig is where that half lives on.
        string[] merge = ["-c", $"mergetool.bc.path={bcomp}", "-c", "mergetool.keepBackup=true", "mergetool", "--no-prompt", "--tool=bc"];

        return
        [
            // Three is a three-way view, the third read as the common ancestor.
            new ToolDefinition
            {
                Id = "bc-compare",
                Title = "Compare in Beyond Compare",
                Group = "Compare",
                AppliesTo = "multi",
                MaxSelected = 3,
                Exec = bcompare,
                Args = ["${selection}"],
                Launch = "process",
            },
            // The same name across the panes, the one row that never needs marking twice.
            new ToolDefinition
            {
                Id = "bc-compare-other-pane",
                Title = "Compare with the other pane in Beyond Compare",
                Group = "Compare",
                AppliesTo = "file|directory",
                Exec = bcompare,
                Args = ["${path}", @"${otherPaneDir}\${name}"],
                Launch = "process",
            },
            // The two folders on screen, the comparison §1.4 leaves to the program that does it well.
            new ToolDefinition
            {
                Id = "bc-compare-folders",
                Title = "Compare folders in Beyond Compare",
                Group = "Compare",
                Exec = bcompare,
                Args = ["${dir}", "${otherPaneDir}"],
                Launch = "process",
            },
            // Jobs because git waits as long as the window is open, and a job says so in the strip
            // without a console standing beside it. Against HEAD: staged or not, what you changed.
            new ToolDefinition
            {
                Id = "bc-git-diff",
                Title = "Git diff in Beyond Compare",
                Group = "Git",
                Exec = "git",
                Args = [.. diff, "--dir-diff", "HEAD"],
                WorkingDirectory = "${repoRoot}",
                OutputEncoding = "utf-8",
                Launch = "job",
            },
            // One window per file, each after the last is closed; a folder has the repository's.
            new ToolDefinition
            {
                Id = "bc-git-diff-file",
                Title = "Git diff for this in Beyond Compare",
                Group = "Git",
                AppliesTo = "file|multi",
                When = new MatchRule { FilesOnly = true },
                InRepository = true,
                Exec = "git",
                Args = [.. diff, "HEAD", "--", "${selection}"],
                OutputEncoding = "utf-8",
                Launch = "job",
            },
            // Consoles: mergetool asks about a file deleted on one side, and asks whether a merge
            // went well when the file came back unchanged. A job has no keyboard to answer with.
            new ToolDefinition
            {
                Id = "bc-git-merge",
                Title = "Resolve conflicts in Beyond Compare",
                Group = "Git",
                InConflict = true,
                Exec = "git",
                Args = [.. merge],
                WorkingDirectory = "${repoRoot}",
                Launch = "console",
            },
            new ToolDefinition
            {
                Id = "bc-git-merge-file",
                Title = "Resolve this conflict in Beyond Compare",
                Group = "Git",
                AppliesTo = "file|directory|multi",
                InConflict = true,
                Exec = "git",
                Args = [.. merge, "--", "${selection}"],
                Launch = "console",
            },
        ];
    }

    // Fork's command line opens a repository and nothing more. An absolute path: "." is ignored
    // (fork-dev/TrackerWin#2464), and ${repoRoot} is one wherever you stand.
    public static ToolPack Fork { get; } = new(
        "fork", "Fork",
        found => found.FirstFile(
            @"%LocalAppData%\Fork\current\Fork.exe", // installed by Velopack, as it is now
            @"%LocalAppData%\Fork\Fork.exe"),
        fork =>
        [
            new ToolDefinition
            {
                Id = "fork",
                Title = "Open repository in Fork",
                Group = "Git",
                Exec = fork,
                Args = ["${repoRoot}"],
                Launch = "process",
            },
        ]);

    // The Squirrel launcher in preference to the gitkraken.cmd on PATH: it forwards its arguments
    // to the current version without cmd.exe reading them first.
    public static ToolPack GitKraken { get; } = new(
        "gitkraken", "GitKraken",
        found => found.FirstFile(@"%LocalAppData%\gitkraken\gitkraken.exe") ?? found.OnPath("gitkraken"),
        gitkraken =>
        [
            new ToolDefinition
            {
                Id = "gitkraken",
                Title = "Open repository in GitKraken",
                Group = "Git",
                Exec = gitkraken,
                Args = ["-p", "${repoRoot}"],
                Launch = "process",
            },
        ]);

    // Windows' own, so found on any machine; a pack all the same, so it can be switched off.
    public static ToolPack Windows { get; } = new(
        "windows", "Windows",
        found => found.OnPath("certutil.exe"),
        _ =>
        [
            new ToolDefinition
            {
                Id = "sha256",
                Title = "SHA-256",
                Group = "Inspect",
                AppliesTo = "file|multi",
                Exec = "certutil",
                Args = ["-hashfile", "${path}", "SHA256"],
                Launch = "capture",
                ForEachSelected = true,
                When = new MatchRule { FilesOnly = true },
            },
        ]);

    public static IReadOnlyList<ToolPack> All { get; } = [GitBash, Terminal, Wsl, VsCode, BeyondCompare, Fork, GitKraken, Windows];
}
