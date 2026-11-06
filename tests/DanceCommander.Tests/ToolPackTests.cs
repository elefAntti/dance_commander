using DanceCommander.Core.Commands;
using DanceCommander.Core.Config;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Listing;
using DanceCommander.Extensibility;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// Tool packs (SETTINGS_PLAN.md §4): a program found brings its tools, unless its pack is switched
/// off in settings.json; a tool of the same id in tools.json is used instead of the pack's.
/// </summary>
public sealed class ToolPackTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-packs-").FullName;

    private readonly List<IDisposable> _stores = [];

    public void Dispose()
    {
        foreach (var store in _stores)
            store.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private const string Bash = @"C:\Program Files\Git\bin\bash.exe";

    /// <summary>A computer with Git for Windows and VS Code, and nothing else.</summary>
    private static ProgramLocator Machine(params string[] onPath) => new(
        name => onPath.Contains(name, StringComparer.OrdinalIgnoreCase) ? $@"C:\bin\{name}" : null,
        path => path == Bash,
        () => false);

    private sealed class Store<T>(T file) : IConfigStore<T>
        where T : class, new()
    {
        public T Current { get; private set; } = file;

        public event EventHandler<T>? Changed;

        public void Edit(T next)
        {
            Current = next;
            Changed?.Invoke(this, next);
        }

        public Task SaveAsync(T value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private PackDetector Detector(ProgramLocator machine)
    {
        var cache = new JsonConfigStore<PacksFoundFile>(_root, "packs.json", NullLogger.Instance);
        _stores.Add(cache);
        return new PackDetector(machine, cache, NullLogger<PackDetector>.Instance);
    }

    private static CommandRegistry Catalog(
        PackDetector packs, Store<SettingsFile> settings, Store<ToolsFile>? tools = null)
    {
        var registry = new CommandRegistry();
        new ToolCatalog(
                tools ?? new Store<ToolsFile>(new ToolsFile()), registry,
                new ToolRunner(new Core.Operations.ActivityLog(), new JobService(), NullLogger<ToolRunner>.Instance),
                new CommandListingCatalog(), NullLogger<ToolCatalog>.Instance, packs, settings)
            .Reload();
        return registry;
    }

    private static SettingsFile Off(string pack) => new() { Tools = { Packs = { [pack] = false } } };

    [Fact]
    public async Task FindsWhatIsInstalledAndNothingElse()
    {
        var packs = Detector(Machine("code", "certutil.exe"));

        await packs.DetectAsync();

        packs.Found.Keys.Order().ShouldBe(["gitbash", "vscode", "windows"]);
        packs.Found["gitbash"].ShouldBe(Bash);
    }

    [Fact]
    public async Task AFoundPacksToolsAreCommandsAndAMissingOnesAreNot()
    {
        var packs = Detector(Machine("code"));
        await packs.DetectAsync();

        var registry = Catalog(packs, new Store<SettingsFile>(new SettingsFile()));

        registry.Find("tool.open-in-bash").ShouldNotBeNull().Binding.ShouldBe(KeyStroke.Parse("Ctrl+Shift+B"));
        registry.Find("tool.vscode").ShouldNotBeNull();
        registry.Find("tool.open-in-terminal").ShouldBeNull();
        registry.Find("tool.open-in-wsl").ShouldBeNull();
    }

    [Fact]
    public async Task SwitchingAPackOffTakesItsToolsAwayAndOnBringsThemBack()
    {
        var packs = Detector(Machine("code"));
        await packs.DetectAsync();
        var settings = new Store<SettingsFile>(new SettingsFile());
        var registry = Catalog(packs, settings);

        settings.Edit(Off("vscode"));
        registry.Find("tool.vscode").ShouldBeNull();
        registry.Find("tool.vscode-folder").ShouldBeNull();
        registry.Find("tool.open-in-bash").ShouldNotBeNull();

        settings.Edit(new SettingsFile());
        registry.Find("tool.vscode").ShouldNotBeNull();
    }

    [Fact]
    public async Task AToolOfYoursWithAPacksIdIsUsedInsteadEvenTurnedOff()
    {
        var packs = Detector(Machine("code"));
        await packs.DetectAsync();
        var yours = new ToolDefinition { Id = "vscode", Title = "Open with Code Insiders", Exec = "code-insiders", Args = ["${selection}"] };
        var off = new ToolDefinition { Id = "open-in-bash", Title = "Open in bash", Exec = "bash", Enabled = false };

        var registry = Catalog(packs, new Store<SettingsFile>(new SettingsFile()),
            new Store<ToolsFile>(new ToolsFile { Tools = [yours, off] }));

        registry.Find("tool.vscode").ShouldNotBeNull().Title.ShouldBe("Open with Code Insiders");
        registry.Find("tool.open-in-bash").ShouldBeNull();
        registry.Find("tool.vscode-folder").ShouldNotBeNull(); // the rest of the pack stays
    }

    [Fact]
    public async Task AKeyOfYoursWinsOverAPacks()
    {
        var packs = Detector(Machine());
        await packs.DetectAsync();
        var yours = new ToolDefinition { Id = "mine", Title = "Mine", Exec = "x", Keybinding = "Ctrl+Shift+B" };

        var registry = Catalog(packs, new Store<SettingsFile>(new SettingsFile()),
            new Store<ToolsFile>(new ToolsFile { Tools = [yours] }));

        registry.ForKey(KeyStroke.Parse("Ctrl+Shift+B"))!.Id.ShouldBe("tool.mine");
    }

    [Fact]
    public async Task TheFirstLookAnnouncesNothingAndALaterOneWhatArrived()
    {
        var first = Detector(Machine());
        var arrived = new List<string>();
        first.Changed += (_, e) => arrived.AddRange(e.Arrived.Select(p => p.Id));
        await first.DetectAsync();
        first.Found.ShouldContainKey("gitbash");
        arrived.ShouldBeEmpty();

        // The next launch, VS Code installed meanwhile: packs.json remembers the last look.
        var next = Detector(Machine("code"));
        next.Found.ShouldContainKey("gitbash"); // from the cache, before looking
        next.Changed += (_, e) => arrived.AddRange(e.Arrived.Select(p => p.Id));
        await next.DetectAsync();

        arrived.ShouldBe(["vscode"]);
    }

    [Fact]
    public async Task APacksToolsComeAndGoWithItsProgram()
    {
        var located = new List<string> { "code" };
        var machine = new ProgramLocator(n => located.Contains(n) ? $@"C:\bin\{n}" : null, _ => false, () => false);
        var packs = Detector(machine);
        await packs.DetectAsync();
        var registry = Catalog(packs, new Store<SettingsFile>(new SettingsFile()));
        registry.Find("tool.vscode").ShouldNotBeNull();

        located.Clear();
        await packs.DetectAsync();

        registry.Find("tool.vscode").ShouldBeNull();
    }

    [Fact]
    public void WslNeedsALinuxNotOnlyItsLauncher()
    {
        ToolPacks.Wsl.Locate(new ProgramLocator(_ => @"C:\Windows\System32\wsl.exe", _ => false, () => false))
            .ShouldBeNull();
        ToolPacks.Wsl.Locate(new ProgramLocator(_ => @"C:\Windows\System32\wsl.exe", _ => false, () => true))
            .ShouldNotBeNull();
    }

    [Fact]
    public void BeyondCompareIsTheNewestInstalled()
    {
        var five = Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Beyond Compare 5\BCompare.exe");
        var four = Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Beyond Compare 4\BCompare.exe");

        ToolPacks.BeyondCompare.Locate(new ProgramLocator(_ => null, p => p == four || p == five, () => false))
            .ShouldBe(five);
        ToolPacks.BeyondCompare.Locate(new ProgramLocator(_ => null, p => p == four, () => false))
            .ShouldBe(four);
        ToolPacks.BeyondCompare.Locate(new ProgramLocator(n => n == "BCompare.exe" ? @"D:\BC\BCompare.exe" : null, _ => false, () => false))
            .ShouldBe(@"D:\BC\BCompare.exe");
    }

    [Fact]
    public void GitIsToldToWaitOnBCompNotBCompare()
    {
        var tools = ToolPacks.BeyondCompare.Tools(@"C:\Program Files\Beyond Compare 5\BCompare.exe");

        var diff = tools.Single(t => t.Id == "bc-git-diff");
        diff.Args.ShouldContain("difftool.bc.path=C:/Program Files/Beyond Compare 5/BComp.exe");
        var merge = tools.Single(t => t.Id == "bc-git-merge");
        merge.Args.ShouldContain("mergetool.bc.path=C:/Program Files/Beyond Compare 5/BComp.exe");
        merge.Args.ShouldContain("mergetool.keepBackup=true");
        tools.Single(t => t.Id == "bc-compare").Exec.ShouldBe(@"C:\Program Files\Beyond Compare 5\BCompare.exe");
    }

    private static readonly FsUri Repo = FsUri.Local(@"C:\src\app");

    private static FsEntry FileRow(string path) => new(FsUri.Local(path), Path.GetFileName(path), IsDirectory: false, 1, null);

    private static FsEntry FolderRow(string path) => new(FsUri.Local(path), Path.GetFileName(path), IsDirectory: true, 0, null);

    private async Task<CommandRegistry> BeyondCompareCatalog()
    {
        var five = Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Beyond Compare 5\BCompare.exe");
        var packs = Detector(new ProgramLocator(_ => null, p => p == five, () => false));
        await packs.DetectAsync();
        return Catalog(packs, new Store<SettingsFile>(new SettingsFile()));
    }

    [Fact]
    public async Task ComparingWithTheOtherPaneNeedsOneThatIsLocal()
    {
        var registry = await BeyondCompareCatalog();
        var compare = registry.Find("tool.bc-compare-other-pane").ShouldNotBeNull();
        var folders = registry.Find("tool.bc-compare-folders").ShouldNotBeNull();
        FsEntry[] row = [FileRow(@"C:\work\a.txt")];

        compare.IsApplicable(new FakeContext { Selection = row, OtherPaneDirectory = FsUri.Local(@"D:\b") }).ShouldBeTrue();
        compare.IsApplicable(new FakeContext { Selection = row }).ShouldBeFalse(); // one pane showing
        compare.IsApplicable(new FakeContext { Selection = row, OtherPaneDirectory = FsUri.Remote("sftp", "me@host", "/home/me") })
            .ShouldBeFalse();
        folders.IsApplicable(new FakeContext { OtherPaneDirectory = FsUri.Local(@"D:\b") }).ShouldBeTrue();
        folders.IsApplicable(new FakeContext()).ShouldBeFalse();
    }

    [Fact]
    public async Task CompareIsForTwoOrThreeRows()
    {
        var compare = (await BeyondCompareCatalog()).Find("tool.bc-compare").ShouldNotBeNull();
        FsEntry[] Rows(int n) => Enumerable.Range(0, n).Select(i => FileRow($@"C:\work\{i}.txt")).ToArray();

        compare.IsApplicable(new FakeContext { Selection = Rows(1) }).ShouldBeFalse();
        compare.IsApplicable(new FakeContext { Selection = Rows(2) }).ShouldBeTrue();
        compare.IsApplicable(new FakeContext { Selection = Rows(3) }).ShouldBeTrue();
        compare.IsApplicable(new FakeContext { Selection = Rows(4) }).ShouldBeFalse();
    }

    [Fact]
    public async Task AMergeToolIsOfferedOnlyWhereThereIsAConflict()
    {
        var registry = await BeyondCompareCatalog();
        var all = registry.Find("tool.bc-git-merge").ShouldNotBeNull();
        var these = registry.Find("tool.bc-git-merge-file").ShouldNotBeNull();
        var clean = new RepositoryInfo(Repo, "main", null, []);
        var conflicted = clean with { Conflicts = ["src/Core/a.cs"] };
        var directory = FsUri.Local(@"C:\src\app\src");

        all.IsApplicable(new FakeContext { Directory = directory }).ShouldBeFalse(); // not a repository
        all.IsApplicable(new FakeContext { Directory = directory, Repository = clean }).ShouldBeFalse();
        all.IsApplicable(new FakeContext { Directory = directory, Repository = conflicted }).ShouldBeTrue();

        these.IsApplicable(new FakeContext { Directory = directory, Repository = conflicted, Selection = [FileRow(@"C:\src\app\src\Core\a.cs")] })
            .ShouldBeTrue();
        these.IsApplicable(new FakeContext { Directory = directory, Repository = conflicted, Selection = [FolderRow(@"C:\src\app\src\Core")] })
            .ShouldBeTrue(); // holds one
        these.IsApplicable(new FakeContext { Directory = directory, Repository = conflicted, Selection = [FileRow(@"C:\src\app\src\b.cs")] })
            .ShouldBeFalse();
        these.IsApplicable(new FakeContext
        {
            Directory = directory, Repository = conflicted,
            Selection = [FileRow(@"C:\src\app\src\Core\a.cs"), FileRow(@"C:\src\app\src\b.cs")],
        }).ShouldBeFalse(); // not every row has one
    }

    [Fact]
    public void AConflictIsMatchedByNameAsWindowsSpellsIt()
    {
        var repository = new RepositoryInfo(Repo, "main", null, ["src/Core/a.cs"]);

        repository.IsConflicted(FsUri.Local(@"C:\SRC\app\src\core\A.cs")).ShouldBeTrue();
        repository.IsConflicted(FsUri.Local(@"C:\src\app")).ShouldBeTrue(); // the root
        repository.IsConflicted(FsUri.Local(@"C:\src\app\src\Co")).ShouldBeFalse(); // a prefix, not a folder
        repository.IsConflicted(FsUri.Local(@"C:\src\other\src\Core\a.cs")).ShouldBeFalse();
        repository.IsConflicted(FsUri.Local(@"D:\src\app\src\Core\a.cs")).ShouldBeFalse();
    }

    [Fact]
    public void ForkAndGitKrakenOpenTheRepositoryWhereverYouStand()
    {
        var fork = Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Fork\current\Fork.exe");
        var kraken = Environment.ExpandEnvironmentVariables(@"%LocalAppData%\gitkraken\gitkraken.exe");
        var machine = new ProgramLocator(_ => null, p => p == fork || p == kraken, () => false);

        ToolPacks.Fork.Locate(machine).ShouldBe(fork);
        ToolPacks.GitKraken.Locate(machine).ShouldBe(kraken);
        ToolPacks.Fork.Tools(fork).Single().Args.ShouldBe(["${repoRoot}"]);
        ToolPacks.GitKraken.Tools(kraken).Single().Args.ShouldBe(["-p", "${repoRoot}"]);
    }

    [Fact]
    public void APacksSwitchIsWrittenSparselyUnderToolsPacks()
    {
        var setting = ToolPacks.VsCode.Setting(() => null);

        setting.Id.ShouldBe("tools.packs.vscode");
        setting.Default.ShouldBe("true");
        setting.Read(new SettingsFile()).ShouldBe("true");
        setting.Read(Off("vscode")).ShouldBe("false");
    }

    [Fact]
    public async Task APacksSwitchWrittenToSettingsJsonReadsBack()
    {
        using var store = new JsonConfigStore<SettingsFile>(_root, "settings.json", NullLogger.Instance);
        var setting = ToolPacks.VsCode.Setting(() => null);

        await new SettingsWriter(store).SetAsync(setting, "false");

        var text = await File.ReadAllTextAsync(Path.Combine(_root, "settings.json"));
        text.ShouldContain("\"packs\"");
        System.Text.Json.JsonSerializer.Deserialize<SettingsFile>(text, JsonConfigStore<SettingsFile>.SerializerOptions)!
            .Tools.Packs["vscode"].ShouldBeFalse();
    }
}
