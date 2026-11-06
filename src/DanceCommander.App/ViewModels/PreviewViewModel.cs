using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DanceCommander.Core.Preview;

namespace DanceCommander.App.ViewModels;

public enum PreviewKind
{
    Text,
    Image,

    /// <summary>Readable, but not as text or a picture. Says what it is instead of showing it.</summary>
    Unsupported,

    /// <summary>Could not be read at all.</summary>
    Failed,
}

public enum PreviewSegmentKind
{
    Content,

    /// <summary>A rendered stand-in for whitespace, drawn faintly so it can be ignored.</summary>
    Whitespace,
}

public sealed record PreviewSegment(string Text, PreviewSegmentKind Kind);

/// <param name="Text">
/// The line as it is in the file, kept alongside the segments so that copying a selection yields
/// what the file says rather than what the preview drew — tabs as tabs, not as arrows.
/// </param>
public sealed record PreviewLine(int Number, IReadOnlyList<PreviewSegment> Segments, string Text);

/// <summary>
/// One file, shown rather than opened (§6.11). Transient: raised with F3 and dismissed with Escape,
/// so it costs no permanent chrome and the pane it covers is one keystroke away.
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    /// <summary>Where a tab stop falls, counted in characters, as most editors default.</summary>
    private const int TabWidth = 4;

    private readonly string _text;

    public PreviewViewModel(string title, PreviewKind kind, string facts, string? message = null)
    {
        Title = title;
        Kind = kind;
        Facts = facts;
        Message = message;
        _text = string.Empty;
        _lines = [];
    }

    public PreviewViewModel(string title, string facts, TextInspection inspection)
    {
        Title = title;
        Kind = PreviewKind.Text;
        Facts = facts;
        _text = inspection.Text;

        // Tabs are marked from the start when the file contains any. In a Makefile or a Python
        // script the difference between a tab and four spaces is the difference between working
        // and not, and a preview that renders them identically is hiding the thing you opened it
        // to see. Files without tabs are unaffected, so prose never picks up speckles.
        _showWhitespace = inspection.HasTabs;

        // Wrapping is on for a file whose lines are paragraphs and off for one whose lines are
        // statements, and the length of the longest line is the only evidence available. Prose
        // written one-line-per-paragraph runs to hundreds of characters; source code that does is
        // already unreadable and its author will have turned this off anyway.
        _wrap = LongestLine(inspection.Text) > 200;
        _lines = Build(_text, _showWhitespace);
    }

    public PreviewViewModel(string title, string facts, ImageSource image)
    {
        Title = title;
        Kind = PreviewKind.Image;
        Facts = facts;
        Image = image;
        _text = string.Empty;
        _lines = [];
    }

    public string Title { get; }

    public PreviewKind Kind { get; }

    /// <summary>Size, encoding, line endings — what the file is, above what it says.</summary>
    public string Facts { get; }

    public string? Message { get; }

    public ImageSource? Image { get; }

    public bool IsText => Kind == PreviewKind.Text;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>The text as it would be copied: without the whitespace stand-ins.</summary>
    public string PlainText => _text;

    /// <summary>
    /// What the Copy button takes: the lines the user picked out, or all of them if none.
    /// </summary>
    /// <remarks>
    /// Copying the whole file was the first reading of "copy" and the wrong one — the reason to
    /// open a preview is usually to take a few lines out of something you are not going to open
    /// properly. Whole-file copy survives as the no-selection case, where it is the only sensible
    /// answer.
    /// </remarks>
    public string TextOf(IEnumerable<PreviewLine> selected)
    {
        var lines = selected.Select(l => l.Text).ToList();
        return lines.Count == 0 ? _text : string.Join(Environment.NewLine, lines);
    }

    [ObservableProperty]
    private IReadOnlyList<PreviewLine> _lines;

    [ObservableProperty]
    private bool _showWhitespace;

    /// <summary>Long lines fold to the width of the pane rather than running off the side.</summary>
    [ObservableProperty]
    private bool _wrap;

    partial void OnShowWhitespaceChanged(bool value) => Lines = Build(_text, value);

    private static int LongestLine(string text)
    {
        var longest = 0;
        var start = 0;

        for (var i = 0; i <= text.Length; i++)
        {
            if (i != text.Length && text[i] is not ('\n' or '\r'))
                continue;

            longest = Math.Max(longest, i - start);
            start = i + 1;
        }

        return longest;
    }

    private static IReadOnlyList<PreviewLine> Build(string text, bool showWhitespace)
    {
        if (text.Length == 0)
            return [];

        var lines = new List<PreviewLine>();
        var number = 1;

        foreach (var raw in text.Split('\n'))
        {
            // Split on '\n' leaves the '\r' of a CRLF file at the end of every line; it is an
            // ending, not content, and drawing it would put a box at the end of every line.
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            lines.Add(new PreviewLine(number++, Segment(line, showWhitespace), line));
        }

        // A trailing newline produces a final empty element; a file ending properly is not an
        // extra blank line anyone wants numbered.
        if (lines.Count > 1 && lines[^1].Segments.Count == 0)
            lines.RemoveAt(lines.Count - 1);

        return lines;
    }

    private static IReadOnlyList<PreviewSegment> Segment(string line, bool showWhitespace)
    {
        if (line.Length == 0)
            return [];

        if (!showWhitespace)
            return [new PreviewSegment(line.Replace("\t", new string(' ', TabWidth)), PreviewSegmentKind.Content)];

        var segments = new List<PreviewSegment>();
        var buffer = new System.Text.StringBuilder();
        var bufferKind = PreviewSegmentKind.Content;
        var column = 0;

        void Flush()
        {
            if (buffer.Length == 0)
                return;
            segments.Add(new PreviewSegment(buffer.ToString(), bufferKind));
            buffer.Clear();
        }

        void Append(string text, PreviewSegmentKind kind)
        {
            if (kind != bufferKind)
            {
                Flush();
                bufferKind = kind;
            }

            buffer.Append(text);
        }

        foreach (var character in line)
        {
            if (character == '\t')
            {
                // Padded to the next tab stop so the text still lines up the way an editor shows
                // it: an arrow alone would shift every column after it.
                var width = TabWidth - (column % TabWidth);
                Append("→" + new string(' ', width - 1), PreviewSegmentKind.Whitespace);
                column += width;
                continue;
            }

            // Every space, not just trailing ones: a long run of them in the middle of a line
            // wraps like text and looks like a rendering fault until it can be seen. The zero-width
            // space after each dot keeps the break opportunity the space gave, or Wrap would cut
            // words in half. Copy reads the raw line, so it never sees either.
            if (character == ' ')
                Append("·\u200B", PreviewSegmentKind.Whitespace);
            else
                Append(character.ToString(), PreviewSegmentKind.Content);
            column++;
        }

        Flush();
        return segments;
    }
}
