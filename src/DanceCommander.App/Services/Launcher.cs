using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace DanceCommander.App.Services;

/// <summary>Where a launch says it is under way: the pane it was started from.</summary>
public interface ILaunchHost
{
    /// <summary>"Starting Word…" while it starts; null once it is up.</summary>
    void ShowStarting(string? text);
}

/// <summary>
/// Opens a file the way Explorer would, and says so while the application is on its way.
/// </summary>
/// <remarks>
/// Between Enter and the application's window Windows shows nothing, and on a slow start that
/// gap reads as "it didn't register": Enter again, and a second copy (friction log, 2026-10-01).
/// So a launch that is not up within the same patience the listing bar keeps says what it is
/// starting (DESIGN.md §1.3: activity says what it is waiting on), and Enter on the same file while
/// it starts does not start it again. "Up" is this window losing the foreground, which is what a
/// new application's window does; failing that, the process being ready for input, or 20 s.
/// </remarks>
public sealed class Launcher
{
    private static readonly TimeSpan PatienceBeforeSaying = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(20);

    /// <summary>After the process is ready for input, a moment more for its window to come forward.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(1500);

    private readonly ConcurrentDictionary<string, string> _starting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called on the UI thread. A failure to start comes back as the exception it was.</summary>
    public Task OpenAsync(string path, ILaunchHost? host) =>
        LaunchAsync(path, FriendlyName(path), host, () => Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
        }));

    /// <summary>
    /// The shell's "edit" verb: Paint for an image where opening it is Photos. A type with no such
    /// verb fails with <see cref="ErrorNoAssociation"/>, asked of Windows only then: ShellExecute
    /// also reads the perceived-type associations (SystemFileAssociations\image) that a lookup by
    /// extension misses, so trying it is the one test that agrees with what it would do.
    /// </summary>
    public Task EditAsync(string path, ILaunchHost? host) =>
        LaunchAsync("edit:" + path, FriendlyName(path, "edit"), host, () => Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Verb = "edit",
            WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
        }));

    /// <summary>
    /// Opens a file as though it were a .txt, so whatever edits text in Windows is used. Never
    /// through the file's own association, which for a script is running it.
    /// </summary>
    /// <remarks>
    /// The shell is asked to open it with the .txt class (SEE_MASK_CLASSNAME), which applies that
    /// association's whole command. Taking only its executable and passing the path ourselves
    /// dropped the rest: a .txt opened through <c>wscript editor.vbs "%1"</c> would have become
    /// <c>wscript evil.vbs</c>, running the script F4 was pressed on (review, 2026-10-12). It also
    /// covers Store Notepad, which has no executable to start directly.
    /// </remarks>
    public Task EditAsTextAsync(string path, ILaunchHost? host)
    {
        var what = FriendlyName(".txt");
        return LaunchAsync("edit:" + path, what == ".txt" ? "the text editor" : what, host, () => OpenAsText(path));
    }

    /// <summary>ShellExecute's answer for a type with no handler for the verb asked.</summary>
    public const int ErrorNoAssociation = 1155;

    private async Task LaunchAsync(string key, string what, ILaunchHost? host, Func<Process?> start)
    {
        if (!_starting.TryAdd(key, what))
        {
            host?.ShowStarting($"Still starting {what}…");
            return;
        }

        var saying = new Saying(host);
        var deactivated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDeactivated(object? sender, EventArgs e) => deactivated.TrySetResult();
        if (Application.Current is { } app)
            app.Deactivated += OnDeactivated;

        try
        {
            _ = SayAfterPatienceAsync(saying, $"Starting {what}…");

            using var process = await StartAsync(start);
            var ready = process is null
                ? Task.Delay(Grace)
                : Task.Run(() => WaitUntilReady(process)).ContinueWith(_ => Task.Delay(Grace)).Unwrap();
            await Task.WhenAny(deactivated.Task, ready, Task.Delay(GiveUpAfter));
        }
        finally
        {
            if (Application.Current is { } current)
                current.Deactivated -= OnDeactivated;
            _starting.TryRemove(key, out _);
            saying.Done();
        }
    }

    private static async Task SayAfterPatienceAsync(Saying saying, string text)
    {
        await Task.Delay(PatienceBeforeSaying);
        saying.Say(text);
    }

    /// <summary>
    /// The pane's line for one launch: said after the patience, taken back when it is up, in that
    /// order whichever comes first. Cancelling the wait was not enough: an application up at about
    /// the moment the patience ran out had the wait's end already queued, past the cancel, and its
    /// "Starting Notepad++…" landed after the line was cleared, and stayed (2026-11-02).
    /// </summary>
    private sealed class Saying(ILaunchHost? host)
    {
        private readonly Lock _gate = new();
        private bool _done;

        public void Say(string text)
        {
            lock (_gate)
            {
                // Up before it was worth saying: nothing shown, which is the point of waiting.
                if (!_done)
                    host?.ShowStarting(text);
            }
        }

        public void Done()
        {
            lock (_gate)
            {
                _done = true;
                host?.ShowStarting(null);
            }
        }
    }

    private static void WaitUntilReady(Process process)
    {
        try
        {
            process.WaitForInputIdle((int)GiveUpAfter.TotalMilliseconds);
        }
        catch (InvalidOperationException)
        {
            // No message loop (a console program) or already gone: nothing more to wait for.
        }
    }

    /// <summary>
    /// ShellExecute on a thread of its own: some file associations block the caller while they
    /// start, and on the UI thread that froze the window the message was meant to appear in. STA,
    /// as the shell expects of a caller.
    /// </summary>
    /// <remarks>
    /// It starts in the file's own folder, as from Explorer. Without one it inherited this app's
    /// current folder, and a program that reads files beside itself by relative name failed to
    /// find them (2026-10-09).
    /// </remarks>
    private static Task<Process?> StartAsync(Func<Process?> start)
    {
        var started = new TaskCompletionSource<Process?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                started.TrySetResult(start());
            }
            catch (Exception ex)
            {
                started.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Launch",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return started.Task;
    }

    /// <summary>ShellExecuteEx with the .txt class; on the launch thread, as Process.Start is.</summary>
    private static Process? OpenAsText(string path)
    {
        // A .txt class with nothing to open it might leave the shell to treat a .bat as a program
        // of its own, which is running it. Asked only whether there is one, never used to build the
        // command; a Store app's handler has a DelegateExecute and no command line.
        if (!HasHandler(AssocstrCommand) && !HasHandler(AssocstrDelegateExecute))
            throw new System.ComponentModel.Win32Exception(ErrorNoAssociation);

        var info = new ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
            fMask = SeeMaskClassName | SeeMaskNoCloseProcess | SeeMaskFlagNoUi,
            lpVerb = "open",
            lpFile = path,
            lpDirectory = Path.GetDirectoryName(path),
            lpClass = ".txt",
            nShow = SwShowNormal,
        };
        if (!ShellExecuteEx(ref info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        // No handle when the shell handed the file to a running instance or a Store app: nothing to
        // wait on, and LaunchAsync then waits the grace period instead.
        if (info.hProcess == IntPtr.Zero)
            return null;

        try
        {
            return Process.GetProcessById(GetProcessId(info.hProcess));
        }
        catch (ArgumentException)
        {
            return null; // already gone
        }
        finally
        {
            CloseHandle(info.hProcess);
        }
    }

    /// <summary>"Word" for a .docx, the program's own description for an .exe, else the file's name.</summary>
    internal static string FriendlyName(string path, string verb = "open")
    {
        var extension = Path.GetExtension(path);
        try
        {
            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                return string.IsNullOrWhiteSpace(description) ? Path.GetFileName(path) : description.Trim();
            }

            if (extension.Length > 0)
            {
                uint length = 256;
                var name = new StringBuilder((int)length);
                if (AssocQueryString(AssocfInitIgnoreUnknown, AssocstrFriendlyAppName, extension, verb, name, ref length) == 0 &&
                    name.Length > 0)
                {
                    return name.ToString();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
        }

        return Path.GetFileName(path);
    }

    private const uint AssocfInitIgnoreUnknown = 0x400;
    private const int AssocstrCommand = 1;
    private const int AssocstrFriendlyAppName = 4;
    private const int AssocstrDelegateExecute = 18;

    private static bool HasHandler(int what)
    {
        uint length = 0;
        // S_FALSE (1) with no buffer: there is a value, and this is its length.
        return AssocQueryString(AssocfInitIgnoreUnknown, what, ".txt", "open", null, ref length) is 0 or 1 && length > 1;
    }

    private const uint SeeMaskClassName = 0x1;
    private const uint SeeMaskNoCloseProcess = 0x40;
    private const uint SeeMaskFlagNoUi = 0x400;
    private const int SwShowNormal = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);

    [DllImport("kernel32.dll")]
    private static extern int GetProcessId(IntPtr process);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(
        uint flags, int str, string assoc, string? extra, StringBuilder? output, ref uint length);
}
