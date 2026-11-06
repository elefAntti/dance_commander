using DanceCommander.Core.Commands;
using DanceCommander.Core.Fs;
using Shouldly;

namespace DanceCommander.Tests;

public class CommandRegistryTests
{
    private static CommandDefinition Command(string id, string? binding = null, CommandScope scope = CommandScope.Any) =>
        new()
        {
            Id = id,
            Title = id,
            Scope = scope,
            Binding = binding is null ? null : KeyStroke.Parse(binding),
            ExecuteAsync = (_, _) => Task.CompletedTask,
        };

    [Theory]
    [InlineData("F7", "F7", KeyModifiers.None)]
    [InlineData("Ctrl+Shift+G", "G", KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData("Alt+Left", "Left", KeyModifiers.Alt)]
    public void ParsesKeyStrokes(string text, string key, KeyModifiers modifiers)
    {
        var stroke = KeyStroke.Parse(text);
        stroke.Key.ShouldBe(key);
        stroke.Modifiers.ShouldBe(modifiers);
        stroke.ToString().ShouldBe(text);
    }

    [Fact]
    public void ResolvesCommandsByStroke()
    {
        var registry = new CommandRegistry();
        registry.Register(Command("dc.newFolder", "F7"));

        registry.ForKey(KeyStroke.Parse("F7"))!.Id.ShouldBe("dc.newFolder");
        registry.ForKey(KeyStroke.Parse("Shift+F7")).ShouldBeNull();
    }

    [Fact]
    public void RejectsDuplicateIds()
    {
        var registry = new CommandRegistry();
        registry.Register(Command("dc.open"));

        Should.Throw<InvalidOperationException>(() => registry.Register(Command("dc.open")));
    }

    [Fact]
    public void LaterBindingWinsSoUserConfigCanOverrideABuiltIn()
    {
        var registry = new CommandRegistry();
        registry.Register(Command("dc.newFolder", "F7"));
        registry.Register(Command("tool.custom", "F7"));

        registry.ForKey(KeyStroke.Parse("F7"))!.Id.ShouldBe("tool.custom");
    }

    [Fact]
    public void LegendIsIndexedByFunctionKeyAndFiltersByHeldModifiers()
    {
        var registry = new CommandRegistry();
        registry.Register(Command("dc.rename", "F2"));
        registry.Register(Command("dc.newFolder", "F7"));
        registry.Register(Command("dc.destroy", "Shift+F8"));

        var plain = registry.Legend(KeyModifiers.None);
        plain[1]!.Id.ShouldBe("dc.rename");
        plain[6]!.Id.ShouldBe("dc.newFolder");
        plain[7].ShouldBeNull();

        // Holding Shift re-labels the row rather than showing a second, fixed one.
        var shifted = registry.Legend(KeyModifiers.Shift);
        shifted[7]!.Id.ShouldBe("dc.destroy");
        shifted[1].ShouldBeNull();
    }
}

/// <summary>Where a command applies at all: capabilities of the place, and writing into from: listings.</summary>
public class CommandAppliesAtTests
{
    private sealed class Provider(string scheme, ProviderCapabilities capabilities) : IFileSystemProvider
    {
        public string Scheme => scheme;
        public ProviderCapabilities Capabilities => capabilities;
        public DanceCommander.Core.Columns.ColumnSet DefaultColumns => DanceCommander.Core.Columns.ColumnSet.Standard;
        public IAsyncEnumerable<FsEntry> ListAsync(FsUri directory, CancellationToken ct) => throw new NotSupportedException();
        public Task<FsEntry?> StatAsync(FsUri path, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(FsUri file, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenWriteAsync(FsUri file, long? length, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(FsUri directory, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(FsUri path, bool recursive, CancellationToken ct) => throw new NotSupportedException();
        public Task MoveAsync(FsUri from, FsUri to, CancellationToken ct) => throw new NotSupportedException();
        public DanceCommander.Core.Watching.IDirectoryWatcher? Watch(FsUri directory) => null;
    }

    private sealed class Services(IProviderRegistry registry) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IProviderRegistry) ? registry : null;
    }

    private static readonly IProviderRegistry Registry = new ProviderRegistry([
        new Provider("file", ProviderCapabilities.Read | ProviderCapabilities.Write | ProviderCapabilities.Rename
                             | ProviderCapabilities.Delete | ProviderCapabilities.CreateDirectory),
        new Provider("from", ProviderCapabilities.Read),
    ]);

    private static readonly CommandDefinition Rename = new()
    {
        Id = "rename", Title = "Rename", Needs = ProviderCapabilities.Rename,
        ExecuteAsync = (_, _) => Task.CompletedTask,
    };

    private static readonly CommandDefinition NewFolder = new()
    {
        Id = "mkdir", Title = "New folder", Subject = CommandSubject.Location,
        Needs = ProviderCapabilities.CreateDirectory, WritesHere = true,
        ExecuteAsync = (_, _) => Task.CompletedTask,
    };

    private static readonly CommandDefinition Copy = new()
    {
        Id = "copy", Title = "Copy", ReadsContents = true,
        ExecuteAsync = (_, _) => Task.CompletedTask,
    };

    private static FakeContext At(string address, FsEntry? focused = null) =>
        new() { Directory = FsUri.Parse(address), Focused = focused, ServiceProvider = new Services(Registry) };

    [Fact]
    public void AReadOnlyScriptLocationOffersNoRenameAndNoNewFolder()
    {
        var context = At("from:C:/src?gitlab/4711");

        Rename.AppliesAt(context).ShouldBeFalse();
        NewFolder.AppliesAt(context).ShouldBeFalse();
    }

    [Fact]
    public void TheComputerPageOffersNothingThatChangesWhatItLists()
    {
        // The row is Documents itself; the page is a way in, not somewhere to delete it from.
        var documents = new FsEntry(FsUri.Local(@"C:\Users\me\Documents"), "Documents", IsDirectory: true, 0, null);
        var context = At("about:computer", documents);

        Rename.AppliesAt(context).ShouldBeFalse();
        NewFolder.AppliesAt(context).ShouldBeFalse();
        Copy.AppliesAt(context).ShouldBeFalse(); // F5 on "C:" would copy the drive
        Copy.AppliesAt(At(@"C:/Users/me", documents)).ShouldBeTrue();
    }

    [Fact]
    public void TheAboutPageLetsNothingRenameDeleteOrMoveTheProgramsOwnFiles()
    {
        // Its rows are the real LICENSE.txt and folders beside the executable: open them, no more.
        var license = new FsEntry(FsUri.Local(@"C:\Users\me\AppData\Local\DanceCommander\LICENSE.txt"), "LICENSE.txt", IsDirectory: false, 1070, null);
        var context = At("about:app", license);
        var delete = new CommandDefinition
        {
            Id = "delete", Title = "Delete", Needs = ProviderCapabilities.Delete, DeletesEntries = true,
            ExecuteAsync = (_, _) => Task.CompletedTask,
        };

        Rename.AppliesAt(context).ShouldBeFalse();
        delete.AppliesAt(context).ShouldBeFalse();
        Copy.AppliesAt(context).ShouldBeFalse();
        NewFolder.AppliesAt(context).ShouldBeFalse();
    }

    [Fact]
    public void AFlatViewStillWritesIntoTheFolderItIsAViewOf()
    {
        var context = At("flat:C:/src");

        NewFolder.AppliesAt(context).ShouldBeTrue();
    }

    [Fact]
    public void RenameAsksTheProviderOfTheEntryNotOfTheListing()
    {
        // A worktree listing is a from: address, but its rows are real folders on disk.
        var worktree = new FsEntry(FsUri.Local(@"C:\src\app-2"), "app-2", IsDirectory: true, 0, null);
        var context = At("from:C:/src?worktrees", worktree);

        Rename.AppliesAt(context).ShouldBeTrue();
        NewFolder.AppliesAt(context).ShouldBeFalse(); // nowhere to put it: the listing is not a folder
    }
}
