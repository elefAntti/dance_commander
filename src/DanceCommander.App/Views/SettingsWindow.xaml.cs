using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DanceCommander.App.Input;
using DanceCommander.App.Services;
using DanceCommander.App.ViewModels;

namespace DanceCommander.App.Views;

/// <summary>
/// The settings window (SETTINGS_PLAN.md §2): one of it, not modal, owned by the main window so it
/// stays in front of what it changes. Closing it is all there is to leaving: every choice was
/// written as it was made.
/// </summary>
/// <remarks>
/// The buttons here are a form's, not the application's actions, so they are handlers rather than
/// commands in the registry; a setting's choices also exist as palette entries, and choosing one
/// here runs the same write (<see cref="Commands.SettingsCommands.SetAsync"/>).
/// </remarks>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _model;
    private readonly Action<string> _showFile;

    /// <summary>The key button waiting for a key, so it can say so and be put back on Esc.</summary>
    private Button? _recordingButton;

    /// <param name="showFile">Shows a config file, by name, in the active pane.</param>
    public SettingsWindow(SettingsViewModel model, Action<string> showFile)
    {
        _model = model;
        _showFile = showFile;
        DataContext = model;
        InitializeComponent();
        TitleBar.Apply(this);

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => SearchBox.Focus();
        Closed += (_, _) => _model.Dispose();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (_model.Recording is not null)
        {
            // Every key goes to the recorder, Tab and Esc included, except the modifiers on their
            // own: Ctrl is the start of Ctrl+S, not a key to bind.
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt
                or Key.RightAlt or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
            {
                return;
            }

            e.Handled = true;
            if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                CancelRecording();
                return;
            }

            if (KeyStrokeMapper.From(key, Keyboard.Modifiers) is { } stroke)
            {
                _recordingButton = null;
                _ = _model.RecordAsync(stroke);
            }
            else if (_recordingButton is not null)
            {
                // Said where the eye is, and still waiting: the next key may be one it knows.
                _recordingButton.Content = "Not a key it can tell apart — try another";
            }
            return;
        }

        if (key == Key.Escape)
        {
            e.Handled = true;
            if (_model.Search.Length > 0)
                _model.Search = string.Empty;
            else
                Close();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && key is Key.PageDown or Key.PageUp)
        {
            e.Handled = true;
            _model.StepSection(key == Key.PageDown ? 1 : -1);
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.F)
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }

    private void CancelRecording()
    {
        if (_recordingButton is { DataContext: KeyRowViewModel row })
            _recordingButton.Content = row.Key;
        _recordingButton = null;
        _model.StopRecording();
    }

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        // The model checking the choice in force raises this too; only a new choice is written.
        if (sender is RadioButton { DataContext: ChoiceViewModel { IsChosen: false } choice })
            _ = _model.ChooseAsync(choice.Setting, choice.Value);
    }

    private void OnResetSetting(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SettingRowViewModel row })
            _ = _model.ResetAsync(row);
    }

    private void OnRecordKey(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: KeyRowViewModel row } button)
            return;

        CancelRecording();
        _recordingButton = button;
        button.Content = "Press a key…";
        _model.StartRecording(row);
    }

    private void OnRemoveKey(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: KeyRowViewModel row })
            _ = _model.RemoveKeyAsync(row);
    }

    private void OnResetKey(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: KeyRowViewModel row })
            _ = _model.ResetKeyAsync(row);
    }

    private void OnKeyFilter(object sender, RoutedEventArgs e)
    {
        // Raised once while the window is still being built, for the filter checked from the start.
        if (_model is not null && sender is FrameworkElement { Tag: string tag } && Enum.TryParse<KeyFilter>(tag, out var filter))
            _model.KeyFilter = filter;
    }

    /// <summary>Opens at a page, when one was asked for: Settings… on a notice about tools.</summary>
    public void ShowSection(string? section)
    {
        if (section is not null && _model.Sections.Contains(section))
        {
            _model.Search = string.Empty;
            _model.Section = section;
        }
    }

    private void OnShowFile(object sender, RoutedEventArgs e) => _showFile("settings.json");

    private void OnShowToolsFile(object sender, RoutedEventArgs e) => _showFile("tools.json");
}
