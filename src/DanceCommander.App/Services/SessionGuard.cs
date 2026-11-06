using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace DanceCommander.App.Services;

/// <summary>
/// What Windows is told while jobs run, so it does not end them unasked: a power request that
/// keeps the machine from sleeping when idle, and a shutdown block reason that puts "Copying 14 to
/// Docs" on its "preventing shutdown" screen, as Explorer does. The user still decides there; when
/// they shut down anyway, <c>ended</c> is called before the session goes.
/// </summary>
/// <remarks>
/// Only idle sleep is held back. A lid closed or Sleep chosen from Start still sleeps: Windows
/// does not let a program refuse that, and a laptop going into a bag should not be kept on. A job
/// cut off that way meets it as any interrupted job does, when the machine wakes.
/// </remarks>
public sealed class SessionGuard : IDisposable
{
    private const int WmQueryEndSession = 0x0011;
    private const int WmEndSession = 0x0016;

    /// <summary>Windows ends the session whatever anybody answers.</summary>
    private const long EndSessionCritical = 0x40000000;

    /// <summary>Not the session ending: an installer or update asking this program to close.</summary>
    private const long EndSessionCloseApp = 0x1;

    private readonly HwndSource _source;
    private readonly Func<bool> _unfinished;
    private readonly Action<bool> _ended;

    private SafeFileHandle? _awake;
    private string? _awakeReason;
    private string? _blockReason;

    /// <summary>
    /// Jobs were running when Windows asked, so neither this window nor WPF let it go ahead, and
    /// what comes after is ours to do. Remembered from the question rather than looked up again
    /// at the end: the jobs may have finished meanwhile, and the end is still ours to see through.
    /// </summary>
    private bool _heldBack;

    private SessionGuard(HwndSource source, Func<bool> unfinished, Action<bool> ended)
    {
        _source = source;
        _unfinished = unfinished;
        _ended = ended;
    }

    /// <summary>Null when the window has no handle to be told through.</summary>
    /// <param name="unfinished">
    /// Whether any job is unfinished, asked of the job service itself: the strip learns of a new
    /// job a moment later, and Windows' question can come in that moment.
    /// </param>
    /// <param name="ended">
    /// Called on the window's thread once the session is ending for certain (false), or this
    /// program is asked to close for an installer or update (true).
    /// </param>
    public static SessionGuard? Attach(Window window, Func<bool> unfinished, Action<bool> ended)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (HwndSource.FromHwnd(handle) is not { } source)
            return null;

        var guard = new SessionGuard(source, unfinished, ended);
        source.AddHook(guard.Hook);
        return guard;
    }

    /// <summary>
    /// What is running now. <paramref name="working"/> keeps the machine awake: a job waiting on an
    /// answer is not working, and a question left overnight should not keep a laptop on until the
    /// battery runs out. <paramref name="unfinished"/> holds back a shutdown, waiting or not.
    /// Null for nothing.
    /// </summary>
    public void Update(string? working, string? unfinished)
    {
        if (working != _awakeReason)
        {
            _awakeReason = working;
            StayAwake(working);
        }

        if (unfinished != _blockReason)
        {
            _blockReason = unfinished;
            if (unfinished is null)
                ShutdownBlockReasonDestroy(_source.Handle);
            else if (!ShutdownBlockReasonCreate(_source.Handle, unfinished))
                Serilog.Log.Warning("Could not tell Windows why shutdown should wait (error {Error})", Marshal.GetLastWin32Error());
        }
    }

    /// <summary>
    /// A request of our own rather than SetThreadExecutionState, for the reason: it is what
    /// <c>powercfg /requests</c> shows when someone asks why the machine stayed on. A new request
    /// for a new reason, because a request's reason is fixed when it is made. Execution as well as
    /// system: on a Modern Standby laptop the system can stay up with the screen off while a
    /// desktop program is paused, and it is this program that has to keep running.
    /// </summary>
    private void StayAwake(string? reason)
    {
        if (_awake is { } old)
        {
            PowerClearRequest(old, PowerRequestSystemRequired);
            PowerClearRequest(old, PowerRequestExecutionRequired);
            old.Dispose();
            _awake = null;
        }

        if (reason is null)
            return;

        var context = new ReasonContext { Version = 0, Flags = PowerRequestContextSimpleString, SimpleReasonString = reason };
        var request = PowerCreateRequest(ref context);
        if (request.IsInvalid || !PowerSetRequest(request, PowerRequestSystemRequired))
        {
            Serilog.Log.Warning("Could not keep the machine awake for {Reason} (error {Error})", reason, Marshal.GetLastWin32Error());
            request.Dispose();
            return;
        }

        // Unknown before Windows 8, where system-required alone already does it.
        PowerSetRequest(request, PowerRequestExecutionRequired);
        _awake = request;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            // Asked, not told: FALSE puts this window on Windows' screen with the reason, and the
            // user chooses there. A critical end is not asked about, so it is not refused, but
            // WPF was held back all the same and the end is still ours.
            case WmQueryEndSession when _unfinished():
                _heldBack = true;
                if ((lParam.ToInt64() & EndSessionCritical) != 0)
                    return IntPtr.Zero;
                Serilog.Log.Information("Windows asked to end the session during {Reason}; asked the user to wait", _blockReason ?? "a job");
                handled = true;
                return IntPtr.Zero;

            // Ending for certain: the user chose to go on anyway, or nobody was asked. When this
            // returns, the process can be ended at any moment. Only if held back: otherwise WPF
            // has already begun its own shutdown, and that is all there is to do.
            case WmEndSession when wParam != IntPtr.Zero && (_heldBack || _unfinished()):
                _heldBack = false;
                var closeApp = (lParam.ToInt64() & EndSessionCloseApp) != 0;
                Serilog.Log.Warning(
                    closeApp ? "Asked to close for an installer or update (during {Reason})" : "The session is ending (during {Reason})",
                    _blockReason ?? "nothing still running");
                handled = true;
                _ended(closeApp);
                return IntPtr.Zero;

            // Not ending after all: the user chose Cancel on Windows' screen.
            case WmEndSession:
                _heldBack = false;
                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (!_source.IsDisposed)
            _source.RemoveHook(Hook);
        Update(null, null);
    }

    private const int PowerRequestSystemRequired = 1;
    private const int PowerRequestExecutionRequired = 3;
    private const uint PowerRequestContextSimpleString = 0x1;

    /// <summary>
    /// REASON_CONTEXT with its simple string. The detailed form is never used, but the union is
    /// its size, so the struct is padded out to it: Windows is handed the whole thing.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string SimpleReasonString;
        public IntPtr DetailedRest1;
        public IntPtr DetailedRest2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(SafeFileHandle request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(SafeFileHandle request, int type);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);
}
