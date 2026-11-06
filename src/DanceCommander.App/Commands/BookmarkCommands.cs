using DanceCommander.Core.Commands;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Navigation;
using DanceCommander.Core.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace DanceCommander.App.Commands;

/// <summary>
/// The bookmarks page (ARCHITECTURE.md §6.19). Its rows are records that point somewhere, so F2
/// renames the label, Del removes the bookmark and F6 moves it to another group — never the folder
/// it points to, which the page's general commands would otherwise reach.
/// </summary>
public static class BookmarkCommands
{
    public static void RegisterAll(ICommandRegistry registry)
    {
        registry.Register(new CommandDefinition
        {
            Id = "dc.renameBookmark",
            Title = "Rename bookmark",
            LegendTitle = "Rename",
            Group = "Bookmarks",
            Scope = CommandScope.File | CommandScope.Directory,
            Binding = KeyStroke.Parse("F2"),
            OnlyAt = AboutPages.IsBookmarks,
            CanExecute = ctx => ctx.Focused is not null,
            ExecuteAsync = RenameAsync,
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.moveBookmarkToGroup",
            Title = "Move to group…",
            LegendTitle = "Group",
            Group = "Bookmarks",
            Scope = CommandScope.File | CommandScope.Directory | CommandScope.Multiple,
            Binding = KeyStroke.Parse("F6"),
            OnlyAt = AboutPages.IsBookmarks,
            CanExecute = ctx => ctx.Selection.Any(e => !e.Uri.IsAbout),
            ExecuteAsync = MoveToGroupAsync,
        });

        // History is a log rather than a list you keep, but Del means the same there: take this out.
        registry.Register(new CommandDefinition
        {
            Id = "dc.forgetVisit",
            Title = "Remove from history",
            LegendTitle = "Forget",
            Group = "Navigate",
            Scope = CommandScope.File | CommandScope.Directory | CommandScope.Multiple,
            Binding = KeyStroke.Parse("Delete"),
            OnlyAt = uri => uri == AboutPages.History,
            ExecuteAsync = ForgetAsync,
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.removeBookmark",
            Title = "Remove bookmark",
            LegendTitle = "Remove",
            Group = "Bookmarks",
            Scope = CommandScope.File | CommandScope.Directory | CommandScope.Multiple,
            Binding = KeyStroke.Parse("Delete"),
            OnlyAt = AboutPages.IsBookmarks,
            ExecuteAsync = RemoveAsync,
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.importQuickAccess",
            Subject = CommandSubject.Window,
            Title = "Import bookmarks from Explorer's Quick access",
            Group = "Bookmarks",
            ExecuteAsync = ImportQuickAccessAsync,
        });
    }

    /// <summary>
    /// What a first run did, again: the folders pinned in Explorer that no bookmark goes to yet,
    /// into the group Quick access (SETTINGS_PLAN.md §8). Never removes or renames a bookmark;
    /// Ctrl+Z takes the new ones out.
    /// </summary>
    private static async Task ImportQuickAccessAsync(ICommandContext ctx, CancellationToken ct)
    {
        if (ctx.Services.GetService<IQuickAccess>() is not { } quickAccess)
            return;

        var bookmarks = ctx.Services.GetRequiredService<IBookmarkService>();
        var pinned = await quickAccess.PinnedAsync(ct);
        var added = QuickAccessBookmarks.NotYetBookmarked(pinned, bookmarks.Bookmarks);
        if (added.Count == 0)
        {
            ctx.Decisions.Report(new Notice(pinned.Count == 0
                ? "Explorer has no pinned folders to import."
                : "Every folder pinned in Explorer is bookmarked already."));
            return;
        }

        // The undo is pushed for whatever was added, even when a cancel stopped it part way.
        var done = new List<Bookmark>();
        try
        {
            foreach (var bookmark in added)
            {
                if (!FsUri.TryParse(bookmark.Uri, out var uri, out _))
                    continue;
                await bookmarks.AddAsync(bookmark.Name, uri, bookmark.Group, ct);
                done.Add(bookmark);
            }
        }
        finally
        {
            if (done.Count > 0)
                PushUndo(ctx, bookmarks, done);
        }

        ctx.Decisions.Report(new Notice(
            $"Bookmarked {(done.Count == 1 ? done[0].Name : $"{done.Count:N0} folders")} pinned in Explorer, in the group {QuickAccessBookmarks.Group}."));
    }

    /// <summary>
    /// Takes out exactly what the import added. Only a bookmark still as it was: one renamed,
    /// regrouped or replaced since then under the same name is the user's now, and stays.
    /// </summary>
    private static void PushUndo(ICommandContext ctx, IBookmarkService bookmarks, IReadOnlyList<Bookmark> added)
    {
        var what = added.Count == 1 ? $"the bookmark {added[0].Name}" : $"{added.Count:N0} bookmarks";
        ctx.Services.GetService<IUndoStack>()?.Push(new DelegateUndo(
            $"Import {what} from Quick access",
            "(bookmarks added; no file was touched)",
            async token =>
            {
                foreach (var bookmark in added)
                {
                    if (bookmarks.Find(bookmark.Name) is { } now && now.Uri == bookmark.Uri && now.Group == bookmark.Group)
                        await bookmarks.RemoveAsync(now.Name, token);
                }
            }));
    }

    private static async Task RenameAsync(ICommandContext ctx, CancellationToken ct)
    {
        if (ctx.Focused is not { } row)
            return;

        var bookmarks = ctx.Services.GetRequiredService<IBookmarkService>();

        if (AboutPages.BookmarkGroupName(row.Uri) is { } group)
        {
            var renamed = await ctx.Decisions.AskTextAsync(
                new TextRequest($"Rename the group {group}", group, "Rename", Validate: CheckGroup), ct);
            if (string.IsNullOrWhiteSpace(renamed) || renamed.Trim() == group)
                return;

            await bookmarks.RenameGroupAsync(group, renamed.Trim(), ct);
            await ctx.NavigateAsync(ctx.Directory, renamed.Trim());
            return;
        }

        var name = await ctx.Decisions.AskTextAsync(
            new TextRequest(
                $"Rename the bookmark {row.Name}",
                row.Name,
                "Rename",
                Detail: $"Goes to {row.Uri.Display}. Renaming the bookmark leaves the place as it is.",
                Validate: text => string.IsNullOrWhiteSpace(text) ? "A bookmark needs a name."
                    : bookmarks.Find(text.Trim()) is { } other && !string.Equals(other.Name, row.Name, StringComparison.OrdinalIgnoreCase)
                        ? $"There is already a bookmark called {other.Name}."
                        : null),
            ct);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == row.Name)
            return;

        if (await bookmarks.RenameAsync(row.Name, name.Trim(), ct))
            await ctx.NavigateAsync(ctx.Directory, name.Trim());
    }

    private static async Task MoveToGroupAsync(ICommandContext ctx, CancellationToken ct)
    {
        var names = ctx.Selection.Where(e => !e.Uri.IsAbout).Select(e => e.Name).ToList();
        if (names.Count == 0)
            return;

        var bookmarks = ctx.Services.GetRequiredService<IBookmarkService>();
        var groups = bookmarks.Bookmarks.Select(b => b.Group).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var what = names.Count == 1 ? names[0] : $"{names.Count:N0} bookmarks";

        var group = await ctx.Decisions.AskTextAsync(
            new TextRequest(
                $"Move {what} to group",
                AboutPages.BookmarkGroupName(ctx.Directory) ?? groups.FirstOrDefault() ?? string.Empty,
                "Move",
                Detail: (groups.Count == 0 ? "There are no groups yet; a name makes one." : $"Groups: {string.Join(", ", groups)}.")
                    + " Leave it empty to take them out of any group.",
                Validate: text => string.IsNullOrWhiteSpace(text) ? null : CheckGroup(text)),
            ct);
        if (group is null)
            return;

        await bookmarks.SetGroupAsync(names, group, ct);
    }

    /// <summary>
    /// Removes the selected bookmarks — the places stay as they are — and a group's row removes the
    /// group's bookmarks, after asking. Ctrl+Z puts them back, groups and all.
    /// </summary>
    private static async Task RemoveAsync(ICommandContext ctx, CancellationToken ct)
    {
        var bookmarks = ctx.Services.GetRequiredService<IBookmarkService>();
        var groups = ctx.Selection.Select(e => AboutPages.BookmarkGroupName(e.Uri)).OfType<string>().ToList();
        var removed = bookmarks.Bookmarks
            .Where(b => ctx.Selection.Any(e => !e.Uri.IsAbout && string.Equals(e.Name, b.Name, StringComparison.OrdinalIgnoreCase))
                || groups.Contains(b.Group ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (removed.Count == 0)
            return;

        if (groups.Count > 0)
        {
            var answer = await ctx.Decisions.AskAsync(
                new ChoiceRequest(
                    groups.Count == 1 ? $"Remove the group {groups[0]}?" : $"Remove {groups.Count:N0} groups?",
                    [
                        new DecisionOption("cancel", "Cancel", IsDefault: true, IsCancel: true),
                        new DecisionOption("remove", "Remove"),
                    ],
                    $"{removed.Count:N0} {(removed.Count == 1 ? "bookmark goes" : "bookmarks go")}; the places themselves are not touched. Ctrl+Z brings them back."),
                ct);
            if (answer != "remove")
                return;
        }

        foreach (var bookmark in removed)
            await bookmarks.RemoveAsync(bookmark.Name, ct);

        var what = removed.Count == 1 ? $"the bookmark {removed[0].Name}" : $"{removed.Count:N0} bookmarks";
        ctx.Services.GetService<IUndoStack>()?.Push(new DelegateUndo(
            $"Remove {what}",
            "(bookmarks removed; no file was touched)",
            async token =>
            {
                foreach (var bookmark in removed)
                {
                    if (FsUri.TryParse(bookmark.Uri, out var uri, out _))
                        await bookmarks.AddAsync(bookmark.Name, uri, bookmark.Group, token);
                }
            }));
    }

    /// <summary>Takes the selected places out of the history; Ctrl+Z puts them back where they stood.</summary>
    private static Task ForgetAsync(ICommandContext ctx, CancellationToken ct)
    {
        var history = ctx.Services.GetRequiredService<IHistoryService>();
        var forgotten = history.Forget(ctx.Selection.Select(e => e.Uri).ToList());
        if (forgotten.Count == 0)
            return Task.CompletedTask;

        var what = forgotten.Count == 1 ? forgotten[0].Uri.Name : $"{forgotten.Count:N0} places";
        ctx.Services.GetService<IUndoStack>()?.Push(new DelegateUndo(
            $"Remove {what} from the history",
            "(history edited; no file was touched)",
            _ =>
            {
                history.Restore(forgotten);
                return Task.CompletedTask;
            }));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Bookmarks each place dropped on the page, into the group the page shows, named after the
    /// place — with a number when that name is taken, since adding under a taken name replaces.
    /// </summary>
    public static async Task Bookmark(
        IServiceProvider services, IDecisionHost decisions, IReadOnlyList<FsUri> places, string? group, CancellationToken ct)
    {
        var bookmarks = services.GetRequiredService<IBookmarkService>();
        foreach (var place in places)
        {
            var name = place.Name;
            for (var n = 2; bookmarks.Find(name) is not null; n++)
                name = $"{place.Name} ({n})";
            await bookmarks.AddAsync(name, place, group, ct);
        }

        decisions.Report(new Notice(places.Count == 1
            ? $"Bookmarked {places[0].Name}."
            : $"Bookmarked {places.Count:N0} places."));
    }

    /// <summary>A group is part of an address (about:bookmarks/&lt;group&gt;), so it may not contain its separators.</summary>
    private static string? CheckGroup(string text) =>
        text.IndexOfAny(['/', '\\', '?', '!', ':']) >= 0 ? @"A group name cannot contain / \ ? ! or :." : null;
}
