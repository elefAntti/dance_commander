using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Listing;

/// <param name="Id">What a <c>from:</c> URI names. Part of a bookmarkable address, so it is stable.</param>
/// <param name="Extract">
/// A regular expression matched repeatedly against the whole output. Each match is one row. The
/// path comes from a group named <c>path</c>, or from group 1 when there is no such group; every
/// other named group becomes a column value.
/// </param>
/// <param name="Columns">
/// Extra columns, by the name of the group that fills them. Declared rather than inferred so that
/// the header text is the author's to choose and the order does not depend on the regex.
/// </param>
/// <param name="InRepository">Offer this listing only inside a git work tree.</param>
/// <param name="IsScript">
/// A script location (§6.16) rather than a list of paths: the command is asked <c>list</c> and
/// <c>read</c> for each level, and its rows are its own, not files elsewhere.
/// </param>
/// <param name="Secrets">
/// Names of secrets from the credential store the script needs, given to it in the environment as
/// <c>DC_SECRET_NAME</c>. Asked for once, inline, when not stored yet.
/// </param>
/// <param name="Sort">
/// The column a script location starts sorted by — <c>name</c>, <c>modified</c> or <c>size</c> —
/// the latter two newest or largest first, as a click on their headers does. Pipelines are a
/// "what happened lately": sorted by name, the oldest came first.
/// </param>
/// <param name="ShowModified">
/// Whether the rows get a Modified column. For files it is the useful column it always is; for a
/// list of folders it is only the time something at their top level last changed, which says
/// little about a worktree or a build directory and takes room from the columns that do.
/// </param>
public sealed record CommandListing(
    string Id,
    string Title,
    string Exec,
    IReadOnlyList<string> Args,
    string Extract,
    IReadOnlyList<CommandListingColumn>? Columns = null,
    bool InRepository = false,
    bool ShowModified = true,
    bool IsScript = false,
    IReadOnlyList<string>? Secrets = null,
    string? Sort = null);

/// <param name="Flexible">
/// Takes the width the other columns leave over, instead of the path. For a column that is the
/// point of the listing — the branch in a list of worktrees — rather than a detail beside it.
/// </param>
public sealed record CommandListingColumn(string Group, string Header, double Width = 120, bool Flexible = false);

/// <summary>The command that goes to a listing, which is what puts it in the menus and the palette.</summary>
/// <remarks>
/// One place for both callers: the shell's built-in listings and tools.json's, which are loaded
/// after the shell and again on every edit of the file. A listing with no command is reachable only
/// by typing its address.
/// </remarks>
public static class CommandListingCommands
{
    public static string IdFor(CommandListing listing) => $"dc.listing.{listing.Id}";

    public static CommandDefinition For(CommandListing listing) => new()
    {
        Id = IdFor(listing),
        Title = listing.Title,
        Group = "Navigate",
        // A listing is somewhere to go from here, whatever row happens to be focused.
        Subject = CommandSubject.Location,
        CanExecute = context =>
            context.Directory.IsLocal && (!listing.InRepository || context.InRepository),
        ExecuteAsync = (context, _) =>
            context.NavigateAsync(FsUri.View("from", context.Directory.Anchor, listing.Id)),
    };
}

/// <summary>Where <c>from:</c> listings are looked up by id.</summary>
public interface ICommandListingCatalog
{
    CommandListing? Find(string id);

    IReadOnlyList<CommandListing> All { get; }

    void Register(CommandListing listing);

    /// <summary>Drops the listings that came from a config file, before reloading it.</summary>
    void RemoveWhere(Func<CommandListing, bool> predicate);
}

public sealed class CommandListingCatalog : ICommandListingCatalog
{
    private readonly ConcurrentDictionary<string, CommandListing> _byId = new(StringComparer.OrdinalIgnoreCase);

    public CommandListing? Find(string id) => _byId.GetValueOrDefault(id);

    public IReadOnlyList<CommandListing> All => _byId.Values.OrderBy(l => l.Title).ToList();

    public void Register(CommandListing listing) => _byId[listing.Id] = listing;

    public void RemoveWhere(Func<CommandListing, bool> predicate)
    {
        foreach (var listing in _byId.Values.Where(predicate).ToList())
            _byId.TryRemove(listing.Id, out _);
    }
}

/// <summary>
/// Runs a program and returns everything it printed.
/// </summary>
/// <remarks>
/// Deliberately not the tools engine's runner, which exists to launch things for the user and
/// knows about shells, windows and the activity log. This one is a function from a command line to
/// a string, for the places that want an answer rather than an effect.
/// </remarks>
public static class ProcessOutput
{
    public static async Task<(int ExitCode, string StdOut, string StdErr)?> RunAsync(
        string exec,
        IReadOnlyList<string> args,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var info = new ProcessStartInfo(exec)
        {
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        foreach (var argument in args)
            info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return null;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);

            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);

            return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // Took longer than we were willing to wait.
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Not installed, or it died on the way up. Not an error condition worth a stack trace:
            // the listing reports that it found nothing and says why.
            return null;
        }
    }
}

/// <summary>Column sets for listings whose rows came from a command.</summary>
public static class CommandListingColumns
{
    public static ColumnSet For(CommandListing listing)
    {
        var extras = listing.Columns ?? [];
        // One column takes the slack. A listing that names one of its own gets that; otherwise
        // it is the path.
        var ownFlexible = extras.FirstOrDefault(c => c.Flexible);

        var columns = new List<ColumnDefinition>
        {
            new(ColumnIds.Name, "Name", ColumnKind.Name, 200),
            // The path, trimmed from the front. These rows are from all over the filesystem and
            // their names are often near-identical -- three checkouts of one repository -- so
            // where each one is is usually the column doing the work.
            new(ColumnIds.RelativePath, "Path", ColumnKind.Path, 260, Flexible: ownFlexible is null),
        };

        foreach (var extra in extras)
            columns.Add(new ColumnDefinition(
                extra.Group, extra.Header, ColumnKind.Text, extra.Width, Flexible: ReferenceEquals(extra, ownFlexible)));

        // Always present, because a row that is not there needs somewhere to say so, and a listing
        // that quietly dropped it would be a list of worktrees missing the one you deleted.
        columns.Add(new ColumnDefinition(ColumnIds.Note, "Note", ColumnKind.Text, 84));
        if (listing.ShowModified)
            columns.Add(new ColumnDefinition(ColumnIds.Modified, "Modified", ColumnKind.Date, 148));

        return new ColumnSet(columns);
    }
}
