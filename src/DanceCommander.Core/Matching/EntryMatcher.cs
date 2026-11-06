using System.Text.RegularExpressions;
using DanceCommander.Core.Fs;
using DotNet.Globbing;

namespace DanceCommander.Core.Matching;

/// <summary>
/// A condition on an entry, as written in a config file. One implementation serves both a tool's
/// <c>when</c> and a highlight rule's <c>match</c> (§6.4, §6.6) — two matchers would drift.
/// </summary>
/// <remarks>Several conditions in one rule are combined with AND.</remarks>
public sealed record MatchRule
{
    public string? Glob { get; init; }

    public string? Regex { get; init; }

    public IReadOnlyList<string>? Extensions { get; init; }

    public bool DirectoriesOnly { get; init; }

    public bool FilesOnly { get; init; }

    /// <summary>Human sizes: "512", "100kB", "2MB", "1.5GB".</summary>
    public string? MinSize { get; init; }

    public string? MaxSize { get; init; }

    /// <summary>Human durations: "30m", "12h", "7d", "2w".</summary>
    public string? NewerThan { get; init; }

    public string? OlderThan { get; init; }
}

public sealed class CompiledMatcher
{
    private readonly MatchRule _rule;
    private readonly Glob[]? _globs;
    private readonly Regex? _regex;
    private readonly HashSet<string>? _extensions;
    private readonly long? _minSize;
    private readonly long? _maxSize;
    private readonly TimeSpan? _newerThan;
    private readonly TimeSpan? _olderThan;

    public static CompiledMatcher Always { get; } = new(new MatchRule());

    private CompiledMatcher(MatchRule rule)
    {
        _rule = rule;

        // Windows names are case-insensitive, and DotNet.Glob is not, so both sides are lowered.
        // Brace alternation is expanded here because the library has no notion of it, and
        // "*.{cs,xaml}" is how anyone writing one of these files expects to say it.
        if (!string.IsNullOrWhiteSpace(rule.Glob))
            _globs = ExpandBraces(rule.Glob.ToLowerInvariant()).Select(Glob.Parse).ToArray();

        // Case sensitivity is left to the pattern: write (?i) if you want it.
        if (!string.IsNullOrWhiteSpace(rule.Regex))
            _regex = new Regex(rule.Regex, RegexOptions.Compiled);

        if (rule.Extensions is { Count: > 0 })
            _extensions = rule.Extensions
                .Select(e => e.TrimStart('.').ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _minSize = ParseSize(rule.MinSize);
        _maxSize = ParseSize(rule.MaxSize);
        _newerThan = ParseDuration(rule.NewerThan);
        _olderThan = ParseDuration(rule.OlderThan);
    }

    public static CompiledMatcher Compile(MatchRule? rule) =>
        rule is null ? Always : new CompiledMatcher(rule);

    public bool Matches(FsEntry entry)
    {
        if (_rule.DirectoriesOnly && !entry.IsDirectory)
            return false;
        if (_rule.FilesOnly && entry.IsDirectory)
            return false;

        if (_globs is not null)
        {
            var lowered = entry.Name.ToLowerInvariant();
            if (!_globs.Any(g => g.IsMatch(lowered)))
                return false;
        }
        if (_regex is not null && !_regex.IsMatch(entry.Name))
            return false;
        if (_extensions is not null && !_extensions.Contains(entry.Extension))
            return false;

        if (_minSize is { } min && entry.Size < min)
            return false;
        if (_maxSize is { } max && entry.Size > max)
            return false;

        if (_newerThan is { } newer)
        {
            if (entry.Modified is not { } modified || DateTimeOffset.Now - modified > newer)
                return false;
        }

        if (_olderThan is { } older)
        {
            if (entry.Modified is not { } modified || DateTimeOffset.Now - modified < older)
                return false;
        }

        return true;
    }

    /// <summary>Turns "*.{cs,xaml}" into "*.cs" and "*.xaml"; nests, and passes anything else through.</summary>
    internal static IEnumerable<string> ExpandBraces(string pattern)
    {
        var open = pattern.IndexOf('{');
        var close = open < 0 ? -1 : pattern.IndexOf('}', open);
        if (open < 0 || close < 0)
        {
            yield return pattern;
            yield break;
        }

        var prefix = pattern[..open];
        var suffix = pattern[(close + 1)..];

        foreach (var option in pattern[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries))
        {
            foreach (var expanded in ExpandBraces(prefix + option + suffix))
                yield return expanded;
        }
    }

    // ---- human values -------------------------------------------------------------------

    private static readonly (string Suffix, long Factor)[] SizeUnits =
    [
        ("tb", 1024L * 1024 * 1024 * 1024),
        ("gb", 1024L * 1024 * 1024),
        ("mb", 1024L * 1024),
        ("kb", 1024L),
        ("b", 1L),
    ];

    public static long? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var value = text.Trim().ToLowerInvariant();
        foreach (var (suffix, factor) in SizeUnits)
        {
            if (!value.EndsWith(suffix, StringComparison.Ordinal))
                continue;

            var number = value[..^suffix.Length].Trim();
            return double.TryParse(number, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? (long)(parsed * factor)
                : null;
        }

        return long.TryParse(value, out var bytes) ? bytes : null;
    }

    public static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var value = text.Trim().ToLowerInvariant();
        var unit = value[^1];
        var number = value[..^1];

        if (!double.TryParse(number, System.Globalization.CultureInfo.InvariantCulture, out var amount))
            return null;

        return unit switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            'w' => TimeSpan.FromDays(amount * 7),
            _ => null,
        };
    }
}
