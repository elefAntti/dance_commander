using DanceCommander.Core.Fs;

namespace DanceCommander.Providers.Sftp;

/// <summary>
/// Which server, as which user. This is the unit a connection, a host key and a saved password
/// are all keyed by — two tabs on the same target share one connection, and a different user on
/// the same host is a different target.
/// </summary>
public sealed record SftpTarget(string Host, int Port, string User)
{
    public const int DefaultPort = 22;

    /// <summary>Parses the authority of an sftp URI: <c>user@host</c>, with an optional <c>:port</c>.</summary>
    public static SftpTarget Parse(string authority)
    {
        if (string.IsNullOrWhiteSpace(authority))
            throw new FormatException("An sftp address needs a host, e.g. sftp://user@host/path.");

        var text = authority.Trim();
        var at = text.LastIndexOf('@');
        var user = at > 0 ? text[..at] : Environment.UserName;
        var hostPort = at >= 0 ? text[(at + 1)..] : text;

        var host = hostPort;
        var port = DefaultPort;

        // "[::1]:2222" for a literal IPv6 address, "host:2222" otherwise.
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']');
            if (close < 0)
                throw new FormatException($"'{authority}' has an unclosed '[' in its address.");
            host = hostPort[1..close];
            var rest = hostPort[(close + 1)..];
            if (rest.StartsWith(':'))
                port = ParsePort(rest[1..]);
        }
        else
        {
            var colon = hostPort.LastIndexOf(':');
            if (colon > 0)
            {
                host = hostPort[..colon];
                port = ParsePort(hostPort[(colon + 1)..]);
            }
        }

        if (host.Length == 0)
            throw new FormatException($"'{authority}' does not name a host.");

        return new SftpTarget(host, port, user);
    }

    public static SftpTarget From(FsUri uri) => Parse(uri.Authority);

    /// <remarks>
    /// The message leaves the text out: in <c>sftp://bob:pa/ss@host/</c> the address ends at the
    /// '/', and what reads as its port is part of a password.
    /// </remarks>
    private static int ParsePort(string text) =>
        int.TryParse(text, out var value) && value is > 0 and <= 65535
            ? value
            : throw new FormatException("The port in this address is not a number between 1 and 65535.");

    /// <summary>The authority as it appears in a URI; the default port is left implicit.</summary>
    public string Authority => Port == DefaultPort ? $"{User}@{Host}" : $"{User}@{Host}:{Port}";

    /// <summary>How OpenSSH names this host in known_hosts: bracketed only on a non-default port.</summary>
    public string KnownHostsName => Port == DefaultPort ? Host : $"[{Host}]:{Port}";

    /// <summary>Key for the credential store. The user is part of it; the password is not.</summary>
    public string CredentialKey => $"sftp:{Authority}";

    public override string ToString() => Authority;
}
