using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Operations;

/// <summary>A program that has a path open, as far as Windows will say.</summary>
/// <param name="Description">What the user would call it: "Windows PowerShell in Windows Terminal".</param>
public sealed record Holder(int ProcessId, string Description);

/// <summary>
/// Finds which programs keep paths from being deleted (DESIGN.md §1.3: an error names its cause).
/// Only asked once something has failed: it can take seconds, and costs nothing until then.
/// </summary>
public interface IUsageFinder
{
    Task<IReadOnlyList<Holder>> FindAsync(IReadOnlyList<string> paths, CancellationToken ct);
}

/// <summary>A path is open in another program, so it cannot be deleted or moved yet.</summary>
public sealed class InUseException(IReadOnlyList<string> paths, string message) : IOException(message, SharingViolationHResult)
{
    internal const int SharingViolationHResult = unchecked((int)0x80070020);

    public IReadOnlyList<string> Paths { get; } = paths;
}

/// <summary>What the user chose when something was in use.</summary>
public enum InUseOutcome
{
    /// <summary>It went through, at once or after a Retry.</summary>
    Done,

    /// <summary>Left alone, the rest carries on (only where Skip was offered).</summary>
    Skipped,

    /// <summary>Cancelled: nothing more is attempted.</summary>
    GaveUp,
}

public static class InUse
{
    /// <summary>
    /// Runs the attempt and, while it fails because another program has the entries open, asks
    /// inline whether to Retry, naming the program once it is found (DESIGN.md §1.3: an error carries
    /// its next action and names its cause). Any other failure goes on up as it always did.
    /// Shared by delete, rename and the transfer engine, so they ask the same question.
    /// </summary>
    public static async Task<InUseOutcome> RetryAsync(
        IReadOnlyList<FsUri> entries,
        Func<Task> attempt,
        IDecisionHost decisions,
        IUsageFinder? usage,
        bool offerSkip,
        CancellationToken ct,
        IReadOnlyList<FsUri>? alsoAskAbout = null)
    {
        while (true)
        {
            try
            {
                await attempt().ConfigureAwait(false);
                return InUseOutcome.Done;
            }
            catch (Exception ex) when (Is(ex))
            {
                // Named by what is still there: a recycle that stops part way has already sent the
                // rest, and "2 items are in use" over the one file left had the eye looking for a
                // second (2026-10-10). The shell doesn't say which one failed; the disk does.
                var left = entries.Where(e => !e.IsLocal || File.Exists(e.ToLocalPath()) || Directory.Exists(e.ToLocalPath())).ToList();
                if (left.Count == 0)
                    left = [.. entries];
                var title = left.Count == 1 ? $"{left[0].Name} is in use" : $"{left.Count:N0} items are in use";

                // The target too, for a transfer: an overwrite fails as surely when the file being
                // replaced is open somewhere, and the question is still about the file being moved.
                // What the provider found held, when it looked (a file deep inside a folder) first:
                // the finder stops after so many files, and behind a big folder it went unseen.
                var local = ((ex as InUseException)?.Paths ?? [])
                    .Concat(left.Concat(alsoAskAbout ?? []).Where(e => e.IsLocal).Select(e => e.ToLocalPath()))
                    .Distinct().ToList();
                var who = usage is null || local.Count == 0 ? null : WhoAsync(usage, local, ct);

                List<DecisionOption> options = [new("retry", "Retry", IsDefault: true)];
                if (offerSkip)
                    options.Add(new("skip", "Skip"));
                options.Add(new("cancel", "Cancel", IsCancel: true));

                // Not red (§1.3): nothing is lost, and the way out is in the message.
                var answer = await decisions.AskAsync(
                    new ChoiceRequest(
                        title,
                        options,
                        who is null ? "Another program has it open. Close it there, then Retry." : "Finding out which program has it open…",
                        NoticeSeverity.Warning,
                        LateDetail: who),
                    ct).ConfigureAwait(false);

                switch (answer)
                {
                    case "retry":
                        continue;
                    case "skip" when offerSkip:
                        return InUseOutcome.Skipped;
                    default:
                        return InUseOutcome.GaveUp;
                }
            }
        }
    }

    private static async Task<string?> WhoAsync(IUsageFinder usage, IReadOnlyList<string> paths, CancellationToken ct)
    {
        try
        {
            return Explain(await usage.FindAsync(paths, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return Explain([]);
        }
    }

    /// <summary>
    /// True for "another program has it open": Windows' sharing and lock violations, however
    /// they arrive. A provider's IOException carries them as an HRESULT, the shell as its code.
    /// </summary>
    public static bool Is(Exception exception) =>
        exception is InUseException ||
        (exception is IOException && (uint)exception.HResult is 0x80070020 or 0x80070021);

    /// <summary>The detail line of the question, once the search has an answer.</summary>
    public static string Explain(IReadOnlyList<Holder> holders)
    {
        if (holders.Count == 0)
            return "Windows did not say which program. Close whatever has it open, then Retry.";

        var named = holders.Take(3).Select(h => h.Description).ToList();
        if (holders.Count > named.Count)
            named.Add($"{holders.Count - named.Count} more");

        var who = named.Count == 1
            ? named[0]
            : string.Join(", ", named.Take(named.Count - 1)) + " and " + named[^1];
        return holders.Count == 1
            ? $"Open in {who}. Close it there, then Retry."
            : $"Open in {who}. Close them there, then Retry.";
    }
}
