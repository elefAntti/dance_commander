using DanceCommander.Core.Commands;
using DanceCommander.Core.Config;
using Shouldly;

namespace DanceCommander.Tests;

public class CommandPaletteTests
{
    private static CommandDefinition Command(string id, string title, string? group = "File", string? binding = null) =>
        new()
        {
            Id = id,
            Title = title,
            Group = group,
            Binding = binding is null ? null : KeyStroke.Parse(binding),
            ExecuteAsync = (_, _) => Task.CompletedTask,
        };

    private static readonly CommandDefinition[] Catalogue =
    [
        Command("dc.newFolder", "New folder", binding: "F7"),
        Command("dc.rename", "Rename", binding: "F2"),
        Command("dc.copyPath", "Copy path", "Clipboard"),
        Command("dc.copyHere", "Copy this folder's path", "Clipboard"),
        Command("dc.git.status", "Git status", "Git"),
        Command("dc.git.blame", "Blame", "Git"),
        Command("dc.dismiss", "Dismiss", group: null),
    ];

    private static IReadOnlyList<string> Titles(IEnumerable<CommandDefinition> commands) =>
        commands.Select(c => c.Title).ToList();

    [Fact]
    public void OffersEverythingApplicableWhenNothingIsTyped()
    {
        var offered = new CommandPalette().Offer(Catalogue, string.Empty);

        offered.Count.ShouldBe(6);
        // A command with no group is a key, not a menu item -- the context menu draws the same line.
        Titles(offered).ShouldNotContain("Dismiss");
    }

    [Fact]
    public void RestingOrderIsTheGroupsTheMenusAlreadyUse()
    {
        // Nothing typed says nothing about what matters, so the list is there to be read.
        Titles(new CommandPalette().Offer(Catalogue, string.Empty)).ShouldBe([
            "Copy path", "Copy this folder's path",
            "New folder", "Rename",
            "Blame", "Git status",
        ]);
    }

    [Fact]
    public void MatchesOnASubstringOfTheTitle()
    {
        var offered = new CommandPalette().Offer(Catalogue, "folder");

        Titles(offered).ShouldBe(["New folder", "Copy this folder's path"]);
    }

    [Fact]
    public void RequiresEveryTermButNotTheirOrder()
    {
        var palette = new CommandPalette();

        Titles(palette.Offer(Catalogue, "path copy")).ShouldBe(["Copy path", "Copy this folder's path"]);
        palette.Offer(Catalogue, "copy nonsense").ShouldBeEmpty();
    }

    [Fact]
    public void FindsACommandByTheGroupItIsIn()
    {
        // "git" is how you look for all of them, and it is not in every title.
        Titles(new CommandPalette().Offer(Catalogue, "git")).ShouldBe(["Git status", "Blame"]);
    }

    [Fact]
    public void RanksATitleAheadOfAGroupAndAWordAheadOfTheMiddleOfOne()
    {
        var commands = new[]
        {
            Command("a", "Something inside", "Inspect"),   // group match only
            Command("b", "Reinspect the thing"),           // mid-word
            Command("c", "Inspect archive"),               // starts with
            Command("d", "Deep inspect"),                  // word start
        };

        Titles(new CommandPalette().Offer(commands, "inspect"))
            .ShouldBe(["Inspect archive", "Deep inspect", "Reinspect the thing", "Something inside"]);
    }

    [Fact]
    public void RecentlyUsedCommandsRiseAmongEqualMatches()
    {
        var palette = new CommandPalette();
        Titles(palette.Offer(Catalogue, "copy"))[0].ShouldBe("Copy path");

        palette.Remember(Catalogue.Single(c => c.Id == "dc.copyHere"));

        Titles(palette.Offer(Catalogue, "copy"))[0].ShouldBe("Copy this folder's path");
    }

    [Fact]
    public void ACommandUsuallyChosenLeadsMatchesOfTheSameKind()
    {
        // "te" starts a word in all three. With no history, the title that starts with it leads;
        // once the terminal has been chosen, it does, and still does in the next session.
        var commands = new[]
        {
            Command("dc.textSize.90", "Text size: 90%", "Settings"),
            Command("open-in-terminal", "Open in Windows Terminal", "Terminal"),
            Command("dc.deletePermanently", "Delete permanently"),
        };
        var usage = new CommandUsage();

        Titles(new CommandPalette(usage).Offer(commands, "te"))
            .ShouldBe(["Text size: 90%", "Open in Windows Terminal", "Delete permanently"]);

        new CommandPalette(usage).Remember(commands[1]);

        Titles(new CommandPalette(usage).Offer(commands, "te"))
            .ShouldBe(["Open in Windows Terminal", "Text size: 90%", "Delete permanently"]);
    }

    [Fact]
    public void UsageNeverLiftsAWeakerKindOfMatch()
    {
        // Chosen often, but "te" is only inside "Delete": a word that starts with it still leads.
        var commands = new[]
        {
            Command("dc.deletePermanently", "Delete permanently"),
            Command("dc.textSize.90", "Text size: 90%", "Settings"),
        };
        var palette = new CommandPalette();
        for (var i = 0; i < 5; i++)
            palette.Remember(commands[0]);

        Titles(palette.Offer(commands, "te"))[0].ShouldBe("Text size: 90%");
    }

    [Fact]
    public void TheGroupMatchesOnlyFromTheStartOfAWord()
    {
        // "te" inside "Navigate" offered Back and every Go to.
        var commands = new[] { Command("dc.back", "Back", "Navigate"), Command("dc.git.status", "Status", "Git") };

        new CommandPalette().Offer(commands, "te").ShouldBeEmpty();
        Titles(new CommandPalette().Offer(commands, "nav")).ShouldBe(["Back"]);
    }

    private sealed class FailingOnce : IConfigStore<CommandUsageFile>
    {
        public int Failures { get; set; } = 1;

        public CommandUsageFile Current { get; private set; } = new();

        public event EventHandler<CommandUsageFile>? Changed { add { } remove { } }

        public Task SaveAsync(CommandUsageFile value, CancellationToken ct = default)
        {
            if (Failures-- > 0)
                throw new IOException("The disk is full.");
            Current = value;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void AUsageSaveThatFailedAtExitIsTriedAgainAndThrowsNothing()
    {
        // Cleared before the save, a failed one was never retried; thrown out of Dispose, it
        // stopped every later save at exit.
        var store = new FailingOnce();
        var usage = new CommandUsage(store);
        usage.Add("dc.copy");

        Should.NotThrow(usage.Dispose);
        store.Current.Taken.ShouldBeEmpty();

        Should.NotThrow(usage.Dispose); // the second close, as the container may make, saves it
        store.Current.Taken.ShouldBe(["dc.copy"]);
    }

    [Fact]
    public void AnUnexpectedFailureAtExitIsSaidAndGoesNoFurther()
    {
        Exception? said = null;
        var before = Debouncer.ExitFailure;
        Debouncer.ExitFailure = ex => said = ex;
        try
        {
            Should.NotThrow(() => Debouncer.FlushAtExit(() => throw new ArgumentException("two keys alike")));
            said.ShouldBeOfType<ArgumentException>();
        }
        finally
        {
            Debouncer.ExitFailure = before;
        }
    }

    [Fact]
    public void UsageKeepsOnlyTheLatestPicks()
    {
        var usage = new CommandUsage();
        usage.Add("old");
        for (var i = 0; i < CommandUsage.Capacity; i++)
            usage.Add("new");

        usage.Count("old").ShouldBe(0);
        usage.Count("new").ShouldBe(CommandUsage.Capacity);
    }

    [Fact]
    public void RememberingTheSameCommandTwiceDoesNotDuplicateIt()
    {
        var palette = new CommandPalette();
        palette.Remember(Catalogue[0]);
        palette.Remember(Catalogue[1]);
        palette.Remember(Catalogue[0]);

        palette.Recent.ShouldBe(["dc.newFolder", "dc.rename"]);
    }

    [Fact]
    public void HistoryIsBounded()
    {
        var palette = new CommandPalette();
        for (var i = 0; i < CommandPalette.RecentLimit + 5; i++)
            palette.Remember(Command($"dc.{i}", $"Command {i}"));

        palette.Recent.Count.ShouldBe(CommandPalette.RecentLimit);
        palette.Recent[0].ShouldBe($"dc.{CommandPalette.RecentLimit + 4}");
    }

    // ---- sections (§6.18) ---------------------------------------------------------------------

    private static CommandDefinition About(string id, string title, CommandSubject subject, string group = "Git") =>
        new()
        {
            Id = id,
            Title = title,
            Group = group,
            Subject = subject,
            ExecuteAsync = (_, _) => Task.CompletedTask,
        };

    private static readonly CommandDefinition[] Mixed =
    [
        About("dc.onePane", "One pane", CommandSubject.Window, "View"),
        About("dc.git.status", "Git status", CommandSubject.Location),
        About("dc.git.diff", "Git diff", CommandSubject.Selection),
        About("dc.listing.worktrees", "Worktrees", CommandSubject.Location, "Navigate"),
        About("dc.git.blame", "Git blame", CommandSubject.Selection),
    ];

    [Fact]
    public void AtRestSectionsComeInAFixedOrder()
    {
        // Selection, then the folder, then the window — whatever order the registry holds them in.
        var sections = new CommandPalette().Sections(Mixed, string.Empty);

        sections.Select(s => s.Subject).ShouldBe(
            [CommandSubject.Selection, CommandSubject.Location, CommandSubject.Window]);
    }

    [Fact]
    public void RankingHappensInsideEachSection()
    {
        // The search's own ranking (shorter title first on a tie) within each section. Every
        // title starts with "git", so the shortest one, in the selection's section, leads.
        var sections = new CommandPalette().Sections(Mixed, "git");

        sections.Select(s => s.Subject).ShouldBe([CommandSubject.Selection, CommandSubject.Location]);
        Titles(sections[0].Commands).ShouldBe(["Git diff", "Git blame"]);
        Titles(sections[1].Commands).ShouldBe(["Git status"]);
    }

    [Fact]
    public void RecentlyUsedRisesOnlyWithinItsOwnSection()
    {
        var palette = new CommandPalette();
        palette.Remember(Mixed.Single(c => c.Title == "Worktrees"));

        var sections = palette.Sections(Mixed, string.Empty);

        sections[0].Subject.ShouldBe(CommandSubject.Selection);
        Titles(sections[1].Commands).First().ShouldBe("Worktrees");
    }

    [Fact]
    public void WithAQueryTheSectionHoldingTheBestMatchComesFirst()
    {
        // "te" starts a word in "Terminal" and only sits inside "Delete": the folder's section
        // leads, so the highlight (and Enter) is on the terminal, not on a delete.
        var commands = new[]
        {
            About("dc.deletePermanently", "Delete permanently", CommandSubject.Selection, "File"),
            About("open-in-terminal", "Open in Windows Terminal", CommandSubject.Location, "Terminal"),
        };

        var sections = new CommandPalette().Sections(commands, "te");

        sections.Select(s => s.Subject).ShouldBe([CommandSubject.Location, CommandSubject.Selection]);
        sections[0].Commands[0].Title.ShouldBe("Open in Windows Terminal");
    }

    [Fact]
    public void EmptySectionsAreLeftOut() =>
        new CommandPalette().Sections(Mixed, "pane")
            .ShouldHaveSingleItem().Subject.ShouldBe(CommandSubject.Window);
}
