using System.Text.Json;

namespace DanceCommander.Core.Config;

/// <summary>
/// A named palette such as Catppuccin or Nord, put onto the app's own tokens (DESIGN.md §1.3): the
/// same surfaces, three text greys, one accent and the semantic colours, so a theme changes how the
/// app looks and nothing about how it is laid out. Not a theming engine (§1.4): a theme is 26
/// colours, never styles or templates.
/// </summary>
/// <remarks>
/// Colours are named by Catppuccin's roles (https://catppuccin.com/palette/), which other themes
/// map onto well: three backgrounds, three surfaces, three overlays, two subtexts, the text, and
/// fourteen accents. Whatever a theme says, every text colour is then moved, along its own hue,
/// until it clears WCAG AA where it is drawn: a theme is a look, and the app stays readable in it.
/// </remarks>
public sealed class ColourTheme
{
    /// <summary>The setting's value for the app's own palettes, dark or light by the scheme setting.</summary>
    public const string Default = "default";

    public static readonly IReadOnlyList<string> Roles =
    [
        "rosewater", "flamingo", "pink", "mauve", "red", "maroon", "peach", "yellow", "green", "teal",
        "sky", "sapphire", "blue", "lavender", "text", "subtext1", "subtext0", "overlay2", "overlay1",
        "overlay0", "surface2", "surface1", "surface0", "base", "mantle", "crust",
    ];

    private const double AA = AccentColour.MinContrast;

    private readonly Dictionary<string, Rgb> _colours;

    private ColourTheme(string id, string label, bool dark, Dictionary<string, Rgb> colours, string accent)
    {
        Id = id;
        Label = label;
        IsDark = dark;
        _colours = colours;
        AccentRole = accent;
    }

    /// <summary>What the setting holds: <c>"nord"</c>, or a theme file's name without <c>.json</c>.</summary>
    public string Id { get; }

    public string Label { get; }

    public bool IsDark { get; }

    /// <summary>The role the accent is taken from, or a <c>#RRGGBB</c> of its own.</summary>
    public string AccentRole { get; }

    public Rgb this[string role] => _colours[role];

    public static IReadOnlyList<ColourTheme> BuiltIn { get; } =
    [
        FromList("catppuccin-latte", "Catppuccin Latte", false,
            "#dc8a78 #dd7878 #ea76cb #8839ef #d20f39 #e64553 #fe640b #df8e1d #40a02b #179299 #04a5e5 #209fb5 #1e66f5 #7287fd " +
            "#4c4f69 #5c5f77 #6c6f85 #7c7f93 #8c8fa1 #9ca0b0 #acb0be #bcc0cc #ccd0da #eff1f5 #e6e9ef #dce0e8"),
        FromList("catppuccin-frappe", "Catppuccin Frappé", true,
            "#f2d5cf #eebebe #f4b8e4 #ca9ee6 #e78284 #ea999c #ef9f76 #e5c890 #a6d189 #81c8be #99d1db #85c1dc #8caaee #babbf1 " +
            "#c6d0f5 #b5bfe2 #a5adce #949cbb #838ba7 #737994 #626880 #51576d #414559 #303446 #292c3c #232634"),
        FromList("catppuccin-macchiato", "Catppuccin Macchiato", true,
            "#f4dbd6 #f0c6c6 #f5bde6 #c6a0f6 #ed8796 #ee99a0 #f5a97f #eed49f #a6da95 #8bd5ca #91d7e3 #7dc4e4 #8aadf4 #b7bdf8 " +
            "#cad3f5 #b8c0e0 #a5adcb #939ab7 #8087a2 #6e738d #5b6078 #494d64 #363a4f #24273a #1e2030 #181926"),
        FromList("catppuccin-mocha", "Catppuccin Mocha", true,
            "#f5e0dc #f2cdcd #f5c2e7 #cba6f7 #f38ba8 #eba0ac #fab387 #f9e2af #a6e3a1 #94e2d5 #89dceb #74c7ec #89b4fa #b4befe " +
            "#cdd6f4 #bac2de #a6adc8 #9399b2 #7f849c #6c7086 #585b70 #45475a #313244 #1e1e2e #181825 #11111b"),
        // Nord: polar night for the surfaces, snow storm for the text, frost and aurora for the rest.
        FromList("nord", "Nord", true,
            "#d08770 #bf616a #b48ead #b48ead #bf616a #bf616a #d08770 #ebcb8b #a3be8c #8fbcbb #88c0d0 #81a1c1 #5e81ac #88c0d0 " +
            "#eceff4 #e5e9f0 #d8dee9 #b6bdca #9aa3b5 #7b88a1 #4c566a #434c5e #3b4252 #2e3440 #2a2f3a #242933",
            accent: "sky"),
        FromList("tokyo-night-storm", "Tokyo Night Storm", true,
            "#ff9e64 #f7768e #ff007c #bb9af7 #f7768e #db4b4b #ff9e64 #e0af68 #9ece6a #73daca #7dcfff #2ac3de #7aa2f7 #b4f9f8 " +
            "#c0caf5 #a9b1d6 #9aa5ce #8089b3 #737aa2 #565f89 #414868 #3b4261 #292e42 #24283b #1f2335 #1a1b26"),
    ];

    /// <summary>
    /// A theme file: <c>{ "name": "Gruvbox", "dark": true, "accent": "yellow", "colours": { "base": "#282828", "text": "#ebdbb2", … } }</c>.
    /// Only base and text are required; missing surfaces and greys are mixed between them, and
    /// missing accents come from Catppuccin's palette for the same darkness.
    /// </summary>
    /// <exception cref="FormatException">The file is not a theme, with what is wrong in words.</exception>
    public static ColourTheme Parse(string id, string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            throw new FormatException($"it isn't valid JSON (line {ex.LineNumber + 1})", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("it isn't a JSON object");

            var colours = new Dictionary<string, Rgb>(StringComparer.Ordinal);
            if (Property(root, "colours", "colors") is { ValueKind: JsonValueKind.Object } given)
            {
                foreach (var entry in given.EnumerateObject())
                {
                    var role = entry.Name.ToLowerInvariant();
                    if (!Roles.Contains(role))
                        throw new FormatException($"“{entry.Name}” is not a colour name it knows");
                    if (entry.Value.ValueKind != JsonValueKind.String || !TryHex(entry.Value.GetString(), out var colour))
                        throw new FormatException($"“{entry.Name}” isn't a colour such as #1E1E2E");
                    colours[role] = colour;
                }
            }

            foreach (var required in (string[])["base", "text"])
            {
                if (!colours.ContainsKey(required))
                    throw new FormatException($"it has no “{required}” colour");
            }

            var dark = Property(root, "dark") is { } darkness && darkness.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? darkness.GetBoolean()
                : AccentColour.Luminance(colours["base"]) < 0.4;

            var label = Property(root, "name") is { ValueKind: JsonValueKind.String } name && name.GetString() is { Length: > 0 } text
                ? text.Trim()
                : id;

            var accent = Property(root, "accent") is { ValueKind: JsonValueKind.String } accentValue
                ? accentValue.GetString()!.Trim().ToLowerInvariant()
                : "blue";
            if (!Roles.Contains(accent) && !TryHex(accent, out _))
                throw new FormatException($"its accent “{accent}” is neither a colour name nor #RRGGBB");

            // A pane no text can be read on (mid grey) is moved away from where its text goes,
            // until there is room for the greys and the accent-tinted selection: 10:1 for the
            // brightest leaves AA for the dimmest, on a selected row too.
            var pane = colours["base"];
            var toward = dark ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0);
            var away = dark ? new Rgb(0, 0, 0) : new Rgb(255, 255, 255);
            for (var amount = 0.0; amount <= 1.0 && AccentColour.Contrast(colours["base"], toward) < 10; amount += 0.02)
                colours["base"] = AccentColour.Mix(pane, away, amount);

            Fill(colours, dark);
            return new ColourTheme(id, label, dark, colours, accent);
        }
    }

    /// <summary>A built-in theme by its id, or null.</summary>
    public static ColourTheme? Find(string id) =>
        BuiltIn.FirstOrDefault(theme => string.Equals(theme.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every token the app's palettes declare (Theme/Dark.xaml), from this theme. With
    /// <paramref name="accent"/> given, that colour is the accent rather than the theme's own.
    /// </summary>
    public IReadOnlyDictionary<string, Rgb> Tokens(Rgb? accent = null)
    {
        var c = _colours;
        var white = new Rgb(255, 255, 255);

        var pane = c["base"];
        var inactive = AccentColour.Mix(pane, c["mantle"], 0.5);
        var surfaces = new[] { pane, inactive };

        var tokens = new Dictionary<string, Rgb>(StringComparer.Ordinal)
        {
            ["WindowBrush"] = c["mantle"],
            ["PaneBrush"] = pane,
            ["PaneInactiveBrush"] = inactive,
            ["SunkenBrush"] = c["crust"],
            ["HoverBrush"] = AccentColour.Mix(pane, c["surface0"], 0.8),
            ["DividerBrush"] = c["surface1"],
            // A step lighter than anything a popup can float over, as the app's own palettes do.
            ["PopupBrush"] = IsDark ? c["surface0"] : AccentColour.Mix(pane, white, 0.7),
            ["PopupBorderBrush"] = c["surface2"],
            ["SelectionInactiveBrush"] = AccentColour.Mix(pane, c["surface1"], 0.7),
            ["ScrollThumbBrush"] = c["surface2"],
            ["ScrollThumbHoverBrush"] = c["overlay0"],
            ["ScrollThumbActiveBrush"] = c["overlay1"],
        };

        // Text and every colour drawn as text, kept legible on both panes.
        Rgb Legible(string role, params Rgb[] more) => Readable(c[role], [.. surfaces, .. more]);
        tokens["TextPrimaryBrush"] = Legible("text");
        tokens["TextSecondaryBrush"] = Legible("subtext1");
        tokens["TextMutedBrush"] = Legible("subtext0");
        tokens["GitModifiedBrush"] = Legible("yellow");
        tokens["GitAddedBrush"] = Legible("green");
        tokens["GitDeletedBrush"] = Legible("maroon");
        tokens["GitUntrackedBrush"] = Legible("sapphire");
        tokens["GitConflictBrush"] = Legible("red");
        tokens["SyntaxKeywordBrush"] = Legible("mauve");
        tokens["SyntaxStringBrush"] = Legible("green");
        tokens["SyntaxNumberBrush"] = Legible("peach");
        tokens["SyntaxNameBrush"] = Legible("blue");

        // Messages sit on their own tint of the pane, and must read on it as on the pane.
        var dangerSurface = AccentColour.Mix(pane, c["red"], IsDark ? 0.16 : 0.12);
        var warningSurface = AccentColour.Mix(pane, c["yellow"], IsDark ? 0.14 : 0.18);
        var infoSurface = AccentColour.Mix(pane, c["blue"], IsDark ? 0.16 : 0.12);
        tokens["DangerSurfaceBrush"] = dangerSurface;
        tokens["WarningSurfaceBrush"] = warningSurface;
        tokens["InfoSurfaceBrush"] = infoSurface;
        tokens["DangerBrush"] = Legible("red", dangerSurface);
        tokens["WarningBrush"] = Legible("yellow", warningSurface);
        tokens["TextPrimaryBrush"] = Readable(tokens["TextPrimaryBrush"], [.. surfaces, infoSurface]);

        // The accent as the app fits any accent: legible on the panes and on the selection it tints.
        var wanted = accent ?? (TryHex(AccentRole, out var own) ? own : c[AccentRole]);
        var hover = tokens["HoverBrush"];
        var strength = AccentColour.Luminance(pane) < 0.4 ? 0.24 : 0.17;
        var first = AccentColour.FitTo(wanted, pane, inactive, hover);
        var fitted = AccentColour.FitTo(first, pane, inactive, hover, AccentColour.Mix(pane, first, strength));
        var selection = AccentColour.Mix(pane, fitted, strength);
        tokens["AccentColor"] = fitted;
        tokens["AccentBrush"] = fitted;
        tokens["SelectionActiveBrush"] = selection;

        // Names on a selected row: the two text greys hold on the selection too.
        tokens["TextPrimaryBrush"] = Readable(tokens["TextPrimaryBrush"], [.. surfaces, infoSurface, selection]);
        tokens["TextSecondaryBrush"] = Readable(tokens["TextSecondaryBrush"], [.. surfaces, selection]);

        return tokens;
    }

    /// <summary>
    /// The colour, moved toward white on a dark theme or black on a light one until it clears AA
    /// on every surface: the hue stays, only its weight changes, as with the accent.
    /// </summary>
    private Rgb Readable(Rgb colour, Rgb[] surfaces)
    {
        var toward = IsDark ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0);
        var result = colour;
        for (var amount = 0.0; amount <= 1.0 && surfaces.Any(s => AccentColour.Contrast(result, s) < AA); amount += 0.02)
            result = AccentColour.Mix(colour, toward, amount);
        return result;
    }

    /// <summary>The roles a theme file left out: greys mixed from base to text, accents from Catppuccin.</summary>
    private static void Fill(Dictionary<string, Rgb> colours, bool dark)
    {
        var bg = colours["base"];
        var fg = colours["text"];
        var deeper = dark ? new Rgb(0, 0, 0) : fg;

        void Default(string role, Rgb value) => colours.TryAdd(role, value);
        Default("mantle", AccentColour.Mix(bg, deeper, dark ? 0.15 : 0.04));
        Default("crust", AccentColour.Mix(bg, deeper, dark ? 0.3 : 0.08));
        Default("surface0", AccentColour.Mix(bg, fg, 0.10));
        Default("surface1", AccentColour.Mix(bg, fg, 0.16));
        Default("surface2", AccentColour.Mix(bg, fg, 0.24));
        Default("overlay0", AccentColour.Mix(bg, fg, 0.35));
        Default("overlay1", AccentColour.Mix(bg, fg, 0.45));
        Default("overlay2", AccentColour.Mix(bg, fg, 0.55));
        Default("subtext0", AccentColour.Mix(bg, fg, 0.65));
        Default("subtext1", AccentColour.Mix(bg, fg, 0.8));

        var catppuccin = BuiltIn[dark ? 3 : 0];
        foreach (var role in Roles)
            Default(role, catppuccin[role]);
    }

    private static ColourTheme FromList(string id, string label, bool dark, string list, string accent = "blue")
    {
        var values = list.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var colours = new Dictionary<string, Rgb>(StringComparer.Ordinal);
        for (var i = 0; i < Roles.Count; i++)
        {
            TryHex(values[i], out var colour);
            colours[Roles[i]] = colour;
        }

        return new ColourTheme(id, label, dark, colours, accent);
    }

    private static bool TryHex(string? text, out Rgb colour) =>
        AccentColour.TryParse(text, out colour) && text!.TrimStart().StartsWith('#');

    private static JsonElement? Property(JsonElement root, params string[] names)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                return property.Value;
        }

        return null;
    }
}
