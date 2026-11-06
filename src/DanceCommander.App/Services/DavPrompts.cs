using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Providers.WebDav;

namespace DanceCommander.App.Services;

/// <summary>
/// Routes the WebDAV provider's password question to whichever pane the user is looking at, as an
/// inline decision rather than a dialog (§1.2/2). The SFTP counterpart, with one difference.
/// </summary>
/// <remarks>
/// The difference is that a WebDAV address need not carry a user name. <c>sftp://host/</c> can
/// default to the logged-in account because that is what ssh does; <c>dav://host/</c> cannot,
/// because a web server's accounts have nothing to do with the machine's. So when the address does
/// not say, the user name is asked for alongside the password rather than guessed at.
/// </remarks>
public sealed class DavPrompts : IDavPrompts
{
    private Func<Func<FsUri, bool>, IDecisionHost?>? _host;

    /// <summary>
    /// Called by the shell once it knows how to find a pane: the one showing the place asked
    /// about, else the active one.
    /// </summary>
    public void UseHost(Func<Func<FsUri, bool>, IDecisionHost?> host) => _host = host;

    /// <summary>The pane whose listing asked, else one showing this server, else the active one.</summary>
    private IDecisionHost? HostFor(DavTarget target) =>
        DecisionScope.Current ?? _host?.Invoke(place => Shows(place, target));

    private static bool Shows(FsUri place, DavTarget target)
    {
        if (!place.Scheme.Equals(DavTarget.Scheme, StringComparison.OrdinalIgnoreCase)
            && !place.Scheme.Equals(DavTarget.SecureScheme, StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            return DavTarget.From(place) == target;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public async Task<DavPasswordAnswer?> AskPasswordAsync(DavTarget target, bool retry, CancellationToken ct)
    {
        if (HostFor(target) is not { } host)
            return null;

        var user = target.User;

        if (user.Length == 0)
        {
            // Named, because TextRequest's second positional parameter is the initial *value*:
            // passed positionally, the explanation arrived pre-typed into the field.
            var typed = await host.AskTextAsync(
                new TextRequest(
                    $"User name for {target.Host}",
                    Detail: $"{target.Host} wants a user name and password.",
                    Validate: value => string.IsNullOrWhiteSpace(value) ? "A user name is needed." : null),
                ct).ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(typed))
                return null;

            user = typed.Trim();
        }

        var answer = await host.AskSecretAsync(
            new SecretRequest(
                $"Password for {user}@{target.Host}",
                retry
                    ? "That password was not accepted. The saved one, if there was one, has been forgotten."
                    : $"Connecting to {target.Host} on port {target.Port}.",
                RememberLabel: "Remember this password",
                FollowsFront: place => Shows(place, target)),
            ct).ConfigureAwait(true);

        return answer is null ? null : new DavPasswordAnswer(user, answer.Secret, answer.Remember);
    }
}
