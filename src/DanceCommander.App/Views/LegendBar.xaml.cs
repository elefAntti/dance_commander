using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DanceCommander.App.ViewModels;
using DanceCommander.Core.Commands;

namespace DanceCommander.App.Views;

public partial class LegendBar : UserControl
{
    /// <summary>
    /// Long enough to read the first line before it goes; a quick Ctrl+T never sees the second.
    /// </summary>
    private static readonly TimeSpan FirstTurn = TimeSpan.FromMilliseconds(5900);

    /// <summary>Each later line stays longer: by then you are reading every entry, not glancing.</summary>
    private static readonly TimeSpan NextTurn = TimeSpan.FromMilliseconds(7800);

    private static readonly Duration Slide = new(TimeSpan.FromMilliseconds(320));

    /// <summary>Let go and pressed again within this, the same modifier turns to the next line.</summary>
    private static readonly TimeSpan TapAgain = TimeSpan.FromMilliseconds(1000);

    private readonly DispatcherTimer _turn;
    private LegendPagePanel? _panel;
    private ShellViewModel? _shell;
    private int _page;

    /// <summary>What was held before, to tell a release and a re-press apart from a new key.</summary>
    private KeyModifiers _held;

    /// <summary>Another key or a click went with the modifier: it was a shortcut, not a read.</summary>
    private bool _holdUsed;

    /// <summary>The last hold that was only read, let go when and on which line, to go on from.</summary>
    private (KeyModifiers Held, DateTime At, int Page)? _letGo;

    public LegendBar()
    {
        InitializeComponent();
        _turn = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _turn.Tick += (_, _) =>
        {
            _turn.Interval = NextTurn;
            ShowPage(_page + 1, animate: true);
        };
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => InputManager.Current.PostNotifyInput += OnAnyInput;
        Unloaded += (_, _) => InputManager.Current.PostNotifyInput -= OnAnyInput;
    }

    /// <summary>
    /// A key other than a modifier, or a click, while a modifier is down makes that hold a
    /// shortcut. Ctrl+C then Ctrl+V within a second must not come back on the second line.
    /// </summary>
    private void OnAnyInput(object sender, NotifyInputEventArgs e)
    {
        // The preview events, which every key and press passes through even when a handler takes it.
        var used = e.StagingItem.Input switch
        {
            KeyEventArgs key when key.RoutedEvent == Keyboard.PreviewKeyDownEvent =>
                !IsModifier(key.Key == Key.System ? key.SystemKey : key.Key),
            MouseButtonEventArgs press => press.RoutedEvent == Mouse.PreviewMouseDownEvent,
            _ => false,
        };
        if (used)
        {
            _holdUsed = true;
            _letGo = null;
        }
    }

    private static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift
        or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    /// <summary>
    /// The wheel over the row turns it either way, for a hand on the mouse. Turning by hand stops
    /// the timer for this hold: it would otherwise take the line away while it is being read.
    /// </summary>
    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (PageCount <= 1)
            return;

        _turn.Stop();
        ShowPage(_page + (e.Delta < 0 ? 1 : PageCount - 1), animate: true);
        e.Handled = true;
    }

    /// <summary>Clicking a legend entry runs it, so the row is also a usable toolbar.</summary>
    public event EventHandler<string>? CommandInvoked;

    private void OnSlotClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string commandId })
            CommandInvoked?.Invoke(this, commandId);
    }

    private void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        if (_panel is not null)
            _panel.PagesChanged -= OnPagesChanged;
        _panel = (LegendPagePanel)sender;
        _panel.PagesChanged += OnPagesChanged;
        OnPagesChanged(_panel, EventArgs.Empty);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_shell is not null)
            _shell.PropertyChanged -= OnShellChanged;
        _shell = DataContext as ShellViewModel;
        if (_shell is not null)
            _shell.PropertyChanged += OnShellChanged;
    }

    /// <summary>
    /// A new key down, or the key let go: start again from the first line, at once. Except that
    /// the same modifier let go after only reading and pressed again straight away goes on to the
    /// next line: paging in the hand, where the timer was sometimes too fast to read a line and
    /// sometimes too slow to reach one (friction log, 2026-10-01).
    /// </summary>
    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.HeldModifiers) || _shell is null)
            return;

        var was = _held;
        _held = _shell.HeldModifiers;
        if (_held == was)
            return;

        if (_held == KeyModifiers.None)
            _letGo = _holdUsed ? null : (was, DateTime.UtcNow, _page);
        if (was == KeyModifiers.None)
            _holdUsed = false;

        ModifierLabel.Visibility = IsHeld ? Visibility.Visible : Visibility.Collapsed;

        if (_letGo is { } last && last.Held == _held && DateTime.UtcNow - last.At <= TapAgain)
        {
            _letGo = null;
            _turn.Stop();

            // The layer was only just put back, so its lines are not counted until a layout.
            _panel?.UpdateLayout();
            ShowPage(last.Page, animate: false);
            ShowPage(last.Page + 1, animate: true);
            return;
        }

        ShowPage(0, animate: false);
        RestartTurning();
    }

    private void OnPagesChanged(object? sender, EventArgs e)
    {
        var count = PageCount;
        PageDots.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_page >= count)
            ShowPage(0, animate: false);
        else
            DrawDots();
        RestartTurning();
    }

    /// <summary>A click turns it, and like any turn by hand stops the timer for this hold.</summary>
    private void OnDotsClicked(object sender, RoutedEventArgs e)
    {
        _turn.Stop();
        ShowPage(_page + 1, animate: true);
    }

    /// <summary>Only while a modifier is held: at rest the row is the F-keys, and still.</summary>
    private void RestartTurning()
    {
        _turn.Stop();
        if (IsHeld && PageCount > 1)
        {
            _turn.Interval = FirstTurn;
            _turn.Start();
        }
    }

    /// <summary>
    /// Slides to a line: the one on show moves up and out as the next comes up from below. Back to
    /// the first after the last, the other way.
    /// </summary>
    private void ShowPage(int page, bool animate)
    {
        _page = PageCount > 0 ? page % PageCount : 0;
        DrawDots();
        if (_panel is null)
            return;

        if (!animate)
        {
            _panel.BeginAnimation(LegendPagePanel.PageProperty, null);
            _panel.Page = _page;
            return;
        }

        _panel.BeginAnimation(LegendPagePanel.PageProperty, new DoubleAnimation(_page, Slide)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private void DrawDots()
    {
        var count = PageCount;
        if (Dots.Children.Count != count)
        {
            Dots.Children.Clear();
            for (var i = 0; i < count; i++)
                Dots.Children.Add(new Ellipse { Width = 4, Height = 4, Margin = new Thickness(2, 0, 2, 0) });
        }

        for (var i = 0; i < count; i++)
        {
            ((Ellipse)Dots.Children[i]).SetResourceReference(
                Shape.FillProperty, i == _page ? "TextSecondaryBrush" : "TextMutedBrush");
            Dots.Children[i].Opacity = i == _page ? 1 : 0.6;
        }
    }

    private int PageCount => _panel?.PageCount ?? 1;

    private bool IsHeld => _shell is { HeldModifiers: not KeyModifiers.None };
}
