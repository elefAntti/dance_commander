using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using System.Windows.Shapes;
using DanceCommander.App.Services;
using DanceCommander.App.ViewModels;
using DanceCommander.Core.Preview;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace DanceCommander.App.Views;

/// <summary>
/// The text of a preview, in a read-only editor: highlighted, selectable by character, and
/// virtualised by line however long the file is.
/// </summary>
/// <remarks>
/// Made the first time a text file is previewed rather than declared in XAML, so launching the
/// app never loads the editor (§1.5). One per pane, reused for every preview after that.
/// </remarks>
internal sealed class TextPreview
{
    private readonly TextEditor _editor;
    private readonly MatchMarks _marks = new();
    private ThemedColorizer? _colorizer;

    public TextPreview(Action copy)
    {
        _editor = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FocusVisualStyle = null,
        };

        // A preview shows a file; it is not a place to act on its text. Links stay text, since a
        // click there would run whatever the file says, and dragging a selection is off so a
        // stray drag can't drop it into something else. Alt+drag still selects a rectangle, for
        // taking a column out of a log or a table.
        var options = _editor.Options;
        options.EnableHyperlinks = false;
        options.EnableEmailHyperlinks = false;
        options.EnableTextDragDrop = false;
        options.EnableRectangularSelection = true;
        options.IndentationSize = 4;
        options.CutCopyWholeLine = false;

        _editor.SetResourceReference(Control.FontFamilyProperty, "MonoFontFamily");
        _editor.SetResourceReference(Control.FontSizeProperty, "FontSizeSmall");
        _editor.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        _editor.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "TextMutedBrush");

        var area = _editor.TextArea;
        area.FocusVisualStyle = null;
        area.SelectionBorder = null;
        area.SelectionCornerRadius = 0;
        area.SelectionForeground = null; // the highlighting stays readable on the selection
        area.SetResourceReference(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionBrushProperty, "SelectionActiveBrush");
        area.TextView.SetResourceReference(TextView.NonPrintableCharacterBrushProperty, "TextMutedBrush");

        // Space instead of the dotted rule the editor draws after the line numbers (§1.3).
        for (var i = 0; i < area.LeftMargins.Count; i++)
        {
            if (area.LeftMargins[i] is Line)
                area.LeftMargins[i] = new Border { Width = 10 };
        }

        area.TextView.ElementGenerators.Add(new LongLineCut());
        area.TextView.BackgroundRenderers.Add(_marks);

        // Every way of copying goes through the preview's own Copy: the clipboard another program
        // holds is reported rather than thrown, and nothing selected means the whole file.
        CommandManager.AddPreviewExecutedHandler(_editor, (_, e) =>
        {
            if (e.Command != ApplicationCommands.Copy)
                return;
            e.Handled = true;
            copy();
        });
    }

    public UIElement View => _editor;

    public bool IsKeyboardFocusWithin => _editor.IsKeyboardFocusWithin;

    public string SelectedText => _editor.SelectionLength > 0 ? _editor.SelectedText : string.Empty;

    public void Focus() => _editor.TextArea.Focus();

    /// <summary>
    /// Where find starts looking: the selection's start, so Ctrl+F again stays on the match it
    /// left selected, or else the caret, as in an editor.
    /// </summary>
    public int FindOrigin => _editor.Document is null ? 0
        : _editor.SelectionLength > 0 ? _editor.SelectionStart
        : _editor.CaretOffset;

    /// <summary>Outlines every match on screen and selects the current one, scrolled into view.</summary>
    public void ShowMatches(IReadOnlyList<TextMatch> matches, int current)
    {
        _marks.Matches = matches;
        _editor.TextArea.TextView.InvalidateLayer(_marks.Layer);

        if (current < 0 || current >= matches.Count || _editor.Document is not { } document)
            return;

        var match = matches[current];
        _editor.Select(match.Start, match.Length);
        var location = document.GetLocation(match.Start);
        _editor.ScrollTo(location.Line, location.Column);
    }

    public void Show(PreviewViewModel preview)
    {
        Clear();
        _editor.Document = preview.Code?.TakeDocument();
        _editor.TextArea.Caret.CaretBrush = (Brush?)_editor.TryFindResource("TextPrimaryBrush");
        _editor.ScrollToHome();

        // Made whether or not it is shown, so turning colours back on doesn't parse the file again.
        if (preview.Code?.Highlighting is { } highlighting)
            _colorizer = new ThemedColorizer(highlighting);

        Apply(preview);
    }

    /// <summary>The view options, read again when the user changes one.</summary>
    public void Apply(PreviewViewModel preview)
    {
        _editor.WordWrap = preview.Wrap;
        _editor.Options.ShowTabs = preview.ShowWhitespace;
        _editor.Options.ShowSpaces = preview.ShowWhitespace;

        if (_colorizer is null)
            return;

        var transformers = _editor.TextArea.TextView.LineTransformers;
        var shown = transformers.Contains(_colorizer);
        if (preview.HighlightSyntax && !shown)
            transformers.Insert(0, _colorizer);
        else if (!preview.HighlightSyntax && shown)
            transformers.Remove(_colorizer);
        else
            return;

        _editor.TextArea.TextView.Redraw();
    }

    /// <summary>Lets go of the file, so a closed preview doesn't keep a megabyte alive.</summary>
    public void Clear()
    {
        _marks.Matches = [];

        if (_colorizer is not null)
        {
            _editor.TextArea.TextView.LineTransformers.Remove(_colorizer);
            _colorizer = null;
        }

        _editor.Document = null;
    }

    /// <summary>
    /// A thin accent outline round each match in view. The current one is also the selection,
    /// so it reads as chosen; the rest stay legible in their own colours, which a fill would cover.
    /// </summary>
    private sealed class MatchMarks : IBackgroundRenderer
    {
        public IReadOnlyList<TextMatch> Matches { get; set; } = [];

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Matches.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0
                || textView.TryFindResource("AccentBrush") is not Brush accent)
                return;

            var first = textView.VisualLines[0].FirstDocumentLine.Offset;
            var last = textView.VisualLines[^1].LastDocumentLine.EndOffset;
            var pen = new Pen(accent, 1);
            pen.Freeze();

            // Only the ones on screen: the list is sorted, so start at the first in view. With none
            // at or after it, FirstFrom wraps round to one before, which ends the loop at once.
            for (var i = Math.Max(0, TextFind.FirstFrom(Matches, first)); i < Matches.Count; i++)
            {
                var match = Matches[i];
                if (match.Start > last || match.Start < first)
                    break;

                var segment = new TextSegment { StartOffset = match.Start, Length = match.Length };
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                    drawingContext.DrawRoundedRectangle(null, pen, rect, 2, 2);
            }
        }
    }

    /// <summary>
    /// Ends a line too long to lay out with a note of how much is left.
    /// </summary>
    /// <remarks>
    /// A line is laid out whole when it comes into view, wrapped or not, so the 600 KB line of a
    /// minified JSON file took five seconds to show. Ten thousand characters are more than anyone
    /// reads in a preview; the rest stays in the document, so selecting across the note and Copy
    /// still take every character.
    /// </remarks>
    private sealed class LongLineCut : VisualLineElementGenerator
    {
        private const int Shown = 10_000;

        public override int GetFirstInterestedOffset(int startOffset)
        {
            var line = CurrentContext.Document.GetLineByOffset(startOffset);
            if (line.Length <= Shown)
                return -1;

            var cut = line.Offset + Shown;

            // Not between the two halves of a surrogate pair, which would draw neither.
            if (char.IsLowSurrogate(CurrentContext.Document.GetCharAt(cut)))
                cut--;

            return startOffset <= cut ? cut : -1;
        }

        public override VisualLineElement ConstructElement(int offset)
        {
            var rest = CurrentContext.Document.GetLineByOffset(offset).EndOffset - offset;
            var properties = new VisualLineElementTextRunProperties(CurrentContext.GlobalTextRunProperties);
            if (CurrentContext.TextView.TryFindResource("TextMutedBrush") is Brush muted)
                properties.SetForegroundBrush(muted);

            var note = FormattedTextElement.PrepareText(
                TextFormatter.Create(TextOptions.GetTextFormattingMode(CurrentContext.TextView)),
                $" … {rest:N0} more characters on this line",
                properties);
            return new FormattedTextElement(note, rest);
        }
    }

    /// <summary>
    /// The language's highlighting with the palette's colours in place of its own.
    /// </summary>
    /// <remarks>
    /// The definitions carry colours chosen for a white background, unreadable on the dark
    /// palette and outside the theme on either (§1.3). Their names say what each span is, so
    /// each is drawn in the theme's colour for that kind (<see cref="SyntaxColours"/>), looked up
    /// from the view so light, dark and high contrast all apply. Weight, slant and backgrounds
    /// are left out with the colours: a preview is read, not edited.
    /// </remarks>
    private sealed class ThemedColorizer(IHighlightingDefinition definition) : HighlightingColorizer(definition)
    {
        private readonly Dictionary<string, Brush?> _brushes = [];
        private TextView? _view;

        protected override void OnAddToTextView(TextView textView)
        {
            base.OnAddToTextView(textView);
            _view = textView;
        }

        protected override void OnRemoveFromTextView(TextView textView)
        {
            base.OnRemoveFromTextView(textView);
            _view = null;
        }

        protected override void ApplyColorToElement(VisualLineElement element, HighlightingColor color)
        {
            if (color.Name is not { } name)
                return;

            if (!_brushes.TryGetValue(name, out var brush))
            {
                brush = SyntaxColours.KindOf(name) switch
                {
                    SyntaxKind.Comment => Find("TextMutedBrush"),
                    SyntaxKind.Keyword => Find("SyntaxKeywordBrush"),
                    SyntaxKind.String => Find("SyntaxStringBrush"),
                    SyntaxKind.Number => Find("SyntaxNumberBrush"),
                    SyntaxKind.Name => Find("SyntaxNameBrush"),
                    _ => null,
                };
                _brushes[name] = brush;
            }

            if (brush is not null)
                element.TextRunProperties.SetForegroundBrush(brush);
        }

        private Brush? Find(string key) => _view?.TryFindResource(key) as Brush;
    }
}
