namespace DanceCommander.Core.Security;

/// <summary>
/// Somewhere to keep secrets that is not a file we write (§7). Passwords never go into the JSON
/// config, the history, or the log; the operating system's own vault holds them, which also means
/// the user can see and revoke them with tools they already have.
/// </summary>
public interface ICredentialStore
{
    /// <summary>The stored secret for a key, or null if there is none.</summary>
    string? Read(string key);

    void Write(string key, string secret);

    /// <summary>Forgets a secret. Called when it turns out not to work.</summary>
    void Remove(string key);
}

/// <summary>Never stores anything. For tests, and for a session the user asked not to remember.</summary>
public sealed class NullCredentialStore : ICredentialStore
{
    public string? Read(string key) => null;

    public void Write(string key, string secret)
    {
    }

    public void Remove(string key)
    {
    }
}
