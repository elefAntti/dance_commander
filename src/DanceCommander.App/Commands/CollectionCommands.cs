using DanceCommander.App.Services;
using DanceCommander.Core.Collections;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace DanceCommander.App.Commands;

/// <summary>
/// Collections (ARCHITECTURE.md §6.19): gathering files into one, and editing the lists. In a
/// collection Del edits the list and never touches a file, so those keys are commands of the page
/// (<see cref="CommandDefinition.OnlyAt"/>), taking Del and F7 there and nowhere else.
/// </summary>
public static class CollectionCommands
{
    private const CommandScope OnEntries = CommandScope.File | CommandScope.Directory | CommandScope.Multiple;

    public static void RegisterAll(ICommandRegistry registry)
    {
        registry.Register(new CommandDefinition
        {
            Id = "dc.addToCollection",
            Title = "Add to collection…",
            LegendTitle = "Collect",
            Group = "Collections",
            Scope = OnEntries,
            // No key of its own yet: Ctrl+Shift+A, which it had, is Select none everywhere else.
            // From the menu and the palette until the shelf settles which key gathers things.
            // A collection holds files and folders; the application's own pages are not either.
            CanExecute = ctx => ctx.Selection.Count > 0 && ctx.Selection.All(e => !e.Uri.IsAbout),
            ExecuteAsync = AddAsync,
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.newCollection",
            Title = "New collection",
            LegendTitle = "New",
            Group = "Collections",
            Subject = CommandSubject.Location,
            Binding = KeyStroke.Parse("F7"),
            OnlyAt = uri => uri == AboutPages.Collections,
            ExecuteAsync = NewAsync,
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.deleteCollection",
            Title = "Delete collection",
            Group = "Collections",
            Scope = OnEntries,
            Binding = KeyStroke.Parse("Delete"),
            OnlyAt = uri => uri == AboutPages.Collections,
            CanExecute = ctx => ctx.Selection.Any(e => AboutPages.CollectionName(e.Uri) is not null),
            ExecuteAsync = DeleteAsync,
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.removeFromCollection",
            Title = "Remove from collection",
            LegendTitle = "Remove",
            Group = "Collections",
            Scope = OnEntries,
            Binding = KeyStroke.Parse("Delete"),
            OnlyAt = uri => AboutPages.CollectionName(uri) is not null,
            ExecuteAsync = RemoveAsync,
        });

        // The shelf (§6.19): marks that outlive the folder they were made in.
        registry.Register(new CommandDefinition
        {
            Id = "dc.addToShelf",
            Title = "Add to shelf",
            LegendTitle = "Shelf",
            Group = "Collections",
            Scope = OnEntries,
            Binding = KeyStroke.Parse("Ctrl+B"),
            CanExecute = ctx => ctx.Selection.Count > 0 && ctx.Selection.All(e => !e.Uri.IsAbout),
            ExecuteAsync = AddToShelfAsync,
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.removeFromShelf",
            Title = "Remove from shelf",
            LegendTitle = "Remove",
            Group = "Collections",
            Scope = OnEntries,
            Binding = KeyStroke.Parse("Delete"),
            OnlyAt = uri => uri == AboutPages.Shelf,
            ExecuteAsync = RemoveFromShelfAsync,
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.saveShelf",
            Title = "Save shelf as collection…",
            LegendTitle = "Save",
            Group = "Collections",
            Subject = CommandSubject.Location,
            Binding = KeyStroke.Parse("F7"),
            OnlyAt = uri => uri == AboutPages.Shelf,
            CanExecute = ctx => ctx.Services.GetRequiredService<IShelf>().Items.Count > 0,
            ExecuteAsync = SaveShelfAsync,
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.emptyShelf",
            Title = "Empty shelf",
            Group = "Collections",
            Subject = CommandSubject.Location,
            OnlyAt = uri => uri == AboutPages.Shelf,
            CanExecute = ctx => ctx.Services.GetRequiredService<IShelf>().Items.Count > 0,
            ExecuteAsync = EmptyShelfAsync,
        });
    }

    private static Task AddToShelfAsync(ICommandContext ctx, CancellationToken ct)
    {
        var shelf = ctx.Services.GetRequiredService<IShelf>();
        var items = ctx.Selection.Select(e => e.Uri).ToList();
        var added = shelf.Add(items);
        var total = shelf.Items.Count;

        // The echo, not a notice: the shelf button already counts, and pulses as it does, so a
        // panel saying so over the listing was one more thing to dismiss per Ctrl+B.
        var text = added == 0
            ? "Already on the shelf"
            : $"Added {(added == 1 ? items[0].Name : $"{added:N0} items")} to the shelf";
        if (ctx.Services.GetService<IEcho>() is { } echo)
            echo.Say(text);
        else
            ctx.Decisions.Report(new Notice($"{text} — {total:N0} in it."));
        return Task.CompletedTask;
    }

    private static Task RemoveFromShelfAsync(ICommandContext ctx, CancellationToken ct)
    {
        var shelf = ctx.Services.GetRequiredService<IShelf>();
        var items = ctx.Selection.Select(e => e.Uri).ToList();
        shelf.Remove(items);

        var what = items.Count == 1 ? ctx.Selection[0].Name : $"{items.Count:N0} items";
        ctx.Services.GetService<IUndoStack>()?.Push(new DelegateUndo(
            $"Remove {what} from the shelf",
            "(taken off the shelf; no file was touched)",
            _ =>
            {
                shelf.Add(items);
                return Task.CompletedTask;
            }));
        return Task.CompletedTask;
    }

    /// <summary>What makes a shelf worth keeping into something kept: a collection, by name.</summary>
    private static async Task SaveShelfAsync(ICommandContext ctx, CancellationToken ct)
    {
        var shelf = ctx.Services.GetRequiredService<IShelf>();
        var collections = ctx.Services.GetRequiredService<ICollectionService>();
        var name = await ctx.Decisions.AskTextAsync(
            new TextRequest(
                $"Save the shelf's {shelf.Items.Count:N0} item{(shelf.Items.Count == 1 ? "" : "s")} as a collection",
                collections.LastUsed ?? string.Empty,
                "Save",
                Detail: "An existing collection's name adds them to it. The shelf stays as it is.",
                Validate: AboutPages.CheckName),
            ct);
        if (string.IsNullOrWhiteSpace(name))
            return;

        await Collect(ctx.Services, ctx.Decisions, shelf.Items, name.Trim(), ct);
    }

    private static Task EmptyShelfAsync(ICommandContext ctx, CancellationToken ct)
    {
        var shelf = ctx.Services.GetRequiredService<IShelf>();
        var items = shelf.Items;
        shelf.Clear();
        ctx.Services.GetService<IUndoStack>()?.Push(new DelegateUndo(
            $"Empty the shelf of {items.Count:N0} item{(items.Count == 1 ? "" : "s")}",
            "(shelf emptied; no file was touched)",
            _ =>
            {
                shelf.Add(items);
                return Task.CompletedTask;
            }));
        return Task.CompletedTask;
    }

    private static async Task AddAsync(ICommandContext ctx, CancellationToken ct)
    {
        var collections = ctx.Services.GetRequiredService<ICollectionService>();
        var names = collections.Collections.Select(c => c.Name).ToList();
        var what = ctx.Selection.Count == 1 ? ctx.Selection[0].Name : $"{ctx.Selection.Count:N0} items";

        var detail = names.Count switch
        {
            0 => "There are no collections yet; this makes one.",
            <= 8 => $"Collections: {string.Join(", ", names)}. Another name makes a new one.",
            _ => $"Collections: {string.Join(", ", names.Take(8))} and {names.Count - 8} more. Another name makes a new one.",
        };

        var name = await ctx.Decisions.AskTextAsync(
            new TextRequest(
                $"Add {what} to collection",
                collections.LastUsed ?? names.FirstOrDefault() ?? "Collection",
                "Add",
                detail,
                Validate: AboutPages.CheckName),
            ct);
        if (string.IsNullOrWhiteSpace(name))
            return;

        await Collect(ctx.Services, ctx.Decisions, ctx.Selection.Select(e => e.Uri).ToList(), name.Trim(), ct);
    }

    /// <summary>Adds and says what happened: the command and a drop onto a collection both end here.</summary>
    public static async Task Collect(
        IServiceProvider services, IDecisionHost decisions, IReadOnlyList<FsUri> items, string name, CancellationToken ct)
    {
        var collections = services.GetRequiredService<ICollectionService>();
        var added = await collections.AddAsync(name, items, ct);
        var shown = collections.Find(name)?.Name ?? name;

        decisions.Report(new Notice(
            added == 0 ? (items.Count == 1 ? $"Already in {shown}." : $"All already in {shown}.")
            : added == items.Count ? $"Added {(added == 1 ? items[0].Name : $"{added:N0} items")} to {shown}."
            : $"Added {added:N0} to {shown}; {items.Count - added:N0} were already there."));
    }

    private static async Task NewAsync(ICommandContext ctx, CancellationToken ct)
    {
        var collections = ctx.Services.GetRequiredService<ICollectionService>();
        var name = await ctx.Decisions.AskTextAsync(
            new TextRequest(
                "New collection",
                string.Empty,
                "Create",
                Validate: text => AboutPages.CheckName(text)
                    ?? (collections.Find(text) is not null ? $"There is already a collection called {text.Trim()}." : null)),
            ct);
        if (string.IsNullOrWhiteSpace(name))
            return;

        await collections.CreateAsync(name.Trim(), ct);
        await ctx.NavigateAsync(AboutPages.Collections, name.Trim());
    }

    /// <summary>
    /// Deleting a collection loses the list, not the files; it asks when there is a list to lose,
    /// because it cannot be undone and gathering it again may be an afternoon's work.
    /// </summary>
    private static async Task DeleteAsync(ICommandContext ctx, CancellationToken ct)
    {
        var collections = ctx.Services.GetRequiredService<ICollectionService>();
        var names = ctx.Selection.Select(e => AboutPages.CollectionName(e.Uri)).OfType<string>().ToList();
        var items = names.Sum(n => collections.Find(n)?.Items.Count ?? 0);

        if (items > 0)
        {
            var what = names.Count == 1 ? names[0] : $"{names.Count:N0} collections";
            var answer = await ctx.Decisions.AskAsync(
                new ChoiceRequest(
                    $"Delete {what}?",
                    [
                        new DecisionOption("cancel", "Cancel", IsDefault: true, IsCancel: true),
                        new DecisionOption("delete", "Delete"),
                    ],
                    $"The list of {items:N0} {(items == 1 ? "item" : "items")} goes; the files themselves are not touched."),
                ct);
            if (answer != "delete")
                return;
        }

        await collections.DeleteAsync(names, ct);
    }

    /// <summary>Takes the selection out of the list — the files stay where they are — and Ctrl+Z puts it back.</summary>
    private static async Task RemoveAsync(ICommandContext ctx, CancellationToken ct)
    {
        if (AboutPages.CollectionName(ctx.Directory) is not { } name || ctx.Selection.Count == 0)
            return;

        var collections = ctx.Services.GetRequiredService<ICollectionService>();
        var items = ctx.Selection.Select(e => e.Uri).ToList();
        await collections.RemoveAsync(name, items, ct);

        var what = items.Count == 1 ? ctx.Selection[0].Name : $"{items.Count:N0} items";
        var shown = collections.Find(name)?.Name ?? name;
        ctx.Services.GetService<IUndoStack>()?.Push(new DelegateUndo(
            $"Remove {what} from {shown}",
            $"(taken out of the collection {shown}; no file was touched)",
            token => collections.AddAsync(name, items, token)));
    }
}
