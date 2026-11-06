using System.Text.RegularExpressions;

namespace DanceCommander.Core.Matching;

/// <summary>
/// <see cref="NameQuery"/>'s rule for text that is not a file name: a bare word means "contains",
/// and <c>*</c> and <c>?</c> are wildcards. Used for a tool's <c>remoteMatches</c> (§6.6).
/// </summary>
/// <remarks>
/// Not a glob in the file-name sense, and that is the point of having it: a glob's <c>*</c> stops at
/// <c>/</c>, and a remote URL is mostly slashes, so <c>*orbit*</c> would never have matched
/// <c>git@gitlab.example.com:team/orbit.git</c>. Case-insensitive, like the host names and
/// Windows paths these are written against.
/// </remarks>
public sealed class TextPattern
{
    private readonly Regex _regex;

    private TextPattern(Regex regex) => _regex = regex;

    public static TextPattern Compile(string pattern)
    {
        var body = Regex.Escape(NameQuery.ToGlob(pattern)).Replace(@"\*", ".*").Replace(@"\?", ".");
        return new TextPattern(new Regex($"^{body}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    public bool Matches(string? text) => text is not null && _regex.IsMatch(text);
}
