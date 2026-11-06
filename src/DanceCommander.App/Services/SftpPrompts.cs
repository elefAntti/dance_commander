using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Providers.Sftp;

namespace DanceCommander.App.Services;

/// <summary>
/// Routes the SFTP provider's questions to the pane whose work needs them (DecisionScope), else
/// to a pane showing that server, else to whichever pane the user is looking at, as inline
/// decisions rather than dialogs (§1.2/2).
/// </summary>
/// <remarks>
/// The provider cannot reference the UI and the shell cannot be a dependency of a provider, so
/// the connection between them is this: the shell registers the active decision host here, and
/// the pool asks through the interface. It means a connection prompt appears where the user's
/// attention already is, and that they can ignore it, keep browsing the other pane, and come
/// back to it — a modal password box would forbid all of that.
/// </remarks>
public sealed class SftpPrompts : ISftpPrompts
{
    private Func<Func<FsUri, bool>, IDecisionHost?>? _host;

    /// <summary>
    /// Called by the shell once it knows how to find a pane: the one showing the place asked
    /// about, else the active one.
    /// </summary>
    public void UseHost(Func<Func<FsUri, bool>, IDecisionHost?> host) => _host = host;

    /// <summary>The pane whose listing asked, else one showing this server, else the active one.</summary>
    private IDecisionHost? HostFor(SftpTarget target) =>
        DecisionScope.Current ?? _host?.Invoke(place => Shows(place, target));

    private static bool Shows(FsUri place, SftpTarget target)
    {
        if (!place.Scheme.Equals("sftp", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            return SftpTarget.From(place) == target;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public async Task<SftpPasswordAnswer?> AskPasswordAsync(SftpTarget target, bool retry, CancellationToken ct)
    {
        if (HostFor(target) is not { } host)
            return null;

        var answer = await host.AskSecretAsync(
            new SecretRequest(
                $"Password for {target}",
                retry
                    ? "That password was not accepted. The saved one, if there was one, has been forgotten."
                    : $"Connecting to {target.Host} on port {target.Port}.",
                RememberLabel: "Remember this password",
                FollowsFront: place => Shows(place, target)),
            ct).ConfigureAwait(true);

        return answer is null ? null : new SftpPasswordAnswer(answer.Secret, answer.Remember);
    }

    public async Task<bool> ConfirmUnknownHostAsync(
        SftpTarget target, HostKeyPresentation key, CancellationToken ct)
    {
        if (HostFor(target) is not { } host)
            return false;

        // The fingerprint is the whole point of the question, so it is the thing shown, in the
        // same form ssh prints it — so it can be compared against what the server's admin says.
        var choice = await host.AskAsync(
            new ChoiceRequest(
                $"First connection to {target.Host}",
                [
                    new DecisionOption("trust", "Trust this host"),
                    new DecisionOption("no", "Cancel", IsDefault: true, IsCancel: true),
                ],
                $"{key.KeyType} key fingerprint:\n{key.Fingerprint}\n\n" +
                "Check this matches the server before accepting. It will be remembered.",
                NoticeSeverity.Warning,
                FollowsFront: place => Shows(place, target)),
            ct).ConfigureAwait(true);

        return choice == "trust";
    }

    public async Task<bool> ConfirmChangedHostAsync(
        SftpTarget target, HostKeyPresentation key, CancellationToken ct)
    {
        if (HostFor(target) is not { } host)
            return false;

        // Deliberately harder to accept than the first-use case: cancel is the default, the
        // wording does not soften it, and nothing has been sent to the server at this point.
        var choice = await host.AskAsync(
            new ChoiceRequest(
                $"The host key for {target.Host} has changed",
                [
                    new DecisionOption("no", "Do not connect", IsDefault: true, IsCancel: true),
                    new DecisionOption("replace", "Replace the stored key"),
                ],
                $"The server is offering a {key.KeyType} key with fingerprint:\n{key.Fingerprint}\n\n" +
                "This happens when a server is rebuilt — and also when something is intercepting " +
                "the connection. Your password has not been sent. Only continue if you know why it changed.",
                NoticeSeverity.Error,
                FollowsFront: place => Shows(place, target)),
            ct).ConfigureAwait(true);

        return choice == "replace";
    }
}
