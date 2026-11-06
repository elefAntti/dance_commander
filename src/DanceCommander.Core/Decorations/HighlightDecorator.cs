using DanceCommander.Core.Config;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Matching;

namespace DanceCommander.Core.Decorations;

/// <summary>One entry in highlighting.json: a condition and how matching rows should look.</summary>
public sealed class HighlightRule
{
    public string? Name { get; set; }

    public MatchRule? Match { get; set; }

    /// <summary>A hex colour such as "#4FC1FF".</summary>
    public string? Foreground { get; set; }

    public string? Background { get; set; }

    public bool Bold { get; set; }

    public bool Italic { get; set; }

    /// <summary>Pins matching rows above the normal ordering. Higher is nearer the top.</summary>
    public int SortGroup { get; set; }

    public bool Enabled { get; set; } = true;
}

public sealed class HighlightingFile
{
    public List<HighlightRule> Rules { get; set; } = [];
}

/// <summary>
/// Applies the user's highlight rules (§6.4). First match wins, top down, so the file reads as a
/// priority list rather than a set of rules that silently combine.
/// </summary>
public sealed class HighlightDecorator : IRowDecorator
{
    private readonly IConfigStore<HighlightingFile> _store;
    private (CompiledMatcher Matcher, HighlightStyle Style, int SortGroup)[] _compiled = [];

    public HighlightDecorator(IConfigStore<HighlightingFile> store)
    {
        _store = store;
        _store.Changed += (_, _) => Compile();
        Compile();
    }

    /// <summary>Raised after the rules change, so panes can redraw what they are showing.</summary>
    public event EventHandler? RulesChanged;

    public ValueTask DecorateAsync(IReadOnlyList<IRowDecorationSink> rows, FsUri directory, CancellationToken ct)
    {
        // Compiled once per config change, then a plain array scan per row — no per-cell
        // converters and no triggers, which is what keeps this affordable on a large listing.
        var rules = _compiled;
        if (rules.Length == 0)
            return ValueTask.CompletedTask;

        foreach (var row in rows)
        {
            foreach (var (matcher, style, sortGroup) in rules)
            {
                if (!matcher.Matches(row.Entry))
                    continue;

                row.Highlight = style;
                if (sortGroup != 0)
                    row.SortGroup = sortGroup;
                break;
            }
        }

        return ValueTask.CompletedTask;
    }

    private void Compile()
    {
        _compiled = _store.Current.Rules
            .Where(r => r.Enabled)
            .Select(r => (
                CompiledMatcher.Compile(r.Match),
                new HighlightStyle(r.Foreground, r.Background, r.Bold, r.Italic),
                r.SortGroup))
            .ToArray();

        RulesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Seeded on first run, every rule switched off. These are examples to edit, not a policy:
    /// the point of the file is that the colours in the grid are yours. Styling is row-level, so
    /// a matched row tints its size and date too, and even the four quiet rules below made a
    /// fresh install look loud before its owner had chosen anything. Written out but off, the
    /// rules people want are one <c>"enabled": true</c> away. highlighting.json hot-reloads.
    /// </summary>
    public static HighlightingFile Defaults() => new()
    {
        Rules =
        [
            new HighlightRule
            {
                Name = "Secrets",
                Match = new MatchRule { Regex = @"(?i)(secret|credential|\.pem$|\.pfx$|id_rsa)" },
                Foreground = "#FF8B82",
                Bold = true,
                Enabled = false,
            },
            new HighlightRule
            {
                Name = "Build output",
                Match = new MatchRule { Glob = "{bin,obj,node_modules,.vs,target}", DirectoriesOnly = true },
                Foreground = "#7E868E",
                Italic = true,
                Enabled = false,
            },
            new HighlightRule
            {
                Name = "Archives",
                Match = new MatchRule { Extensions = ["zip", "7z", "tar", "gz", "rar", "iso"] },
                Foreground = "#C9A470",
                Enabled = false,
            },
            new HighlightRule
            {
                Name = "Very large files",
                Match = new MatchRule { MinSize = "1GB", FilesOnly = true },
                Background = "#332C1D",
                Enabled = false,
            },
            new HighlightRule
            {
                Name = "Sources",
                Match = new MatchRule { Glob = "*.{cs,xaml,fs,cpp,h,py,ts,js}" },
                Foreground = "#7FC8FF",
                Enabled = false,
            },
        ],
    };
}
