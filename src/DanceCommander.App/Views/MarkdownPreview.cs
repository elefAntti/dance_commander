using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using DanceCommander.App.Services;

namespace DanceCommander.App.Views;

/// <summary>
/// A Markdown file as it reads, beside the editor that shows it as it is written. Made the first
/// time a Markdown file is previewed, like TextPreview, and reused after that.
/// </summary>
internal sealed class MarkdownPreview
{
    private readonly FlowDocumentScrollViewer _viewer;
    private MarkdownText? _shown;

    public MarkdownPreview(Action copy)
    {
        _viewer = new FlowDocumentScrollViewer
        {
            IsToolBarVisible = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FocusVisualStyle = null,
            Focusable = true,
        };
        _viewer.SetResourceReference(FlowDocumentScrollViewer.SelectionBrushProperty, "AccentBrush");

        // Copying goes through the preview's own Copy, as in the editor: a held clipboard is
        // reported rather than thrown, and nothing selected means the whole file.
        CommandManager.AddPreviewExecutedHandler(_viewer, (_, e) =>
        {
            if (e.Command != ApplicationCommands.Copy)
                return;
            e.Handled = true;
            copy();
        });
    }

    public UIElement View => _viewer;

    public bool IsKeyboardFocusWithin => _viewer.IsKeyboardFocusWithin;

    public string SelectedText => _viewer.Selection is { IsEmpty: false } selection ? selection.Text : string.Empty;

    public void Focus() => _viewer.Focus();

    public void Show(MarkdownText markdown)
    {
        if (ReferenceEquals(_shown, markdown))
            return;

        _shown = markdown;
        _viewer.Document = MarkdownFlow.Build(markdown);
    }

    public void Clear()
    {
        _shown = null;
        _viewer.Document = null;
    }
}
