using DanceCommander.Core.Commands;
using DanceCommander.Core.Fs;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>Which commands go in which context menu (§6.18).</summary>
public class ContextMenuPlanTests
{
    private static CommandDefinition Command(
        string id, string title, string group, CommandSubject subject = CommandSubject.Selection,
        CommandScope scope = CommandScope.Any, Func<ICommandContext, bool>? canExecute = null) => new()
    {
        Id = id,
        Title = title,
        Group = group,
        Subject = subject,
        Scope = scope,
        CanExecute = canExecute,
        ExecuteAsync = (_, _) => Task.CompletedTask,
    };

    private const CommandScope OnRows = CommandScope.File | CommandScope.Directory | CommandScope.Multiple;

    /// <summary>A registry shaped like the real one, as far as the menus are concerned.</summary>
    private static CommandRegistry Registry()
    {
        var registry = new CommandRegistry();
        registry.Register(Command("dc.open", "Open", "Navigate"));
        registry.Register(Command("dc.openInNewTab", "Open in new tab", "Tabs",
            canExecute: c => c.Focused is { IsDirectory: true }));
        registry.Register(Command("dc.preview", "Preview", "View",
            canExecute: c => c.Focused is { IsDirectory: false }));
        registry.Register(Command("dc.rename", "Rename", "File", scope: OnRows));
        registry.Register(Command("dc.delete", "Delete", "File", scope: OnRows));
        registry.Register(Command("dc.copyPath", "Copy path", "Clipboard", scope: OnRows));
        registry.Register(Command("dc.newFolder", "New folder", "File", CommandSubject.Location));
        registry.Register(Command("dc.paste", "Paste", "Clipboard", CommandSubject.Location));
        registry.Register(Command("dc.listing.worktrees", "Worktrees", "Navigate", CommandSubject.Location));
        registry.Register(Command("dc.onePane", "One pane", "View", CommandSubject.Window));
        return registry;
    }

    private static IEnumerable<string> Titles(IEnumerable<MenuEntry> entries) =>
        entries.OfType<MenuCommand>().Select(c => c.Command.Title);

    private static readonly FakeContext OnAFile = new() { Selection = [FakeContext.Entry("a.txt")] };

    private static readonly FakeContext OnAFolder = new() { Selection = [FakeContext.Entry("src", directory: true)] };

    [Fact]
    public void EmptySpaceOffersTheFolderEvenWithSomethingSelected()
    {
        // The bug: a right-click that missed every row built the selection's menu, so Worktrees
        // could not be reached with the mouse while anything was selected.
        var menu = ContextMenuPlan.Build(Registry(), MenuTarget.Location, OnAFile);

        menu[0].ShouldBeOfType<MenuHeader>();
        Titles(menu).ShouldBe(["New folder", "Paste", "Worktrees"]);
    }

    [Fact]
    public void ARowMenuEndsWithTheFoldersOwnMenu()
    {
        var menu = ContextMenuPlan.Build(Registry(), MenuTarget.Selection, OnAFile);

        var folder = menu[^1].ShouldBeOfType<MenuFolder>();
        Titles(folder.Items).ShouldContain("Worktrees");
        Titles(menu).ShouldNotContain("Worktrees"); // one level down, not mixed in with the row's
    }

    [Fact]
    public void OpeningLeadsInOneBlockAndOffersOnlyWhatWorks()
    {
        var file = ContextMenuPlan.Build(Registry(), MenuTarget.Selection, OnAFile);
        var folder = ContextMenuPlan.Build(Registry(), MenuTarget.Selection, OnAFolder);

        // A preview for a file, a new tab for a folder — never a menu entry that does nothing.
        Titles(file).Take(2).ShouldBe(["Open", "Preview"]);
        Titles(folder).Take(2).ShouldBe(["Open", "Open in new tab"]);
        file.OfType<MenuCommand>().First().IsPrimary.ShouldBeTrue();
        file[2].ShouldBeOfType<MenuSeparator>();
    }

    [Fact]
    public void TheParentRowIsAboutTheParentAndNothingElse()
    {
        var parent = new FakeContext { Selection = [new FsEntry(FsUri.Local(@"C:\"), "..", true, 0, null)] };

        var menu = ContextMenuPlan.Build(Registry(), MenuTarget.Parent, parent);

        // No Rename or Delete on a folder the listing is not even showing.
        Titles(menu).ShouldBe(["Open", "Open in new tab", "Copy path"]);
    }

    [Fact]
    public void WindowCommandsAreNeverInAContextMenu()
    {
        foreach (var target in Enum.GetValues<MenuTarget>())
        {
            var menu = ContextMenuPlan.Build(Registry(), target, OnAFile);
            var all = Titles(menu).Concat(menu.OfType<MenuFolder>().SelectMany(f => Titles(f.Items)));
            all.ShouldNotContain("One pane");
        }
    }

    [Fact]
    public void GroupsAreSeparatedAndNeverDoubled()
    {
        var menu = ContextMenuPlan.Build(Registry(), MenuTarget.Selection, OnAFile);

        for (var i = 1; i < menu.Count; i++)
            (menu[i] is MenuSeparator && menu[i - 1] is MenuSeparator).ShouldBeFalse();
        menu[0].ShouldNotBeOfType<MenuSeparator>();
    }
}
