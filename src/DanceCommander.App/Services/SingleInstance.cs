using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace DanceCommander.App.Services;

/// <summary>
/// Hands a command line to the copy already running, if there is one (§6.11).
/// </summary>
/// <remarks>
/// This exists for one behaviour: "show in folder" has to reuse the window you already have open.
/// Explorer does, and a file manager that opened a fifth window every time a download finished
/// would be abandoned by lunchtime — the point of the feature is to show you something, and a new
/// window buries the four you were using to do it.
///
/// The mutex and the pipe are per-user, not per-machine. Two people on one terminal server each get
/// their own Dance Commander, and neither can send the other a command line.
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private readonly string _name;
    private readonly Mutex _mutex;
    private CancellationTokenSource? _listening;

    public SingleInstance(string application)
    {
        // The user's SID rather than their name: names are not unique across domains, and this
        // becomes the name of a kernel object.
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _name = $"{application}.{user}";
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{_name}", out var first);
        IsFirst = first;
    }

    /// <summary>True when nothing else is running and this process should be the window.</summary>
    public bool IsFirst { get; }

    /// <summary>
    /// Sends the arguments to the running copy. False means it did not take them, and the caller
    /// should carry on and open a window of its own.
    /// </summary>
    /// <remarks>
    /// Failure here is ordinary rather than exceptional: the other copy may be shutting down, or
    /// wedged, or mid-upgrade. Starting a second window is a worse outcome than reusing the first
    /// and a much better one than refusing to start at all.
    /// </remarks>
    public bool TrySend(IReadOnlyList<string> args, TimeSpan timeout)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", _name, PipeDirection.Out);
            pipe.Connect((int)timeout.TotalMilliseconds);

            var payload = Encoding.UTF8.GetBytes(string.Join('\n', args));
            pipe.Write(payload);
            pipe.Flush();
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Starts accepting command lines from later invocations.</summary>
    public void Listen(Action<IReadOnlyList<string>> handler)
    {
        _listening = new CancellationTokenSource();
        var ct = _listening.Token;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // One server instance at a time, recreated per connection: this is a doorbell,
                    // not a protocol, and it is rung by hand a few times an hour at most.
                    using var pipe = new NamedPipeServerStream(
                        _name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

                    var args = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (args.Length > 0)
                        handler(args);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // A caller that died mid-write. The next one gets a fresh pipe.
                }
            }
        }, ct);
    }

    public void Dispose()
    {
        _listening?.Cancel();
        _listening?.Dispose();

        if (IsFirst)
            _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
