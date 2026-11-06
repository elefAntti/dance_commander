using System.Runtime.InteropServices;
using System.Threading;

namespace DanceCommander.App.Services;

/// <summary>
/// A bare Win32 window, up before WPF has loaded, so that a click on the taskbar is answered at
/// once rather than when the real window is built — which on a loaded machine can be seconds.
/// </summary>
/// <remarks>
/// Its taskbar button runs the indeterminate progress marquee. That is drawn by Explorer, not by
/// us, so it keeps moving however busy the UI thread is building the main window; and this window
/// has a thread and message loop of its own, so it is never marked "Not responding" meanwhile.
/// The main window then takes its place on screen and it goes, so to the user the window simply
/// appeared early and filled in.
///
/// Nothing here may touch WPF: the whole point is to run before it has loaded.
/// </remarks>
internal sealed class LaunchPlaceholder
{
    private const string ClassName = "DanceCommander.LaunchPlaceholder";

    // MainWindow.xaml's Width and Height, in device-independent pixels.
    private const int WidthDip = 1180;
    private const int HeightDip = 740;

    // Dark.xaml's WindowBrush, TextSecondaryBrush and DividerBrush, as COLORREF (0x00BBGGRR): the
    // colours for a first launch. After that, the palette the last window closed with, saved
    // beside its position (SavedPlacement), so a light window doesn't open as a dark one.
    // Never read from the XAML: that would mean loading what this exists to precede.
    private static readonly (int Window, int Text, int Divider) DarkColours = (0x1A1816, 0xC4BCB4, 0x3E3833);

    private readonly ManualResetEventSlim _created = new();
    private IntPtr _hwnd;
    private uint _taskbarButtonCreated;

    // Held so the collector cannot take the delegate Windows is calling.
    private WndProc? _wndProc;

    private readonly bool _restore;
    private readonly (int Window, int Text, int Divider) _colours = SavedPlacement.Colours() ?? DarkColours;

    /// <summary>A COLORREF's brightness, as the title bar decides between dark and light captions.</summary>
    private static bool IsDark(int colorRef) =>
        0.299 * (colorRef & 0xFF) + 0.587 * ((colorRef >> 8) & 0xFF) + 0.114 * ((colorRef >> 16) & 0xFF) < 128;

    private LaunchPlaceholder(bool restore)
    {
        _restore = restore;
    }

    /// <param name="restore">
    /// Open where the last window closed. Only for the first window: a second one opened exactly
    /// on top of the first would look like no second window at all, so it takes the cascade.
    /// </param>
    public static LaunchPlaceholder Show(bool restore)
    {
        var placeholder = new LaunchPlaceholder(restore);
        var thread = new Thread(placeholder.Run) { IsBackground = true, Name = "Launch placeholder" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return placeholder;
    }

    /// <summary>
    /// Puts the main window, not yet shown, exactly where this one is, so the swap moves nothing.
    /// Returns whether to show it maximised, or null if there was nothing to copy and the window
    /// should place itself.
    /// </summary>
    public bool? PlaceOver(IntPtr window) =>
        _created.Wait(TimeSpan.FromMilliseconds(500)) && _hwnd != IntPtr.Zero
            ? SavedPlacement.Copy(_hwnd, window)
            : null;

    /// <summary>
    /// Closes the placeholder in favour of <paramref name="successor"/>, the main window, once that
    /// is drawn underneath it.
    /// </summary>
    public void Dismiss(IntPtr successor)
    {
        if (_created.Wait(TimeSpan.FromMilliseconds(500)) && _hwnd != IntPtr.Zero)
            PostMessage(_hwnd, WmDismiss, successor, IntPtr.Zero);
    }

    private void Run()
    {
        try
        {
            var module = GetModuleHandle(null);
            ExtractIconEx(Environment.ProcessPath ?? "", 0, out var largeIcon, out var smallIcon, 1);

            _wndProc = WindowProc;
            var windowClass = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = module,
                hIcon = largeIcon,
                hIconSm = smallIcon,
                // The arrow with the spinner: the same "starting" answer, for the pointer over it.
                hCursor = LoadCursor(IntPtr.Zero, IdcAppStarting),
                hbrBackground = CreateSolidBrush(_colours.Window),
                lpszClassName = ClassName,
            };
            if (RegisterClassEx(ref windowClass) == 0)
                return;

            // Explorer announces the button; progress set before then is lost.
            _taskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");

            _hwnd = CreateWindowEx(0, ClassName, "Dance Commander", WsOverlappedWindow,
                CwUseDefault, 0, CwUseDefault, 0, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
                return;

            var dark = IsDark(_colours.Window) ? 1 : 0;
            DwmSetWindowAttribute(_hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
            var caption = _colours.Window;
            DwmSetWindowAttribute(_hwnd, DwmCaptionColor, ref caption, sizeof(int));
            var text = _colours.Text;
            DwmSetWindowAttribute(_hwnd, DwmTextColor, ref text, sizeof(int));
            var border = _colours.Divider;
            DwmSetWindowAttribute(_hwnd, DwmBorderColor, ref border, sizeof(int));

            if (!(_restore && SavedPlacement.TryShow(_hwnd)))
            {
                // The size in physical pixels depends on the monitor, known only once the window exists.
                var scale = GetDpiForWindow(_hwnd) / 96.0;
                SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, (int)(WidthDip * scale), (int)(HeightDip * scale),
                    SwpNoMove | SwpNoZOrder | SwpNoActivate);
                ShowWindow(_hwnd, SwShowNormal);
            }
            UpdateWindow(_hwnd);
            StartupClock.Mark("placeholder shown");
        }
        finally
        {
            _created.Set();
        }

        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == _taskbarButtonCreated && _taskbarButtonCreated != 0)
        {
            try
            {
                var taskbar = (ITaskbarList3)new TaskbarList();
                taskbar.HrInit();
                taskbar.SetProgressState(hwnd, TbpfIndeterminate);
            }
            catch (COMException)
            {
                // No taskbar (Explorer restarting, a shell replacement): the window is feedback enough.
            }
            return IntPtr.Zero;
        }

        switch (message)
        {
            case WmDismiss:
                // The foreground is this thread's, and only its holder may hand it on. Shown from
                // the UI thread, the main window was put behind whatever had the foreground, not in
                // front, and closing the placeholder gave the keyboard back to the folder the user
                // launched from. Unless the user has gone elsewhere meanwhile: then it stays there.
                if (wParam != IntPtr.Zero && GetForegroundWindow() == hwnd)
                    SetForegroundWindow(wParam);

                // Without the closing animation: to the user this is the same window, not one closing.
                var noTransitions = 1;
                DwmSetWindowAttribute(hwnd, DwmTransitionsForceDisabled, ref noTransitions, sizeof(int));
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WmClose:
                // The user closed the window before it finished opening: they changed their mind.
                Environment.Exit(0);
                return IntPtr.Zero;
            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    private const uint WmDestroy = 0x0002;
    private const uint WmClose = 0x0010;
    private const uint WmDismiss = 0x8000; // WM_APP
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const int CwUseDefault = unchecked((int)0x80000000);
    private const int SwShowNormal = 1;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int IdcAppStarting = 32650;
    private const int TbpfIndeterminate = 0x1;
    private const int DwmTransitionsForceDisabled = 3;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    // Only as far into the vtable as SetProgressState; the slots after it are never called.
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int state);
    }

    [ComImport, Guid("56fdf344-fd6d-11d0-958a-006097c9a090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList
    {
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint icons);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, int cursor);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int color);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG message, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
