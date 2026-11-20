namespace DanceCommander.Core.Config;

/// <summary>
/// The user's own themes: <c>themes\*.json</c> beside settings.json, each one a
/// <see cref="ColourTheme"/> file, chosen by its name without <c>.json</c>.
/// </summary>
public static class ColourThemeFiles
{
    /// <summary>Where they are looked for; set once at launch. Null finds none.</summary>
    public static string? Directory { get; set; }

    /// <summary>A theme file is small; anything larger isn't one, and isn't read.</summary>
    private const long MostBytes = 64 * 1024;

    /// <summary>The theme files there are, as choices for the setting, by the name each gives itself.</summary>
    public static IReadOnlyList<SettingChoice> Choices()
    {
        if (Directory is not { } folder || !System.IO.Directory.Exists(folder))
            return [];

        var choices = new List<SettingChoice>();
        try
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                var id = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                if (!IsSafeId(id) || ColourTheme.Find(id) is not null)
                    continue;

                // Named as the file names itself; one that can't be read is still offered by its
                // file name, so choosing it says what is wrong with it.
                var label = TryLoad(id, out var theme, out _) ? theme!.Label : id;
                choices.Add(new SettingChoice(id, label));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return choices;
    }

    /// <summary>
    /// The theme a setting names: a built-in one, or a file in <see cref="Directory"/>. False with
    /// the reason in words when the file is missing or not a theme.
    /// </summary>
    public static bool TryLoad(string id, out ColourTheme? theme, out string? problem)
    {
        theme = ColourTheme.Find(id);
        problem = null;
        if (theme is not null)
            return true;

        // The setting is text from a file, and it becomes part of a path: only a plain name, so it
        // can't reach outside the themes folder.
        id = id.Trim();
        if (!IsSafeId(id) || Directory is not { } folder)
        {
            problem = $"There is no theme called “{id}”.";
            return false;
        }

        var path = Path.Combine(folder, id + ".json");
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                problem = $"There is no theme called “{id}”: themes\\{id}.json wasn't found.";
                return false;
            }

            if (file.Length > MostBytes)
            {
                problem = $"themes\\{id}.json is too large to be a theme.";
                return false;
            }

            theme = ColourTheme.Parse(id, File.ReadAllText(path));
            return true;
        }
        catch (FormatException ex)
        {
            problem = $"themes\\{id}.json can't be used: {ex.Message}.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"themes\\{id}.json couldn't be read: {ex.Message}";
            return false;
        }
    }

    private static bool IsSafeId(string id) =>
        id.Length is > 0 and <= 64
        && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or ' ' or '.')
        && !id.Contains("..", StringComparison.Ordinal)
        && !id.StartsWith('.') && !id.EndsWith('.') && !id.EndsWith(' ');
}
