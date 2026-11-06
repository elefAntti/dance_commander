namespace DanceCommander.Providers.Sftp;

/// <param name="Fingerprint">The SHA-256 fingerprint, formatted the way OpenSSH prints it.</param>
public sealed record HostKeyPresentation(string KeyType, string Fingerprint, int KeyLength);

/// <summary>
/// How the provider asks the user the two questions only they can answer. Kept as an abstraction
/// so the provider stays free of the UI: the application implements this over the active pane's
/// inline decision host, and a test implements it by answering directly.
/// </summary>
public interface ISftpPrompts
{
    /// <param name="retry">True when a password was already tried and rejected.</param>
    /// <returns>The password, or null if the user declined — which must abort the connection.</returns>
    Task<SftpPasswordAnswer?> AskPasswordAsync(SftpTarget target, bool retry, CancellationToken ct);

    /// <summary>First contact with a host. Returns true to trust and record the key.</summary>
    Task<bool> ConfirmUnknownHostAsync(SftpTarget target, HostKeyPresentation key, CancellationToken ct);

    /// <summary>
    /// The host's key is not the one we recorded. Returns true only on an explicit override;
    /// the default must be no.
    /// </summary>
    Task<bool> ConfirmChangedHostAsync(SftpTarget target, HostKeyPresentation key, CancellationToken ct);
}

/// <param name="Remember">Whether to put the password in the credential store.</param>
public sealed record SftpPasswordAnswer(string Password, bool Remember);
