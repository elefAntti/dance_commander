using System.Collections.Concurrent;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Listing;
using DanceCommander.Core.Security;

namespace DanceCommander.App.Services;

/// <summary>
/// The secrets script locations declare (§6.16): from the OS vault when stored, otherwise asked for
/// inline in the pane — never a dialog (§1.2/2) — and stored only if the user says so.
/// </summary>
public sealed class ScriptSecrets(ICredentialStore vault) : IScriptSecrets
{
    private Func<IDecisionHost?>? _host;

    /// <summary>
    /// Given but not to be remembered: kept for this session, or a script location would ask again
    /// for every level you opened.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _session = new(StringComparer.Ordinal);

    public void UseHost(Func<IDecisionHost?> host) => _host = host;

    public async Task<string?> GetAsync(string name, string listingTitle, CancellationToken ct)
    {
        var key = $"script:{name}";
        if (vault.Read(key) is { } stored)
            return stored;
        if (_session.TryGetValue(key, out var given))
            return given;

        if ((DecisionScope.Current ?? _host?.Invoke()) is not { } host)
            return null;

        var answer = await host.AskSecretAsync(
            new SecretRequest(
                $"{listingTitle} needs \"{name}\"",
                "Given to the script in its environment as " + ScriptProvider.SecretVariable(name) +
                ", never on its command line.",
                RememberLabel: "Remember it in Windows Credential Manager"),
            ct).ConfigureAwait(true);

        if (answer is null)
            return null;

        if (answer.Remember)
            vault.Write(key, answer.Secret);
        else
            _session[key] = answer.Secret;

        return answer.Secret;
    }
}
