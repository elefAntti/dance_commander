using System.IO;
using DanceCommander.App.Services;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace DanceCommander.App.Commands;

/// <summary>
/// Split into parts and Join parts (<see cref="PartFiles"/>): a large file cut into pieces a
/// server will take, and put back together on the other side.
/// </summary>
/// <remarks>
/// Two ordinary operations rather than something a copy does on its own, so what happens is in
/// plain sight: the parts are files you can see, copy anywhere and join with any tool. Both land
/// beside the file they start from, as Extract here does, and both are undone by removing exactly
/// what they made, only while it is unchanged.
/// </remarks>
internal static class PartCommands
{
    private const string OfferedSize = "100 MB";

    // Not a part itself: big.iso.001.001 is a split nobody means.
    internal static bool CanSplit(ICommandContext ctx) =>
        !ctx.Directory.IsArchiveEntry && ctx.Selection is [{ IsDirectory: false, Size: > 1 } file] && !IsPart(file.Name);

    private static bool IsPart(string name) =>
        name.Length > 4 && name[^4] == '.' && name[^3..].All(char.IsAsciiDigit);

    internal static bool CanJoin(ICommandContext ctx) =>
        !ctx.Directory.IsArchiveEntry && ctx.Selection is [{ IsDirectory: false } part] && PartFiles.IsFirstPart(part.Name);

    internal static async Task SplitAsync(ICommandContext ctx, CancellationToken ct)
    {
        // Read once: the size question lets the user click about meanwhile.
        if (ctx.Selection is not [{ IsDirectory: false } file] || file.Uri.Parent is not { } directory)
            return;

        var typed = await ctx.Decisions.AskTextAsync(
            new TextRequest(
                $"Split {file.Name} ({Format.Size(file.Size)}) into parts of",
                OfferedSize,
                "Split",
                Detail: $"Beside it as {PartFiles.PartName(file.Name, 1)}, {PartFiles.PartName(file.Name, 2)} …, "
                    + $"with {PartFiles.ChecksumName(file.Name)} to check the file when it is joined.",
                Validate: text => PartFiles.ParseSize(text) switch
                {
                    null => "A size, such as 100 MB or 1.5 GB.",
                    var size when size >= file.Size => "That is the whole file; choose a smaller size.",
                    var size when PartFiles.CountParts(file.Size, size.Value) > PartFiles.MostParts =>
                        $"That makes more than {PartFiles.MostParts} parts; choose a larger size.",
                    _ => null,
                }),
            ct);

        if (string.IsNullOrWhiteSpace(typed) || PartFiles.ParseSize(typed) is not { } partSize)
            return;

        var providers = ctx.Services.GetRequiredService<IProviderRegistry>();
        // Gigabytes take a while: Ctrl+Z meanwhile says so rather than undo something beside it.
        using var working = ctx.Services.GetRequiredService<IUndoStack>().Working($"splitting {file.Name}");
        IReadOnlyList<FsUri> made;
        try
        {
            made = await PartFiles.SplitAsync(providers, file.Uri, directory, partSize, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            ctx.Decisions.Report(new Notice($"Could not split {file.Name}.", NoticeSeverity.Error, ex.Message));
            return;
        }

        var parts = made.Count - 1;
        var description = $"Split {file.Name} into {parts:N0} parts";
        var shell = $"split -b {partSize} -a 3 --numeric-suffixes=1 {ShellText.Quote(file.Name)} {ShellText.Quote(file.Name + ".")}"
            + $" && sha256sum -b {ShellText.Quote(file.Name)} > {ShellText.Quote(PartFiles.ChecksumName(file.Name))}";

        // Undo removes the parts only while the file they came from is still there as it was:
        // otherwise they are the last copy of it.
        await RecordAsync(ctx, working, providers.Resolve(directory), made, [file.Uri], description, shell);
        ctx.Decisions.Report(new Notice(
            $"Split {file.Name} into {parts:N0} parts.",
            Detail: $"Copy all of them and {PartFiles.ChecksumName(file.Name)}; Join parts on {PartFiles.PartName(file.Name, 1)} puts it back."));
    }

    internal static async Task JoinAsync(ICommandContext ctx, CancellationToken ct)
    {
        if (ctx.Selection is not [{ IsDirectory: false } first] || first.Uri.Parent is not { } directory)
            return;

        var providers = ctx.Services.GetRequiredService<IProviderRegistry>();
        var provider = providers.Resolve(directory);
        using var working = ctx.Services.GetRequiredService<IUndoStack>().Working($"joining {first.Name}");

        FsUri target;
        (int Parts, bool Checked) joined;
        try
        {
            // Beside the parts under the name they came from; if that is taken (joining where the
            // original still is), under a free one, as Keep both does. Never over anything.
            var wanted = directory.Child(PartFiles.WholeName(first.Name));
            target = await provider.StatAsync(wanted, ct) is null
                ? wanted
                : await BuiltInCommands.AvailableAsync(provider, wanted, ct);

            joined = await PartFiles.JoinAsync(providers, first.Uri, target, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            ctx.Decisions.Report(new Notice($"Could not join {first.Name}.", NoticeSeverity.Error, ex.Message));
            return;
        }

        var whole = PartFiles.WholeName(first.Name);
        var partUris = Enumerable.Range(1, joined.Parts).Select(n => directory.Child(PartFiles.PartName(whole, n))).ToList();
        var description = $"Joined {joined.Parts:N0} parts into {target.Name}";
        var shell = $"copy /b {string.Join("+", partUris.Select(u => ShellText.Quote(u.Name)))} {ShellText.Quote(target.Name)}";

        // Undo removes the joined file only while all its parts are still there: otherwise it is
        // the last copy of what they held.
        await RecordAsync(ctx, working, provider, [target], partUris, description, shell);
        var checksum = PartFiles.ChecksumName(PartFiles.WholeName(first.Name));
        ctx.Decisions.Report(joined.Checked
            ? new Notice($"Joined {target.Name}; it matches {checksum}.")
            : new Notice(
                $"Joined {target.Name}.",
                NoticeSeverity.Warning,
                $"There was no {checksum} beside the parts, so it could not be checked."));
    }

    /// <summary>
    /// The log line and the undo for files an operation made, as every mutation has (§1.2/6).
    /// </summary>
    /// <param name="made">What undo removes.</param>
    /// <param name="sources">What must still be there, unchanged, for undo to remove it.</param>
    private static async Task RecordAsync(
        ICommandContext ctx, IUndoSlot working, IFileSystemProvider provider, IReadOnlyList<FsUri> made,
        IReadOnlyList<FsUri> sources, string description, string shell)
    {
        // As they are now, so undo can tell them from what has changed since. A stat that fails is
        // recorded as unknown, which undo refuses: the operation is logged and on the undo stack
        // either way, never logged with nothing pushed and Ctrl+Z reaching something older.
        var asMade = await SnapshotAsync(provider, made);
        var asFound = await SnapshotAsync(provider, sources);

        var log = ctx.Services.GetRequiredService<IActivityLog>();
        log.Record(description, shell, ActivityOutcome.Succeeded);
        working.Push(new DelegateUndo(
            description, shell, token => UndoAsync(provider, log, asMade, asFound, description, token)));
        // Let go as soon as it is recorded: the refresh can be slow, and a Ctrl+Z meanwhile said
        // "Still splitting" after the echo had offered it.
        working.Dispose();

        await ctx.RefreshAsync();
        ctx.Services.GetService<IArrivalFeed>()?.Report(made);
    }

    /// <summary>
    /// Removes what the operation made, and only that: if any of it has changed since, none of it
    /// goes, because half a set of parts is worse than a whole one left behind.
    /// </summary>
    private static async Task UndoAsync(
        IFileSystemProvider provider, IActivityLog log, IReadOnlyList<(FsUri Uri, FsEntry? Entry)> made,
        IReadOnlyList<(FsUri Uri, FsEntry? Entry)> sources, string description, CancellationToken ct)
    {
        // What it was made from first: gone or changed since, and what was made is the last copy.
        foreach (var (uri, entry) in sources)
        {
            if (!Same(entry, await provider.StatAsync(uri, ct).ConfigureAwait(false)))
                throw new CannotUndoException($"{uri.Name} is no longer there as it was, so what was made from it is kept.");
        }

        var there = new List<FsUri>();
        foreach (var (uri, entry) in made)
        {
            var now = await provider.StatAsync(uri, ct).ConfigureAwait(false);
            if (now is null)
                continue;
            if (!Same(entry, now))
                throw new CannotUndoException($"{uri.Name} has changed since it was made, so nothing was removed.");
            there.Add(uri);
        }

        var removed = new List<FsUri>();
        try
        {
            foreach (var uri in there)
            {
#pragma warning disable RS0030 // §1.6: exactly the files this operation wrote, unchanged since, while what they
                // were made from is still there, unchanged (both checked above).
                await provider.DeleteAsync(uri, recursive: false, ct).ConfigureAwait(false);
#pragma warning restore RS0030
                removed.Add(uri);
            }
        }
        finally
        {
            // Recorded even when it stops partway: what went is gone, whatever stopped the rest.
            var whole = removed.Count == there.Count;
            log.Record(
                whole ? $"Undid: {description}" : $"Undo stopped partway: {description}",
                ShellText.Lines(removed.Select(ShellText.Delete)),
                whole ? ActivityOutcome.Undone : ActivityOutcome.Failed);
        }
    }

    /// <summary>Unchanged since recorded; a file whose state was not known never counts as unchanged.</summary>
    private static bool Same(FsEntry? then, FsEntry? now) =>
        then is { Modified: not null } && now is { Modified: not null }
        && now.Size == then.Size && now.Modified == then.Modified;

    private static async Task<List<(FsUri Uri, FsEntry? Entry)>> SnapshotAsync(
        IFileSystemProvider provider, IReadOnlyList<FsUri> uris)
    {
        var snapshot = new List<(FsUri Uri, FsEntry? Entry)>();
        foreach (var uri in uris)
        {
            FsEntry? entry;
            try
            {
                entry = await provider.StatAsync(uri, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Any failure (a WebDAV connection drop is an HttpRequestException) is "unknown".
                entry = null;
            }

            snapshot.Add((uri, entry));
        }

        return snapshot;
    }
}
