using System.Windows;
using System.Windows.Media;
using DanceCommander.Core.Config;
using Microsoft.Win32;

namespace DanceCommander.App.Services;

/// <summary>
/// Dark or light (DESIGN.md §1.3: both first-class). <c>settings.json</c>'s <c>appearance.scheme</c>
/// says which: <c>"system"</c> (the default) follows Windows' app mode, <c>"dark"</c> and
/// <c>"light"</c> stay put. Windows' own high contrast theme overrides the choice and lends its
/// colours (Theme/HighContrast.xaml). On top of the palette goes the accent
/// (<c>appearance.accent</c>), Windows' by default. Applied before the window is built, and again
/// whenever any of it changes, live: every brush is a DynamicResource, so swapping the palette
/// dictionary repaints the lot.
/// </summary>
/// <remarks>
/// What does not follow a DynamicResource on its own listens to <see cref="Changed"/>: a pane's
/// surface, set once from code, and rows whose colours come through converters.
/// </remarks>
public sealed class ColourScheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    private readonly IConfigStore<SettingsFile> _settings;
    private (Palette Palette, string Accent)? _applied;

    /// <summary>The palette put in place by the last switch, found again by reference: a theme's is made in code and has no Source.</summary>
    private ResourceDictionary? _current;

    private ColourTheme? _theme;
    private string? _themeTokens;

    /// <summary>Made once a theme file is in use, so saving it shows the change as settings.json does.</summary>
    private System.IO.FileSystemWatcher? _themeFiles;

    private enum Palette { Dark, Light, HighContrast, Theme }

    /// <summary>After a switch, on the UI thread.</summary>
    public static event EventHandler? Changed;

    /// <summary>The chosen theme couldn't be used, in words; the app's own colours are in place instead.</summary>
    public event EventHandler<string>? ThemeProblem;

    /// <summary>Why the chosen theme isn't the one shown, or null.</summary>
    public string? Problem { get; private set; }

    public ColourScheme(IConfigStore<SettingsFile> settings)
    {
        _settings = settings;
        settings.Changed += (_, _) => OnUiThread(Apply);

        // Windows switching between light and dark app mode, or its accent, is a General or Color
        // preference change; a contrast theme coming or going is an Accessibility one.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility)
                OnUiThread(Apply);
        };
    }

    public bool IsLight => _applied?.Palette == Palette.Light || _theme is { IsDark: false };

    /// <summary>Puts the chosen palette and accent in place, if they are not already, and tells whoever draws by hand.</summary>
    public void Apply()
    {
        var palette = Resolve();
        var accent = ResolveAccent(palette);

        // A theme's colours are part of what is in place: the same name can be a file edited since.
        IReadOnlyDictionary<string, Rgb>? tokens = null;
        if (palette == Palette.Theme)
        {
            WatchThemeFiles();
            tokens = _theme!.Tokens(accent);
            var signature = string.Join(",", tokens.Select(t => t.Value.ToString()));
            if (signature != _themeTokens)
                _applied = null;
            _themeTokens = signature;
        }

        var state = (palette, accent?.ToString() ?? string.Empty);
        if (_applied == state)
            return;

        var merged = Application.Current.Resources.MergedDictionaries;
        var current = _current is not null && merged.Contains(_current) ? _current
            : merged.FirstOrDefault(d => d.Source?.OriginalString is { } source
                && (source.EndsWith("Dark.xaml", StringComparison.Ordinal)
                    || source.EndsWith("Light.xaml", StringComparison.Ordinal)
                    || source.EndsWith("HighContrast.xaml", StringComparison.Ordinal)));

        ResourceDictionary chosen;
        if (tokens is not null)
        {
            chosen = FromTokens(tokens);
        }
        else
        {
            var file = palette switch { Palette.Light => "Light", Palette.HighContrast => "HighContrast", _ => "Dark" };
            chosen = new ResourceDictionary { Source = new Uri($"Theme/{file}.xaml", UriKind.Relative) };
            if (palette == Palette.HighContrast)
                MixHighContrastFills(chosen);
            else if (accent is { } colour)
                ApplyAccent(chosen, colour);
        }

        if (current is null)
            merged.Insert(1, chosen);
        else
            merged[merged.IndexOf(current)] = chosen;

        _current = chosen;
        _applied = state;

        foreach (Window window in Application.Current.Windows)
            TitleBar.Apply(window);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The three fills a contrast theme has no system colour for: steps from the window colour
    /// toward the text colour. The strongest, the selection, goes only as far as keeps the theme's
    /// text at 7:1 on it (WCAG's AAA, what contrast themes are for), and the other two keep their
    /// proportion to it, so hover still reads as less than selected. Mixed by fixed amounts,
    /// Dusk's selected rows came to 3:1. Rows keep their own text colours, so Highlight and
    /// HighlightText, a pair that needs both halves, can't be used here.
    /// </summary>
    private static void MixHighContrastFills(ResourceDictionary palette)
    {
        var window = ToRgb(SystemColors.WindowColor);
        var text = ToRgb(SystemColors.WindowTextColor);
        var reach = AccentColour.Reach(window, text, most: 0.38, keep: 7);
        palette["HoverBrush"] = Brush(AccentColour.Mix(window, text, reach * 0.18 / 0.38));
        palette["SelectionInactiveBrush"] = Brush(AccentColour.Mix(window, text, reach * 0.28 / 0.38));
        palette["SelectionActiveBrush"] = Brush(AccentColour.Mix(window, text, reach));
    }

    /// <summary>
    /// The accent fitted to this palette's surfaces, and the selection fill tinted with it: a
    /// selected row is the accent held at low strength over the pane, as the palettes' own are.
    /// </summary>
    private static void ApplyAccent(ResourceDictionary palette, Rgb wanted)
    {
        var pane = ToRgb(((SolidColorBrush)palette["PaneBrush"]).Color);
        var inactive = ToRgb(((SolidColorBrush)palette["PaneInactiveBrush"]).Color);
        var hover = ToRgb(((SolidColorBrush)palette["HoverBrush"]).Color);
        var strength = AccentColour.Luminance(pane) < 0.4 ? 0.24 : 0.17;

        // The fill depends on the accent and the accent has to be legible on the fill: fit to the
        // panes, tint, then fit again against what the tint came to.
        var first = AccentColour.FitTo(wanted, pane, inactive, hover);
        var fitted = AccentColour.FitTo(first, pane, inactive, hover, AccentColour.Mix(pane, first, strength));

        palette["AccentColor"] = Color.FromRgb(fitted.R, fitted.G, fitted.B);
        palette["AccentBrush"] = Brush(fitted);
        palette["SelectionActiveBrush"] = Brush(AccentColour.Mix(pane, fitted, strength));
    }

    private void WatchThemeFiles()
    {
        if (_themeFiles is not null || ColourThemeFiles.Directory is not { } folder || !System.IO.Directory.Exists(folder))
            return;

        _themeFiles = new System.IO.FileSystemWatcher(folder, "*.json")
        {
            NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.Size,
        };

        // An editor saves in more than one write; a moment later the file is whole.
        var pending = 0;
        void OnChange(object sender, System.IO.FileSystemEventArgs e)
        {
            if (Interlocked.Exchange(ref pending, 1) == 1)
                return;
            _ = Task.Delay(300).ContinueWith(_ =>
            {
                Volatile.Write(ref pending, 0);
                OnUiThread(Apply);
            }, TaskScheduler.Default);
        }

        _themeFiles.Changed += OnChange;
        _themeFiles.Created += OnChange;
        _themeFiles.Renamed += OnChange;
        _themeFiles.EnableRaisingEvents = true;
    }

    /// <summary>
    /// A theme's palette, keyed as Theme/Dark.xaml is, with the corner where scroll bars meet in
    /// the theme's colours too.
    /// </summary>
    private static ResourceDictionary FromTokens(IReadOnlyDictionary<string, Rgb> tokens)
    {
        var palette = new ResourceDictionary();
        foreach (var (key, colour) in tokens)
            palette[key] = key == "AccentColor" ? Color.FromRgb(colour.R, colour.G, colour.B) : Brush(colour);

        palette[SystemColors.ControlBrushKey] = Brush(AccentColour.Mix(tokens["PaneBrush"], tokens["HoverBrush"], 0.5));
        return palette;
    }

    private static SolidColorBrush Brush(Rgb colour)
    {
        var brush = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B));
        brush.Freeze();
        return brush;
    }

    private static Rgb ToRgb(Color colour) => new(colour.R, colour.G, colour.B);

    /// <summary>
    /// The colour asked for, or Windows' when the setting says so (or says something unreadable).
    /// Null keeps the palette's own: when Windows doesn't say, and in a contrast theme, whose
    /// colours are the user's.
    /// </summary>
    /// <remarks>
    /// With a theme, "Windows" means the theme's own accent: it is part of the look someone
    /// picked the theme for. An accent chosen in the settings still wins.
    /// </remarks>
    private Rgb? ResolveAccent(Palette palette) =>
        palette == Palette.HighContrast ? null
        : palette == Palette.Theme ? (AccentColour.TryParse(_settings.Current.Appearance.Accent, out var mine) ? mine : null)
        : AccentColour.TryParse(_settings.Current.Appearance.Accent, out var chosen) ? chosen
        : WindowsAccent();

    /// <summary>The colour from Windows' Settings as the window manager keeps it (0xAABBGGRR); missing until one was chosen.</summary>
    private static Rgb? WindowsAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DwmKey);
            return key?.GetValue("AccentColor") is int value
                ? new Rgb((byte)value, (byte)(value >> 8), (byte)(value >> 16))
                : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }
    }

    /// <summary>High contrast wins over the choice: it is the user's own setting for being able to read anything.</summary>
    private Palette Resolve()
    {
        _theme = null;
        string? problem = null;
        try
        {
            if (SystemParameters.HighContrast)
                return Palette.HighContrast;

            var theme = _settings.Current.Appearance.Theme.Trim();
            if (theme.Length > 0 && !string.Equals(theme, ColourTheme.Default, StringComparison.OrdinalIgnoreCase))
            {
                if (ColourThemeFiles.TryLoad(theme, out var loaded, out problem))
                {
                    _theme = loaded;
                    return Palette.Theme;
                }

                Serilog.Log.Warning("Theme {Theme} not used: {Problem}", theme, problem);
            }

            return _settings.Current.Appearance.Scheme.Trim().ToLowerInvariant() switch
            {
                "light" => Palette.Light,
                "dark" => Palette.Dark,
                _ => WindowsAppsAreLight() ? Palette.Light : Palette.Dark,
            };
        }
        finally
        {
            // Said once when it changes, not again on every Windows preference event.
            if (problem != Problem)
            {
                Problem = problem;
                if (problem is not null)
                    ThemeProblem?.Invoke(this, problem + " The app's own colours are used.");
            }
        }
    }

    /// <summary>Windows' "default app mode". Missing means light, as on a fresh Windows.</summary>
    private static bool WindowsAppsAreLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    private static void OnUiThread(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.InvokeAsync(action);
    }
}
