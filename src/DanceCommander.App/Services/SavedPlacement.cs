using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DanceCommander.App.Services;

/// <summary>
/// Where the window was when it last closed, so the next launch opens there. Without it every
/// launch took Windows' default position, which cascades: each one a little right and down from
/// the last, until it met the taskbar and started again at the top left.
/// </summary>
/// <remarks>
/// Kept as the Win32 window placement rather than WPF's Left/Top/Width/Height: the placement is
/// what the launch placeholder can use before WPF has loaded, it carries the restored bounds of a
/// maximised window, and applying it puts a window that was on a monitor since unplugged back on
/// one that is there. Under %LOCALAPPDATA%, not beside the settings in %APPDATA%: it describes
/// this machine's monitors and should not follow the user to another one.
/// </remarks>
internal static class SavedPlacement
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DanceCommander", "window.json");

    /// <summary>Shows <paramref name="hwnd"/> where the last window closed. False if there is no record.</summary>
    public static bool TryShow(IntPtr hwnd)
    {
        if (Load() is not { } saved)
            return false;

        var placement = NewPlacement();
        placement.showCmd = saved.Maximized ? SwShowMaximized : SwShowNormal;
        placement.rcNormalPosition = saved.Bounds;
        return SetWindowPlacement(hwnd, ref placement);
    }

    /// <summary>
    /// Gives the hidden window <paramref name="to"/> the placement of <paramref name="from"/>,
    /// without showing it. Returns whether it should be shown maximised, or null if nothing was copied.
    /// </summary>
    public static bool? Copy(IntPtr from, IntPtr to)
    {
        var placement = NewPlacement();
        if (!GetWindowPlacement(from, ref placement))
            return null;

        var maximized = IsMaximized(placement);
        placement.showCmd = SwHide;
        placement.flags = 0;

        // Twice: moving onto a monitor with a different scale makes the window resize itself for
        // the new DPI, and the second pass, now on the right monitor, restores the exact size.
        SetWindowPlacement(to, ref placement);
        return SetWindowPlacement(to, ref placement) ? maximized : null;
    }

    public static void Save(IntPtr hwnd)
    {
        var placement = NewPlacement();
        if (!GetWindowPlacement(hwnd, ref placement))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
#pragma warning disable RS0030 // §1.6: our own window-placement file; a crash mid-write costs only the window's position.
            using var stream = File.Create(FilePath);
#pragma warning restore RS0030
            using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            var bounds = placement.rcNormalPosition;
            json.WriteStartObject();
            json.WriteNumber("left", bounds.Left);
            json.WriteNumber("top", bounds.Top);
            json.WriteNumber("right", bounds.Right);
            json.WriteNumber("bottom", bounds.Bottom);
            json.WriteBoolean("maximized", IsMaximized(placement));

            // The palette's window, text and divider colours, for the launch placeholder, which
            // draws before there is any XAML to read them from.
            WriteColour(json, "windowColor", "WindowBrush");
            WriteColour(json, "textColor", "TextSecondaryBrush");
            WriteColour(json, "dividerColor", "DividerBrush");
            json.WriteEndObject();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Opening at the default position next time is the whole cost.
        }
    }

    private static void WriteColour(Utf8JsonWriter json, string name, string brushKey)
    {
        if (System.Windows.Application.Current?.TryFindResource(brushKey) is System.Windows.Media.SolidColorBrush { Color: var c })
            json.WriteString(name, $"#{c.R:X2}{c.G:X2}{c.B:X2}");
    }

    /// <summary>
    /// The palette as the last window closed with it, as COLORREFs (0x00BBGGRR), or null when there
    /// is no record of it.
    /// </summary>
    public static (int Window, int Text, int Divider)? Colours()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(FilePath));
            var root = document.RootElement;
            return (ColorRef(root, "windowColor"), ColorRef(root, "textColor"), ColorRef(root, "dividerColor"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static int ColorRef(JsonElement root, string name)
    {
        var hex = root.GetProperty(name).GetString() ?? throw new FormatException();
        if (hex.Length != 7 || hex[0] != '#')
            throw new FormatException();
        var rgb = Convert.ToInt32(hex[1..], 16);
        return ((rgb >> 16) & 0xFF) | (rgb & 0xFF00) | ((rgb & 0xFF) << 16);
    }

    private static (RECT Bounds, bool Maximized)? Load()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(FilePath));
            var root = document.RootElement;
            var bounds = new RECT
            {
                Left = root.GetProperty("left").GetInt32(),
                Top = root.GetProperty("top").GetInt32(),
                Right = root.GetProperty("right").GetInt32(),
                Bottom = root.GetProperty("bottom").GetInt32(),
            };
            if (bounds.Right - bounds.Left < 100 || bounds.Bottom - bounds.Top < 100)
                return null;

            return (bounds, root.TryGetProperty("maximized", out var maximized) && maximized.GetBoolean());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    // Closed while minimised, the window comes back the way it was before it was minimised.
    private static bool IsMaximized(WINDOWPLACEMENT placement) =>
        placement.showCmd == SwShowMaximized ||
        (placement.showCmd == SwShowMinimized && (placement.flags & WpfRestoreToMaximized) != 0);

    private static WINDOWPLACEMENT NewPlacement() => new() { length = Marshal.SizeOf<WINDOWPLACEMENT>() };

    private const int SwHide = 0;
    private const int SwShowNormal = 1;
    private const int SwShowMinimized = 2;
    private const int SwShowMaximized = 3;
    private const int WpfRestoreToMaximized = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);
}
