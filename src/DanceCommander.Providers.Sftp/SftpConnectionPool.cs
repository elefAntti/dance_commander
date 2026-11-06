using System.Collections.Concurrent;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Security;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DanceCommander.Providers.Sftp;

/// <summary>Raised when the user declines to authenticate or to trust a host key.</summary>
public sealed class SftpAbortedException(string message) : Exception(message);

public interface ISftpConnections
{
    /// <summary>A connected client for this target, opening one if necessary.</summary>
    Task<SftpClient> GetAsync(SftpTarget target, CancellationToken ct);

    /// <summary>
    /// A shell session on the same host, for running a command there. Opened on first use and
    /// pooled alongside the file session, because most connections never run a command.
    /// </summary>
    Task<SshClient> GetShellAsync(SftpTarget target, CancellationToken ct);

    /// <summary>Drops a connection, e.g. after an error that makes it unusable.</summary>
    void Drop(SftpTarget target);

    /// <summary>True when a live connection to this target is already open.</summary>
    bool IsConnected(SftpTarget target);

    /// <summary>
    /// A connection failed under us (the server went away, the network dropped). Once per
    /// connection, and never for one the pool closed itself. The next use reconnects; this is for
    /// saying so before then, since a pane on that server otherwise looked fine until touched.
    /// </summary>
    event EventHandler<SftpTarget>? Lost;

    /// <summary>
    /// The server's account names by user id, read once per connection. Empty when the server
    /// does not let us see them.
    /// </summary>
    Task<IReadOnlyDictionary<int, string>> GetUserNamesAsync(SftpTarget target, CancellationToken ct);

    /// <summary>
    /// Keeps the connection to this target from being closed as idle until disposed: for a
    /// command that runs for minutes, or a file being read or written, which use one session
    /// all along and so never look busy. The idle reaper cut those off part way.
    /// </summary>
    IDisposable Hold(SftpTarget target) => NoHold.Instance;
}

internal sealed class NoHold : IDisposable
{
    public static readonly NoHold Instance = new();

    public void Dispose()
    {
    }
}

/// <summary>
/// One connection per target, kept warm (§6.8). Opening an SFTP session costs a TCP handshake, a
/// key exchange and an authentication round trip; doing that per directory listing would make
/// browsing feel broken, so connections are pooled and idle ones are retired.
/// </summary>
public sealed class SftpConnectionPool : ISftpConnections, IDisposable
{
    public event EventHandler<SftpTarget>? Lost;

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private readonly IKnownHostsStore _knownHosts;
    private readonly ICredentialStore _credentials;
    private readonly ISftpPrompts _prompts;
    private readonly ILogger<SftpConnectionPool> _log;

    // Concurrent: each target's own gate guards its entry, so two targets touch the maps at once.
    private readonly ConcurrentDictionary<SftpTarget, Session> _sessions = new();
    private readonly Dictionary<SftpTarget, int> _holds = [];

    // One gate per target, held through connecting, prompts included. One for all of them let a
    // password question in a tab out of sight stop every other server from connecting, with the
    // waiting pane saying only "Connecting" (BUGS.md 51).
    private readonly ConcurrentDictionary<SftpTarget, SemaphoreSlim> _gates = new();

    // The password of a session whose silent reconnect failed (the server was down, the network
    // gone), kept as long as an idle session would have kept it. Dropped with the session, the
    // next look asked for it again, and that question took the place of the error that said
    // what had failed (BUGS.md 52).
    private readonly ConcurrentDictionary<SftpTarget, (string Password, DateTime Since)> _unreachable = new();

    private readonly Timer _reaper;

    public SftpConnectionPool(
        IKnownHostsStore knownHosts,
        ICredentialStore credentials,
        ISftpPrompts prompts,
        ILogger<SftpConnectionPool> log)
    {
        _knownHosts = knownHosts;
        _credentials = credentials;
        _prompts = prompts;
        _log = log;
        _reaper = new Timer(_ => Reap(), null, IdleTimeout, IdleTimeout);
    }

    /// <param name="Password">
    /// Held only for as long as this pooled connection lives, so that opening a shell on the same
    /// host, or reconnecting after the network dropped it, does not mean asking again. It is
    /// already inside SSH.NET's own ConnectionInfo; it is never written to a file, a log or the
    /// history. Closed as idle, it goes with the session.
    /// </param>
    private sealed class Session(SftpClient client, string password)
    {
        public SftpClient Client { get; } = client;

        public string Password { get; } = password;

        /// <summary>Opened lazily: most sessions only ever move files.</summary>
        public SshClient? Shell { get; set; }

        public DateTime LastUsed { get; set; } = DateTime.UtcNow;

        /// <summary>Read on first use; see <see cref="GetUserNamesAsync"/>.</summary>
        public IReadOnlyDictionary<int, string>? UserNames { get; set; }
    }

    /// <summary>The gate for one target: held while it connects, never while another does.</summary>
    private SemaphoreSlim GateFor(SftpTarget target) => _gates.GetOrAdd(target, _ => new SemaphoreSlim(1, 1));

    public async Task<SftpClient> GetAsync(SftpTarget target, CancellationToken ct)
    {
        var gate = GateFor(target);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? known = null;
            var lastGood = DateTime.UtcNow;
            if (_unreachable.TryRemove(target, out var kept) && kept.Since > DateTime.UtcNow - IdleTimeout)
                (known, lastGood) = kept;

            if (_sessions.TryGetValue(target, out var existing))
            {
                if (existing.Client.IsConnected)
                {
                    existing.LastUsed = DateTime.UtcNow;
                    return existing.Client;
                }

                // Dropped under us: a laptop slept, a NAT table expired, the server restarted.
                // Reconnecting once silently is right; failing the first listing after a sleep
                // would make the application look broken when the network is merely slow. With the
                // password this session was opened with: asking for it again is not silent.
                _log.LogInformation("Reconnecting to {Target}", target);
                known = existing.Password;
                lastGood = existing.LastUsed;
                Close(target, existing);
            }

            try
            {
                var (client, password) = await ConnectAsync(target, known, ct).ConfigureAwait(false);
                _sessions[target] = new Session(client, password);
                return client;
            }
            catch (Exception ex) when (known is not null && ex is not SftpAbortedException)
            {
                // Not reached: the password was never tried, so it is no less good than it was.
                // Not kept past a refusal (SftpAbortedException: a declined key or question). If
                // the server turned it down and the network then failed on the one typed instead,
                // the old one is kept; the next try is refused once and asks, as it would have.
                _unreachable[target] = (known, lastGood);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SshClient> GetShellAsync(SftpTarget target, CancellationToken ct)
    {
        // Make sure the file session exists first: that is what performs authentication and host
        // key checking, so by the time a shell is opened both questions are already settled.
        await GetAsync(target, ct).ConfigureAwait(false);

        await GateFor(target).WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(target, out var session))
                throw new InvalidOperationException($"No connection to {target}.");

            if (session.Shell is { IsConnected: true } existing)
            {
                session.LastUsed = DateTime.UtcNow;
                return existing;
            }

            session.Shell?.Dispose();

            var info = new ConnectionInfo(
                target.Host, target.Port, target.User,
                new PasswordAuthenticationMethod(target.User, session.Password))
            {
                Timeout = TimeSpan.FromSeconds(20),
            };

            var shell = new SshClient(info);

            // The key is already trusted by now, so this only has to agree with the store. Refused
            // first: SSH.NET's default is to trust, and a store that can't be read throws.
            shell.HostKeyReceived += (_, args) =>
            {
                args.CanTrust = false;
                try
                {
                    args.CanTrust = _knownHosts.Check(target, args.HostKeyName, args.HostKey) == HostKeyVerdict.Trusted;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not read known_hosts to check {Target}", target);
                }
            };

            try
            {
                await shell.ConnectAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                shell.Dispose();
                throw;
            }

            session.Shell = shell;
            session.LastUsed = DateTime.UtcNow;
            return shell;
        }
        finally
        {
            GateFor(target).Release();
        }
    }

    /// <remarks>
    /// Never waits for the gate. Held means someone is connecting at this moment, and "not yet"
    /// is the true answer; waiting would stall the UI thread behind a handshake to find that out.
    /// </remarks>
    public bool IsConnected(SftpTarget target)
    {
        if (!GateFor(target).Wait(TimeSpan.Zero))
            return false;

        try
        {
            return _sessions.TryGetValue(target, out var session) && session.Client.IsConnected;
        }
        finally
        {
            GateFor(target).Release();
        }
    }

    /// <remarks>
    /// SFTP reports owners as numbers only; the names live in the server's /etc/passwd, which is
    /// world-readable on an ordinary host and can be read over the session already open, so it
    /// costs one small read per connection and no shell. A chrooted SFTP-only account usually
    /// cannot see it, and accounts from a directory service are not in it: those stay numbers,
    /// which is what the column showed before.
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, string>> GetUserNamesAsync(SftpTarget target, CancellationToken ct)
    {
        var client = await GetAsync(target, ct).ConfigureAwait(false);

        await GateFor(target).WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(target, out var session) && session.UserNames is { } known)
                return known;
        }
        finally
        {
            GateFor(target).Release();
        }

        // Read outside the gate: it is a round trip, and other panes may want the connection
        // meanwhile. Two listings racing here both read the file, which is harmless.
        IReadOnlyDictionary<int, string> names;
        try
        {
            var passwd = await Task.Run(() => client.ReadAllText("/etc/passwd"), ct).ConfigureAwait(false);
            names = ParsePasswd(passwd);
        }
        catch (Exception ex) when (ex is SshException or IOException)
        {
            _log.LogDebug(ex, "No account names from {Target}", target);
            names = new Dictionary<int, string>();
        }

        await GateFor(target).WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(target, out var session) && ReferenceEquals(session.Client, client))
                session.UserNames = names;
        }
        finally
        {
            GateFor(target).Release();
        }

        return names;
    }

    /// <summary>"name:x:uid:gid:..." per line; the first line for an id wins, as it does for getpwuid.</summary>
    internal static Dictionary<int, string> ParsePasswd(string passwd)
    {
        var names = new Dictionary<int, string>();
        foreach (var line in passwd.Split('\n'))
        {
            var fields = line.Split(':');
            if (fields.Length > 2 && fields[0].Length > 0 && int.TryParse(fields[2], out var id))
                names.TryAdd(id, fields[0]);
        }

        return names;
    }

    public void Drop(SftpTarget target)
    {
        GateFor(target).Wait();
        try
        {
            if (_sessions.TryGetValue(target, out var session))
                Close(target, session);
        }
        finally
        {
            GateFor(target).Release();
        }
    }

    /// <summary>
    /// Opens a session, asking for whatever it needs on the way.
    /// </summary>
    /// <remarks>
    /// The host key is judged inside SSH.NET's callback, which runs on the session's own message
    /// loop -- so the callback only ever consults the store and returns. It must not wait for a
    /// person: blocking there stalls the key exchange itself and the handshake times out while
    /// the prompt is still on screen. When the key is not already trusted the connection is
    /// allowed to fail, the user is asked out here where blocking costs nothing, and the attempt
    /// is repeated. That costs one extra handshake on first contact and nothing afterwards, and
    /// it keeps the useful property: a refused key means the connection dies during key exchange,
    /// before any password is offered to whoever is on the other end.
    /// </remarks>
    /// <param name="known">
    /// The password a dropped session to this target was opened with, tried before asking. Not
    /// remembered by this: it is only ever saved when the user ticks the box.
    /// </param>
    private async Task<(SftpClient Client, string Password)> ConnectAsync(
        SftpTarget target, string? known, CancellationToken ct)
    {
        var stored = _credentials.Read(target.CredentialKey);
        var password = stored ?? known;
        var remember = false;
        var retry = false;

        // Password auth only in v1; keys and the agent are the next step and slot in here as
        // earlier attempts in the same chain.
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (password is null)
            {
                var answer = await _prompts.AskPasswordAsync(target, retry, ct).ConfigureAwait(false);
                if (answer is null)
                    throw new SftpAbortedException($"Connecting to {target} was cancelled.");

                password = answer.Password;
                remember = answer.Remember;
            }

            var client = Build(target, password);

            HostKeyEventArgs? offered = null;
            var verdict = HostKeyVerdict.Unknown;
            Exception? unreadable = null;

            client.HostKeyReceived += (_, args) =>
            {
                offered = args;
                // Refused first: SSH.NET's default is to trust, and a store that can't be read
                // throws. That is no verdict at all, so it must not reach "Unknown"'s question.
                args.CanTrust = false;
                try
                {
                    verdict = _knownHosts.Check(target, args.HostKeyName, args.HostKey);
                    args.CanTrust = verdict == HostKeyVerdict.Trusted;
                }
                catch (Exception ex)
                {
                    // Every exception: on SSH.NET's own thread nothing else would see it, and any
                    // that escaped this left the verdict at Unknown for a host we know.
                    unreadable = ex;
                }
            };

            try
            {
                await client.ConnectAsync(ct).ConfigureAwait(false);
                client.KeepAliveInterval = TimeSpan.FromSeconds(30);

                // The keep-alive is what notices; this is what tells. SSH.NET raises it from its
                // own reader when the session fails, not when we disconnect on purpose.
                var lost = 0;
                client.ErrorOccurred += (_, args) =>
                {
                    if (Interlocked.Exchange(ref lost, 1) != 0)
                        return;
                    _log.LogInformation(args.Exception, "Lost the connection to {Target}", target);
                    Lost?.Invoke(this, target);
                };

                if (remember)
                    _credentials.Write(target.CredentialKey, password);

                _log.LogInformation("Connected to {Target}", target);
                return (client, password);
            }
            catch (SshAuthenticationException)
            {
                client.Dispose();

                // A saved password that no longer works is worse than none: it would fail forever
                // without ever asking. Forget it and ask once. Not in work nobody asked for (a folder
                // listed ahead, reconnecting a dropped session): there is no one to ask, and the
                // user's next own listing finds out the same, with someone there to answer.
                if (DecisionScope.Current is NoOneAsks)
                    throw new SftpAbortedException($"{target} did not accept the saved password.");
                if (stored is not null && password == stored)
                    _credentials.Remove(target.CredentialKey);

                password = null;
                stored = null;
                retry = true;
            }
            catch (Exception) when (offered is { CanTrust: false })
            {
                // The handshake failed because we refused the key, not for any other reason.
                client.Dispose();

                if (unreadable is not null)
                {
                    throw new IOException(
                        $"The list of trusted servers could not be read, so {target.Host}'s key could not be " +
                        "checked and no password was sent to it.", unreadable);
                }

                if (!await AcceptHostKeyAsync(target, offered, verdict, ct).ConfigureAwait(false))
                {
                    throw new SftpAbortedException(
                        $"The host key for {target.Host} was not accepted, so no password was sent to it.");
                }

                // Now trusted: the next attempt gets through the callback without asking.
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    /// <summary>Asks about a key we would not accept, and records it if the user vouches for it.</summary>
    private async Task<bool> AcceptHostKeyAsync(
        SftpTarget target, HostKeyEventArgs offered, HostKeyVerdict verdict, CancellationToken ct)
    {
        var presentation = new HostKeyPresentation(
            offered.HostKeyName, KnownHostsStore.Fingerprint(offered.HostKey), offered.KeyLength);

        var accepted = verdict switch
        {
            HostKeyVerdict.Unknown =>
                await _prompts.ConfirmUnknownHostAsync(target, presentation, ct).ConfigureAwait(false),
            _ => await ConfirmChangedAsync(target, presentation, ct).ConfigureAwait(false),
        };

        if (accepted)
            _knownHosts.Trust(target, offered.HostKeyName, offered.HostKey);

        return accepted;
    }

    private Task<bool> ConfirmChangedAsync(
        SftpTarget target, HostKeyPresentation presentation, CancellationToken ct)
    {
        _log.LogWarning(
            "Host key for {Target} has changed to {Fingerprint}", target, presentation.Fingerprint);
        return _prompts.ConfirmChangedHostAsync(target, presentation, ct);
    }

    private static SftpClient Build(SftpTarget target, string password)
    {
        var info = new ConnectionInfo(
            target.Host, target.Port, target.User, new PasswordAuthenticationMethod(target.User, password))
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        return new SftpClient(info) { OperationTimeout = TimeSpan.FromMinutes(2) };
    }

    private void Close(SftpTarget target, Session session)
    {
        // This session only: one made since under the same name stays.
        _sessions.TryRemove(new KeyValuePair<SftpTarget, Session>(target, session));
        try
        {
            session.Shell?.Dispose();
            session.Client.Dispose();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Closing the connection to {Target} failed", target);
        }
    }

    public IDisposable Hold(SftpTarget target)
    {
        lock (_holds)
            _holds[target] = _holds.GetValueOrDefault(target) + 1;
        return new Lease(this, target);
    }

    private bool IsHeld(SftpTarget target)
    {
        lock (_holds)
            return _holds.GetValueOrDefault(target) > 0;
    }

    private void Release(SftpTarget target)
    {
        lock (_holds)
        {
            if (_holds.GetValueOrDefault(target) <= 1)
                _holds.Remove(target);
            else
                _holds[target]--;
        }
    }

    private sealed class Lease(SftpConnectionPool pool, SftpTarget target) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                pool.Release(target);
        }
    }

    private void Reap()
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        foreach (var (target, kept) in _unreachable.ToArray())
        {
            if (kept.Since < cutoff)
                _unreachable.TryRemove(new KeyValuePair<SftpTarget, (string, DateTime)>(target, kept));
        }

        foreach (var (target, session) in _sessions.Where(p => p.Value.LastUsed < cutoff).ToList())
        {
            var gate = GateFor(target);
            if (!gate.Wait(TimeSpan.Zero))
                continue; // Connecting or in use; idle cleanup can wait for the next tick.

            try
            {
                // Looked at again under the gate: it may have been used or replaced meanwhile.
                if (IsHeld(target) || !_sessions.TryGetValue(target, out var current)
                    || !ReferenceEquals(current, session) || session.LastUsed >= cutoff)
                {
                    continue;
                }

                _log.LogInformation("Closing idle connection to {Target}", target);
                Close(target, session);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    public void Dispose()
    {
        _reaper.Dispose();
        foreach (var (target, session) in _sessions.ToList())
            Close(target, session);
        foreach (var gate in _gates.Values)
            gate.Dispose();
    }
}
