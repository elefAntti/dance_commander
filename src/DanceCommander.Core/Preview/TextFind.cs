namespace DanceCommander.Core.Preview;

public readonly record struct TextMatch(int Start, int Length);

/// <summary>
/// Find in a previewed file: plain text, and case-sensitive only when what was typed has a
/// capital in it, as in the editors and search tools a developer already uses.
/// </summary>
public static class TextFind
{
    /// <summary>
    /// Enough to page through; past it the count says "10,000+". A single letter in a megabyte
    /// would otherwise list every other character before the first keystroke was drawn.
    /// </summary>
    public const int MostMatches = 10_000;

    public static IReadOnlyList<TextMatch> All(string text, string pattern, int most = MostMatches)
    {
        if (pattern.Length == 0)
            return [];

        var comparison = pattern.Any(char.IsUpper) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var found = new List<TextMatch>();

        // Ordinal comparisons fold case character by character, so a match is always as long as
        // what was typed.
        for (var at = text.IndexOf(pattern, comparison); at >= 0 && found.Count < most;
             at = text.IndexOf(pattern, at + pattern.Length, comparison))
        {
            found.Add(new TextMatch(at, pattern.Length));
        }

        return found;
    }

    /// <summary>The first match starting at or after <paramref name="offset"/>, wrapping round to the first.</summary>
    public static int FirstFrom(IReadOnlyList<TextMatch> matches, int offset)
    {
        if (matches.Count == 0)
            return -1;

        var low = 0;
        var high = matches.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (matches[middle].Start < offset)
                low = middle + 1;
            else
                high = middle;
        }

        return low == matches.Count ? 0 : low;
    }
}
