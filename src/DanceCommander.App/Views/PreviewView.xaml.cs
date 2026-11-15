using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DanceCommander.App.Services;
using DanceCommander.App.ViewModels;

namespace DanceCommander.App.Views;

/// <summary>
/// The preview overlay: what a file is, above what it says. The pane hosting it owns when it is
/// shown and where the keyboard goes after; this owns the text and what Copy takes from it.
/// </summary>
public partial class PreviewView : UserControl
{
    private TextPreview? _text;
    private PreviewViewModel? _shown;

    public PreviewView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Show(DataContext as PreviewViewModel);
    }

    /// <summary>Close (Esc) was pressed.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The clipboard was held by another program, so nothing was copied.</summary>
    public event EventHandler? CopyFailed;

    /// <summary>True while the keyboard is in the text, which is what decides who owns Ctrl+C.</summary>
    public bool IsTextFocused => _shown is { IsText: true } && _text?.IsKeyboardFocusWithin == true;

    /// <summary>Hands the keyboard to the text, so Page Up, Page Down, Home, End and Ctrl+A reach it.</summary>
    public void FocusText()
    {
        if (_text is not { } text)
            return;

        // The first text preview makes the editor, which can't take the keyboard until it has
        // been laid out: focused straight away, the keys went to the hidden listing under it.
        if (text.View is FrameworkElement { IsLoaded: false } view)
        {
            void OnLoaded(object sender, RoutedEventArgs e)
            {
                view.Loaded -= OnLoaded;
                text.Focus();
            }

            view.Loaded += OnLoaded;
            return;
        }

        text.Focus();
    }

    /// <summary>Opens find from the caret, with the last search selected so typing replaces it.</summary>
    public void FocusFind()
    {
        if (_shown is not { IsText: true } preview || _text is null)
            return;

        preview.StartFind(_text.FindOrigin);

        // After layout: the box is collapsed until the binding has shown it, and a collapsed
        // element can't take the keyboard.
        Dispatcher.BeginInvoke(() =>
        {
            FindBox.Focus();
            FindBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Show(PreviewViewModel? preview)
    {
        if (_shown is not null)
            _shown.PropertyChanged -= OnOptionChanged;
        _shown = preview;

        if (preview is not { IsText: true })
        {
            _text?.Clear();
            return;
        }

        if (_text is null)
        {
            _text = new TextPreview(Copy);
            TextHost.Content = _text.View;
        }

        _text.Show(preview);
        preview.PropertyChanged += OnOptionChanged;
    }

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PreviewViewModel preview)
            return;

        if (e.PropertyName is nameof(PreviewViewModel.Wrap) or nameof(PreviewViewModel.ShowWhitespace)
            or nameof(PreviewViewModel.HighlightSyntax))
            _text?.Apply(preview);
        else if (e.PropertyName is nameof(PreviewViewModel.Matches) or nameof(PreviewViewModel.CurrentMatch))
            _text?.ShowMatches(preview.Matches, preview.CurrentMatch);
    }

    /// <summary>
    /// The find box owns its keys, as every text box in the window does (MainWindow.OnPreviewKeyDown),
    /// so the ones that step through matches or leave are handled here.
    /// </summary>
    private void OnFindKeyDown(object sender, KeyEventArgs e)
    {
        if (_shown is not { } preview)
            return;

        var shift = Keyboard.Modifiers == ModifierKeys.Shift;
        switch (e.Key)
        {
            case Key.Enter or Key.F3 when shift:
                preview.PreviousMatch();
                break;
            case Key.Enter or Key.F3 when Keyboard.Modifiers == ModifierKeys.None:
                preview.NextMatch();
                break;
            case Key.Escape:
                // Back to the text with the match selected, where Ctrl+C takes it.
                preview.StopFind();
                FocusText();
                break;
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                FindBox.SelectAll();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Opens the find row, or closes it when it is open: for the mouse the button is the row's
    /// switch, since its Escape is a key. Closing leaves the current match selected, as Escape does.
    /// </summary>
    private void OnFind(object sender, RoutedEventArgs e)
    {
        if (_shown is { IsFinding: true } preview)
        {
            preview.StopFind();
            FocusText();
            return;
        }

        FocusFind();
    }

    /// <summary>
    /// The view options under the button. Each item is the preview's own setting, ticked when on,
    /// so the menu shows how the text is being drawn as well as changing it.
    /// </summary>
    private void OnViewMenu(object sender, RoutedEventArgs e)
    {
        if (_shown is not { IsText: true } preview)
            return;

        var menu = new ContextMenu
        {
            PlacementTarget = ViewButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            VerticalOffset = 4,
        };
        menu.Items.Add(Option("Wrap long lines", nameof(PreviewViewModel.Wrap)));
        menu.Items.Add(Option("Show tabs and spaces", nameof(PreviewViewModel.ShowWhitespace)));

        // Only for a language the preview knows: for plain text the item would do nothing.
        if (preview.CanHighlight)
            menu.Items.Add(Option("Syntax colours", nameof(PreviewViewModel.HighlightSyntax)));

        // The text keeps the keyboard it had: Page Down after picking an option should scroll.
        menu.Closed += (_, _) => FocusText();
        menu.IsOpen = true;

        MenuItem Option(string header, string property)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, StaysOpenOnClick = true };
            item.SetBinding(MenuItem.IsCheckedProperty, new System.Windows.Data.Binding(property)
            {
                Source = preview,
                Mode = System.Windows.Data.BindingMode.TwoWay,
            });
            return item;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnCopy(object sender, RoutedEventArgs e) => Copy();

    /// <summary>
    /// What was picked out of the file, or all of it when nothing was. Ctrl+C does the same as
    /// the button: taking a few lines out of something you are not going to open is the reason
    /// the preview has one. The window defers Ctrl+C to the preview while the text has the
    /// keyboard (MainWindow.OnPreviewKeyDown).
    /// </summary>
    private void Copy()
    {
        if (_shown is not { IsText: true } preview)
            return;

        var selected = _text?.SelectedText;
        var text = string.IsNullOrEmpty(selected) ? preview.PlainText : selected;

        // Left uncaught, a clipboard another program was holding took the whole window down here
        // (2026-10-20).
        if (!WindowsClipboard.PutText(text))
            CopyFailed?.Invoke(this, EventArgs.Empty);
    }
}
