namespace DanceCommander.Core.Fs;

/// <summary>Text-editing help for addresses as typed, where a "word" is a path part.</summary>
public static class PathText
{
    /// <summary>What separates the parts of an address: path separators, a drive or scheme colon,
    /// a view's parameter mark and an archive boundary.</summary>
    private static readonly char[] Separators = ['\\', '/', ':', '?', '!'];

    /// <summary>
    /// The path part around <paramref name="index"/> — what a double-click in the address bar
    /// selects, as in Explorer: in <c>C:\Users\me\Downloads</c>, a click on "Users" selects
    /// "Users", not the whole address the way a text box's word rules would.
    /// </summary>
    /// <remarks>
    /// A click on a separator, or just past a part's end, takes the part to its left: the character
    /// index a click maps to sits between characters, and the part the pointer was over is the one
    /// that ends there. Empty when there is no part at all (an empty box, a run of separators).
    /// </remarks>
    public static (int Start, int Length) SegmentAt(string text, int index)
    {
        if (string.IsNullOrEmpty(text))
            return (0, 0);

        index = Math.Clamp(index, 0, text.Length);

        // On a separator (or at the end): the part that ends here, if any.
        if (index == text.Length || Array.IndexOf(Separators, text[index]) >= 0)
        {
            if (index == 0 || Array.IndexOf(Separators, text[index - 1]) >= 0)
                return (index, 0);
            index--;
        }

        var start = index;
        while (start > 0 && Array.IndexOf(Separators, text[start - 1]) < 0)
            start--;

        var end = index;
        while (end < text.Length && Array.IndexOf(Separators, text[end]) < 0)
            end++;

        return (start, end - start);
    }
}
