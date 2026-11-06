using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Decorations;

/// <summary>
/// How a row should be drawn. Colours are strings because Core has no idea what a brush is; the
/// view parses and caches them.
/// </summary>
public sealed record HighlightStyle(
    string? Foreground = null,
    string? Background = null,
    bool Bold = false,
    bool Italic = false);

/// <summary>
/// A one-character flag on a row, with the colour it should be drawn in.
/// </summary>
/// <remarks>
/// A letter and a colour is how every tool that reports git status reports it, and copying that
/// is the point: nobody has to learn what this app means by "M". It is deliberately not the
/// row's <see cref="HighlightStyle"/>, because the user's own highlighting rules own the colour
/// of a row and a status that quietly overrode them would be the app disagreeing with its
/// configuration.
/// </remarks>
public sealed record RowMarker(string Glyph, string Colour, string Meaning);

/// <summary>
/// The part of a row a decorator is allowed to touch. Everything else about the row — its entry,
/// its selection, its icon — is none of a decorator's business.
/// </summary>
public interface IRowDecorationSink
{
    FsEntry Entry { get; }

    HighlightStyle? Highlight { get; set; }

    /// <summary>Short trailing label, e.g. a git branch on a repository folder.</summary>
    string? Badge { get; set; }

    /// <summary>One-character status flag, e.g. git's M, ? or !.</summary>
    RowMarker? Marker { get; set; }

    /// <summary>Higher sorts nearer the top, above the usual ordering. Zero is normal.</summary>
    int SortGroup { get; set; }
}

/// <summary>
/// Adds information to rows after a listing is materialised (§6.5). Git branches and highlight
/// styles are both decorators, which is why the grid knows about neither: adding "show image
/// dimensions" later means adding one of these, not touching the view.
/// </summary>
/// <remarks>
/// Called on the UI thread so that assigning to a row is safe. Implementations that need to touch
/// the disk or a network should offload that work and assign only once it is back.
/// </remarks>
public interface IRowDecorator
{
    ValueTask DecorateAsync(IReadOnlyList<IRowDecorationSink> rows, FsUri directory, CancellationToken ct);
}
