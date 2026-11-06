using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DanceCommander.App.Commands;
using DanceCommander.App.Services;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Listing;
using DanceCommander.Core.Navigation;
using DanceCommander.Core.Startup;
using DanceCommander.Providers.Local;
using DanceCommander.Core.Operations;
using DanceCommander.Core.Transfer;
using DanceCommander.Extensibility;
using DanceCommander.Providers.Archive;
using DanceCommander.Providers.Sftp;
using Microsoft.Extensions.DependencyInjection;

namespace DanceCommander.App.ViewModels;

/// <summary>
/// The commands the shell itself registers: panes, tabs, navigation, the palette, git, listings,
/// sorting. The file-level ones that need nothing but a context live in
/// <see cref="Commands.BuiltInCommands"/>; these stay on the shell because nearly all of them act on
/// its state — which pane is active, which tab, what has focus.
/// </summary>
public sealed partial class ShellViewModel
{
    private void RegisterShellCommands(ICommandRegistry commands, ICommandListingCatalog listings)
    {
        Register("dc.switchPane", "Switch pane", "Navigate", "Tab", () =>
        {
            Activate(InactivePane);
            // Where the keyboard was in that pane: its command line, if it left from there.
            FocusRequested?.Invoke(this, "pane");
        });

        Register("dc.focusAddress", "Go to location", "Navigate", "Ctrl+L",
            () => FocusRequested?.Invoke(this, "address"), legend: "Location");

        // Not inside an archive: there is no shell there, nothing to run a command against, and
        // no way to write a result back. Offering the prompt would only be offering a dead end.
        RegisterAsync("dc.commandLine", "Run a command here", "Navigate", "Ctrl+E",
            (_, _) =>
            {
                FocusRequested?.Invoke(this, "command");
                return Task.CompletedTask;
            },
            canExecute: _ => ActiveTab.Uri is not { IsArchiveEntry: true },
            subject: CommandSubject.Location,
            legend: "Command");

        // Enter opens a file the way Explorer would, which for a .bat is a console that closes
        // before it can be read and for a .ps1 is Notepad. Run is the line that runs it, through
        // the command line: output in the pane, a job if it takes a while, Up to run it again.
        // Shift+Enter, as in Total Commander, where it runs a file and keeps what it printed.
        RegisterAsync("dc.run", "Run", "File", "Shift+Enter",
            async (ctx, ct) =>
            {
                // The shell taken once: the line is quoted and checked for it, and Ctrl+E on the
                // command line while the question below is open must not send it to another.
                var shell = ActiveTab.LocalShell;
                if (RunLineFor(ctx, shell) is not { } line)
                    await CannotRun(ctx);
                else if (await TrustedToRunAsync(ctx, ct) && StillWhereAsked(ctx))
                    await RunCommandLineAsync(line, shell);
            },
            canExecute: CanRun,
            subject: CommandSubject.Selection);

        // The same line, left on the command line for the arguments to be typed after it.
        RegisterAsync("dc.runWithArguments", "Run with arguments…", "File", null,
            async (ctx, ct) =>
            {
                if (RunLineFor(ctx, ActiveTab.LocalShell) is not { } line)
                {
                    await CannotRun(ctx);
                    return;
                }

                if (!await TrustedToRunAsync(ctx, ct) || !StillWhereAsked(ctx))
                    return;
                _commandLineDraft = line + " ";
                FocusRequested?.Invoke(this, "command");
            },
            canExecute: CanRun,
            subject: CommandSubject.Selection);

        Register("dc.newTab", "New tab", "Tabs", "Ctrl+T", () =>
        {
            // A copy of this tab, its Back and Forward included, as a browser's duplicate is: a new
            // tab that knew only where it was had to be walked back by hand (2026-10-08, Friction).
            // On the same row, too. Only when the history says where the tab is; one still on its
            // way somewhere is copied as where it is going. The keyboard goes to its address bar,
            // as a browser's does: a tab is mostly opened to go somewhere, and Escape is the list.
            var from = ActiveTab;
            if (from.History.Current is { } current && current == from.Location)
            {
                var copy = ActivePane.AddTab();
                copy.Restore(from.History.Entries.ToList(), from.History.Position, from.FocusedRow?.Name);
                ActivePane.ActiveTab = copy; // which lists it (PaneViewModel.OnActiveTabChanged)
                FocusRequested?.Invoke(this, "address");
                return;
            }

            // Where this tab is going; home if nowhere. A copy of a tab whose connection was
            // cancelled before it arrived used to be a tab with no place at all.
            var uri = from.Location
                ?? FsUri.Local(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            ActivePane.ActiveTab = ActivePane.AddTab(uri);
            FocusRequested?.Invoke(this, "address");
        });

        Register("dc.closeTab", "Close tab", "Tabs", "Ctrl+W", () =>
        {
            ActivePane.CloseActiveTab();
            FocusRequested?.Invoke(this, "grid");
        });

        // Plain PgUp/PgDn stay with the grid, where they move the cursor a page at a time as any
        // commander does. With Ctrl they step through tabs, as any browser does.
        Register("dc.nextTab", "Next tab", "Tabs", "Ctrl+PageDown", () => StepTab(1));
        Register("dc.previousTab", "Previous tab", "Tabs", "Ctrl+PageUp", () => StepTab(-1));
        // Off the legend: every application's, and the same step as Ctrl+PgDn beside it.
        Register("dc.cycleTab", "Cycle tabs", "Tabs", "Ctrl+Tab", () => StepTab(1), inLegend: false);
        Register("dc.cycleTabBack", "Cycle tabs backwards", "Tabs", "Ctrl+Shift+Tab", () => StepTab(-1), inLegend: false);

        void StepTab(int direction)
        {
            var tabs = ActivePane.Tabs;
            if (tabs.Count < 2)
                return;

            var index = tabs.IndexOf(ActivePane.ActiveTab);
            ActivePane.ActiveTab = tabs[(index + direction + tabs.Count) % tabs.Count];
            FocusRequested?.Invoke(this, "grid");
        }

        // The browsers' and VS Code's keys (PLAN.md §3, decided 2026-10-10): Ctrl+PgUp/PgDn with
        // Shift carries the tab along the strip, and Ctrl+Alt with an arrow carries it to the
        // group on that side, as VS Code's "Move Editor into Next Group". The arrow pointing away
        // from the other pane does nothing; there is nothing on that side.
        RegisterAsync("dc.moveTabLeft", "Move tab left", "Tabs", "Ctrl+Shift+PageUp",
            (_, _) => ShiftTab(-1), canExecute: _ => ActivePane.Tabs.Count > 1, legend: "Tab left");
        RegisterAsync("dc.moveTabRight", "Move tab right", "Tabs", "Ctrl+Shift+PageDown",
            (_, _) => ShiftTab(1), canExecute: _ => ActivePane.Tabs.Count > 1, legend: "Tab right");
        RegisterAsync("dc.moveTabToLeftPane", "Move tab to the left pane", "Tabs", "Ctrl+Alt+Left",
            (_, _) => MoveTabToPane(ActiveTab, Left), canExecute: _ => ReferenceEquals(ActivePane, Right),
            legend: "To left pane");
        RegisterAsync("dc.moveTabToRightPane", "Move tab to the right pane", "Tabs", "Ctrl+Alt+Right",
            (_, _) => MoveTabToPane(ActiveTab, Right), canExecute: _ => ReferenceEquals(ActivePane, Left),
            legend: "To right pane");

        // Not round the end, unlike stepping: a tab carried past the last place turning up first
        // would be lost from where the eye is following it.
        Task ShiftTab(int direction)
        {
            var tabs = ActivePane.Tabs;
            var index = tabs.IndexOf(ActivePane.ActiveTab);
            if (index + direction >= 0 && index + direction < tabs.Count)
                ActivePane.MoveTab(ActivePane.ActiveTab, direction > 0 ? index + 2 : index - 1);
            return Task.CompletedTask;
        }

        RegisterAsync("dc.back", "Back", "Navigate", "Alt+Left",
            (_, _) => ActiveTab.GoBackAsync(), canExecute: _ => ActiveTab.CanGoBack);

        RegisterAsync("dc.forward", "Forward", "Navigate", "Alt+Right",
            (_, _) => ActiveTab.GoForwardAsync(), canExecute: _ => ActiveTab.CanGoForward);

        // From the context, not the grid, so the ".." row's menu opens the parent it stands on.
        // Only on a folder or an archive: on another file there is no tab to open, and a menu
        // entry that does nothing is worse than one that is not there.
        //
        // With several folders selected, each gets a tab: the selection is what Ctrl+Enter acts on.
        RegisterAsync("dc.openInNewTab", "Open in new tab", "Tabs", "Ctrl+Enter", async (ctx, ct) =>
        {
            var pane = ActivePane;
            if (ctx.Selection.Count > 1 && ctx.Selection.All(e => e.IsDirectory))
                await ctx.OpenInNewTabsAsync(ctx.Selection.Select(e => e.Uri).ToList());
            else if (await OpenableAsync(ctx, ct) is { } location)
                await pane.OpenInNewTabAsync(location);
        },
        canExecute: MayOpen,
        subject: CommandSubject.Selection,
        legend: "In new tab");

        // Total Commander's Ctrl+Left and Ctrl+Right, said as "the other pane" rather than a side.
        // In place, as a build's output folder opens there (OpenJobResultAsync): the other pane's
        // Back undoes it. The keyboard stays here, where the next folder to look at is likely to
        // be. Unbound until a key is chosen (PLAN.md §3); the middle button can be set to run it.
        RegisterAsync("dc.openInOtherPane", "Open in other pane", "Navigate", null, async (ctx, ct) =>
        {
            // Taken before anything is awaited: the pane that is "other" is the one at the press.
            var other = InactivePane;
            if (ctx.Selection.Count > 1 && ctx.Selection.All(e => e.IsDirectory))
            {
                IsDualPane = true;
                await other.OpenInNewTabsAsync(ctx.Selection.Select(e => e.Uri).ToList());
            }
            else if (await OpenableAsync(ctx, ct) is { } location)
            {
                IsDualPane = true;
                await other.ActiveTab.NavigateAsync(location);
            }
        },
        canExecute: MayOpen,
        subject: CommandSubject.Selection,
        legend: "In other pane");

        // The name is only a hint, as for Extract: the archive is read when it is chosen.
        static bool MayOpen(ICommandContext ctx) => ctx.Focused is { } entry
            && (entry.IsDirectory || (!ZipDocuments.IsDocument(entry.Name) && BuiltInCommands.LooksPackable(entry.Name)));

        // The cursor's folder, or its archive opened as one, decided by reading it as Enter does
        // (BuiltInCommands.OpenAsync). Null, and said, for a file that turned out not to be one.
        async Task<FsUri?> OpenableAsync(ICommandContext ctx, CancellationToken ct)
        {
            if (ctx.Focused is not { } target)
                return null;
            if (target.IsDirectory)
                return target.Uri;
            if (!ZipDocuments.IsDocument(target.Name) && await ArchiveDetector.IsBrowsableAsync(target.Uri, _services, ct))
                return FsUri.ArchiveEntry(target.Uri, "/");

            ctx.Decisions.Report(new Notice($"{target.Name} could not be read as an archive."));
            return null;
        }

        RegisterAsync("dc.bookmark", "Bookmark this location", "Navigate", "Ctrl+D", async (ctx, ct) =>
        {
            if (ActiveTab.Uri is not { } uri)
                return;

            var typed = await ctx.Decisions.AskTextAsync(
                new TextRequest(
                    "Bookmark name", uri.Name, "Add",
                    Detail: "Type Group/Name to file it under a group."),
                ct);
            if (string.IsNullOrWhiteSpace(typed))
                return;

            // "Work/Build logs" files the bookmark under a Work group. Foldering with no extra UI.
            var (group, name) = SplitGroup(typed.Trim());
            await BookmarkService.AddAsync(name, uri, group, ct);
            ctx.Decisions.Report(new Notice(
                group is null ? $"Bookmarked as “{name}”." : $"Bookmarked as “{name}” under {group}."));
        },
        subject: CommandSubject.Location,
        legend: "Bookmark");

        // Selection from the keyboard. Insert and Space are the commander's marking keys — both, as
        // Total Commander has them — and the rest are the keys every other program uses for these.
        Register("dc.mark", "Mark and move down", "Selection", "Insert",
            () => ActiveTab.RequestSelection(SelectionOp.ToggleAndAdvance), legend: "Mark");
        Register("dc.markWithSpace", "Mark and move down (Space)", "Selection", "Space",
            () => ActiveTab.RequestSelection(SelectionOp.ToggleAndAdvance), legend: "Mark");
        Register("dc.selectAll", "Select all", "Selection", "Ctrl+A",
            () => ActiveTab.RequestSelection(SelectionOp.All), legend: "All", inLegend: false);
        Register("dc.selectNone", "Unmark all", "Selection", "Ctrl+Shift+A",
            () => ActiveTab.RequestSelection(SelectionOp.None), legend: "None");
        Register("dc.invertSelection", "Invert selection", "Selection", "Ctrl+I",
            () => ActiveTab.RequestSelection(SelectionOp.Invert), legend: "Invert");

        Register("dc.toggleHidden", "Show hidden entries", "View", "Ctrl+H",
            () => ActiveTab.ToggleHidden(), legend: "Hidden", inLegend: false);

        Register("dc.toggleDirectoriesFirst", "Sort folders before files", "View", binding: null,
            () => ActiveTab.ToggleDirectoriesFirst());

        // No key inside the application: Win+Shift+B reaches it from any program, which is the
        // point of a shelf, and the main window registers that one (GlobalHotKey).
        Register("dc.toggleShelfWindow", "Show or hide the shelf window", "Collections", binding: null,
            () => ShelfWindowRequested?.Invoke(this, EventArgs.Empty), inLegend: false);

        // A view of where you already are, so it starts from the pane rather than asking where.
        // Ctrl+Shift+F: Ctrl+B gathers onto the shelf (§6.19).
        RegisterAsync("dc.flatten", "Flatten this tree", "View", "Ctrl+Shift+F", async (ctx, _) =>
        {
            var tab = ActiveTab;
            if (tab.Uri is not { } here)
                return;

            // Pressing it again in a flat view goes back to the folder, so one key is in and out.
            await tab.NavigateAsync(here.IsView ? here.Inner! : FsUri.View("flat", here));
        },
        canExecute: _ => ActiveTab.Uri is not null,
        subject: CommandSubject.Location,
        readsContents: true);

        RegisterAsync("dc.search", "Search here", "View", "Ctrl+S", async (ctx, ct) =>
        {
            var tab = ActiveTab;
            if (tab.Uri is not { } here)
                return;

            // Search from the folder itself, not from a view of it, so repeating a search does not
            // stack "search over search over folder".
            var root = here.IsView ? here.Inner! : here;

            var typed = await ctx.Decisions.AskTextAsync(
                new TextRequest(
                    $"Search in {root.Name}",
                    here.IsView ? here.Parameter : string.Empty,
                    "Search",
                    Detail: "A word matches anywhere in the name. Wildcards such as *.cs work too."),
                ct).ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(typed))
                return;

            await tab.NavigateAsync(AddressSearch.From(here, typed));
        },
        canExecute: _ => ActiveTab.Uri is not null,
        subject: CommandSubject.Location,
        readsContents: true,
        legend: "Search");

        RegisterAsync("dc.preview", "Preview", "View", "F3", async (ctx, ct) =>
        {
            var tab = ActiveTab;

            // F3 again closes it: the same key in and out, so it never becomes a mode you have to
            // remember the way out of.
            if (tab.Preview is not null)
            {
                tab.Preview = null;
                // And hand the keyboard back, which Escape did and F3 did not: closing a panel
                // without saying where focus went leaves it on a collapsed element, so the next
                // key you press goes nowhere at all.
                FocusRequested?.Invoke(this, "grid");
                return;
            }

            if (tab.FocusedRow is not { IsDirectory: false } row)
                return;

            var loader = _services.GetRequiredService<IPreviewLoader>();
            tab.Preview = await loader.LoadAsync(row.Entry, ct).ConfigureAwait(true);
        },
        canExecute: _ => ActiveTab.Preview is not null
            || ActiveTab.FocusedRow is { IsDirectory: false },
        subject: CommandSubject.Selection,
        readsContents: true);

        // Escape clears whatever transient panel is showing, innermost first. Without it, captured
        // tool output could only be dismissed with the mouse — and a keyboard-first file manager
        // that needs the mouse to close its own output panel is not keyboard-first (§1.2/7).
        // Group null keeps it out of the context menu: it is a key, not a menu item.
        commands.Register(new CommandDefinition
        {
            Id = "dc.dismiss",
            Subject = CommandSubject.Window,
            Title = "Dismiss",
            Binding = KeyStroke.Parse("Escape"),
            CanExecute = _ => ActiveTab.Preview is not null
                || ActiveTab.Decision is not null
                || ActiveTab.Output is not null
                || ActiveTab.Notice is not null
                || HasFinishedJobs
                || ActiveTab.IsFiltering
                || ActiveTab.HasMarks
                || ActiveTab.IsBusy,
            ExecuteAsync = (_, _) =>
            {
                var tab = ActiveTab;
                if (tab.Preview is not null)
                    tab.Preview = null;
                else if (tab.Decision is { } decision)
                    decision.CancelCommand.Execute(null);
                else if (tab.Output is not null)
                    tab.Output = null;
                else if (tab.Notice is not null)
                    tab.Notice = null;
                else if (HasFinishedJobs)
                    // The strip is the window's, not the pane's, so it comes after the pane's own
                    // panels -- but before the filter and the marks, which are not panels at all.
                    // A running job is never dismissed by a key; its button cancels it.
                    foreach (var job in Jobs.Where(j => j.IsFinished && !j.IsLeaving && !j.IsHeld).ToList())
                        DismissJob(job);
                else if (tab.IsFiltering)
                    tab.ClearFilter();
                else if (tab.HasMarks)
                    tab.RequestSelection(SelectionOp.None);
                else
                    // Last in the chain because it is the outermost thing on screen: a panel over
                    // a loading pane is what Escape should reach for first.
                    tab.StopLoading();

                // Back to whatever the user was doing. Dismissing the output of a command while
                // the command line is still open means they are still at the command line; sending
                // focus to the grid there would make them reach for Ctrl+E again to carry on.
                FocusRequested?.Invoke(this, ActivePane.ShowCommandLine ? "command" : "grid");
                return Task.CompletedTask;
            },
        });

        // The palette. Group null for the same reason dc.dismiss has none — it is a key, not a menu
        // item — which also keeps it from offering itself as something to do once it is already up.
        // The binding is VS Code's, because that is the one everybody already has in their fingers.
        // The server's own "Open in Windows Terminal": the tool of that name is for local folders,
        // and on a server it would mean nothing. Same title, so the palette offers the one that
        // applies where you are (COMMAND_LINE_PLAN.md J).
        commands.Register(new CommandDefinition
        {
            Id = "dc.terminalOnServer",
            Subject = CommandSubject.Location,
            Title = "Open a shell on the server",
            Group = "Terminal",
            // Ctrl+Shift+B opens bash in a local folder (the open-in-bash tool); on a server the same
            // key opens that server's shell, so it means "a shell here" wherever you are. Placed,
            // so it wins only on a server and the tool keeps the key everywhere else.
            Binding = KeyStroke.Parse("Ctrl+Shift+B"),
            OnlyAt = uri => string.Equals(uri.Scheme, "sftp", StringComparison.OrdinalIgnoreCase),
            ExecuteAsync = async (context, ct) =>
            {
                var remote = _services.GetRequiredService<IRemoteCommandService>();
                var shell = await _services.GetRequiredService<CommandShells>().AskAsync(remote, context.Directory, ct);
                TerminalLauncher.OpenRemote(context.Directory, shell.IsPosix, line: null);
            },
        });

        // History from the keyboard: Ctrl+H is the hidden-files toggle here, so Firefox's "show all
        // history", Ctrl+Shift+H, rather than typing about:history (friction log, 2026-10-04).
        commands.Register(new CommandDefinition
        {
            Id = "dc.history",
            Subject = CommandSubject.Window,
            Title = "Go to: History",
            Group = "Navigate",
            Binding = KeyStroke.Parse("Ctrl+Shift+H"),
            ExecuteAsync = (context, _) => context.NavigateAsync(AboutPages.History),
        });

        commands.Register(new CommandDefinition
        {
            Id = "dc.bookmarks",
            Subject = CommandSubject.Window,
            Title = "Go to: Bookmarks",
            Group = "Navigate",
            Binding = KeyStroke.Parse("Ctrl+Shift+O"),
            ExecuteAsync = (context, _) => context.NavigateAsync(AboutPages.Bookmarks),
        });

        commands.Register(new CommandDefinition
        {
            Id = "dc.computer",
            Subject = CommandSubject.Window,
            Title = "Go to: This computer",
            Group = "Navigate",
            Binding = KeyStroke.Parse("Alt+Home"),
            ExecuteAsync = (context, _) => context.NavigateAsync(AboutPages.Computer),
        });

        // No key: it is looked for once in a while, and the palette and This computer both find it.
        commands.Register(new CommandDefinition
        {
            Id = "dc.about",
            Subject = CommandSubject.Window,
            Title = "Go to: About Dance Commander",
            Group = "Navigate",
            ExecuteAsync = (context, _) => context.NavigateAsync(AboutPages.App),
        });

        commands.Register(new CommandDefinition
        {
            Id = PaletteId,
            Subject = CommandSubject.Window,
            Title = "Command palette",
            Binding = KeyStroke.Parse("Ctrl+Shift+P"),
            ExecuteAsync = (_, _) =>
            {
                Palette.Open();
                FocusRequested?.Invoke(this, "palette");
                return Task.CompletedTask;
            },
        });

        // This folder's menu, from the keyboard (§6.18), opened at the location icon. "." is the
        // shell's name for the current folder as ".." is for its parent, so the key spells what it
        // opens. Group null: it is a key, and offering "open the folder menu" inside a menu or the
        // palette would be offering a way into the room you are already standing in.
        commands.Register(new CommandDefinition
        {
            Id = "dc.locationMenu",
            Title = "This folder's commands",
            LegendTitle = "This folder",
            Subject = CommandSubject.Window,
            Binding = KeyStroke.Parse("Ctrl+."),
            CanExecute = _ => ActiveTab.Uri is not null,
            ExecuteAsync = (_, _) =>
            {
                FocusRequested?.Invoke(this, "locationMenu");
                return Task.CompletedTask;
            },
        });

        // Git, as ordinary commands that decline to appear outside a repository (§6.6). This is
        // the cheap two-thirds of what a plugin host would have been for: no process boundary, no
        // load context, no API to keep stable -- the commands read git's output into the same
        // capture pane a tool uses, and a user who wants more adds them to tools.json with
        // "inRepository": true rather than writing a DLL.
        //
        // All read-only, and all --no-optional-locks. Staging and committing are deliberately
        // absent: they belong to whatever you already commit with, and a file manager that half
        // implements git is worse than one that shows you what git thinks and gets out of the way.
        //
        // Status and log are about the repository, not about the row under the cursor, so they are
        // offered whatever is selected. Scoped to empty space they disappeared the moment a row had
        // focus -- which in a listing is nearly always -- and the palette looked as if it did not
        // know it was in a repository at all.
        RegisterGit("dc.git.status", "Git status", ["status"], CommandScope.Any, CommandSubject.Location);
        RegisterGit("dc.git.log", "Git log", ["log", "--oneline", "--decorate", "-n", "40"],
            CommandScope.Any, CommandSubject.Location);
        RegisterGit("dc.git.diff", "Git diff", ["diff", "--", "${selection}"],
            CommandScope.File | CommandScope.Directory | CommandScope.Multiple);
        RegisterGit("dc.git.fileLog", "Git log for this", ["log", "--oneline", "-n", "40", "--", "${path}"],
            CommandScope.File | CommandScope.Directory);
        RegisterGit("dc.git.blame", "Git blame", ["blame", "--date=short", "--", "${path}"],
            CommandScope.File);

        void RegisterGit(
            string id, string title, string[] args, CommandScope scope,
            CommandSubject subject = CommandSubject.Selection) =>
            commands.Register(new CommandDefinition
            {
                Id = id,
                Title = title,
                Group = "Git",
                Scope = scope,
                Subject = subject,
                // The branch decoration already worked this out to draw the chip, so asking costs
                // nothing and the menu simply has no Git group outside a repository.
                CanExecute = context => context.InRepository && context.Directory.IsLocal,
                ExecuteAsync = (context, ct) => _services.GetRequiredService<IToolRunner>().RunAsync(
                    new ToolDefinition
                    {
                        Id = id,
                        Title = title,
                        Exec = "git",
                        // Never the index lock: a file manager that idly locks the repository you
                        // are working in will eventually be the reason a commit fails.
                        Args = ["--no-optional-locks", .. args],
                        WorkingDirectory = "${dir}",
                        Launch = "capture",
                    },
                    context, ct),
            });

        // Ctrl+Shift+C is Explorer's own "copy as path", so the muscle memory already exists.
        commands.Register(new CommandDefinition
        {
            Id = "dc.copyPath",
            Title = "Copy path",
            Group = "Clipboard",
            Scope = CommandScope.File | CommandScope.Directory | CommandScope.Multiple,
            Binding = KeyStroke.Parse("Ctrl+Shift+C"),
            ExecuteAsync = (context, _) => CopyAsync(context, e => Spell(e.Uri)),
        });

        commands.Register(new CommandDefinition
        {
            Id = "dc.copyName",
            Title = "Copy name",
            Group = "Clipboard",
            Scope = CommandScope.File | CommandScope.Directory | CommandScope.Multiple,
            ExecuteAsync = (context, _) => CopyAsync(context, e => e.Name),
        });

        // The file's page in the server's own web interface, for pasting to a colleague who will
        // open it in a browser rather than in a file manager. Only where there is one (Nextcloud).
        commands.Register(new CommandDefinition
        {
            Id = "dc.copyWebLink",
            Title = "Copy web link",
            Group = "Clipboard",
            Scope = CommandScope.File | CommandScope.Directory,
            CanExecute = context =>
                context.Focused?.Uri is { } uri
                && _services.GetRequiredService<IProviderRegistry>().TryResolve(uri, out var provider)
                && provider is IWebLinks links
                && links.MayHaveWebLink(uri),
            ExecuteAsync = async (context, ct) =>
            {
                if (context.Focused is not { } entry
                    || !_services.GetRequiredService<IProviderRegistry>().TryResolve(entry.Uri, out var provider)
                    || provider is not IWebLinks links)
                    return;

                var link = await links.WebLinkAsync(entry.Uri, ct)
                    ?? throw new System.IO.IOException($"The server gave no web link for {entry.Name}.");
                PutText(context, link.AbsoluteUri);
            },
        });

        // Errors carried past this session (ErrorReports), to send: the notice that told of each
        // names this command.
        commands.Register(new CommandDefinition
        {
            Id = "dc.copyErrorReport",
            Title = "Copy error report",
            Group = "Clipboard",
            Subject = CommandSubject.Window,
            InLegend = false,
            CanExecute = _ => ErrorReports.Count > 0,
            ExecuteAsync = (context, _) =>
            {
                var count = ErrorReports.Count;
                if (WindowsClipboard.PutText(ErrorReports.Report()))
                    context.Decisions.Report(new Notice(
                        $"Copied the report of {count:N0} error{(count == 1 ? "" : "s")}.",
                        Detail: $"Each is also kept in {ErrorReports.FileName}, until it reaches 2 MB. " +
                                "Windows' clipboard history may keep the report too."));
                else
                    context.Decisions.Report(WindowsClipboard.CouldNotCopy);
                return Task.CompletedTask;
            },
        });

        RegisterAsync("dc.showErrorLog", "Show the error log", "Help", null,
            (_, _) => System.IO.File.Exists(ErrorReports.FileName)
                ? ActiveTab.NavigateAsync(
                    FsUri.Local(System.IO.Path.GetDirectoryName(ErrorReports.FileName)!),
                    System.IO.Path.GetFileName(ErrorReports.FileName))
                : Task.CompletedTask,
            canExecute: _ => System.IO.File.Exists(ErrorReports.FileName));

        // What got in the way, noted in a line while it is fresh, for colleagues and for us
        // (PLAN.md §3, "Friction from other people"). Kept on this computer; sending is the next command.
        commands.Register(new CommandDefinition
        {
            Id = NoteFrictionId,
            Title = "Note friction…",
            Group = "Help",
            Subject = CommandSubject.Window,
            InLegend = false,
            ExecuteAsync = async (context, ct) =>
            {
                // Taken before asking: what led up to it, not what was done while typing.
                var scheme = context.Directory.Anchor.Scheme;
                var recent = FrictionNotes.RecentCommands();
                var typed = await context.Decisions.AskTextAsync(
                    new TextRequest(
                        "What got in your way?", ConfirmLabel: "Note",
                        Detail: $"One line, kept on this computer with the time, the kind of place ({scheme}) and " +
                                "your last few commands. Nothing is sent: \"Copy friction report\" puts the notes " +
                                "on the clipboard for you to send."),
                    ct);
                if (string.IsNullOrWhiteSpace(typed))
                    return;

                context.Decisions.Report(FrictionNotes.Note(typed, scheme, recent)
                    ? new Notice("Noted.", Detail: "\"Copy friction report\" puts every note on the clipboard to send.")
                    : new Notice($"Could not keep the note in {FrictionNotes.FileName}.", NoticeSeverity.Warning,
                        $"It is too large to add to, or can't be written. Your words: {typed}"));
            },
        });

        commands.Register(new CommandDefinition
        {
            Id = "dc.copyFrictionReport",
            Title = "Copy friction report",
            Group = "Clipboard",
            Subject = CommandSubject.Window,
            InLegend = false,
            ExecuteAsync = (context, _) =>
            {
                if (WindowsClipboard.PutText(FrictionNotes.Report()))
                    context.Decisions.Report(new Notice(
                        "Copied the friction report.",
                        Detail: "Your notes, what was slower than it should be this session, and the version. " +
                                "Read it over, then paste it into a new issue at " +
                                "github.com/elefAntti/dance_commander/issues; issues there are public. " +
                                $"The notes are kept in {FrictionNotes.FileName}."));
                else
                    context.Decisions.Report(WindowsClipboard.CouldNotCopy);
                return Task.CompletedTask;
            },
        });

        commands.Register(new CommandDefinition
        {
            Id = "dc.copyHere",
            Title = "Copy this folder's path",
            Group = "Clipboard",
            Subject = CommandSubject.Location,
            ExecuteAsync = (context, _) =>
            {
                PutText(context, Spell(context.Directory));
                return Task.CompletedTask;
            },
        });

        // Both are about the repository, not the row under the cursor, and decline to appear outside
        // one, like the other Git commands.
        commands.Register(new CommandDefinition
        {
            Id = "dc.git.copyBranch",
            Title = "Copy current branch",
            Group = "Git",
            Subject = CommandSubject.Location,
            CanExecute = context => context.InRepository && context.Directory.IsLocal,
            ExecuteAsync = (context, _) =>
            {
                // No branch name when HEAD is detached; the hash command is the one for that.
                if (context.Repository?.Branch is { } branch)
                    PutText(context, branch);
                else
                    context.Decisions.Report(new Notice(
                        "HEAD is detached, so there is no current branch.",
                        NoticeSeverity.Warning,
                        "Copy commit hash gives the commit it is on."));
                return Task.CompletedTask;
            },
        });

        commands.Register(new CommandDefinition
        {
            Id = "dc.git.copyCommitHash",
            Title = "Copy commit hash",
            Group = "Git",
            Subject = CommandSubject.Location,
            CanExecute = context => context.InRepository && context.Directory.IsLocal,
            ExecuteAsync = (context, _) =>
            {
                var hash = _services.GetRequiredService<IGitService>().Describe(context.Directory) is { } info
                    ? GitHead.Commit(info.GitDirectory)
                    : null;
                if (hash is null)
                    context.Decisions.Report(new Notice(
                        "Could not read the current commit.",
                        NoticeSeverity.Warning,
                        "A branch with no commits yet has none."));
                else
                    PutText(context, hash);
                return Task.CompletedTask;
            },
        });

        // A row in a flat or search view is a file somewhere else entirely, and the one thing you
        // always want next is to go to where it actually lives. Ctrl+Shift+Enter continues the
        // ladder: Enter opens it, Ctrl+Enter opens it in a new tab, and one more modifier goes to
        // where it lives.
        commands.Register(new CommandDefinition
        {
            Id = "dc.revealParent",
            Title = "Open containing folder",
            Group = "Navigate",
            Scope = CommandScope.File | CommandScope.Directory,
            Binding = KeyStroke.Parse("Ctrl+Shift+Enter"),
            // Only where the answer is somewhere else. In an ordinary directory listing the
            // containing folder is the one you are already looking at, and an entry that navigates
            // you to where you already are is a menu item that does nothing.
            CanExecute = context =>
                context.Focused?.Uri.Parent is { } parent && !parent.Equals(context.Directory),
            ExecuteAsync = async (context, _) =>
            {
                if (context.Focused is { } entry && entry.Uri.Parent is { } parent)
                    await context.NavigateAsync(parent, entry.Name);
            },
        });

        static Task CopyAsync(ICommandContext context, Func<FsEntry, string> of)
        {
            var lines = context.Selection.Select(of).ToList();
            if (lines.Count == 0 && context.Focused is { } focused)
                lines.Add(of(focused));

            if (lines.Count > 0)
                PutText(context, string.Join(Environment.NewLine, lines));
            return Task.CompletedTask;
        }

        // A warning in the pane rather than the runner's "did not complete" error, whose detail
        // was a COM error code: a busy clipboard is another program's moment, not a failure here.
        static void PutText(ICommandContext context, string text)
        {
            if (!WindowsClipboard.PutText(text))
                context.Decisions.Report(WindowsClipboard.CouldNotCopy);
        }

        // A local path for a local file, because that is what you are going to paste it into. A
        // remote or in-archive entry has no path to give, so it gives the URI it does have.
        static string Spell(FsUri uri) => uri.IsLocal ? uri.ToLocalPath() : uri.Text;

        // Listings that come from a command (§6.12). The worktree one ships built in because it is
        // the case that motivated the feature; anything else goes in tools.json under "listings".
        //
        // --porcelain and not the plain form: the plain form separates the path from the SHA with
        // spaces, so a worktree in "C:/My Projects" would be read as a path called "C:/My". The
        // stanza format has one field per line and cannot be ambiguous, which is what it is for.
        listings.Register(new CommandListing(
            Id: "worktrees",
            Title: "Worktrees",
            Exec: "git",
            Args: ["--no-optional-locks", "worktree", "list", "--porcelain"],
            Extract: """
                         ^worktree (?<path>.+?)\s*?\r?\n(?:HEAD \S+\r?\n)?(?:branch refs/heads/(?<branch>\S+))?
                         """,
            // The branch is what tells worktrees apart; they all sit side by side in one folder,
            // so the path column says the same thing on every row.
            Columns: [new CommandListingColumn("branch", "Branch", 160, Flexible: true)],
            InRepository: true,
            // The time something at a checkout's top level last changed; not what anyone opens a
            // list of worktrees to find out.
            ShowModified: false));

        // Only the built-ins exist yet; tools.json's listings get their commands from ToolCatalog,
        // which loads after the shell.
        foreach (var listing in listings.All)
            commands.Register(CommandListingCommands.For(listing));

        // Sorting from the keyboard, which is where it should always have been: the column headers
        // were the only way to do it and a header is a mouse target. Ctrl+F3..F6 are Total
        // Commander's, so anyone arriving from there already knows them, and they cost nothing to
        // document — holding Ctrl turns the F-key legend into exactly this list.
        //
        // Pressing the same one again reverses it, the way clicking the same header twice does.
        RegisterSort("dc.sortByName", "Sort by name", "Ctrl+F3", ColumnIds.Name);
        RegisterSort("dc.sortByType", "Sort by type", "Ctrl+F4", ColumnIds.Extension);
        RegisterSort("dc.sortByDate", "Sort by date", "Ctrl+F5", ColumnIds.Modified);
        RegisterSort("dc.sortBySize", "Sort by size", "Ctrl+F6", ColumnIds.Size);

        // The way back to the order a search opens in, once Ctrl+F3..F6 have left it. Ctrl+F7 is
        // Total Commander's "unsorted", the order a listing comes in, which in a search is this.
        // Only in a search, where the legend shows it; anywhere else there is no such order.
        commands.Register(new CommandDefinition
        {
            Id = "dc.sortByRelevance",
            Title = "Sort by relevance",
            LegendTitle = "Relevance",
            Group = "Sort",
            Subject = CommandSubject.Window,
            Binding = KeyStroke.Parse("Ctrl+F7"),
            OnlyAt = uri => uri.Scheme is "search" or FsUri.EverywhereScheme,
            ExecuteAsync = (_, _) =>
            {
                if (ActiveTab.Columns.RelevanceColumnId is { } relevance)
                    ActiveTab.SortBy(relevance);
                return Task.CompletedTask;
            },
        });

        void RegisterSort(string id, string title, string binding, string columnId) =>
            commands.Register(new CommandDefinition
            {
                Id = id,
                Title = title,
                // Compressed for the legend, where twelve of these share one row.
                LegendTitle = title.Replace("Sort by ", string.Empty),
                // A kind of its own on the legend, where the four sit together (UpdateLegend).
                Group = "Sort",
                Subject = CommandSubject.Window,
                Binding = KeyStroke.Parse(binding),
                ExecuteAsync = (_, _) =>
                {
                    ActiveTab.SortBy(columnId);
                    return Task.CompletedTask;
                },
            });

        Register("dc.onePane", "One pane", "View", "Ctrl+1", () => IsDualPane = false);

        Register("dc.twoPanes", "Two panes", "View", "Ctrl+2", () => IsDualPane = true);

        // Shell commands are mostly about the window; the few about the folder say so.
        void RegisterAsync(
            string id, string title, string group, string? binding,
            Func<ICommandContext, CancellationToken, Task> action,
            Func<ICommandContext, bool>? canExecute = null,
            CommandSubject subject = CommandSubject.Window,
            bool readsContents = false,
            string? legend = null) =>
            commands.Register(new CommandDefinition
            {
                Id = id,
                Title = title,
                LegendTitle = legend,
                Group = group,
                Subject = subject,
                ReadsContents = readsContents,
                Binding = binding is null ? null : KeyStroke.Parse(binding),
                CanExecute = canExecute,
                ExecuteAsync = action,
            });

        void Register(
            string id, string title, string group, string? binding, Action action,
            string? legend = null, bool inLegend = true) =>
            commands.Register(new CommandDefinition
            {
                Id = id,
                Title = title,
                Subject = CommandSubject.Window,
                LegendTitle = legend,
                InLegend = inLegend,
                Group = group,
                Binding = binding is null ? null : KeyStroke.Parse(binding),
                ExecuteAsync = (_, _) =>
                {
                    action();
                    return Task.CompletedTask;
                },
            });
    }

    /// <summary>Not among the commands a note says led up to it (<see cref="FrictionNotes.Ran"/>).</summary>
    private const string NoteFrictionId = "dc.noteFriction";

    /// <summary>Not among them either: it is how a note is reached, not something that led to it.</summary>
    private const string PaletteId = "dc.palette";

    /// <summary>What Run with arguments left for the command line to show; taken once.</summary>
    private string? _commandLineDraft;

    /// <summary>The window asks for it as it focuses the command line.</summary>
    public string? TakeCommandLineDraft()
    {
        var draft = _commandLineDraft;
        _commandLineDraft = null;
        return draft;
    }

    /// <summary>
    /// A local file Run knows by its name, in a pane with a folder here to run in. Names only: this
    /// runs on every legend repaint.
    /// </summary>
    private static bool CanRun(ICommandContext ctx) =>
        ctx.Focused is { IsDirectory: false, Uri.IsLocal: true } file
        && ctx.Directory.Anchor is { IsLocal: true }
        && RunLine.Runs(file.Name);

    /// <param name="shell">The shell the line will be handed to: the open line's, if Ctrl+E changed it.</param>
    private static string? RunLineFor(ICommandContext ctx, CommandShell shell)
    {
        if (ctx.Focused is not { IsDirectory: false, Uri.IsLocal: true } file || ctx.Directory.Anchor is not { IsLocal: true } here)
            return null;

        return RunLine.For(
            file.Uri.ToLocalPath(),
            here.ToLocalPath(),
            shell.Quoting,
            name => ExecutableSearch.Resolve(name) is var path && !ExecutableSearch.IsBare(path) ? path : null,
            CommandShells.FindGitBash());
    }

    /// <summary>
    /// A file downloaded from the internet is asked about first. Enter goes through the shell,
    /// which shows Windows' own warning for one; a line run by a shell gets none (review,
    /// 2026-10-12). Read from the mark Windows keeps beside the file, only when Run is pressed.
    /// </summary>
    private static async Task<bool> TrustedToRunAsync(ICommandContext ctx, CancellationToken ct)
    {
        if (ctx.Focused is not { } file)
            return false;

        // The mark is a few lines of INI; the first 4 kB hold it, whatever else a stream carries.
        string? mark;
        try
        {
            await using var stream = new System.IO.FileStream(
                file.Uri.ToLocalPath() + ":Zone.Identifier", System.IO.FileMode.Open, System.IO.FileAccess.Read,
                System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete, 4096, useAsync: true);
            var buffer = new byte[4096];
            var read = await stream.ReadAsync(buffer, ct);
            mark = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is System.IO.FileNotFoundException or NotSupportedException
            || (ex is System.IO.IOException && ex.HResult == unchecked((int)0x8007007B))) // ERROR_INVALID_NAME
        {
            return true; // no mark: made here, or a volume that keeps none
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            mark = null; // a mark that can't be read is asked about, not taken for none
        }

        // 3 is the Internet, 4 Restricted sites; 0-2 are this machine, the intranet and trusted sites.
        // Its own line only: a ReferrerUrl may hold "ZoneId=0" too.
        if (mark is not null)
        {
            var zone = System.Text.RegularExpressions.Regex.Match(
                mark, @"^\s*ZoneId\s*=\s*(\d{1,9})\s*$",
                System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!zone.Success)
                mark = null; // there, but not in a form this reads: asked about, as unreadable
            else if (int.Parse(zone.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) < 3)
                return true;
        }

        var answer = await ctx.Decisions.AskAsync(new ChoiceRequest(
            mark is null ? $"Is {file.Name} from the internet?" : $"{file.Name} came from the internet",
            [
                new DecisionOption("cancel", "Do not run", IsDefault: true, IsCancel: true),
                new DecisionOption("run", "Run it"),
            ],
            mark is null
                ? "Windows' mark saying where it came from could not be read. Run it only if you know where it came from."
                : "Windows marked it as downloaded. Run it only if you know where it came from.",
            NoticeSeverity.Warning),
            ct);
        return answer == "run";
    }

    /// <summary>
    /// Whether the pane is still the one Run was pressed in, and still there. The line is relative
    /// to that folder and runs in whatever the active pane shows, so a question answered after
    /// moving on would have run a setup.exe somewhere else (review, 2026-10-12).
    /// </summary>
    private bool StillWhereAsked(ICommandContext ctx)
    {
        if (ctx is PaneCommandContext { Tab: var tab } && tab == ActiveTab && Equals(tab.Location, ctx.Directory))
            return true;

        ctx.Decisions.Report(new Notice(
            "Not run: the pane has moved since you asked.",
            NoticeSeverity.Info,
            "Run it again from where it is."));
        return false;
    }

    private static Task CannotRun(ICommandContext ctx)
    {
        var name = ctx.Focused?.Name ?? "This file";
        ctx.Decisions.Report(new Notice(
            $"{name} cannot be put on a command line as it is.",
            NoticeSeverity.Warning,
            "Its path has characters a shell would read as commands or placeholders, or nothing here " +
            "runs this kind of file (a .ps1 needs PowerShell, a .py Python, a .sh Git Bash). Type the " +
            "line yourself with Ctrl+E."));
        return Task.CompletedTask;
    }
}
