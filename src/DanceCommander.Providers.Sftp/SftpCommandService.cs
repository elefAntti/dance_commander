using System.Text;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Operations;

namespace DanceCommander.Providers.Sftp;

/// <param name="ExitStatus">Null when the command was killed by a signal rather than exiting.</param>
public sealed record RemoteResult(string Output, string Error, int? ExitStatus)
{
    public bool Failed => ExitStatus is not 0;

    /// <summary>Both streams, in the order a terminal would have shown them well enough.</summary>
    public string Combined =>
        string.IsNullOrEmpty(Error) ? Output
        : string.IsNullOrEmpty(Output) ? Error
        : Output.TrimEnd('\n') + "\n" + Error;
}

public interface IRemoteCommandService
{
    /// <summary>True when a command typed here would run on a server rather than on this machine.</summary>
    bool CanRun(FsUri location);

    Task<RemoteResult> RunAsync(FsUri directory, string command, CancellationToken ct);

    /// <summary>The same line as a job: streamed, stoppable, reported when it ends. Not yet running.</summary>
    RemoteCommandJob Start(FsUri directory, string command);
}

/// <summary>
/// Runs a typed command on the host a pane is connected to, in the directory the pane is showing.
/// </summary>
/// <remarks>
/// The command line is meant to act on where you are, and where you are may be a server. Running
/// <c>cmd.exe</c> on the local machine while the pane shows a remote directory is not a lesser
/// version of that — it is the wrong thing, because the path the user is looking at does not
/// exist locally at all. This is one SSH exec channel per command, on the connection that is
/// already open, so there is no second password prompt and no second host key check.
/// </remarks>
public sealed class SftpCommandService(ISftpConnections connections, IActivityLog activity) : IRemoteCommandService
{
    public RemoteCommandJob Start(FsUri directory, string command) =>
        new(connections, directory, command, activity);

    public bool CanRun(FsUri location) =>
        string.Equals(location.Scheme, "sftp", StringComparison.OrdinalIgnoreCase);

    public async Task<RemoteResult> RunAsync(FsUri directory, string command, CancellationToken ct)
    {
        var target = SftpTarget.From(directory);
        var shell = await connections.GetShellAsync(target, ct).ConfigureAwait(false);

        var script = Script(directory.Path, command);

        using var execution = shell.CreateCommand(script);
        try
        {
            await execution.ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw SftpFileSystemProvider.Translate(ex, directory);
        }

        return new RemoteResult(execution.Result ?? string.Empty, execution.Error ?? string.Empty, execution.ExitStatus);
    }

    /// <summary>
    /// The script sent to the server: go where the pane is pointed, or stop; then the line, exactly
    /// as typed, on a line of its own.
    /// </summary>
    /// <remarks>
    /// Never joined to the line with an operator. It was <c>cd '&lt;dir&gt;' &amp;&amp; line</c>, and
    /// <c>&amp;&amp;</c> binds only to the first command: with <c>a; b</c> or <c>a || b</c>, a failed
    /// cd still ran <c>b</c>, in the home folder (2026-10-03, found before it bit). On a line of its own,
    /// the user's line is parsed exactly as if typed at a prompt, and nothing of ours can change how
    /// it groups. The directory is quoted rather than trusted: a quote or a space in a path is
    /// ordinary on a Unix host. The line itself is deliberately not quoted; the user typed shell.
    /// </remarks>
    internal static string Script(string directory, string command) =>
        $"cd {Quote(directory)} || exit 1\n{command}";

    /// <summary>
    /// Single-quoted for a POSIX shell, with embedded quotes escaped the only way that works. A
    /// backslash too: fish reads \' as an escape even inside single quotes, so a folder called
    /// <c>a\</c> left the quote open there, over the newline and into the user's line.
    /// </summary>
    public static string Quote(string path)
    {
        var builder = new StringBuilder("'");
        foreach (var character in path)
        {
            if (character is '\'' or '\\')
                builder.Append('\'').Append('\\').Append(character).Append('\''); // close, escaped, reopen
            else
                builder.Append(character);
        }

        return builder.Append('\'').ToString();
    }
}
