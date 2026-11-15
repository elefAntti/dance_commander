using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DanceCommander.App.Services;
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

/// <summary>
/// One file, shown rather than opened (§6.11). Transient: raised with F3 and dismissed with Escape,
/// so it costs no permanent chrome and the pane it covers is one keystroke away.
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    private readonly string _text;

    public PreviewViewModel(string title, PreviewKind kind, string facts, string? message = null)
    {
        Title = title;
        Kind = kind;
        Facts = facts;
        Message = message;
        _text = string.Empty;
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
        var longest = LongestLine(inspection.Text);
        _wrap = longest > 200;

        // Made here because this runs where the file was read, off the UI thread.
        Code = CodeText.Prepare(_text, title, longest);
    }

    public PreviewViewModel(string title, string facts, ImageSource image)
    {
        Title = title;
        Kind = PreviewKind.Image;
        Facts = facts;
        Image = image;
        _text = string.Empty;
    }

    public string Title { get; }

    public PreviewKind Kind { get; }

    /// <summary>Size, encoding, line endings — what the file is, above what it says.</summary>
    public string Facts { get; }

    public string? Message { get; }

    public ImageSource? Image { get; }

    public bool IsText => Kind == PreviewKind.Text;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>The whole text, which Copy takes when nothing is selected.</summary>
    public string PlainText => _text;

    /// <summary>The text as an editor document, with its highlighting; null for anything but text.</summary>
    public CodeText? Code { get; }

    [ObservableProperty]
    private bool _showWhitespace;

    /// <summary>Long lines fold to the width of the pane rather than running off the side.</summary>
    [ObservableProperty]
    private bool _wrap;

    /// <summary>Colours from the file's language. On by default; off shows the text as it is.</summary>
    [ObservableProperty]
    private bool _highlightSyntax = true;

    /// <summary>The file's language is one the preview knows, so there are colours to turn off.</summary>
    public bool CanHighlight => Code?.Highlighting is not null;

    /// <summary>The find box is open under the facts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchSummary))]
    private bool _isFinding;

    /// <summary>Kept when the box closes, so Ctrl+F again offers the last search.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchSummary))]
    private string _findText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchSummary))]
    private IReadOnlyList<TextMatch> _matches = [];

    /// <summary>Index into <see cref="Matches"/> of the one selected, or -1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchSummary))]
    private int _currentMatch = -1;

    /// <summary>Where the search looks from until there is a match: the caret when find opened.</summary>
    private int _findFrom;

    /// <summary>"3 of 41", or that there are none.</summary>
    public string MatchSummary =>
        !IsFinding || FindText.Length == 0 ? string.Empty
        : Matches.Count == 0 ? "No matches"
        : $"{CurrentMatch + 1:N0} of {Matches.Count:N0}" + (Matches.Count >= TextFind.MostMatches ? "+" : "");

    public void StartFind(int from)
    {
        _findFrom = from;
        CurrentMatch = -1;
        IsFinding = true;
        Search();
    }

    public void StopFind()
    {
        IsFinding = false;
        Matches = [];
        CurrentMatch = -1;
    }

    public void NextMatch()
    {
        if (Matches.Count > 0)
            CurrentMatch = (CurrentMatch + 1) % Matches.Count;
    }

    public void PreviousMatch()
    {
        if (Matches.Count > 0)
            CurrentMatch = (CurrentMatch - 1 + Matches.Count) % Matches.Count;
    }

    partial void OnFindTextChanged(string value)
    {
        if (IsFinding)
            Search();
    }

    private void Search()
    {
        // Typing on stays where it is when the longer text still matches there, as in an editor's
        // incremental search, rather than jumping back to where find was opened.
        var from = CurrentMatch >= 0 ? Matches[CurrentMatch].Start : _findFrom;
        var matches = TextFind.All(_text, FindText);
        CurrentMatch = -1;
        Matches = matches;
        CurrentMatch = TextFind.FirstFrom(matches, from);
    }

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
}
