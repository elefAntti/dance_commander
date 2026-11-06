using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Security;

namespace DanceCommander.Providers.WebDav;

/// <summary>
/// A server that wants a password over plain dav:// while that isn't allowed (BUGS.md 54). An
/// IOException, so every caller that passes over a place it can't reach (a transfer's clean-up,
/// an undo's folders, a flat view's walk) does so here too. Not an UnauthorizedAccessException,
/// which the pane words as "you do not have permission" and would hide what to do; nor a
/// NotSupportedException, which those callers let escape (review, 2026-11-04).
/// </summary>
public sealed class UnencryptedSignInRefusedException(string message) : IOException(message);

/// <param name="Remember">Whether to put the password in the credential store.</param>
public sealed record DavPasswordAnswer(string User, string Password, bool Remember);

/// <summary>
/// How the provider asks the one question only the user can answer. An abstraction for the same
/// reason SFTP has one: the provider stays free of the UI, the application implements it over the
/// active pane's inline decision host, and a test answers directly.
/// </summary>
public interface IDavPrompts
{
    /// <param name="retry">True when a password was already tried and rejected.</param>
    Task<DavPasswordAnswer?> AskPasswordAsync(DavTarget target, bool retry, CancellationToken ct);
}

/// <summary>Never asks. For servers that turn out not to want credentials.</summary>
public sealed class NoDavPrompts : IDavPrompts
{
    public Task<DavPasswordAnswer?> AskPasswordAsync(DavTarget target, bool retry, CancellationToken ct) =>
        Task.FromResult<DavPasswordAnswer?>(null);
}

public interface IDavConnections
{
    /// <summary>
    /// Sends a request to this server, obtaining credentials if the server asks for them.
    /// </summary>
    /// <param name="make">
    /// Builds the request. A factory and not a message, because a request that has been sent
    /// cannot be sent again and answering a challenge means sending it again.
    /// </param>
    /// <param name="completion">
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> for a body that is a file: the time
    /// limit then covers only the server's answer, and the body is the caller's to read.
    /// </param>
    /// <param name="limit">
    /// How long the whole request may take; <see cref="DavConnections.Silence"/> if not given. Longer
    /// for a request that carries a piece of a file or makes the server assemble one.
    /// </param>
    Task<HttpResponseMessage> SendAsync(
        DavTarget target, Func<HttpRequestMessage> make, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, TimeSpan? limit = null);

    /// <summary>
    /// The authenticated client for this server, for the one case that cannot go through
    /// <see cref="SendAsync"/>: a streamed upload, whose body cannot be replayed to answer a
    /// challenge. The caller is expected to have made an ordinary request first.
    /// </summary>
    Task<HttpClient> ClientFor(DavTarget target, CancellationToken ct);

    /// <summary>Called when a stored password turns out to be wrong.</summary>
    void Forget(DavTarget target);
}

/// <summary>
/// One <see cref="HttpClient"/> per server, authenticating when the server says to (§6.9).
/// </summary>
/// <remarks>
/// Credentials are obtained in response to a 401 on the request that actually matters, rather than
/// by probing the server first. The probe was the original design and it was wrong in a way only a
/// real server showed: on Nextcloud the DAV area is a sub-path, <c>PROPFIND /</c> answers 405, and
/// a probe of the root therefore reports "no authentication needed" for a server that very much
/// requires it. The user then saw "access denied" and was never asked for a password. Where the
/// DAV area lives is the server's business; the only reliable signal is the challenge itself.
///
/// This is also what HTTP's own design expects, and it means an anonymous share is still never
/// prompted for -- the first request simply succeeds.
/// </remarks>
/// <param name="unencryptedSignIn">settings.json's <c>remote.unencryptedSignIn</c>, read at each use.</param>
public sealed class DavConnections(ICredentialStore credentials, IDavPrompts prompts, Func<bool>? unencryptedSignIn = null)
    : IDavConnections, IDisposable
{
    /// <summary>
    /// How long a server may stay silent: for an ordinary request, from sending it to having the
    /// whole answer; for a file's body, between one piece and the next.
    /// </summary>
    /// <remarks>
    /// Not <see cref="HttpClient.Timeout"/>, which bounds a whole request including its body. That
    /// cut every upload or download that took longer than this off mid-file, however fast it was
    /// moving: a 1.4 GB upload stopped at 80%.
    /// </remarks>
    internal static readonly TimeSpan Silence = TimeSpan.FromSeconds(100);

    /// <summary>Each server's client, and what it was made to send: the setting can change under it.</summary>
    private readonly Dictionary<DavTarget, (HttpClient Client, SignInPolicy Policy)> _clients = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<HttpResponseMessage> SendAsync(
        DavTarget target, Func<HttpRequestMessage> make, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, TimeSpan? limit = null)
    {
        var (client, policy) = await GetAsync(target, ct).ConfigureAwait(false);

        var response = await TimedAsync(client, make(), completion, limit ?? Silence, target, ct).ConfigureAwait(false);
        RefuseSilentDowngrade(target, response);

        if (response.StatusCode is not HttpStatusCode.Unauthorized)
            return response;

        // Neither asked nor sent: a password typed here would cross the network readable, and the
        // question would look like any other. Decided by the client's own policy, not the setting
        // read again: a password put on a client made not to sign in would stay on it, and the
        // next request after the setting went back to off would carry it.
        if (!policy.SignsIn)
        {
            response.Dispose();
            throw Unencrypted(target);
        }

        // The server wants credentials. Anything stored is tried first and silently, because a
        // password that works should never produce a prompt.
        var key = KeyFor(target);
        if (target.User.Length > 0 && credentials.Read(key) is { } saved)
        {
            response.Dispose();
            // The setting may have been turned off while the server took its time to refuse.
            if (!MayUse(target).SignsIn)
                throw Unencrypted(target);
            Authenticate(client, target.User, saved);

            response = await TimedAsync(client, make(), completion, limit ?? Silence, target, ct).ConfigureAwait(false);
            if (response.StatusCode is not HttpStatusCode.Unauthorized)
                return response;

            // Stored and wrong: forget it, or every future attempt fails the same way. Unless no one
            // asked for this request (a folder listed ahead): that is no moment to log anyone out.
            if (DecisionScope.Current is NoOneAsks)
            {
                response.Dispose();
                throw new UnauthorizedAccessException($"{target.Display} did not accept the saved password.");
            }
            credentials.Remove(key);
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            response.Dispose();

            var answer = await prompts.AskPasswordAsync(target, attempt > 0, ct).ConfigureAwait(false);
            if (answer is null)
                throw new UnauthorizedAccessException($"{target.Display} needs a password.");

            // The question may have stood open while the setting was turned off.
            if (!MayUse(target).SignsIn)
                throw Unencrypted(target);

            Authenticate(client, answer.User, answer.Password);
            response = await TimedAsync(client, make(), completion, limit ?? Silence, target, ct).ConfigureAwait(false);

            if (response.StatusCode is not HttpStatusCode.Unauthorized)
            {
                if (answer.Remember)
                    credentials.Write(KeyFor(target with { User = answer.User }), answer.Password);
                return response;
            }
        }

        response.Dispose();
        throw new UnauthorizedAccessException($"{target.Display} did not accept the password.");
    }

    private static UnencryptedSignInRefusedException Unencrypted(DavTarget target) => new(
        $"{target.Display} wants a password, which over dav:// would go unencrypted. Use " +
        $"davs://{target.Display}/ if the server has HTTPS, or for a network you trust, turn on " +
        "\"Sign in to unencrypted WebDAV\" in Settings.");

    /// <summary>
    /// One request, given <paramref name="within"/> to answer. A server that runs out of it is a
    /// failure with a reason, not a cancellation: the transfer reports a cancelled job as the
    /// user's own doing and says nothing. An IOException, because that is what the callers'
    /// filters already treat as "the file could not be reached".
    /// </summary>
    private static async Task<HttpResponseMessage> TimedAsync(
        HttpClient client, HttpRequestMessage message, HttpCompletionOption completion, TimeSpan within, DavTarget target, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(within);
        try
        {
            return await client.SendAsync(message, completion, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && limit.IsCancellationRequested)
        {
            throw new IOException($"{target.Display} did not answer for {within.TotalSeconds:0} seconds.", ex);
        }
    }

    public void Forget(DavTarget target)
    {
        // Dropped, not disposed: disposing would cut off every upload and download still using it,
        // a whole copy failed because some other request was refused. Its connections close when
        // they have been idle a while, and the next request makes a new client.
        lock (_clients)
            _clients.Remove(target);

        credentials.Remove(KeyFor(target));
    }

    /// <summary>What a saved password is filed under. The scheme is part of it: the same host over
    /// http and over https are different servers as far as a secret is concerned.</summary>
    internal static string KeyFor(DavTarget target) =>
        $"DanceCommander:{(target.Secure ? "davs" : "dav")}:{target.Host}:{target.Port}:{target.User}";

    /// <summary>
    /// Refuses a request that was redirected from http to https rather than letting it fail
    /// mysteriously.
    /// </summary>
    /// <remarks>
    /// A server that serves WebDAV over TLS usually answers plain HTTP with a 301 to the https
    /// address, and <see cref="HttpClient"/> follows it. What it does not carry across a change of
    /// scheme is the Authorization header, and it is right not to — sending a password to a
    /// different origin than the one it was typed for is how credentials leak. The result is a
    /// request that is redirected, stripped and refused, forever, with nothing on screen to say
    /// why. So the redirect is reported instead, naming the address that would have worked.
    /// </remarks>
    internal static void RefuseSilentDowngrade(DavTarget target, HttpResponseMessage response)
    {
        if (target.Secure || response.RequestMessage?.RequestUri is not { } final)
            return;

        if (!final.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            return;

        response.Dispose();
        throw new NotSupportedException(
            $"{target.Host} redirects to HTTPS, and a password cannot safely be carried across " +
            $"that redirect. Use davs://{target.Display}/ instead of dav://.");
    }

    public async Task<HttpClient> ClientFor(DavTarget target, CancellationToken ct) =>
        (await GetAsync(target, ct).ConfigureAwait(false)).Client;

    private async Task<(HttpClient Client, SignInPolicy Policy)> GetAsync(DavTarget target, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var policy = MayUse(target);
            // Forget removes under this lock, from another pane's refused request.
            lock (_clients)
            {
                if (_clients.TryGetValue(target, out var existing) && existing.Policy == policy)
                    return existing;
            }

            // A client made before the setting changed is dropped, not disposed, as Forget does:
            // a transfer may still be using it.
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                // Windows integrated auth, for the IIS and SharePoint case where there is nothing
                // to type: if the server asks for Negotiate or NTLM, the logged-in identity
                // answers and the 401 never reaches us. Only to this server, and only one on the
                // organisation's own network: UseDefaultCredentials answered any host that asked,
                // a redirect's target included, and an NTLM answer can be cracked or relayed. And
                // only over TLS unless the user said otherwise: a dotless name is answered by
                // whoever claims it on a strange network, but can't show a certificate for it.
                Credentials = policy.Windows ? IntegratedAuthFor(target) : null,
                PreAuthenticate = true,
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = target.BaseAddress,
                // Each request sets its own limit (Silence), because this one would also bound
                // the time it takes to move a file's body.
                Timeout = Timeout.InfiniteTimeSpan,
            };

            // Attached up front when it is already known, so the common case costs one round trip
            // rather than a challenge and an answer.
            if (policy.SignsIn && target.User.Length > 0 && credentials.Read(KeyFor(target)) is { } saved)
                Authenticate(client, target.User, saved);

            lock (_clients)
                _clients[target] = (client, policy);
            return (client, policy);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The logged-in identity for this server's own address, or nothing. A server counts as the
    /// organisation's own as Windows' Local intranet zone decides by default: a name without dots
    /// (<c>dav://fileserver/</c>), or one inside this machine's own domain. Anything else asks for
    /// a password, as it would have had integrated auth never been offered.
    /// </summary>
    internal static ICredentials? IntegratedAuthFor(DavTarget target)
    {
        if (!IsIntranet(target.Host, IPGlobalProperties.GetIPGlobalProperties().DomainName))
            return null;

        var cache = new CredentialCache();
        foreach (var scheme in (string[])["Negotiate", "NTLM"])
            cache.Add(target.BaseAddress, scheme, CredentialCache.DefaultNetworkCredentials);
        return cache;
    }

    /// <summary>What a server's client may send: a password, and the Windows account.</summary>
    internal readonly record struct SignInPolicy(bool SignsIn, bool Windows);

    private SignInPolicy MayUse(DavTarget target) => MayUse(target, unencryptedSignIn?.Invoke() ?? false);

    /// <summary>
    /// A password goes over TLS, to this machine (a local test server), or where the user has said
    /// plain HTTP is fine. The Windows account not to this machine on its own: a local port can be
    /// a tunnel to someone else's, and "localhost" has no dots, so it would count as the intranet.
    /// </summary>
    internal static SignInPolicy MayUse(DavTarget target, bool unencryptedAllowed) => new(
        SignsIn: target.Secure || unencryptedAllowed || IsLoopback(target.Host),
        Windows: target.Secure || unencryptedAllowed);

    /// <summary>
    /// Taken as the network stack will take it: "localhost." or "127.0.0.1." with a trailing dot
    /// is a name for DNS to answer, and so whoever answers it.
    /// </summary>
    internal static bool IsLoopback(string host)
    {
        host = host.Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
    }

    internal static bool IsIntranet(string host, string? domain)
    {
        host = host.TrimEnd('.');
        // An address is not a name: 10.0.0.5 has dots but says nothing about whose it is.
        if (host.Length == 0 || IPAddress.TryParse(host.Trim('[', ']'), out _))
            return false;
        if (!host.Contains('.'))
            return true;
        domain = domain?.Trim('.');
        return !string.IsNullOrEmpty(domain) && host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    private static void Authenticate(HttpClient client, string user, string password) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    public void Dispose()
    {
        foreach (var (client, _) in _clients.Values)
            client.Dispose();
        _clients.Clear();
        _gate.Dispose();
    }
}
