using System.Diagnostics;
using System.IO;
using System.Text;
using DanceCommander.App.Services;
using DanceCommander.Core.Clipboard;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Operations;
using DanceCommander.Core.Preview;
using DanceCommander.Core.Transfer;
using DanceCommander.Extensibility;
using DanceCommander.Providers.Archive;
using Microsoft.Extensions.DependencyInjection;

namespace DanceCommander.App.Commands;

/// <summary>
/// Built-in commands. These go through the same registry as tools from tools.json (§1.2/1), so
/// the legend bar, context menu and keybindings never need to know which is which.
/// </summary>
public static class BuiltInCommands
{
    private static readonly char[] InvalidNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private const CommandScope OnEntries = CommandScope.File | CommandScope.Directory | CommandScope.Multiple;

    public static void RegisterAll(ICommandRegistry registry)
    {
        registry.Register(new CommandDefinition
        {
            Id = "dc.open",
            Title = "Open",
            Group = "Navigate",
            Binding = KeyStroke.Parse("Enter"),
            ExecuteAsync = OpenAsync,
        });

        // Enter uses a file, F4 shows what it is made of: for a script those are running it and its
        // text, for a page the browser and its markup. Never the same as Enter by falling through:
        // F4 is how you look at a script you don't trust, so nothing here ever opens the file itself.
        registry.Register(new CommandDefinition
        {
            Id = "dc.edit",
            ReadsContents = true,
            Title = "Edit",
            Group = "Edit",
            Scope = CommandScope.File | CommandScope.Multiple,
            Binding = KeyStroke.Parse("F4"),
            CanExecute = ctx => ctx.Selection.Any(e => !e.IsDirectory),
            ExecuteAsync = EditAsync,
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.up",
            Subject = CommandSubject.Window,
            Title = "Go to parent folder",
            LegendTitle = "Up",
            Group = "Navigate",
            Binding = KeyStroke.Parse("Backspace"),
            CanExecute = ctx => ctx.Directory.Up is not null,
            ExecuteAsync = (ctx, _) => ctx.Directory.Up is { } parent
                ? ctx.NavigateAsync(parent, ctx.Directory.Name)
                : Task.CompletedTask,
        });

        // "Here" means beside the archive, which is what the word says and what Explorer does. It
        // used to extract into the other pane — a good command, under the wrong name, and the
        // name is the only thing anybody reads before choosing it. That one is now its own entry.
        registry.Register(new CommandDefinition
        {
            Id = "dc.extract",
            Needs = ProviderCapabilities.Write,
            WritesHere = true,
            Title = "Extract here",
            LegendTitle = "Extract",
            Group = "File",
            Scope = CommandScope.File | CommandScope.Multiple,
            // The name is only a hint, so that the menu entry can appear without reading every
            // selected file. Execute re-checks by content, so a tarball called ".gz" still works.
            // Not inside an archive: there is no "here" to write to.
            CanExecute = ctx => !ctx.Directory.IsArchiveEntry
                && ctx.Selection.Any(e => !e.IsDirectory && LooksPackable(e.Name)),
            ExecuteAsync = (ctx, ct) => ExtractAsync(ctx, toOtherPane: false, ct),
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.extractToOtherPane",
            Title = "Extract to other pane",
            Group = "File",
            Scope = CommandScope.File | CommandScope.Multiple,
            CanExecute = ctx => ctx.OtherPaneDirectory is { } other && IsFolderToWriteInto(other)
                && ctx.Selection.Any(e => !e.IsDirectory && LooksPackable(e.Name)),
            ExecuteAsync = (ctx, ct) => ExtractAsync(ctx, toOtherPane: true, ct),
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.compress",
            Needs = ProviderCapabilities.Write,
            WritesHere = true,
            Title = "Compress into a zip",
            LegendTitle = "Zip",
            Group = "File",
            Scope = OnEntries,
            CanExecute = ctx => ctx.Selection.Count > 0 && !ctx.Directory.IsArchiveEntry,
            ExecuteAsync = CompressAsync,
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.split",
            Needs = ProviderCapabilities.Write,
            WritesHere = true,
            Title = "Split into parts",
            Group = "File",
            Scope = CommandScope.File,
            CanExecute = PartCommands.CanSplit,
            ExecuteAsync = PartCommands.SplitAsync,
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.join",
            Needs = ProviderCapabilities.Write,
            WritesHere = true,
            Title = "Join parts",
            Group = "File",
            Scope = CommandScope.File,
            CanExecute = PartCommands.CanJoin,
            ExecuteAsync = PartCommands.JoinAsync,
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.refresh",
            Subject = CommandSubject.Location,
            Title = "Refresh",
            Group = "Navigate",
            Binding = KeyStroke.Parse("Ctrl+R"),
            // Offered in the footer of a pane that is not live, the one place it is needed.
            InLegend = false,
            ExecuteAsync = (ctx, _) => ctx.RefreshAsync(),
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.copy",
            ReadsContents = true,
            Title = "Copy to other pane",
            LegendTitle = "Copy",
            Group = "File",
            Scope = OnEntries,
            Binding = KeyStroke.Parse("F5"),
            ExecuteAsync = (ctx, ct) => TransferAsync(ctx, TransferMode.Copy),
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.move",
            Needs = ProviderCapabilities.Delete,
            Title = "Move to other pane",
            LegendTitle = "Move",
            Group = "File",
            Scope = OnEntries,
            Binding = KeyStroke.Parse("F6"),
            ExecuteAsync = (ctx, ct) => TransferAsync(ctx, TransferMode.Move),
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.newFolder",
            Needs = ProviderCapabilities.CreateDirectory,
            WritesHere = true,
            Subject = CommandSubject.Location,
            Title = "New folder",
            LegendTitle = "MkDir",
            Group = "File",
            Binding = KeyStroke.Parse("F7"),
            ExecuteAsync = NewFolderAsync,
        });

        // Shift+F4 is the commanders' key for it (Norton, Total Commander, Far). Only where F4 can
        // hand the file to an editor, which is a real path on this machine.
        registry.Register(new CommandDefinition
        {
            Id = "dc.newFile",
            Needs = ProviderCapabilities.Write | ProviderCapabilities.ExecuteHost,
            WritesHere = true,
            Subject = CommandSubject.Location,
            Title = "New file",
            Group = "File",
            Binding = KeyStroke.Parse("Shift+F4"),
            ExecuteAsync = NewFileAsync,
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.rename",
            Needs = ProviderCapabilities.Rename,
            Title = "Rename",
            Group = "File",
            Scope = CommandScope.File | CommandScope.Directory,
            Binding = KeyStroke.Parse("F2"),
            CanExecute = ctx => ctx.Focused is not null,
            ExecuteAsync = RenameAsync,
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.delete",
            DeletesEntries = true,
            Needs = ProviderCapabilities.Delete,
            Title = "Delete",
            Group = "File",
            Scope = OnEntries,
            Binding = KeyStroke.Parse("Delete"),
            ExecuteAsync = (ctx, ct) => DeleteAsync(ctx, permanent: false, ct),
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.deletePermanently",
            DeletesEntries = true,
            Needs = ProviderCapabilities.Delete,
            Title = "Delete permanently",
            LegendTitle = "Erase",
            Group = "File",
            Scope = OnEntries,
            Binding = KeyStroke.Parse("Shift+Delete"),
            ExecuteAsync = (ctx, ct) => DeleteAsync(ctx, permanent: true, ct),
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.groupIntoFolder",
            Needs = ProviderCapabilities.CreateDirectory | ProviderCapabilities.Rename,
            WritesHere = true,
            Title = "Group into folder",
            LegendTitle = "Group",
            Group = "File",
            Scope = OnEntries,
            Binding = KeyStroke.Parse("Ctrl+Shift+G"),
            ExecuteAsync = GroupIntoFolderAsync,
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.undo",
            Subject = CommandSubject.Window,
            Title = "Undo",
            Group = "Edit",
            Binding = KeyStroke.Parse("Ctrl+Z"),
            // With only an operation under way too, so Ctrl+Z says what it waits for rather than nothing.
            CanExecute = ctx => ctx.Services.GetRequiredService<IUndoStack>() is var undo && (undo.CanUndo || undo.Waits),
            ExecuteAsync = UndoAsync,
        });

        // The system clipboard (§6.14), alongside F5/F6 rather than instead of them: the two-pane
        // keys move things between the panes you can see, and these move things between this
        // application and every other one.
        registry.Register(new CommandDefinition
        {
            Id = "dc.clipboardCopy",
            ReadsContents = true,
            Title = "Copy",
            Group = "Clipboard",
            Scope = OnEntries,
            Binding = KeyStroke.Parse("Ctrl+C"),
            InLegend = false,
            ExecuteAsync = (ctx, _) => PutOnClipboardAsync(ctx, ClipboardMode.Copy),
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.clipboardCut",
            Needs = ProviderCapabilities.Delete,
            Title = "Cut",
            Group = "Clipboard",
            Scope = OnEntries,
            Binding = KeyStroke.Parse("Ctrl+X"),
            InLegend = false,
            ExecuteAsync = (ctx, _) => PutOnClipboardAsync(ctx, ClipboardMode.Move),
        });

        registry.Register(new CommandDefinition
        {
            Id = "dc.paste",
            Needs = ProviderCapabilities.Write,
            WritesHere = true,
            Subject = CommandSubject.Location,
            Title = "Paste",
            Group = "Clipboard",
            Binding = KeyStroke.Parse("Ctrl+V"),
            InLegend = false,
            // No clipboard check here on purpose. CanExecute runs on every legend repaint, which
            // happens on every modifier keypress, and reading the clipboard means a synchronous
            // call into whichever process owns it -- one slow OLE server would be felt as a
            // stuttering keyboard. Paste therefore always offers itself and says so if there turns
            // out to be nothing there, which is one wasted keystroke against a responsive window.
            ExecuteAsync = PasteAsync,
        });
    }

    private static async Task OpenAsync(ICommandContext ctx, CancellationToken ct)
    {
        // Several folders cannot all be where this pane goes, so each becomes a tab. Only when all
        // of them are folders: a mix is one Enter too many things, and opens what the cursor is on.
        if (ctx.Selection.Count > 1 && ctx.Selection.All(e => e.IsDirectory))
        {
            await ctx.OpenInNewTabsAsync(ctx.Selection.Select(e => e.Uri).ToList());
            return;
        }

        if (ctx.Focused is not { } target)
            return;

        if (target.IsDirectory)
        {
            await ctx.NavigateAsync(target.Uri);
            return;
        }

        // An archive opens as a folder. Decided by reading the file, not by its extension: a great
        // many tarballs are named ".gz", and some archives are named nothing in particular. The
        // check reads one block, so it costs the same on a 2 GB file as on a small one.
        // No ConfigureAwait(false) anywhere in this method, deliberately. Commands run on the UI
        // thread and navigation updates the view; dropping the synchronisation context here sends
        // everything downstream -- including the grid's own column refresh -- to a thread-pool
        // thread, where WPF refuses to be touched.
        //
        // Except a document that is a zip inside: Enter on a .docx opened its XML parts instead of
        // Word (ZipDocuments says which). Checked by name first, so these cost no read at all.
        if (!ZipDocuments.IsDocument(target.Name) &&
            await ArchiveDetector.IsBrowsableAsync(target.Uri, ctx.Services, ct))
        {
            await ctx.NavigateAsync(FsUri.ArchiveEntry(target.Uri, "/"));
            return;
        }

        if (!target.Uri.IsLocal)
        {
            ctx.Decisions.Report(new Notice(
                "Opening files from this location is not supported yet.",
                NoticeSeverity.Warning,
                "Copy it somewhere local first."));
            return;
        }

        // Says "Starting Word…" if it is slow to come up, and does not start it twice (Launcher).
        await ctx.Services.GetRequiredService<Launcher>().OpenAsync(target.Uri.ToLocalPath(), ctx.Decisions as ILaunchHost);
    }

    /// <summary>
    /// A tool marked <c>edits</c> whose conditions take these files; else text goes to the text
    /// editor and anything else to the shell's own "edit" (Paint for an image), and a file with
    /// neither is said so in the pane.
    /// </summary>
    private static async Task EditAsync(ICommandContext ctx, CancellationToken ct)
    {
        if (await EditWithToolAsync(ctx, ct))
            return;

        // One file, as Enter on a mixed selection: the cursor's, or the first marked one.
        var target = ctx.Focused is { IsDirectory: false } focused
            ? focused
            : ctx.Selection.FirstOrDefault(e => !e.IsDirectory);
        if (target is null)
            return;

        if (!target.Uri.IsLocal)
        {
            ctx.Decisions.Report(new Notice(
                "Editing files from this location is not supported yet.",
                NoticeSeverity.Warning,
                "Copy it somewhere local first."));
            return;
        }

        var launcher = ctx.Services.GetRequiredService<Launcher>();
        var path = target.Uri.ToLocalPath();
        if (await IsTextAsync(target, ctx.Services, ct))
        {
            await EditAsTextAsync(ctx, target);
            return;
        }

        try
        {
            await launcher.EditAsync(path, ctx.Decisions as ILaunchHost);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == Launcher.ErrorNoAssociation)
        {
            ctx.Decisions.Report(new Notice(
                $"Nothing here edits {target.Name}.",
                NoticeSeverity.Info,
                "It is not text, and Windows has no editor for this kind of file. Enter opens it; " +
                "a tool in tools.json marked \"edits\" can take F4 for it."));
        }
    }

    /// <summary>The first tool in tools.json marked "edits" that takes this selection, run on it.</summary>
    private static async Task<bool> EditWithToolAsync(ICommandContext ctx, CancellationToken ct)
    {
        if (ctx.Services.GetService<ToolCatalog>() is not { } tools ||
            tools.Editors.FirstOrDefault(editor => editor.IsApplicable(ctx)) is not { } chosen)
            return false;

        await chosen.ExecuteAsync(ctx, ct);
        return true;
    }

    private static async Task EditAsTextAsync(ICommandContext ctx, FsEntry target)
    {
        try
        {
            await ctx.Services.GetRequiredService<Launcher>()
                .EditAsTextAsync(target.Uri.ToLocalPath(), ctx.Decisions as ILaunchHost);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == Launcher.ErrorNoAssociation)
        {
            ctx.Decisions.Report(new Notice(
                $"Nothing here edits {target.Name}.",
                NoticeSeverity.Warning,
                "Windows has nothing that opens .txt files. Choose a program for them in Settings, " +
                "or mark a tool in tools.json \"edits\"."));
        }
    }

    /// <summary>
    /// Shift+F4: a new, empty file here, opened as F4 opens one, so a file is made where it belongs
    /// rather than written first and then found again in a save dialog.
    /// </summary>
    /// <remarks>
    /// A file already of that name is opened as it is, as the commanders do: the name is where the
    /// user wants to write, and replacing what is there is never what Shift+F4 means (§1.6).
    /// Opened as text whatever the extension: it is empty, so there is nothing else it can be, and
    /// asking Windows for an editor of ".md" found none.
    /// </remarks>
    private static async Task NewFileAsync(ICommandContext ctx, CancellationToken ct)
    {
        var typed = await ctx.Decisions.AskTextAsync(
            new TextRequest(
                "New file", ConfirmLabel: "Create", Validate: ValidateFilePath,
                Detail: "Made empty and opened for editing. Use / to make it inside new folders, e.g. "
                        + "docs/notes.md. A file of that name already there opens as it is."),
            ct);
        if (string.IsNullOrWhiteSpace(typed))
            return;

        // Split as New folder's parser splits, so the folders made are the ones this names
        // (AUDIT.md kind 58): the validation has already refused a gap between two slashes.
        var levels = typed.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (levels.Length == 0)
            return;
        var name = levels[^1];
        var folders = levels[..^1];

        var operations = ctx.Services.GetRequiredService<IFileOperationService>();
        var directory = ctx.Directory.Anchor;
        var provider = ctx.Services.GetRequiredService<IProviderRegistry>().Resolve(directory);
        var folder = folders.Aggregate(directory, (at, level) => at.Child(level));
        var file = folder.Child(name);

        var there = await provider.StatAsync(file, ct);
        var existed = there is not null;
        if (there is not null)
        {
            if (there.IsDirectory)
            {
                ctx.Decisions.Report(new Notice($"{name} is a folder there.", NoticeSeverity.Warning, "Choose another name."));
                return;
            }
        }
        else
        {
            // The folders first, as F7 makes them: levels already there are left alone, and the
            // ones made are their own step, undone after the file.
            if (folders.Length > 0 && await operations.CreateFolderAsync(directory, string.Join('/', folders), ct) is null)
                return;

            // Stepped around rather than written over if something of that name arrives meanwhile;
            // the name it was made under comes back.
            var created = await operations.CreateFileAsync(
                folder, name, [], $"New-Item -ItemType File {ShellText.Quote(file)}", ct);
            if (created is null || await provider.StatAsync(created, ct) is not { } made)
                return;
            there = made;
        }

        // The cursor goes on what appears in this listing: the file, or for docs/notes.md, docs.
        await ctx.NavigateAsync(ctx.Directory, folders.Length > 0 ? folders[0] : there.Name);

        // One already there is whatever it is, a picture as much as text: F4 decides, as for any
        // file. Not by its size: a symbolic link to a picture says 0 (review, 2026-10-29).
        var onIt = new StandingOn(ctx, there);
        if (existed)
            await EditAsync(onIt, ct);
        else if (!await EditWithToolAsync(onIt, ct))
            await EditAsTextAsync(onIt, there);
    }

    /// <summary>
    /// A command's context moved onto one entry it did not start on — the file it has just made —
    /// so what F4 does with the selection it does with that.
    /// </summary>
    private sealed class StandingOn(ICommandContext inner, FsEntry entry) : ICommandContext
    {
        // The file's own folder, not the pane's: a tool given ${dir}\${name} or run there with
        // ${name} opened notes.md in the pane's folder, not the one made in docs (review, 2026-10-29).
        public FsUri Directory => entry.Uri.Parent ?? inner.Directory;
        public IReadOnlyList<FsEntry> Selection { get; } = [entry];
        public FsEntry? Focused => entry;
        public FsUri? OtherPaneDirectory => inner.OtherPaneDirectory;
        public RepositoryInfo? Repository => inner.Repository;
        public IDecisionHost Decisions => inner.Decisions;
        public IOutputHost Output => inner.Output;
        public IServiceProvider Services => inner.Services;
        public Task RefreshAsync() => inner.RefreshAsync();
        public IDisposable MarkLeaving(IReadOnlyCollection<FsUri> items) => inner.MarkLeaving(items);
        public Task NavigateAsync(FsUri uri, string? selectName = null) => inner.NavigateAsync(uri, selectName);
        public Task OpenInNewTabsAsync(IReadOnlyList<FsUri> locations) => inner.OpenInNewTabsAsync(locations);
    }

    /// <summary>
    /// The preview's test, on its first block: enough to tell a script from an image, and the same
    /// cost on a 2 GB file as on a small one. Unreadable counts as not text, which only means the
    /// shell is asked instead.
    /// </summary>
    private static async Task<bool> IsTextAsync(FsEntry entry, IServiceProvider services, CancellationToken ct)
    {
        try
        {
            var provider = services.GetRequiredService<IProviderRegistry>().Resolve(entry.Uri);
            await using var stream = await provider.OpenReadAsync(entry.Uri, ct);
            var head = new byte[8000];
            var read = 0;
            while (read < head.Length && await stream.ReadAsync(head.AsMemory(read), ct) is var n and > 0)
                read += n;
            return TextInspector.Inspect(head.AsSpan(0, read), truncated: read == head.Length).IsText;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Extensions worth offering "Extract" for, before anything has been read. Only what the archive
    /// reader opens: an .iso was offered and then answered "Nothing there could be read as an archive".
    /// </summary>
    internal static bool LooksPackable(string name)
    {
        foreach (var suffix in (string[])[".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz"])
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Unpacks the selected archives into the other pane, each into a folder of its own name.
    /// </summary>
    /// <remarks>
    /// This is the transfer service doing what it already does -- a copy from one provider to
    /// another -- so extraction gets progress, cancellation, conflict handling and the activity
    /// log without any of it being written a second time. The destination folder is created here
    /// rather than left to the copy, because the root of an archive has no name of its own.
    /// </remarks>
    private static async Task ExtractAsync(ICommandContext ctx, bool toOtherPane, CancellationToken ct)
    {
        if (toOtherPane && (ctx.OtherPaneDirectory is not { } other || !IsFolderToWriteInto(other)))
        {
            ctx.Decisions.Report(new Notice(
                "There is nowhere to extract to.", NoticeSeverity.Warning,
                "Open the destination in the other pane first."));
            return;
        }

        var providers = ctx.Services.GetRequiredService<IProviderRegistry>();
        var operations = ctx.Services.GetRequiredService<IFileOperationService>();
        var transfers = ctx.Services.GetRequiredService<ITransferService>();

        var extracted = 0;

        foreach (var entry in ctx.Selection.Where(e => !e.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();

            if (!await ArchiveDetector.IsBrowsableAsync(entry.Uri, ctx.Services, ct))
                continue;

            var root = FsUri.ArchiveEntry(entry.Uri, "/");

            // Beside the archive itself rather than in the pane's directory: in a flattened tree or
            // a search, those are different places, and "here" is where the archive is.
            // The folder a view in the other pane is of; a from: listing or a page was refused above.
            if ((toOtherPane ? ctx.OtherPaneDirectory!.Anchor : entry.Uri.Parent) is not { } target)
                continue;

            // Everything inside, placed under a folder named after the archive. Without the
            // folder, extracting two archives into one place would interleave their contents.
            var contents = new List<FsUri>();
            await foreach (var child in providers.Resolve(root).ListAsync(root, ct).ConfigureAwait(false))
                contents.Add(child.Uri);

            if (contents.Count == 0)
            {
                ctx.Decisions.Report(new Notice($"{entry.Name} is empty.", NoticeSeverity.Warning));
                continue;
            }

            var folder = await operations
                .CreateFolderAsync(target, StemOf(entry.Name), ct)
                .ConfigureAwait(false);

            if (folder is null)
            {
                // Somewhere read-only, or a name already taken by a file. Extracting into the
                // target directory anyway would scatter the archive's contents across it.
                ctx.Decisions.Report(new Notice(
                    $"Could not make a folder for {entry.Name}.", NoticeSeverity.Error,
                    $"Nothing was extracted from it into {target.Display}."));
                continue;
            }

            transfers.Enqueue(contents, folder, TransferMode.Copy, ctx.Decisions);
            extracted++;
        }

        if (extracted == 0)
        {
            ctx.Decisions.Report(new Notice(
                "Nothing there could be read as an archive.", NoticeSeverity.Warning,
                "The contents decide that, not the file name."));
        }
    }

    /// <summary>"logs.tar.gz" becomes "logs": the folder an archive should unpack into.</summary>
    internal static string StemOf(string name)
    {
        var stem = name;
        foreach (var suffix in (string[])[".gz", ".bz2", ".xz"])
        {
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                stem = stem[..^suffix.Length];
                break;
            }
        }

        var dot = stem.LastIndexOf('.');
        return dot > 0 ? stem[..dot] : stem;
    }

    private static async Task CompressAsync(ICommandContext ctx, CancellationToken ct)
    {
        // What and where, read once: the name question below lets the user click about, and a
        // zip of whatever was selected by the time they pressed Enter is not what they asked for.
        var selection = ctx.Selection.ToList();
        var directory = ctx.Directory.Anchor;

        var operations = ctx.Services.GetRequiredService<IFileOperationService>();
        var proposed = selection.Count == 1
            ? StemOf(selection[0].Name)
            : operations.ProposeGroupName(selection, directory);

        var typed = await ctx.Decisions.AskTextAsync(
            new TextRequest(
                $"Compress {Describe(selection.Count)} into a zip",
                proposed,
                "Compress",
                Detail: "Created in this folder.",
                Validate: name => string.IsNullOrWhiteSpace(name)
                    ? "The archive needs a name."
                    : name.IndexOfAny(InvalidNameChars) >= 0
                        ? "That name contains a character a file name cannot."
                        : null),
            ct);

        if (string.IsNullOrWhiteSpace(typed))
            return;

        var name = typed.Trim();
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            name += ".zip";

        var target = directory.Child(name);
        var builder = ctx.Services.GetRequiredService<IArchiveBuilder>();
        var providers = ctx.Services.GetRequiredService<IProviderRegistry>();
        var provider = providers.Resolve(target);
        var recycleBin = ctx.Services.GetRequiredService<IRecycleBin>();

        // A zip of that name already there was written over without a word, and nothing went on
        // the undo stack, so the next Ctrl+Z undid something else (§1.6). Replacing is offered only
        // where the old one can go to the Recycle Bin and so come back.
        var replacing = false;
        var existing = await provider.StatAsync(target, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            var canReplace = recycleBin.CanRecycle(target);
            var answer = await ctx.Decisions.AskAsync(
                new ChoiceRequest(
                    $"{name} already exists here.",
                    canReplace
                        ?
                        [
                            new DecisionOption("keepBoth", "Keep both", IsDefault: true),
                            new DecisionOption("replace", "Replace it"),
                            new DecisionOption("cancel", "Cancel", IsCancel: true),
                        ]
                        :
                        [
                            new DecisionOption("keepBoth", "Keep both", IsDefault: true),
                            new DecisionOption("cancel", "Cancel", IsCancel: true),
                        ],
                    canReplace
                        ? "Replacing sends the old one to the Recycle Bin."
                        : "There is no Recycle Bin here to keep the old one in, so it cannot be replaced from here.",
                    NoticeSeverity.Warning),
                ct);

            switch (answer)
            {
                case "keepBoth":
                    target = await AvailableAsync(provider, target, ct).ConfigureAwait(false);
                    name = target.Name;
                    break;
                case "replace":
                    replacing = true;
                    break;
                default:
                    return;
            }
        }

        // A replacement is built under a free name first and swapped in only once it is whole:
        // recycling the old one before the build left nothing at the name when the build failed,
        // which it always did when the old zip was among what was being compressed.
        var built = replacing ? await AvailableAsync(provider, target, ct).ConfigureAwait(false) : target;
        var recycledAt = DateTimeOffset.MaxValue;
        var recycledBy = DateTimeOffset.MaxValue;
        var replacedChanged = false;
        var oldInBin = false;
        var swapped = false;
        IReadOnlyList<PackedFile> packed = [];

        // A job once it runs past a moment, as a Del is: a large folder takes a while to pack, and
        // with nothing on screen the command looked like it had not happened. Ctrl+Z meanwhile
        // says it is still going rather than undo what came before it.
        var what = selection.Count == 1 ? selection[0].Name : Describe(selection.Count);
        var doing = $"compressing {what} into {name}";
        using var working = ctx.Services.GetRequiredService<IUndoStack>().Working(doing);
        var job = new OperationJob(char.ToUpperInvariant(doing[0]) + doing[1..]);

        try
        {
            await OperationJob.RunAsync(
                ctx.Services.GetRequiredService<IJobService>(),
                job,
                async (running, token) =>
                {
                    packed = await builder
                        .CreateZipAsync(
                            selection.Select(e => e.Uri).ToList(), built,
                            new ZipJobProgress(running), token)
                        .ConfigureAwait(false);

                    // The swap is not stopped part way: Cancel pressed as the zip finishes would
                    // otherwise leave the old one in the bin and the new one under a free name.
                    if (replacing)
                    {
                        // "Replace it" was said about the zip there then, and the window stays
                        // usable while this builds: one changed or put there since is not replaced
                        // without asking, and the new one stays beside it (review, 2026-10-21).
                        var now = await provider.StatAsync(target, CancellationToken.None).ConfigureAwait(false);
                        if (now is null || now.Size != existing!.Size || now.Modified != existing.Modified)
                        {
                            replacing = false;
                            replacedChanged = true;
                            target = built;
                            name = built.Name;
                            return;
                        }

                        recycledAt = DateTimeOffset.Now;
                        await recycleBin.RecycleAsync([target], CancellationToken.None).ConfigureAwait(false);
                        oldInBin = true;
                        recycledBy = DateTimeOffset.Now;
                        await provider.MoveAsync(built, target, CancellationToken.None).ConfigureAwait(false);
                        swapped = true;
                    }
                },
                ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or OperationCanceledException)
        {
            // Every way out says where both zips are, as they are found now, and goes in the log.
            // Told by which step failed, it said "the old one is no longer here" over both, and
            // "Could not create" over a new zip at its name or beside it (review, 2026-10-28).
            var stopped = ex is OperationCanceledException;
            var detail = stopped ? null : ex.Message.Trim();

            // A look that fails is "can't tell", never "nothing there": with the connection gone,
            // the clean-up's own look threw the library's words over the real failure, and one
            // read as absent would have moved the new zip onto a name it couldn't see (2026-11-03).
            var couldNotLook = false;
            async Task<FsEntry?> LookAsync(FsUri at)
            {
                try
                {
                    return await provider.StatAsync(at, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception look) when (look is not OperationCanceledException)
                {
                    couldNotLook = true;
                    return null;
                }
            }

            // Only before the swap: once the new zip has the name, the old one can't come back to
            // it, and stays in the bin, said.
            var oldStillInBin = oldInBin;
            if (oldInBin && !swapped)
            {
                try
                {
                    await recycleBin.RestoreAsync([target], recycledAt, recycledBy, CancellationToken.None).ConfigureAwait(false);
                    oldStillInBin = false;
                }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                }
            }

            bool OldAsItWas(FsEntry? at) => at is not null && existing is not null && at.Size == existing.Size && at.Modified == existing.Modified;

            // Where the new zip is, if anywhere. Beside an old one back as it was, it goes: that is
            // what the delete's reason says, and a file another program put at the name meanwhile
            // is not that (it stays, and so does the new zip). With nothing at the name (Windows
            // deleted the old one outright), the new one takes it.
            FsUri? newAt = swapped ? target : null;
            if (!swapped && await LookAsync(built).ConfigureAwait(false) is not null)
            {
                newAt = built;
                if (replacing)
                {
                    var atName = await LookAsync(target).ConfigureAwait(false);
                    try
                    {
                        if (OldAsItWas(atName))
                        {
#pragma warning disable RS0030 // §1.6: the zip this command just built under a free name, never swapped in, with the old one back at its name as it was.
                            await provider.DeleteAsync(built, recursive: false, CancellationToken.None).ConfigureAwait(false);
#pragma warning restore RS0030
                            newAt = null;
                        }
                        else if (atName is null && !couldNotLook)
                        {
                            await provider.MoveAsync(built, target, CancellationToken.None).ConfigureAwait(false);
                            newAt = target;
                        }
                    }
                    catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }

            string? oldWhere = null;
            if (replacing)
            {
                var atNameNow = oldStillInBin ? null : await LookAsync(target).ConfigureAwait(false);
                oldWhere = oldStillInBin ? $"The old {target.Name} is in the Recycle Bin."
                    : OldAsItWas(atNameNow) ? $"The old {target.Name} is as it was."
                    : couldNotLook ? null
                    : $"The old {target.Name} is no longer here.";
            }

            var unseen = couldNotLook
                ? $"{(built.Parent is { IsRoot: false } folder ? folder.Name : "The folder")} could not be looked at afterwards to see what is left there."
                : null;
            var said = string.Join(" ", new[] { detail, oldWhere, unseen }.Where(t => !string.IsNullOrEmpty(t)));
            var shell = $"zip -r {(newAt ?? built).Name} .";
            var failures = ctx.Services.GetRequiredService<IActivityLog>();
            if (newAt is not null)
            {
                // Stopping is something the user did; a Stop that lands as a server finishes the
                // upload still leaves a zip there, with no undo (review, 2026-10-21).
                failures.Record($"Compressed into {newAt.Name}, with a failure", shell, ActivityOutcome.Failed, said.Length == 0 ? null : said);
                ctx.Decisions.Report(new Notice(
                    stopped && !replacing ? $"Stopped too late: {newAt.Name} had already been written." : $"{newAt.Name} was made, but something went wrong.",
                    NoticeSeverity.Warning,
                    $"{said} Ctrl+Z will not take it back; check it before relying on it.".Trim()));
                await ctx.RefreshAsync();
                return;
            }

            // Not looked at is not "not made": a commit can land just as the server goes, and a whole
            // zip there was said not to be (review, 2026-11-03).
            if (couldNotLook)
            {
                failures.Record($"Compressing into {name} failed; whether it was made is not known", shell, ActivityOutcome.Failed, said.Length == 0 ? null : said);
                ctx.Decisions.Report(new Notice($"{name} may or may not have been made.", NoticeSeverity.Error, said.Length == 0 ? null : said));
                await ctx.RefreshAsync();
                return;
            }

            if (!stopped)
                failures.Record($"Could not compress into {name}", shell, ActivityOutcome.Failed, said.Length == 0 ? null : said);
            ctx.Decisions.Report(stopped
                ? new Notice($"Stopped. {name} was not created.", NoticeSeverity.Info, said.Length == 0 ? null : said)
                : new Notice($"Could not create {name}.", NoticeSeverity.Error, said.Length == 0 ? null : said));
            await ctx.RefreshAsync();
            return;
        }

        var description = $"Compressed {selection.Count:N0} item{(selection.Count == 1 ? "" : "s")} into {name}";
        var log = ctx.Services.GetRequiredService<IActivityLog>();
        log.Record(description, $"zip -r {name} .", ActivityOutcome.Succeeded,
            replacing ? "The old one went to the Recycle Bin." : null);

        // Undoable as every mutation is (§1.2/6): the zip goes if it is still as made, and a zip it
        // replaced comes back out of the Recycle Bin.
        var made = await provider.StatAsync(target, ct).ConfigureAwait(false);
        var undoTarget = target;
        working.Push(new DelegateUndo(
            description,
            $"zip -r {name} .",
            token => UndoCompressAsync(provider, providers, recycleBin, log, undoTarget, made, packed, replacing ? (recycledAt, recycledBy) : null, description, token)));
        // Let go as soon as it is recorded, as a transfer does, not after the refresh.
        working.Dispose();

        await ctx.RefreshAsync();
        ctx.Services.GetService<IArrivalFeed>()?.Report([target]);
        ctx.Decisions.Report(replacedChanged
            ? new Notice($"Created {name}.", NoticeSeverity.Info,
                "The zip it was to replace changed while this was compressing, so it was kept.")
            : new Notice($"Created {name}."));
    }

    /// <summary>
    /// The zip's progress onto its job, straight from the thread packing it: the job raises its own
    /// changes, as a Del's does, so there is no need to go through the window's thread first.
    /// </summary>
    private sealed class ZipJobProgress(OperationJob job) : IProgress<ZipProgress>
    {
        public void Report(ZipProgress value) => job.Report(value.Fraction, value.Detail);
    }

    /// <summary>
    /// Takes back a zip that Compress made, only as it was made, and brings back the one it
    /// replaced. The old one is restored before the new one goes anywhere: deleting first lost
    /// both when the Recycle Bin had been emptied in between.
    /// </summary>
    private static async Task UndoCompressAsync(
        IFileSystemProvider provider, IProviderRegistry providers, IRecycleBin recycleBin, IActivityLog log,
        FsUri target, FsEntry? made, IReadOnlyList<PackedFile> packed,
        (DateTimeOffset Since, DateTimeOffset By)? replacedAt, string description, CancellationToken ct)
    {
        var now = await provider.StatAsync(target, ct).ConfigureAwait(false);
        if (now is not null && (made is null || now.Size != made.Size || now.Modified != made.Modified))
            throw new CannotUndoException($"{target.Name} has changed since it was made, so it stays.");

        // Gone since, most likely deleted, and so in the bin under the same name as the one it
        // replaced: restoring "the newest x.zip" would bring back the wrong one.
        if (now is null && replacedAt is not null)
            throw new CannotUndoException($"{target.Name} is no longer there; the one it replaced is still in the Recycle Bin.");

        if (replacedAt is { } replaced)
        {
            // Out of the way under a free name, so the old one can have its name back, and then to
            // the bin rather than deleted: it was the newest zip, and undo should not destroy it.
            var aside = now is null ? null : await AvailableAsync(provider, target, ct).ConfigureAwait(false);
            if (aside is not null)
                await provider.MoveAsync(target, aside, ct).ConfigureAwait(false);
            try
            {
                await recycleBin.RestoreAsync([target], replaced.Since, replaced.By, ct).ConfigureAwait(false);
            }
            catch
            {
                if (aside is not null)
                    await provider.MoveAsync(aside, target, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            if (aside is not null)
                await recycleBin.RecycleAsync([aside], ct).ConfigureAwait(false);
        }
        else if (now is not null && recycleBin.CanRecycle(target))
        {
            await recycleBin.RecycleAsync([target], ct).ConfigureAwait(false);
        }
        else if (now is not null)
        {
            // With no bin to keep it, the zip is gone for good, and it may hold the last copy of
            // what it was made from: compress a folder on a stick, delete the folder, and Ctrl+Z
            // took the photos with the zip (review, 2026-10-22). It goes only while every file it
            // holds is still where it was packed from, as it was.
            if (await PackedFile.FirstChangedAsync(providers, packed, ct).ConfigureAwait(false) is { } changed)
                throw new CannotUndoException(
                    $"{target.Name} stays: {changed.Source.Name}, packed into it, is no longer as it was, so the zip may be the only copy.");

#pragma warning disable RS0030 // §1.6: exactly the zip this command wrote, unchanged since (size and time checked above), where no bin can take it, and every file in it still where it was packed from (checked just above).
            await provider.DeleteAsync(target, recursive: false, ct).ConfigureAwait(false);
#pragma warning restore RS0030
        }

        log.Record($"Undid: {description}", ShellText.Delete(target), ActivityOutcome.Undone);
    }

    /// <summary><paramref name="wanted"/> if free, else the same name with a number, as "keep both" does elsewhere.</summary>
    internal static async Task<FsUri> AvailableAsync(IFileSystemProvider provider, FsUri wanted, CancellationToken ct)
    {
        var dot = wanted.Name.LastIndexOf('.');
        var stem = dot > 0 ? wanted.Name[..dot] : wanted.Name;
        var extension = dot > 0 ? wanted.Name[dot..] : string.Empty;
        var parent = wanted.Parent ?? wanted;

        for (var n = 2; n < 1000; n++)
        {
            var candidate = parent.Child($"{stem} ({n}){extension}");
            if (await provider.StatAsync(candidate, ct).ConfigureAwait(false) is null)
                return candidate;
        }

        throw new IOException($"Could not find a free name beside {wanted.Name}.");
    }

    private static string Describe(int count) => count == 1 ? "1 item" : $"{count:N0} items";

    private static Task TransferAsync(ICommandContext ctx, TransferMode mode)
    {
        if (ctx.Selection.Count == 0)
            return Task.CompletedTask;

        if (ctx.OtherPaneDirectory is not { } other)
        {
            ctx.Decisions.Report(new Notice(
                "There is no other pane on screen to transfer to.", NoticeSeverity.Warning,
                "Ctrl+2 shows both panes."));
            return Task.CompletedTask;
        }

        // A from: listing (also inside a flattened or searched view of one), an archive or a page
        // is not a folder to put files in. Its anchor is the folder the listing was run in, and F6
        // moved files there while the pane showed something else; a drop already refused it.
        if (!IsFolderToWriteInto(other))
        {
            ctx.Decisions.Report(new Notice(
                "The other pane is not showing a folder files can go into.", NoticeSeverity.Warning));
            return Task.CompletedTask;
        }

        var target = other.Anchor;

        if (target == ctx.Directory.Anchor)
        {
            ctx.Decisions.Report(new Notice(
                "Both panes are showing the same folder.", NoticeSeverity.Warning,
                "Open the destination in the other pane first."));
            return Task.CompletedTask;
        }

        // Returns immediately: the job reports through the transfer strip and the user keeps working.
        ctx.Services.GetRequiredService<ITransferService>()
            .Enqueue(ctx.Selection.Select(e => e.Uri).ToList(), target, mode, ctx.Decisions);

        return Task.CompletedTask;
    }

    /// <summary>Whether this location, through every view it is wrapped in, ends at a folder.</summary>
    private static bool IsFolderToWriteInto(FsUri location)
    {
        for (FsUri? at = location; at is not null; at = at.IsView ? at.Inner : null)
        {
            if (at.Scheme == FsUri.FromScheme || at.IsArchiveEntry || at.IsAbout)
                return false;
        }

        return true;
    }

    private static Task PutOnClipboardAsync(ICommandContext ctx, ClipboardMode mode)
    {
        var uris = ctx.Selection.Select(e => e.Uri).ToList();
        if (uris.Count == 0 && ctx.Focused is { } focused)
            uris.Add(focused.Uri);

        // Said, because the failure is otherwise invisible: the next paste would bring back
        // whatever was copied before, other files than the ones on screen, and as a move if
        // that was a cut.
        if (uris.Count > 0 && !ctx.Services.GetRequiredService<IClipboardService>().PutFiles(uris, mode))
            ctx.Decisions.Report(WindowsClipboard.CouldNotCopy);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Pastes whatever the clipboard holds: files as a transfer, and anything else as a new file
    /// made out of it (§6.14).
    /// </summary>
    /// <remarks>
    /// Always into the directory the pane is showing, never into a folder that happens to be
    /// selected. Explorer does the second thing from a right-click and the first from Ctrl+V, and
    /// the difference between them is invisible at the moment you press the key -- in a pane whose
    /// whole point is that you always know where you are standing, "here" is the only answer that
    /// never needs checking afterwards.
    /// </remarks>
    private static async Task PasteAsync(ICommandContext ctx, CancellationToken ct)
    {
        if (ctx.Directory.IsArchiveEntry)
        {
            ctx.Decisions.Report(new Notice(
                "An archive cannot be written into.", NoticeSeverity.Warning,
                "Extract it somewhere first, then paste there."));
            return;
        }

        if (!ctx.Services.GetRequiredService<IClipboardService>().TryRead(out var content))
        {
            ctx.Decisions.Report(new Notice(
                "Could not read the clipboard.", NoticeSeverity.Warning,
                "Another program is holding it. Try again in a moment."));
            return;
        }

        if (content is null)
        {
            ctx.Decisions.Report(new Notice("There is nothing on the clipboard to paste."));
            return;
        }

        switch (content)
        {
            case ClipboardContent.Files files:
                PasteFiles(ctx, files);
                break;

            case ClipboardContent.Image image:
                await PasteNewFileAsync(
                    ctx, image.Png, $"Pasted image {Stamp()}.png",
                    target => $"(Get-Clipboard -Format Image).Save({ShellText.Quote(target)})", ct);
                break;

            case ClipboardContent.Text text:
                await PasteNewFileAsync(
                    ctx, TextFileContent(text.Value), $"Pasted text {Stamp()}.txt",
                    target => $"Get-Clipboard | Set-Content -Encoding utf8 {ShellText.Quote(target)}", ct);
                break;
        }
    }

    /// <summary>
    /// Text made into a file, pasted or dropped. No BOM: it is what the rest of the world writes,
    /// and the preview's own encoding line will say "UTF-8" rather than "UTF-8 with BOM" for it.
    /// </summary>
    internal static byte[] TextFileContent(string text) => new UTF8Encoding(false).GetBytes(text);

    private static void PasteFiles(ICommandContext ctx, ClipboardContent.Files files)
    {
        var action = PastePolicy.Decide(files.Uris, ctx.Directory.Anchor, files.Mode);
        if (action == DropAction.None)
        {
            // The only way to get here is a cut that cannot land: pasting it where it already is,
            // or into a folder inside itself. Saying so beats a paste that silently does nothing.
            ctx.Decisions.Report(new Notice(
                "These files cannot be moved here.", NoticeSeverity.Warning,
                "They are already in this folder, or this folder is inside one of them."));
            return;
        }

        var mode = action == DropAction.Move ? TransferMode.Move : TransferMode.Copy;

        // Returns immediately; the transfer strip carries it from here, exactly as a drop does.
        ctx.Services.GetRequiredService<ITransferService>()
            .Enqueue(files.Uris, ctx.Directory.Anchor, mode, ctx.Decisions);

        // A cut is used up by its paste. If the move fails, the files are where they were and can
        // be cut again; left on the clipboard, a second Ctrl+V moved them on from where they landed.
        if (mode == TransferMode.Move && !ctx.Services.GetRequiredService<IClipboardService>().ForgetCut(files.Uris))
        {
            ctx.Decisions.Report(new Notice(
                "The cut is still on the clipboard.", NoticeSeverity.Warning,
                "Another program was holding it. Copy something else before pressing Ctrl+V again."));
        }
    }

    private static async Task PasteNewFileAsync(
        ICommandContext ctx, byte[] content, string name, Func<FsUri, string> shellEquivalent, CancellationToken ct)
    {
        var operations = ctx.Services.GetRequiredService<IFileOperationService>();
        var created = await operations.CreateFileAsync(
            ctx.Directory.Anchor, name, content, shellEquivalent(ctx.Directory.Anchor.Child(name)), ct);

        if (created is not null)
            await ctx.NavigateAsync(ctx.Directory, created.Name);
    }

    /// <summary>
    /// Enough to name a pasted file without asking. Sortable, and second-resolution, so pasting
    /// three screenshots in a row produces three names rather than three collisions -- and the new
    /// row is selected on arrival, so F2 is right there for anyone who wants to say more.
    /// </summary>
    internal static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");

    private static async Task NewFolderAsync(ICommandContext ctx, CancellationToken ct)
    {
        var name = await ctx.Decisions.AskTextAsync(
            new TextRequest(
                "New folder", ConfirmLabel: "Create", Validate: ValidateFolderPath,
                Detail: "Use / to make folders inside folders, e.g. docs/2026."),
            ct);
        if (string.IsNullOrWhiteSpace(name))
            return;

        var operations = ctx.Services.GetRequiredService<IFileOperationService>();
        var created = await operations.CreateFolderAsync(ctx.Directory.Anchor, name.Trim(), ct);

        // The cursor goes on the first level, which is the one in this listing; for "docs/2026"
        // that is docs, with 2026 inside it.
        if (created is not null)
            await ctx.NavigateAsync(ctx.Directory, TopLevel(name));
    }

    private static async Task RenameAsync(ICommandContext ctx, CancellationToken ct)
    {
        if (ctx.Focused is not { } entry)
            return;

        var name = await ctx.Decisions.AskTextAsync(
            new TextRequest(
                $"Rename {entry.Name}", entry.Name, "Rename", Validate: ValidateName,
                // A directory has no extension to protect (FsEntry.Stem agrees: it never splits
                // one), and a dot in a folder name is just part of the name.
                SelectStemOnly: !entry.IsDirectory),
            ct);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == entry.Name)
            return;

        var operations = ctx.Services.GetRequiredService<IFileOperationService>();
        var renamed = await operations.RenameAsync(entry, name.Trim(), ct, ctx.Decisions);
        if (renamed is not null)
            await ctx.NavigateAsync(ctx.Directory, renamed.Name);
    }

    private static async Task DeleteAsync(ICommandContext ctx, bool permanent, CancellationToken ct)
    {
        if (ctx.Selection.Count == 0)
            return;

        // Seen from the first moment, and as a job once it runs long (OperationJob): a 2 s Del
        // with nothing on screen read as one that hadn't happened (2026-10-18). Its hold on Ctrl+Z
        // is the delete's own, from its first question to its record.
        var selection = ctx.Selection;
        var what = selection.Count == 1 ? selection[0].Name : $"{selection.Count:N0} items";
        var doing = permanent ? $"deleting {what}" : $"sending {what} to the Recycle Bin";
        var job = new OperationJob(char.ToUpperInvariant(doing[0]) + doing[1..]);
        using var progress = new DeleteProgress(ctx, job);
        try
        {
            await OperationJob.RunAsync(
                ctx.Services.GetRequiredService<IJobService>(),
                job,
                (running, token) => ctx.Services.GetRequiredService<IFileOperationService>()
                    .DeleteAsync(selection, permanent, new WaitingDecisions(ctx.Decisions, running), token, progress),
                ct);
        }
        finally
        {
            // Stopped or failed too: a listing taken while the rows were struck through would
            // otherwise keep them so, over files still there (review, 2026-10-18).
            progress.Dispose();
            await ctx.RefreshAsync();
        }
    }

    /// <summary>
    /// What a Del shows while it runs: its rows struck through once every question is answered,
    /// until it ends; and on its job, how far it is.
    /// </summary>
    private sealed class DeleteProgress(ICommandContext ctx, OperationJob job) : IOperationProgress, IDisposable
    {
        private readonly List<IDisposable> _marks = [];

        public void Starting(IReadOnlyCollection<FsUri> items)
        {
            lock (_marks)
                _marks.Add(ctx.MarkLeaving(items));
        }

        public void Report(double? fraction, string? detail) => job.Report(fraction, detail);

        public void Dispose()
        {
            lock (_marks)
            {
                foreach (var mark in _marks)
                    mark.Dispose();
                _marks.Clear();
            }
        }
    }

    private static async Task GroupIntoFolderAsync(ICommandContext ctx, CancellationToken ct)
    {
        if (ctx.Selection.Count == 0)
            return;

        var operations = ctx.Services.GetRequiredService<IFileOperationService>();
        var provider = ctx.Services.GetRequiredService<IProviderRegistry>().Resolve(ctx.Directory.Anchor);
        string? name = operations.ProposeGroupName(ctx.Selection, ctx.Directory.Anchor);
        string? taken = null;

        // A taken name is asked again, saying so: Windows' own "Cannot create a file when that
        // file already exists" named neither the file nor what to do (2026-10-14).
        while (true)
        {
            name = await ctx.Decisions.AskTextAsync(
                new TextRequest(
                    $"Move {ctx.Selection.Count:N0} item{(ctx.Selection.Count == 1 ? "" : "s")} into a new folder",
                    name, "Group",
                    Detail: taken is null ? null : $"{taken} already exists here. Choose another name.",
                    Validate: ValidateName),
                ct);
            if (string.IsNullOrWhiteSpace(name))
                return;

            name = name.Trim();
            if (await provider.StatAsync(ctx.Directory.Anchor.Child(name), ct) is null)
                break;
            taken = name;
        }

        var folder = await operations.GroupIntoFolderAsync(ctx.Selection, ctx.Directory.Anchor, name, ct, ctx.Decisions);
        if (folder is not null)
            await ctx.NavigateAsync(ctx.Directory, folder.Name);
    }

    private static async Task UndoAsync(ICommandContext ctx, CancellationToken ct)
    {
        var undo = ctx.Services.GetRequiredService<IUndoStack>();
        var log = ctx.Services.GetRequiredService<IActivityLog>();
        var newestBefore = log.Entries.FirstOrDefault();
        IUndoableOperation? operation = null;
        try
        {
            // A job too once it runs long, as a Del is: a copy's undo of a large tree is seconds.
            // Not one to stop: it has left the stack already, and stopped part way what it had put
            // back would be on record nowhere, the rest in the bin with no Ctrl+Z to bring it
            // (review, 2026-10-18). It runs to its end.
            var job = new OperationJob(undo.NextDescription is { } next ? $"Undoing: {next}" : "Undoing", canCancel: false);
            await OperationJob.RunAsync(
                ctx.Services.GetRequiredService<IJobService>(), job,
                async (_, _) => operation = await undo.UndoAsync(CancellationToken.None), CancellationToken.None);
        }
        catch (CannotUndoException ex)
        {
            // Not a failure: it says what it is, and the next Ctrl+Z goes on to what came before.
            ctx.Decisions.Report(new Notice(ex.Message, NoticeSeverity.Info,
                undo.NextDescription is { } next ? $"Ctrl+Z again: {next}." : null));
            // Some of it may have been undone before it stopped (a copy's other folders).
            await ctx.RefreshAsync();
            return;
        }
        catch (UndoBusyException ex)
        {
            // Nothing taken off the stack: the one after waits its turn. Named only behind a running
            // undo: an operation still under way lands its own record above it when it ends.
            ctx.Decisions.Report(new Notice(ex.Message, NoticeSeverity.Info,
                undo.Running is not null && undo.UnderWay.Count == 0 && undo.NextDescription is { } next
                    ? $"Once it is done, Ctrl+Z again: {next}."
                    : null));
            return;
        }
        if (operation is null)
            return;

        // The undo's own entry carries what it left and why ("Kept, because each has changed
        // since: …"). Only in the activity log, the notice said "Undid" over a copy that was all
        // kept, and the file still in the list looked like the undo had failed (2026-10-10).
        // Found by its own description among those recorded since: another undo or a job may have
        // written in between.
        var detail = log.Entries
            .TakeWhile(e => !ReferenceEquals(e, newestBefore))
            .FirstOrDefault(e => e.Outcome == ActivityOutcome.Undone && e.Description == $"Undid: {operation.Description}")
            ?.Detail;
        ctx.Decisions.Report(new Notice($"Undid: {operation.Description}", Detail: detail));
        await ctx.RefreshAsync();
    }

    private static string? ValidateName(string value)
    {
        var name = value.Trim();
        if (name.Length == 0)
            return null; // nothing typed yet is not an error, it just cannot be confirmed
        if (name is "." or "..")
            return "That name is reserved.";
        return name.IndexOfAny(InvalidNameChars) >= 0
            ? "A name cannot contain \\ / : * ? \" < > |"
            : null;
    }

    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>
    /// A new folder's name may be a path, "docs/2026", made level by level. Each level is held to
    /// the rules of a name; ".." is refused so the path cannot climb out of where you are.
    /// </summary>
    internal static string? ValidateFolderPath(string value)
    {
        var segments = value.Trim().Split(Separators, StringSplitOptions.TrimEntries);
        if (segments.All(s => s.Length == 0))
            return null; // nothing typed yet

        // Leading or trailing separators are forgiven; a gap in the middle ("a//b") is a typo.
        var inner = segments.SkipWhile(s => s.Length == 0).Reverse().SkipWhile(s => s.Length == 0).Reverse().ToList();
        if (inner.Any(s => s.Length == 0))
            return "There is an empty name between two slashes.";

        foreach (var segment in inner)
        {
            if (segment is "." or "..")
                return "A folder cannot be called . or .. — and a new one cannot be made outside this folder.";
            if (segment.IndexOfAny(InvalidNameChars) >= 0)
                return "A name cannot contain : * ? \" < > |";
        }

        return null;
    }

    /// <summary>
    /// A new file's name may be a path too, "docs/notes.md", held to New folder's rules, with a
    /// name for the file after the last slash.
    /// </summary>
    /// <remarks>
    /// Stricter than F7 in two ways, because what is made here is then handed to an editor
    /// (review, 2026-10-29). A device name: an editor opened on NUL saves without an error and
    /// keeps nothing. A last dot: "foo." is made beside "foo", and an editor that reads the path
    /// the usual way opens and saves "foo" instead.
    /// </remarks>
    internal static string? ValidateFilePath(string value)
    {
        if (ValidateFolderPath(value) is { } wrong)
            return wrong;
        if (value.TrimEnd().EndsWith('/') || value.TrimEnd().EndsWith('\\'))
            return "The file's name goes after the last slash.";

        foreach (var level in value.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (level.EndsWith('.'))
                return "A name cannot end in a dot.";
            if (ForeignName.NamesDevice(level))
                return $"{level} is a name Windows keeps for a device.";
        }

        return null;
    }

    /// <summary>"docs/2026" → "docs": the level that appears in the listing you are looking at.</summary>
    internal static string TopLevel(string path) =>
        path.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? path.Trim();
}
