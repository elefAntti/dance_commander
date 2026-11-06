namespace DanceCommander.Core.Decisions;

public enum NoticeSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record DecisionOption(string Id, string Label, bool IsDefault = false, bool IsCancel = false);

/// <param name="TakesFocus">
/// Whether the question takes the keyboard when it appears. Yes for anything the user just asked
/// for — the answer is the next thing they will type. No for a question the application raises on
/// its own, such as offering new tools at startup: taking the keyboard then sent the next Enter,
/// meant for the listing, to the default button, and answered the question for them.
/// </param>
/// <param name="FollowsFront">
/// A question that waits on the user rather than on this tab, such as a connection's password:
/// when its tab goes behind another showing a place this says is on the same connection, it moves
/// to the one in front. Others asking for the same connection wait behind it, and with it out of
/// sight they said only "Connecting…" (2026-10-14). Only those: a tab elsewhere, brought forward,
/// had the password prompt for a server it has nothing to do with (2026-10-26).
/// </param>
/// <param name="LateDetail">
/// A detail still being worked out, which replaces <paramref name="Detail"/> when it arrives:
/// the question goes up at once saying what is being looked into, rather than after it (§1.3).
/// A null result leaves <paramref name="Detail"/> as it was.
/// </param>
public sealed record ChoiceRequest(
    string Title,
    IReadOnlyList<DecisionOption> Options,
    string? Detail = null,
    NoticeSeverity Severity = NoticeSeverity.Info,
    bool TakesFocus = true,
    Task<string?>? LateDetail = null,
    Func<Fs.FsUri, bool>? FollowsFront = null);

/// <param name="SelectStemOnly">
/// Pre-select <paramref name="InitialValue"/> up to its extension rather than the whole thing.
/// Renaming a file is almost always renaming it, not re-choosing its type, so the common case
/// should not require retyping ".docx" every time -- while leaving Ctrl+A one keystroke away for
/// the times it really is the extension changing.
/// </param>
public sealed record TextRequest(
    string Title,
    string InitialValue = "",
    string ConfirmLabel = "OK",
    string? Detail = null,
    Func<string, string?>? Validate = null,
    bool SelectStemOnly = false);

/// <param name="RememberLabel">When set, the prompt offers to save the secret under this label.</param>
/// <param name="FollowsFront">See <see cref="ChoiceRequest.FollowsFront"/>.</param>
public sealed record SecretRequest(string Title, string? Detail = null, string? RememberLabel = null, Func<Fs.FsUri, bool>? FollowsFront = null);

/// <param name="Remember">True when the user asked for the secret to be saved.</param>
public sealed record SecretAnswer(string Secret, bool Remember);

public sealed record Notice(string Message, NoticeSeverity Severity = NoticeSeverity.Info, string? Detail = null);

/// <summary>
/// How an operation asks the user something. Per §1.2/2 there are no modal dialogs: the host
/// renders the request inline in the pane or the transfer strip and the user may ignore it,
/// keep browsing, and come back. Every implementation must therefore be able to return a
/// pending result that completes much later, or not at all.
/// </summary>
public interface IDecisionHost
{
    /// <summary>Returns the chosen option id, or null if the user dismissed or cancelled it.</summary>
    Task<string?> AskAsync(ChoiceRequest request, CancellationToken ct);

    /// <summary>Returns the entered text, or null if dismissed or cancelled.</summary>
    Task<string?> AskTextAsync(TextRequest request, CancellationToken ct);

    /// <summary>
    /// Asks for a secret. Separate from <see cref="AskTextAsync"/> on purpose: what comes back is
    /// a password, and it must not travel the same path as a filename, which is pre-filled from
    /// the selection, kept in view state, and perfectly safe to log.
    /// </summary>
    Task<SecretAnswer?> AskSecretAsync(SecretRequest request, CancellationToken ct);

    /// <summary>Fire-and-forget message. Errors are shown in place, never in a message box.</summary>
    void Report(Notice notice);
}

/// <summary>
/// Shows the output of something that ran. Transient like every other reporting surface: it
/// appears in the pane, is dismissible, and never blocks (§1.2/2).
/// </summary>
public interface IOutputHost
{
    void ShowOutput(string title, string text, bool failed = false);

    /// <summary>
    /// Something finished, said nothing, and needs no answer: "✓ touch foo", in passing (§1.3:
    /// completion is boring). The activity log keeps the record; nothing has to be dismissed.
    /// </summary>
    void ShowDone(string text)
    {
    }

    /// <summary>
    /// Shows a running job's output and keeps it current as the job prints (§6.17). Dismissing
    /// the panel stops the following, not the job.
    /// </summary>
    void Follow(Jobs.Job job);
}
