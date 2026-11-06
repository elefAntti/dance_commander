namespace DanceCommander.Core.Decisions;

/// <summary>
/// The pane a piece of work belongs to, for questions raised far below it: a provider asking for
/// a password knows nothing of panes. It flows with the work through every await and worker
/// thread, so a listing's connection prompt appears in the pane doing the listing.
/// </summary>
/// <remarks>
/// Without it, prompts went to whichever pane was active. Resuming a session with an SFTP pane
/// on the right put its password prompt over the local listing on the left, while the right pane
/// said only "Connecting…". Work with no pane of its own (a copy started from a command) has no
/// scope, and its prompts still go to the active pane.
/// </remarks>
public static class DecisionScope
{
    private static readonly AsyncLocal<IDecisionHost?> Host = new();

    /// <summary>Set by a pane at the start of work it owns; an async method's setting ends with it.</summary>
    public static IDecisionHost? Current
    {
        get => Host.Value;
        set => Host.Value = value;
    }
}

/// <summary>
/// The scope of work nobody asked for, such as listing a folder ahead of being opened: it has no
/// pane to ask in, and a password prompt it raised would be a question out of nowhere. Every
/// question is declined, so the work fails quietly instead, as if the prompt had been cancelled.
/// </summary>
public sealed class NoOneAsks : IDecisionHost
{
    public static NoOneAsks Instance { get; } = new();

    private NoOneAsks()
    {
    }

    public Task<string?> AskAsync(ChoiceRequest request, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<string?> AskTextAsync(TextRequest request, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<SecretAnswer?> AskSecretAsync(SecretRequest request, CancellationToken ct) => Task.FromResult<SecretAnswer?>(null);

    public void Report(Notice notice)
    {
    }
}
