using DanceCommander.Core.Fs;

namespace DanceCommander.Providers.WebDav;

/// <summary>
/// Which server, as which user, over which transport. The unit a connection and a saved password
/// are keyed by, the same way <c>SftpTarget</c> is.
/// </summary>
/// <remarks>
/// WebDAV needs one decision SFTP does not: whether to speak HTTP or HTTPS. There is no port that
/// settles it — a server on 8080 may be either — so the address carries it. <c>dav://</c> is
/// plain HTTP and <c>davs://</c> is TLS, which is the spelling every other client uses and the
/// reason not to invent one.
/// </remarks>
public sealed record DavTarget(string Host, int Port, string User, bool Secure)
{
    public const string Scheme = "dav";
    public const string SecureScheme = "davs";

    /// <summary>Where HTTP requests actually go, with no path.</summary>
    public Uri BaseAddress =>
        new($"{(Secure ? "https" : "http")}://{Host}:{Port}/");

    /// <summary>What a saved password is filed under, and what a prompt says out loud.</summary>
    public string Display =>
        $"{(User.Length == 0 ? string.Empty : User + "@")}{Host}{(Port == DefaultPort(Secure) ? string.Empty : ":" + Port)}";

    public static int DefaultPort(bool secure) => secure ? 443 : 80;

    public static DavTarget From(FsUri uri) =>
        Parse(uri.Authority, uri.Scheme.Equals(SecureScheme, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses <c>user@host:port</c>, where the user and the port are both optional.</summary>
    public static DavTarget Parse(string authority, bool secure)
    {
        if (string.IsNullOrWhiteSpace(authority))
            throw new FormatException("A WebDAV address needs a host, e.g. dav://host/path.");

        var text = authority.Trim();

        // LastIndexOf, because a user name may contain '@' — a Nextcloud login is often an
        // email address, and splitting on the first one would cut it in half.
        var at = text.LastIndexOf('@');
        var user = at > 0 ? text[..at] : string.Empty;
        var hostPort = at >= 0 ? text[(at + 1)..] : text;

        var host = hostPort;
        var port = DefaultPort(secure);

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

        return new DavTarget(host, port, user, secure);
    }

    /// <summary>
    /// The absolute URL of a path on this server, with each segment escaped.
    /// </summary>
    /// <remarks>
    /// Escaped segment by segment rather than whole, because the separators must survive: running
    /// an entire path through an escaper turns every '/' into %2F and asks the server for one file
    /// with slashes in its name. Spaces and non-ASCII characters in names are the common case, not
    /// the exotic one, and this is where providers usually get them wrong.
    /// </remarks>
    public Uri UrlOf(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);

        return new Uri(BaseAddress, string.Join('/', segments));
    }

    /// <remarks>Leaves the text out, as SftpTarget's does: what reads as a port may be part of a password.</remarks>
    private static int ParsePort(string text) =>
        int.TryParse(text, out var value) && value is > 0 and <= 65535
            ? value
            : throw new FormatException("The port in this address is not a number between 1 and 65535.");
}
