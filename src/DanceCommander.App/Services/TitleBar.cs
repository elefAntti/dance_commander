using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DanceCommander.App.Services;

/// <summary>
/// Extends the palette onto the window's title bar. A dark application wearing a light caption
/// (or the other way round) looks like two programs stacked on top of each other, and the title
/// bar is the one piece of chrome WPF does not own — DWM does.
/// </summary>
public static class TitleBar
{
    private const int UseImmersiveDarkMode = 20;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    /// <summary>DWMWA_COLOR_DEFAULT: the system's own colour for that part of the frame.</summary>
    private const int DefaultColor = unchecked((int)0xFFFFFFFF);

    /// <summary>
    /// Applies the caption colours of the palette in place. Safe to call on any build: the
    /// per-colour attributes need Windows 11, and older systems keep the plain dark or light
    /// caption. Dark or light is read off the palette's own window colour, so the two can't disagree.
    /// </summary>
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();

        // A contrast theme draws its own caption, in the colours the user chose for it. Ours set
        // over it dimmed the title (2026-10-17); given back to the system, also when a theme
        // comes on while the window is open.
        if (SystemParameters.HighContrast)
        {
            foreach (var attribute in new[] { CaptionColor, TextColor, BorderColor })
            {
                var systemDefault = DefaultColor;
                DwmSetWindowAttribute(handle, attribute, ref systemDefault, sizeof(int));
            }
            return;
        }

        var darkMode = window.TryFindResource("WindowBrush") is SolidColorBrush { Color: var c }
                       && (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) >= 128 ? 0 : 1;
        DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref darkMode, sizeof(int));

        TrySetColor(handle, CaptionColor, window, "WindowBrush");
        TrySetColor(handle, TextColor, window, "TextSecondaryBrush");
        TrySetColor(handle, BorderColor, window, "DividerBrush");
    }

    private static void TrySetColor(IntPtr handle, int attribute, Window window, string brushKey)
    {
        if (window.TryFindResource(brushKey) is not SolidColorBrush brush)
            return;

        var colorRef = ToColorRef(brush.Color);
        DwmSetWindowAttribute(handle, attribute, ref colorRef, sizeof(int));
    }

    /// <summary>COLORREF is 0x00BBGGRR, the reverse of the usual byte order.</summary>
    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
