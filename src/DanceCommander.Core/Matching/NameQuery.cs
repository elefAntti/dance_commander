using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Matching;

/// <summary>
/// What a typed query means when it is matched against file names.
/// </summary>
/// <remarks>
/// One rule, used by both the search location (§1.2/3) and the quick filter, because the two are
/// the same gesture at different scopes and a user who learns one has learned the other. A bare
/// word means "the name contains this", which is what people type and expect; anything containing
/// a wildcard is a glob, because that is what a wildcard means everywhere else on a computer.
/// Several words separated by spaces are all required, each read by that same rule — the command
/// palette's rule too — so "mon d" narrows "CPU monitor, disk monitor, pokemon" to the disk one.
/// </remarks>
public static class NameQuery
{
    /// <summary>True when the query should be read as a glob rather than as a substring.</summary>
    public static bool IsGlob(string query) => query.Contains('*') || query.Contains('?');

    /// <summary>The glob a typed query stands for. An empty query matches everything.</summary>
    public static string ToGlob(string query)
    {
        query = query.Trim();
        return query.Length == 0 ? "*" : IsGlob(query) ? query : $"*{query}*";
    }

    /// <summary>The words of a query: what each has to match on its own.</summary>
    public static string[] Terms(string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static NameMatcher Compile(string query) => new(Terms(query));
}

/// <summary>A compiled <see cref="NameQuery"/>: whether a name matches, and how well.</summary>
public sealed class NameMatcher
{
    /// <summary>The plain words, in the order typed. Only these take part in ranking.</summary>
    private readonly string[] _words;

    private readonly CompiledMatcher[] _globs;

    internal NameMatcher(string[] terms)
    {
        _words = terms.Where(t => !NameQuery.IsGlob(t)).ToArray();
        _globs = terms.Where(NameQuery.IsGlob)
            .Select(t => CompiledMatcher.Compile(new MatchRule { Glob = t }))
            .ToArray();
    }

    public bool Matches(FsEntry entry) =>
        _words.All(w => entry.Name.Contains(w, StringComparison.OrdinalIgnoreCase))
        && _globs.All(g => g.Matches(entry));

    /// <summary>
    /// Where a matching name ranks among the matches; lower is better, and names of equal rank keep
    /// whatever order the listing gives them. In order of weight: the first word starts the name
    /// ("rea" lands on README.md before already.txt, as type-ahead did); every word starts a word
    /// of the name ("mon" puts CPU monitor before pokemon); the words appear in the order typed.
    /// Globs take no part — a wildcard says exactly where the text may be, and ranking on top of
    /// that would second-guess it.
    /// </summary>
    public int Rank(string name)
    {
        if (_words.Length == 0)
            return 0;

        var rank = 0;
        if (!name.StartsWith(_words[0], StringComparison.OrdinalIgnoreCase))
            rank += 4;
        if (!_words.All(w => StartsAWord(name, w)))
            rank += 2;
        if (!InOrder(name))
            rank += 1;
        return rank;
    }

    /// <summary>
    /// The matches among rows already in the listing's order, best <see cref="Rank"/> first and in
    /// that order within a rank.
    /// </summary>
    /// <remarks>
    /// The same order as sorting the matches by rank and then by the listing's own comparison, but
    /// without the comparison: the listing is sorted once, and each keystroke only filters it and
    /// orders a handful of small numbers. Sorting by name again on every key was what made the
    /// quick filter lag in a flattened tree of tens of thousands of files.
    /// </remarks>
    public List<T> Arrange<T>(IEnumerable<T> ordered, Func<T, FsEntry> entryOf)
    {
        var matched = ordered.Where(r => Matches(entryOf(r)));
        // OrderBy is stable, which is what keeps the listing's order within a rank.
        return _words.Length == 0 ? matched.ToList() : matched.OrderBy(r => Rank(entryOf(r).Name)).ToList();
    }

    private static bool StartsAWord(string name, string word)
    {
        for (var at = name.IndexOf(word, StringComparison.OrdinalIgnoreCase);
             at >= 0;
             at = name.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (IsWordStart(name, at))
                return true;
        }

        return false;
    }

    /// <summary>After a separator, at a camel-case hump, or where letters turn to digits or back.</summary>
    private static bool IsWordStart(string name, int at)
    {
        if (at == 0)
            return true;
        char before = name[at - 1], here = name[at];
        return !char.IsLetterOrDigit(before)
            || (char.IsLower(before) && char.IsUpper(here))
            || char.IsDigit(before) != char.IsDigit(here);
    }

    /// <summary>Each word found after the end of the one before it; the earliest match is always the best bet.</summary>
    private bool InOrder(string name)
    {
        var from = 0;
        foreach (var word in _words)
        {
            var at = name.IndexOf(word, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return false;
            from = at + word.Length;
        }

        return true;
    }
}
