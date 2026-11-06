using System.Security.Cryptography;
using System.Text;

namespace DanceCommander.Providers.Sftp;

public enum HostKeyVerdict
{
    /// <summary>Never seen. Ask the user, showing the fingerprint (trust on first use).</summary>
    Unknown,

    /// <summary>Matches what we recorded. Connect without asking.</summary>
    Trusted,

    /// <summary>
    /// We have a key for this host and it is a different one. Either the server was rebuilt, or
    /// someone is between you and it. Refuse by default; only the user can override.
    /// </summary>
    Changed,
}

public interface IKnownHostsStore
{
    /// <exception cref="IOException">The file is there but could not be read: no verdict.</exception>
    HostKeyVerdict Check(SftpTarget target, string keyType, byte[] hostKey);

    /// <summary>Records a key as trusted, replacing every previous key for the same host.</summary>
    void Trust(SftpTarget target, string keyType, byte[] hostKey);
}

/// <summary>
/// An OpenSSH-shaped <c>known_hosts</c> under <c>%APPDATA%/DanceCommander</c> (§6.8).
/// </summary>
/// <remarks>
/// Host key verification is not optional and is never blanket-accepted: an SFTP client that
/// accepts any key offers encryption without authentication, which is the part that actually
/// protects you. The file format is OpenSSH's own so it can be read, diffed and edited with
/// tools people already have. Hashed hostnames (<c>|1|…</c>) are recognised and skipped rather
/// than silently mismatched — we only write plain entries, so a hashed line is not ours.
/// </remarks>
public sealed class KnownHostsStore(string directory) : IKnownHostsStore
{
    private readonly string _path = Path.Combine(directory, "known_hosts");
    private readonly Lock _gate = new();

    /// <summary>The SHA-256 fingerprint in the form OpenSSH prints: <c>SHA256:base64</c>, unpadded.</summary>
    public static string Fingerprint(byte[] hostKey) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');

    /// <remarks>
    /// Judged by the key itself, whatever its type is called. Matched by type as well, a host known
    /// by its ed25519 key that offered an RSA one was "unknown", the polite first-use question,
    /// and the server side chooses the type, so whoever sits between can always choose that. One
    /// RSA key also comes under three algorithm names (ssh-rsa, rsa-sha2-256, rsa-sha2-512); its
    /// bytes, which carry the key's own type, are the same under each.
    /// </remarks>
    public HostKeyVerdict Check(SftpTarget target, string keyType, byte[] hostKey)
    {
        var expected = Convert.ToBase64String(hostKey);
        var found = false;

        lock (_gate)
        {
            foreach (var entry in Read())
            {
                if (!Matches(entry, target))
                    continue;

                if (string.Equals(entry.Key, expected, StringComparison.Ordinal))
                    return HostKeyVerdict.Trusted;

                found = true;
            }
        }

        return found ? HostKeyVerdict.Changed : HostKeyVerdict.Unknown;
    }

    public void Trust(SftpTarget target, string keyType, byte[] hostKey)
    {
        lock (_gate)
        {
            // Drop every previous key for this host, of any type, so an accepted change replaces
            // rather than accumulates and leaves two keys both looking valid: the question said
            // "Replace the stored key", and a key the user thinks is gone must not still pass.
            // Every other line is kept exactly as it was: comments, hashed hosts and @markers are
            // not ours to rewrite.
            // Read without Read()'s forgiveness: a file that could only be half read (locked by
            // another window, say) must make this fail, not be rewritten from the half.
            var lines = File.Exists(_path) ? File.ReadAllLines(_path) : [];
            var kept = new List<string>(lines.Length + 1);
            foreach (var line in lines)
            {
                if (Parse(line) is not { } entry || !Matches(entry, target))
                {
                    kept.Add(line);
                    continue;
                }

                // "hostA,hostB type key" names two hosts with one key: only this host's name goes,
                // and the other keeps the key it was vetted with. Dropped whole, hostB's next
                // connection was a first-time question again.
                var others = entry.Host.Split(',').Where(name => !IsThisHost(name, target)).ToList();
                if (others.Count > 0)
                {
                    var text = line.TrimStart();
                    kept.Add(string.Join(',', others) + text[text.IndexOfAny([' ', '\t'])..]);
                }
            }
            kept.Add($"{target.KnownHostsName} {keyType} {Convert.ToBase64String(hostKey)}");

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Its own name, so two windows trusting at once cannot write into one temp file.
            var temp = $"{_path}.{Guid.NewGuid():N}.tmp";
#pragma warning disable RS0030 // §1.6: a new temp file with a GUID name; nothing is there to write over.
            File.WriteAllLines(temp, kept, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
#pragma warning restore RS0030

            // Atomic, like every other file we own: a half-written known_hosts is a lockout.
            if (File.Exists(_path))
            {
#pragma warning disable RS0030 // §1.6: known_hosts' next version, every line kept but this host's old key, which the
                // user has just agreed to replace; swapped in whole.
                File.Replace(temp, _path, null);
#pragma warning restore RS0030
            }
            else
                File.Move(temp, _path);
        }
    }

    private sealed record Entry(string Host, string KeyType, string Key);

    private static bool Matches(Entry entry, SftpTarget target) =>
        entry.Host.Split(',').Any(name => IsThisHost(name, target));

    private static bool IsThisHost(string name, SftpTarget target) =>
        string.Equals(name.Trim(), target.KnownHostsName, StringComparison.OrdinalIgnoreCase);

    /// <remarks>
    /// An unreadable file throws rather than returning what was read before it failed. Read as
    /// fewer entries, a host whose key had changed was "unknown", the polite first-use question,
    /// and trusting there replaced the vetted key without the warning ever being shown. Not
    /// File.Exists: it says false for a file in a folder that can't be read, too.
    /// </remarks>
    private List<Entry> Read()
    {
        var entries = new List<Entry>();
        try
        {
            foreach (var line in File.ReadLines(_path))
            {
                if (Parse(line) is { } entry)
                    entries.Add(entry);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Truly none yet: the first server anyone connects to.
        }

        return entries;
    }

    /// <summary>A plain "host type key" line, or null for anything else.</summary>
    private static Entry? Parse(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text[0] == '#')
            return null;

        // Not ours: OpenSSH may have hashed the hostname, which we cannot match against, and a
        // @cert-authority or @revoked line says something other than "trust this key".
        if (text.StartsWith("|1|", StringComparison.Ordinal) || text[0] == '@')
            return null;

        var parts = text.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? new Entry(parts[0], parts[1], parts[2]) : null;
    }
}
