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
/// Listings from tools.json get a command (§6.12, §6.16), so they are in the menus and the palette
/// and not only reachable by typing their address. They are loaded after the shell has registered
/// its own, which is how "Deleted files" went missing from every menu.
/// </summary>
public class ListingCommandTests
{
    private static readonly FsUri Root = FsUri.Local(@"C:\work\orbit");

    private sealed class Store(ToolsFile file) : IConfigStore<ToolsFile>
    {
        public ToolsFile Current { get; set; } = file;

        public event EventHandler<ToolsFile>? Changed;

        public void Edit(ToolsFile next)
        {
            Current = next;
            Changed?.Invoke(this, next);
        }

        public Task SaveAsync(ToolsFile value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static readonly ListingDefinition GitGone = new()
    {
        Id = "git-gone",
        Title = "Deleted files",
        Exec = "pwsh",
        Args = ["-NoProfile", "-File", "git-history.ps1", "${repoRoot}"],
        Script = true,
        InRepository = true,
    };

    private static (CommandRegistry Registry, Store Store) Load(params ListingDefinition[] listings)
    {
        var registry = new CommandRegistry();
        var store = new Store(new ToolsFile { Listings = [.. listings] });
        new ToolCatalog(
                store, registry,
                new ToolRunner(new Core.Operations.ActivityLog(), new JobService(), NullLogger<ToolRunner>.Instance),
                new CommandListingCatalog(), NullLogger<ToolCatalog>.Instance)
            .Reload();
        return (registry, store);
    }

    [Fact]
    public async Task AListingFromTheFileIsACommandThatGoesToItsAddress()
    {
        var command = Load(GitGone).Registry.Find("dc.listing.git-gone").ShouldNotBeNull();
        command.Title.ShouldBe("Deleted files");

        var context = new FakeContext { Directory = Root, Repository = new RepositoryInfo(Root, "main", null) };
        command.IsApplicable(context).ShouldBeTrue();
        await command.ExecuteAsync(context, CancellationToken.None);

        context.Navigated.ShouldHaveSingleItem().ShouldBe(FsUri.View("from", Root, "git-gone"));
    }

    [Fact]
    public void AListingForRepositoriesIsOfferedOnlyInOne()
    {
        var command = Load(GitGone).Registry.Find("dc.listing.git-gone").ShouldNotBeNull();

        command.IsApplicable(new FakeContext { Directory = Root, Repository = null }).ShouldBeFalse();
    }

    [Fact]
    public void EditingTheFileAddsAndRemovesTheCommand()
    {
        var (registry, store) = Load();
        registry.Find("dc.listing.git-gone").ShouldBeNull();

        store.Edit(new ToolsFile { Listings = [GitGone] });
        registry.Find("dc.listing.git-gone").ShouldNotBeNull();

        // And again, which would throw on a second registration if the first were not taken out.
        store.Edit(new ToolsFile { Listings = [GitGone] });
        registry.Find("dc.listing.git-gone").ShouldNotBeNull();

        store.Edit(new ToolsFile());
        registry.Find("dc.listing.git-gone").ShouldBeNull();
    }

    [Fact]
    public void ADisabledListingHasNoCommand() =>
        Load(new ListingDefinition { Id = "off", Title = "Off", Exec = "x", Enabled = false })
            .Registry.Find("dc.listing.off").ShouldBeNull();
}
