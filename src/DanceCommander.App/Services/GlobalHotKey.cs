using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DanceCommander.App.Services;

/// <summary>
/// A key that reaches us from any application (RegisterHotKey), delivered as WM_HOTKEY to one of
/// our windows. Windows gives each combination to one program only, so registering can fail: the
/// caller says so rather than assuming the key works.
/// </summary>
public sealed class GlobalHotKey : IDisposable
{
    private const int WmHotKey = 0x0312;

    public const uint ModAlt = 0x1;
    public const uint ModControl = 0x2;
    public const uint ModShift = 0x4;
    public const uint ModWin = 0x8;

    /// <summary>Holding the key down does not repeat it: a toggle pressed once is pressed once.</summary>
    private const uint ModNoRepeat = 0x4000;

    private static int _nextId = 0x5DC0;

    private readonly HwndSource _source;
    private readonly int _id = _nextId++;
    private readonly Action _pressed;
    private bool _registered;

    private GlobalHotKey(HwndSource source, Action pressed)
    {
        _source = source;
        _pressed = pressed;
    }

    /// <summary>Null when another program already holds the combination.</summary>
    public static GlobalHotKey? TryRegister(Window window, uint modifiers, uint virtualKey, Action pressed)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (HwndSource.FromHwnd(handle) is not { } source)
            return null;

        var key = new GlobalHotKey(source, pressed);
        if (!RegisterHotKey(handle, key._id, modifiers | ModNoRepeat, virtualKey))
            return null;

        key._registered = true;
        source.AddHook(key.Hook);
        return key;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == _id)
        {
            handled = true;
            _pressed();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (!_registered)
            return;

        _registered = false;
        _source.RemoveHook(Hook);
        UnregisterHotKey(_source.Handle, _id);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
