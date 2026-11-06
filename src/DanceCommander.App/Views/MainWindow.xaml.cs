using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using DanceCommander.App.Input;
using DanceCommander.App.Services;
using DanceCommander.App.ViewModels;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Navigation;
using DanceCommander.Core.Startup;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Transfer;

namespace DanceCommander.App.Views;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;

    private readonly Func<ShelfWindow> _makeShelf;
    private ShelfWindow? _shelf;
    private GlobalHotKey? _shelfKey;

    private readonly Func<SettingsViewModel> _makeSettings;
    private SettingsWindow? _settings;

    public MainWindow(
        ShellViewModel shell, IHistoryService history, FolderCompletion folders, JobNotifier notifier,
        Func<ShelfWindow> makeShelf, Func<SettingsViewModel> makeSettings)
    {
        _shell = shell;
        _makeShelf = makeShelf;
        _makeSettings = makeSettings;
        InitializeComponent();
        PopupToggle.Attach(
            ActivityPopup, ActivityButton, () =>
            {
                UndoList.ItemsSource = _shell.UndoRows;
                ActivityList.ItemsSource = _shell.Activity;
            });

        TitleBar.Apply(this);

        DataContext = shell;
        LeftPane.DataContext = shell.Left;
        RightPane.DataContext = shell.Right;

        foreach (var pane in new[] { LeftPane, RightPane })
        {
            pane.History = history;
            pane.Folders = folders;
            pane.Bookmarks = shell.Bookmarks;
            pane.PreviewGotKeyboardFocus += OnPaneGotFocus;
            pane.Opened += OnPaneOpened;
            pane.MiddleClicked += OnPaneMiddleClicked;
            pane.BuildingContextMenu += OnBuildingContextMenu;
            pane.CommandSubmitted += OnCommandSubmitted;
            pane.FilesDropped += OnFilesDropped;
            pane.TabDropped += OnTabDropped;
        }

        Legend.CommandInvoked += async (_, commandId) => await _shell.ExecuteAsync(commandId);

        shell.FocusRequested += OnFocusRequested;
        shell.ShelfWindowRequested += (_, _) => ToggleShelfWindow();
        shell.SettingsRequested += (_, section) => ShowSettings(section);

        // A build that finishes while you are in another window says so; one that finishes in
        // front of you has already said so in the strip (§6.17).
        shell.JobAnnounced += (_, job) => notifier.Announce(this, job);
        notifier.Activated += (_, clicked) => Dispatcher.InvokeAsync(async () =>
        {
            BringForward();
            if (clicked.Action == JobNotificationAction.OpenResult)
                await _shell.OpenJobResultAsync(clicked.Job);
        });
        shell.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ShellViewModel.IsDualPane) or nameof(ShellViewModel.ActivePane))
                ApplyPaneLayout();
            if (args.PropertyName is nameof(ShellViewModel.TaskbarBusy) or nameof(ShellViewModel.TaskbarFailed)
                or nameof(ShellViewModel.TaskbarIndeterminate))
                ApplyTaskbarState();
            if (args.PropertyName is nameof(ShellViewModel.ShelfCount))
                PulseShelf();
            if (args.PropertyName is nameof(ShellViewModel.WorkingReason) or nameof(ShellViewModel.UnfinishedReason))
                _sessionGuard?.Update(_shell.WorkingReason, _shell.UnfinishedReason);
        };

        // Windows ending the session with a job running is asked to wait, with the job named.
        // WPF answers that question too, from a window of its own, and on any answer but a cancel
        // closes every window there and then, before the user has chosen anything.
        SourceInitialized += (_, _) =>
        {
            _sessionGuard = SessionGuard.Attach(this, () => _shell.UnfinishedJobs.Count > 0, StopJobsForSessionEnd);
            _sessionGuard?.Update(_shell.WorkingReason, _shell.UnfinishedReason);
        };
        Application.Current.SessionEnding += (_, e) =>
        {
            if (_shell.UnfinishedJobs.Count > 0)
                e.Cancel = true;
        };

        // The shell may already be in one-pane form: --one-pane is applied before the window
        // exists, so the change it made has no listener to reach.
        ApplyPaneLayout();

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        PreviewMouseDown += OnPreviewMouseDown;
        Deactivated += (_, _) =>
        {
            _answers.Left();
            _shell.HeldModifiers = Core.Commands.KeyModifiers.None;
            _typingIn = Keyboard.FocusedElement as System.Windows.Controls.Primitives.TextBoxBase
                ?? (UIElement?)(Keyboard.FocusedElement as PasswordBox);
            _rowOnLeaving = ActivePaneView.KeyboardRow;
            _leftFrom = Keyboard.FocusedElement?.GetType().Name;
            LeftPane.ShowWindowActive(false);
            RightPane.ShowWindowActive(false);
        };
        Activated += OnActivatedAgain;
        InputManager.Current.PostProcessInput += OnAnyInput;
        Loaded += OnLoaded;
        // The next launch opens where this one closed: the window (SavedPlacement), and the
        // panes and tabs in it (session.json).
        Closing += (_, e) =>
        {
            // Saved first, whatever follows: at logoff Windows closes the window regardless of the
            // question below, and the session went unsaved whenever a job was running.
            SavedPlacement.Save(new WindowInteropHelper(this).Handle);
            _shell.SaveSessionNow();

            // Jobs still running are asked about first: closing used to give them two seconds
            // and then kill them, a copy stopped mid-file and nothing recorded of what it did.
            if (!_closeAgreed && _shell.Jobs.Any(j => !j.IsFinished))
            {
                e.Cancel = true;
                if (!_askingToClose)
                {
                    _ = AskToCloseAsync();
                }
                else
                {
                    // Not silence: a close that seems to do nothing is how people end up in Task
                    // Manager, which stops a cancel's clean-up in the middle.
                    Activate();
                    _shell.ActiveTab.Report(new Notice(
                        "Waiting for the jobs before closing.",
                        Detail: "The question about them is in the tab where you first closed; the jobs are in the strip."));
                }
            }
        };
        Closed += (_, _) =>
        {
            _sessionGuard?.Dispose();
            _shelfKey?.Dispose();
            _shelf?.CloseForGood();
        };
    }

    /// <summary>Made the first time it is asked for: most sessions never need one.</summary>
    private void ToggleShelfWindow() => (_shelf ??= _makeShelf()).Toggle();

    /// <summary>
    /// One settings window: Ctrl+, again brings it forward. Owned, so it stays in front of the panes
    /// it changes and closes with them; made afresh each time, as it holds nothing worth keeping.
    /// </summary>
    /// <param name="section">The page to open at, or null for wherever it was.</param>
    private void ShowSettings(string? section)
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(_makeSettings(), ShowConfigFile) { Owner = this };
            _settings.Closed += (_, _) => _settings = null;
            _settings.ShowSection(section);
            _settings.Show();
            return;
        }

        if (_settings.WindowState == WindowState.Minimized)
            _settings.WindowState = WindowState.Normal;
        _settings.ShowSection(section);
        _settings.Activate();
    }

    /// <summary>
    /// A config file (settings.json, tools.json) in the active pane, where F4 edits it: the file
    /// stays the interface. The folder alone when nothing has been chosen yet and there is no file.
    /// </summary>
    private void ShowConfigFile(string name)
    {
        BringForward();
        _ = _shell.ActiveTab.NavigateAsync(FsUri.Local(App.DataDirectory), selectName: name);
    }

    private bool _closeAgreed;
    private bool _askingToClose;

    /// <summary>How long cancelled jobs get to stop before the window asks whether to keep waiting.</summary>
    private static readonly TimeSpan CancelPatience = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Closing with jobs running: asked in the pane, as every question is (§1.2/2). Keep open is
    /// the default, because the window going away is what can't be taken back. Waiting lets them
    /// finish; cancelling lets each one stop as a cancel does, its temporary files gone and what
    /// it did recorded, rather than cut off in the middle of a file.
    /// </summary>
    /// <remarks>
    /// Never a cut-off of its own: a cancel's clean-up (a move's files put back one by one, a
    /// replaced folder swapped back) runs to the end on purpose, and closing under it left files
    /// under staging or set-aside names with nothing recorded. So when it takes long, the window
    /// says which job is still stopping and asks again; only the user closes anyway.
    /// </remarks>
    private async Task AskToCloseAsync()
    {
        _askingToClose = true;
        try
        {
            // In front, or the question is asked in a window nobody can see (closed from the taskbar).
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();

            // Not over a question a job is waiting on: asking replaces it, and a job whose question
            // is taken away takes that as Cancel.
            var pane = _shell.ActiveTab;
            if (pane.Decision is not null)
            {
                // One job said as one: "Jobs are still running" over the only one read as if others
                // were hidden somewhere (seen 2026-10-15, again 2026-11-03).
                var others = _shell.Jobs.Count(j => !j.IsFinished) > 1;
                var waiting = new Notice(
                    others ? "Jobs are still running, and one is waiting for your answer here." : "A job is waiting for your answer here.",
                    NoticeSeverity.Warning,
                    "Answer it, or cancel the job in the strip, then close again.");
                pane.Report(waiting);

                // Said about that question, so it goes when the question does: left up, it told
                // someone who had answered that something was still waiting (2026-10-14). Only
                // this notice: one put up since is about something else.
                void OnAnswered(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if (e.PropertyName != nameof(TabViewModel.Decision) || pane.Decision is not null)
                        return;
                    pane.PropertyChanged -= OnAnswered;
                    if (ReferenceEquals(pane.Notice, waiting))
                        pane.Notice = null;
                }

                pane.PropertyChanged += OnAnswered;
                return;
            }

            var running = _shell.Jobs.Where(j => !j.IsFinished).ToList();
            var answer = await pane.AskAsync(
                new ChoiceRequest(
                    running.Count == 1 ? $"{running[0].Title} is still running." : $"{running.Count:N0} jobs are still running.",
                    [
                        new DecisionOption("keep", "Keep open", IsDefault: true, IsCancel: true),
                        new DecisionOption("wait", "Close when finished"),
                        new DecisionOption("cancel", "Cancel and close"),
                    ],
                    running.Count == 1 ? "Closing now would stop it part way." : "Closing now would stop them part way.",
                    NoticeSeverity.Warning),
                CancellationToken.None);

            switch (answer)
            {
                case "wait":
                    pane.Report(new Notice("Closing when the jobs have finished."));

                    // Every job, including any started meanwhile: closing must not cut off one
                    // the user began while waiting.
                    while (_shell.Jobs.Any(j => !j.IsFinished))
                        await Task.Delay(200);
                    break;

                case "cancel":
                    pane.Report(new Notice("Cancelling the jobs; closing once they have stopped."));
                    foreach (var job in _shell.Jobs.Where(j => !j.IsFinished).ToList())
                        job.Cancel();
                    if (!await WaitForJobsToStopAsync(pane))
                        return;
                    break;

                default:
                    return;
            }

            _closeAgreed = true;
            Close();
        }
        finally
        {
            _askingToClose = false;
        }
    }

    /// <returns>False when the user chose to keep the window open after all.</returns>
    private async Task<bool> WaitForJobsToStopAsync(TabViewModel pane)
    {
        while (true)
        {
            var deadline = DateTime.UtcNow + CancelPatience;
            while (_shell.Jobs.Any(j => !j.IsFinished) && DateTime.UtcNow < deadline)
                await Task.Delay(100);

            var still = _shell.Jobs.Where(j => !j.IsFinished).ToList();
            if (still.Count == 0)
                return true;

            // Not over a question a job asked meanwhile: asking replaces it, which cancels that job.
            while (pane.Decision is not null && _shell.Jobs.Any(j => !j.IsFinished))
                await Task.Delay(200);
            if (!_shell.Jobs.Any(j => !j.IsFinished))
                return true;

            // Taken back once they have stopped: it stayed up asking about jobs long finished, and
            // the window waited for an answer that no longer mattered (2026-10-14).
            using var stopped = new CancellationTokenSource();
            var watching = WithdrawWhenStoppedAsync(stopped);
            var answer = await pane.AskAsync(
                new ChoiceRequest(
                    still.Count == 1 ? $"{still[0].Title} is still stopping." : $"{still.Count:N0} jobs are still stopping.",
                    [
                        new DecisionOption("wait", "Keep waiting", IsDefault: true),
                        new DecisionOption("keep", "Keep open", IsCancel: true),
                        new DecisionOption("close", "Close anyway"),
                    ],
                    "A cancel puts things back before it stops. Closing now could leave files under a temporary name, unrecorded.",
                    NoticeSeverity.Warning),
                stopped.Token);
            await stopped.CancelAsync();
            await watching;
            // Withdrawn means no answer; an answer given as the last job stopped still counts.
            if (answer is null && !_shell.Jobs.Any(j => !j.IsFinished))
                return true;

            switch (answer)
            {
                case "wait":
                    continue;
                case "close":
                    return true;
                default:
                    return false;
            }
        }
    }

    private SessionGuard? _sessionGuard;

    /// <summary>How often the log says which jobs are still stopping while the session waits for them.</summary>
    private static readonly TimeSpan SessionEndReport = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The session is ending and the user chose that over the running jobs, or was not asked (a
    /// forced update restart), or an installer wants this program closed. Each job is cancelled,
    /// as "Cancel and close" does, so it stops the way a cancel stops, its temporary files gone
    /// and what it did recorded, instead of being cut off mid-file when the process goes.
    /// </summary>
    /// <remarks>
    /// Has to be done before returning: once Windows has its answer to WM_ENDSESSION it can end the
    /// process at any moment, and nothing queued for later runs. So the dispatcher is pumped here
    /// while the jobs stop, as a cancel's clean-up may need this thread. The placement and the
    /// session are saved for the same reason; WPF closes no windows at this point.
    ///
    /// No deadline of our own: a cancel's clean-up (a move's files put back one by one) runs to the
    /// end on purpose, and returning early only ends the process under it. Windows has one, and
    /// once the user has chosen to go on, or the end is forced, it can end the process at it
    /// without asking again; nothing here can prevent that. So the log names what is still
    /// stopping every few seconds, and the placement and session are saved before the wait,
    /// as a job that never stops would otherwise keep them from being saved at all.
    /// </remarks>
    private void StopJobsForSessionEnd(bool closeApp)
    {
        SavedPlacement.Save(new WindowInteropHelper(this).Handle);
        _shell.SaveSessionNow();

        var nextReport = DateTime.UtcNow + SessionEndReport;
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(100), System.Windows.Threading.DispatcherPriority.Normal,
            (_, _) =>
            {
                // Each time, for a job added after the first look.
                var running = _shell.UnfinishedJobs;
                foreach (var job in running)
                    job.Cancel();
                if (running.Count == 0)
                {
                    frame.Continue = false;
                }
                else if (DateTime.UtcNow >= nextReport)
                {
                    nextReport = DateTime.UtcNow + SessionEndReport;
                    Serilog.Log.Warning("Still stopping as the session ends: {Jobs}", running.Select(j => j.Title).ToList());
                }
            },
            Dispatcher);

        timer.Start();
        try
        {
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        catch (InvalidOperationException ex)
        {
            // Dispatcher processing suspended: the jobs are cancelled, but cannot be waited for here.
            Serilog.Log.Warning(ex, "Could not wait for the jobs while the session ends");
            foreach (var job in _shell.UnfinishedJobs)
                job.Cancel();
        }
        finally
        {
            timer.Stop();
        }

        // The frame also ends when the dispatcher shuts down (a close resumed inside it), so its
        // end alone does not say the jobs stopped: asked again here.
        var still = _shell.UnfinishedJobs.Select(j => j.Title).ToList();
        if (still.Count > 0)
            Serilog.Log.Warning(
                "The session ended with jobs still stopping{Why}: {Jobs}",
                Dispatcher.HasShutdownStarted ? " (the window was closing)" : string.Empty, still);
        else
            Serilog.Log.Information("Jobs stopped for the end of the session");

        SavedPlacement.Save(new WindowInteropHelper(this).Handle);
        _shell.SaveSessionNow();

        // An installer asking us to close does not end the process itself, and may not end it at
        // all: so close as usual, the log still open for whatever follows. Only with every job
        // stopped, or closing would cut off the clean-up this waited for.
        if (closeApp)
        {
            if (still.Count == 0)
            {
                _closeAgreed = true;
                _ = Dispatcher.BeginInvoke(Close);
            }
            return;
        }

        Serilog.Log.CloseAndFlush();
    }

    /// <summary>Cancels <paramref name="stopped"/> once no job is left running, or returns when it already is.</summary>
    private async Task WithdrawWhenStoppedAsync(CancellationTokenSource stopped)
    {
        while (!stopped.IsCancellationRequested && _shell.Jobs.Any(j => !j.IsFinished))
            await Task.Delay(200);
        if (!stopped.IsCancellationRequested)
            await stopped.CancelAsync();
    }

    /// <summary>
    /// Win+Shift+B, from any program: the shelf is for when this window is not the one in front.
    /// Windows gives a combination to one program, so if another has it the key is said to be
    /// taken, once, and the shelf is still in the palette.
    /// </summary>
    private void RegisterShelfKey()
    {
        const uint VirtualKeyB = 0x42;
        _shelfKey = GlobalHotKey.TryRegister(
            this, GlobalHotKey.ModWin | GlobalHotKey.ModShift, VirtualKeyB, ToggleShelfWindow);
        if (_shelfKey is null)
        {
            Serilog.Log.Warning("Win+Shift+B could not be registered for the shelf: another program holds it");
            _shell.Say("Win+Shift+B is taken by another program — the shelf is in the palette");
        }
    }

    /// <summary>What the command line that started us asked for, applied once the window loads.</summary>
    private LaunchRequest? _launch;

    /// <summary>The first listings, started before the window was built (App.OnStartup).</summary>
    private Task? _initialized;

    public void Launch(LaunchRequest request, Task initialized)
    {
        _launch = request;
        _initialized = initialized;
    }

    /// <summary>
    /// A command line from a later invocation, handed over by the copy that received it (§6.11).
    /// </summary>
    /// <remarks>
    /// The window has to come forward as well as change: the whole reason another program sent this
    /// was to show the user something, and doing it behind three other windows is the same as not
    /// doing it. Restoring a minimised window first, because Activate on a minimised window brings
    /// nothing to look at.
    /// </remarks>
    public async void Reopen(LaunchRequest request)
    {
        if (request.Help)
            return;

        // Nothing to open, only what couldn't be read: said, as a first launch says it. Dropped,
        // a refused address (one carrying a password) did nothing at all, and that looked like a
        // window that hadn't heard.
        if (!request.HasWork)
        {
            if (request.Problems.Count > 0)
            {
                BringForward();
                _shell.Report(request);
            }

            return;
        }

        BringForward();
        await _shell.OpenAsync(request);
        ActivePaneView.FocusGrid();
    }

    private void BringForward()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await (_initialized ?? _shell.InitializeAsync(_launch));

        // No terminal to have printed it to. The output panel is where this application already
        // puts text you asked for and can copy, so the usage goes there rather than into a dialog.
        if (_launch is { Help: true })
            _shell.ActiveTab.ShowOutput("Dance Commander — command line", CommandLine.Usage);

        ActivePaneView.FocusGrid();
        RegisterShelfKey();

        // After the first listings, when nothing is waiting on the UI thread.
        _ = Dispatcher.BeginInvoke(WarmMenus, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        _ = Dispatcher.BeginInvoke(WarmPalette, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        _shell.Palette.PropertyChanged += TimePalette;
    }

    private bool _warmingPalette;
    private int _paletteOpens;

    /// <summary>
    /// Opens the palette once, invisibly, for one frame, so the first real open finds its visual
    /// tree, templates and code paths already built: the same reason menus are warmed. A first
    /// palette of the session lagged (friction log, 2026-10-01), and lag in the palette, of all
    /// things, undoes the feeling it exists for. The log line says what a cold open costs.
    /// </summary>
    private void WarmPalette()
    {
        var palette = _shell.Palette;
        if (palette.IsOpen)
            return;

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        void Restore()
        {
            palette.Close();
            PaletteCard.ClearValue(OpacityProperty);
            PaletteCard.ClearValue(IsHitTestVisibleProperty);
            _warmingPalette = false;
        }

        try
        {
            _warmingPalette = true;
            PaletteCard.Opacity = 0;
            PaletteCard.IsHitTestVisible = false;
            palette.Open();
            // Laid out now, so anything a template throws is caught here (as for the menus).
            PaletteCard.UpdateLayout();

            void OnRendered(object? sender, EventArgs e)
            {
                System.Windows.Media.CompositionTarget.Rendering -= OnRendered;
                _ = Dispatcher.BeginInvoke(() =>
                {
                    Restore();
                    Serilog.Log.Information(
                        "Palette warmed in {Elapsed} ms",
                        (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            System.Windows.Media.CompositionTarget.Rendering += OnRendered;
        }
        catch (Exception ex)
        {
            // Only ever an optimisation: the first real palette still opens, just more slowly.
            Serilog.Log.Warning(ex, "Warming the palette failed");
            Restore();
        }
    }

    /// <summary>
    /// A line for a palette that took long enough to feel (DESIGN.md §1.5), from the key to the first
    /// frame with it on screen: our part (what applies, the rows) and WPF's (templates, layout).
    /// </summary>
    private void TimePalette(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var palette = _shell.Palette;
        if (e.PropertyName != nameof(palette.IsOpen) || !palette.IsOpen || _warmingPalette)
            return;

        var opens = ++_paletteOpens;
        var started = palette.OpenedAt;
        var built = palette.Built;
        var rows = palette.Rows.Count;
        var steps = palette.BuildSteps;

        void OnRendered(object? s, EventArgs args)
        {
            System.Windows.Media.CompositionTarget.Rendering -= OnRendered;
            var total = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (Services.Budgets.Verdict(("palette", total, Services.Budgets.Palette)) is { } verdict)
            {
                // With the build down to a millisecond, the rest is WPF drawing the rows. How many it
                // drew says whether the list is virtualizing: a screenful, or all of them.
                var drawn = TreeWalk.Descendant<VirtualizingStackPanel>(PaletteList)?.Children.Count ?? -1;
                Serilog.Log.Information(
                    "Slow palette: {Total} ms to show, {Built} ms of it building {Rows} rows ({Steps}), {Drawn} rows drawn, open {Opens} this session, {Verdict}",
                    (long)total, (long)built.TotalMilliseconds, rows, steps, drawn, opens, verdict);
            }
        }
        System.Windows.Media.CompositionTarget.Rendering += OnRendered;
    }

    /// <summary>
    /// A click anywhere in the other pane makes it the active one, empty space included: only a
    /// click on a row did, because a row takes the keyboard and the space below the rows does not
    /// (friction log, 2026-10-04). The keyboard follows to its list, unless the click was on
    /// something that takes the keyboard itself (a box, a button), which is left to do so.
    /// </summary>
    private void ActivateClickedPane(MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;
        var view = TreeWalk.Within(LeftPane, source) ? LeftPane : TreeWalk.Within(RightPane, source) ? RightPane : null;
        if (view?.ViewModel is not { IsActive: false } pane)
            return;

        _shell.Activate(pane);
        if (TreeWalk.Ancestor<System.Windows.Controls.Primitives.TextBoxBase>(source) is null &&
            TreeWalk.Ancestor<System.Windows.Controls.Primitives.ButtonBase>(source) is null &&
            TreeWalk.Ancestor<ListViewItem>(source) is null)
        {
            _ = Dispatcher.BeginInvoke(() => view.FocusGrid(), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    /// <summary>
    /// The mouse's back and forward buttons do what they do in a browser and in Explorer. In the
    /// pane under the pointer, as they do there when a window has two, not the active one: a thumb
    /// button is aimed with the pointer. That pane becomes the active one, as a click would make it.
    /// </summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            ActivateClickedPane(e);
            return;
        }

        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2))
            return;

        e.Handled = true;
        if (e.OriginalSource is DependencyObject source)
        {
            if (TreeWalk.Within(LeftPane, source) && LeftPane.ViewModel is { } left)
                _shell.Activate(left);
            else if (TreeWalk.Within(RightPane, source) && RightPane.ViewModel is { } right)
                _shell.Activate(right);
        }

        var tab = _shell.ActiveTab;
        _ = e.ChangedButton == MouseButton.XButton1
            ? (tab.CanGoBack ? tab.GoBackAsync() : Task.CompletedTask)
            : (tab.CanGoForward ? tab.GoForwardAsync() : Task.CompletedTask);
    }

    /// <summary>
    /// The first menu of a session used to be noticeably slower than every later one: WPF parses
    /// the menu templates, compiles the drop shadow's shader and makes its first transparent popup
    /// window on first use, and our own menu code is compiled on first call. This pays all of that
    /// once, at idle after startup, by building a real menu and showing it for one frame --
    /// invisible, not hit-testable and not focusable, so it can neither be seen nor take the
    /// keyboard from whatever the user is doing.
    /// </summary>
    /// <remarks>
    /// Not a ContextMenu in the popup: WPF refuses one with any parent ("'ContextMenu' cannot have
    /// a logical or visual parent"), and opening a real one takes the keyboard focus. The same
    /// items on the same shadowed surface, in a plain ItemsControl, exercise the same templates,
    /// the same effect and the same kind of popup window. The ContextMenu's own template is a
    /// Border and a StackPanel, so it is applied and measured apart from the popup.
    /// </remarks>
    private void WarmMenus()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Popup? popup = null;
        try
        {
            var context = _shell.CreateContext();
            var items = new ItemsControl();
            Render(items.Items, ContextMenuPlan.Build(_shell.Commands, MenuTarget.Location, context), context);

            // A submenu too, which has a template of its own.
            var folder = new MenuItem { Header = string.Empty };
            folder.Items.Add(new MenuItem { Header = string.Empty });
            items.Items.Add(folder);

            var menu = new ContentControl
            {
                Style = (Style)FindResource("PopupSurfaceStyle"),
                Padding = new Thickness(4),
                Opacity = 0,
                IsHitTestVisible = false,
                Focusable = false,
                Content = items,
            };

            var standalone = new ContextMenu();
            standalone.Items.Add(new MenuItem { Header = string.Empty });
            standalone.ApplyTemplate();
            standalone.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var shown = popup = new Popup
            {
                Child = menu,
                Placement = PlacementMode.Absolute,
                AllowsTransparency = true,
                Focusable = false,
                StaysOpen = true,
            };

            void OnRendered(object? sender, EventArgs e)
            {
                System.Windows.Media.CompositionTarget.Rendering -= OnRendered;
                // Closed after that frame has been drawn, not before it.
                _ = Dispatcher.BeginInvoke(() =>
                {
                    shown.IsOpen = false;
                    shown.Child = null;
                    Serilog.Log.Information(
                        "Menus warmed in {Elapsed} ms",
                        (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }, System.Windows.Threading.DispatcherPriority.Background);
            }

            shown.IsOpen = true;
            // Laid out here rather than on the next pass, so that anything the templates throw is
            // caught below instead of reaching the dispatcher, where it would end the process.
            menu.UpdateLayout();
            System.Windows.Media.CompositionTarget.Rendering += OnRendered;
        }
        catch (Exception ex)
        {
            // Only ever an optimisation: the first real menu still works, just more slowly.
            Serilog.Log.Warning(ex, "Warming the menus failed");
            if (popup is not null)
                popup.IsOpen = false;
        }
    }

    /// <summary>
    /// A line in the log for a menu that took long enough to feel -- from the request to the first
    /// frame with it on screen -- split into our part (deciding and building the items) and WPF's
    /// (templates, layout, the popup window). Quiet otherwise.
    /// </summary>
    private static void TimeMenu(ContextMenu menu, long started, long built)
    {
        void OnOpened(object sender, RoutedEventArgs e)
        {
            menu.Opened -= OnOpened;
            System.Windows.Media.CompositionTarget.Rendering += OnRendered;
        }

        void OnRendered(object? sender, EventArgs e)
        {
            System.Windows.Media.CompositionTarget.Rendering -= OnRendered;
            var total = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (Services.Budgets.Verdict(("menu", total, Services.Budgets.Menu)) is { } verdict)
                Serilog.Log.Information(
                    "Slow menu: {Total} ms to show, {Built} ms of it building {Items} items, {Verdict}",
                    (long)total,
                    (long)System.Diagnostics.Stopwatch.GetElapsedTime(started, built).TotalMilliseconds,
                    menu.Items.Count, verdict);
        }

        menu.Opened += OnOpened;
    }

    private int _shelfCount;

    /// <summary>
    /// The shelf's answer to Ctrl+B: its count dims and comes back up, once, where the eye can
    /// catch it without being called to it. What was added is said in the echo beside it.
    /// </summary>
    private void PulseShelf()
    {
        var count = _shell.ShelfCount;
        var grew = count > _shelfCount;
        _shelfCount = count;
        if (!grew)
            return;

        ShelfButton.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 0.35,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(450),
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase(),
        });
    }

    private PaneView ActivePaneView =>
        ReferenceEquals(_shell.ActivePane, _shell.Left) ? LeftPane : RightPane;

    private void ApplyPaneLayout()
    {
        var dual = _shell.IsDualPane;
        var leftActive = ReferenceEquals(_shell.ActivePane, _shell.Left);
        var star = new GridLength(1, GridUnitType.Star);
        var none = new GridLength(0);

        LeftPane.Visibility = dual || leftActive ? Visibility.Visible : Visibility.Collapsed;
        RightPane.Visibility = dual || !leftActive ? Visibility.Visible : Visibility.Collapsed;
        Splitter.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;

        LeftColumn.MinWidth = LeftPane.Visibility == Visibility.Visible ? 220 : 0;
        RightColumn.MinWidth = RightPane.Visibility == Visibility.Visible ? 220 : 0;
        LeftColumn.Width = LeftPane.Visibility == Visibility.Visible ? star : none;
        RightColumn.Width = RightPane.Visibility == Visibility.Visible ? star : none;
    }

    /// <summary>
    /// TaskbarItemInfo.ProgressState is a WPF enum, so the view maps the view model's plain flags
    /// onto it rather than the view model taking a dependency on the shell types.
    /// </summary>
    private void ApplyTaskbarState() =>
        Taskbar.ProgressState = _shell.TaskbarFailed
            ? System.Windows.Shell.TaskbarItemProgressState.Error
            : _shell.TaskbarIndeterminate
                ? System.Windows.Shell.TaskbarItemProgressState.Indeterminate
            : _shell.TaskbarBusy
                ? System.Windows.Shell.TaskbarItemProgressState.Normal
                : System.Windows.Shell.TaskbarItemProgressState.None;

    /// <summary>
    /// The menu is assembled from the registry each time it opens, because what applies depends on
    /// where you clicked — and built-ins and tools from tools.json are indistinguishable here
    /// (§1.2/1). What goes in it is <see cref="ContextMenuPlan"/>'s decision; this only draws it.
    /// </summary>
    private void OnBuildingContextMenu(object? sender, ContextMenuRequest request)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        BuildContextMenu(sender, request);

        // An empty menu is never opened, and a timer left waiting on the grid's reused menu would
        // report the next one with this one's start.
        if (request.Menu.Items.Count > 0)
            TimeMenu(request.Menu, started, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private void BuildContextMenu(object? sender, ContextMenuRequest request)
    {
        if (sender is PaneView { ViewModel: { } pane })
            _shell.Activate(pane);

        ICommandContext context;
        if (request.Target == MenuTarget.Parent)
        {
            // The Up button stands for the parent, which the selection never contains, so its menu
            // runs against a context standing on that folder.
            if (_shell.ActiveTab.Uri?.Up is not { } parent)
                return;
            context = _shell.CreateContext(new FsEntry(parent, parent.Name, IsDirectory: true, 0, null));
        }
        else
        {
            context = _shell.CreateContext();
        }

        // As for keys: a command whose check throws must not end the process with the menu.
        IReadOnlyList<MenuEntry> plan;
        try
        {
            plan = ContextMenuPlan.Build(_shell.Commands, request.Target, context);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not build the {Target} menu", request.Target);
            return;
        }
        Render(request.Menu.Items, plan, context);
    }

    private void Render(ItemCollection items, IReadOnlyList<MenuEntry> entries, ICommandContext context)
    {
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case MenuSeparator:
                    items.Add(new Separator());
                    break;

                case MenuHeader:
                    items.Add(LocationHeader(context));
                    break;

                case MenuFolder folder:
                    var submenu = new MenuItem
                    {
                        Header = Named(context.Directory, $"In {_shell.ActiveTab.Title}", FontWeights.Normal),
                    };
                    Render(submenu.Items, folder.Items, context);
                    items.Add(submenu);
                    break;

                case MenuCommand command:
                    var item = Item(command.Command, context);
                    if (command.IsPrimary)
                        item.FontWeight = FontWeights.SemiBold;
                    items.Add(item);
                    break;
            }
        }
    }

    /// <summary>
    /// Bound to the context the menu was built from, not a fresh one: the ".." menu stands on the
    /// parent, which a context read off the selection at click time would not.
    /// </summary>
    private MenuItem Item(CommandDefinition command, ICommandContext context)
    {
        var item = new MenuItem
        {
            Header = command.Title,
            InputGestureText = command.Binding?.ToString() ?? string.Empty,
        };
        item.Click += async (_, _) => await _shell.ExecuteAsync(command, context);
        return item;
    }

    /// <summary>
    /// Names the place the menu is about: its icon, its name, and its branch — the same chip as
    /// the pane footer. Not clickable. A menu about a place should say which place, and this line is
    /// what tells the folder's menu apart from a row's at a glance.
    /// </summary>
    private MenuItem LocationHeader(ICommandContext context)
    {
        var row = new DockPanel { LastChildFill = true };

        if (_shell.ActiveTab.Branch is { } branch)
        {
            var chip = new Border
            {
                Margin = new Thickness(16, 0, 0, 0),
                Padding = new Thickness(7, 1, 7, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = $"⎇ {branch}",
                    MaxWidth = 200,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Style = (Style)FindResource("SecondaryTextStyle"),
                },
            };
            chip.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            chip.SetResourceReference(Border.CornerRadiusProperty, "RadiusS");
            DockPanel.SetDock(chip, Dock.Right);
            row.Children.Add(chip);
        }

        row.Children.Add(Named(context.Directory, _shell.ActiveTab.Title, FontWeights.SemiBold));

        return new MenuItem
        {
            Header = row,
            // A label, not an option: no highlight, no keyboard stop, nothing happens on click.
            IsHitTestVisible = false,
            Focusable = false,
        };
    }

    /// <summary>A location's icon beside a line of text, the way bookmarks draw a place.</summary>
    private StackPanel Named(Core.Fs.FsUri location, string text, FontWeight weight)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (_shell.IconFor(location) is { } icon)
        {
            panel.Children.Add(new Image
            {
                Source = icon,
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = weight,
            MaxWidth = 320,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return panel;
    }

    private async void OnCommandSubmitted(object? sender, string line)
    {
        if (sender is PaneView { ViewModel: { } pane })
            _shell.Activate(pane);
        await _shell.RunCommandLineAsync(line);
    }

    /// <summary>A tab dropped along its own strip moves along it; onto the other pane, it moves there.</summary>
    private void OnTabDropped(object? sender, TabDropRequest request)
    {
        if (sender is not PaneView { ViewModel: { } pane })
            return;

        if (pane.Tabs.Contains(request.Tab))
            pane.MoveTab(request.Tab, request.Before ?? pane.Tabs.Count);
        else
            _ = _shell.MoveTabToPane(request.Tab, pane, request.Before);
    }

    /// <summary>
    /// A drop becomes an ordinary transfer, which is the point: progress, cancellation, conflict
    /// handling and the activity log all exist already and none of them care how the user asked.
    /// The pane is activated first, so the job reports where the user is looking.
    /// </summary>
    private void OnFilesDropped(object? sender, DropRequest request)
    {
        if (sender is PaneView { ViewModel: { } pane })
            _shell.Activate(pane);

        if (request.Contents is { } contents)
        {
            _ = _shell.WriteDroppedAsync(contents, request.Target);
            return;
        }

        if (request.Text is { } text)
        {
            _ = _shell.WriteDroppedTextAsync(text, request.Target);
            return;
        }

        if (request.Action == DropAction.Collect)
        {
            _ = _shell.CollectAsync(request.Sources, request.Target);
            return;
        }

        var mode = request.Action == DropAction.Move ? TransferMode.Move : TransferMode.Copy;
        _shell.EnqueueTransfer(request.Sources, request.Target, mode);
    }

    private async void OnShelfClick(object sender, RoutedEventArgs e) => await _shell.ShowShelfPageAsync();

    private void OnCopyJobError(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Job { Error: { } error } job })
            return;

        if (WindowsClipboard.PutText($"{job.FailureHeadline}{Environment.NewLine}{error}"))
            _shell.Say("Error copied to the clipboard.");
        else
            _shell.ActiveTab.Report(WindowsClipboard.CouldNotCopy);
    }

    private void OnShowJobOutput(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Job job })
            _shell.ShowJobOutput(job);
    }

    private async void OnOpenJobResult(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Job job })
            await _shell.OpenJobResultAsync(job);
    }

    private void OnCancelJob(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Job job })
            return;

        // A finished job's button dismisses it; a running one cancels it.
        if (job.IsFinished)
            _shell.DismissJob(job);
        else
            job.Cancel();
    }

    private void OnPaneGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is PaneView { ViewModel: { } pane })
            _shell.Activate(pane);
    }

    private async void OnPaneOpened(object? sender, FileRowViewModel row)
    {
        if (sender is PaneView { ViewModel: { } pane })
            _shell.Activate(pane);
        await _shell.ExecuteAsync("dc.open");
    }

    /// <summary>
    /// The pane clicked becomes the active one, as any click makes it, so "the other pane" is the
    /// one across from the click. Its list takes the keyboard too: an active pane typing went
    /// elsewhere would be a pane that only looks active.
    /// </summary>
    private async void OnPaneMiddleClicked(object? sender, FileRowViewModel row)
    {
        if (sender is PaneView { ViewModel: { IsActive: false } pane } view)
        {
            _shell.Activate(pane);
            view.FocusGrid(clicked: true);
        }
        await _shell.MiddleClickAsync(row.Entry);
    }

    /// <summary>The input that had the keyboard when the window lost it, if one did.</summary>
    private UIElement? _typingIn;

    /// <summary>The row in the active pane's list the keyboard was on when the window lost it.</summary>
    private object? _rowOnLeaving;

    /// <summary>What kind of element had the keyboard on leaving, for the log if it is lost.</summary>
    private string? _leftFrom;

    /// <summary>
    /// Coming back to the window, the keyboard goes back to the input it was in: the command line,
    /// a password or other inline prompt, the address bar. It did not always, and with the box
    /// still on screen it was not clear how to get back into it (friction log, 2026-10-01). Only
    /// an input, and only one still showing.
    ///
    /// Anything else WPF puts back as it was, which fails for a row: the list recycles its rows,
    /// so one refreshed while you were away (a file changed, the git marks arrived) no longer
    /// exists or now shows another file, and the keyboard came back to nowhere (2026-10-09). Then
    /// it goes to the list's cursor row, as after any other command that loses it.
    /// </summary>
    private void OnActivatedAgain(object? sender, EventArgs e)
    {
        LeftPane.ShowWindowActive(true);
        RightPane.ShowWindowActive(true);

        var row = _rowOnLeaving;
        var leftFrom = _leftFrom;
        _rowOnLeaving = null;
        _leftFrom = null;

        if (_typingIn is { } input)
        {
            _typingIn = null;
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (input.IsVisible && input.IsEnabled && !input.IsKeyboardFocusWithin)
                    input.Focus();
            }, System.Windows.Threading.DispatcherPriority.Input);
            return;
        }

        // Once things settle, so a list rebuilt meanwhile has its rows. Only if the keyboard is
        // nowhere, or in the list but off the row it left: a click that brought the window back
        // put it where the user wanted it, and that is left alone.
        _ = Dispatcher.BeginInvoke(() =>
        {
            var lost = KeyboardIsLost();
            var pane = ActivePaneView;
            var offItsRow = row is not null && pane.KeyboardInList && !ReferenceEquals(pane.KeyboardRow, row);
            if (!lost && !offItsRow)
                return;

            Serilog.Log.Information(
                "Returning to the window, the keyboard was {Where} (left from {LeftFrom}); put back on the list",
                lost ? "nowhere" : "off its row", leftFrom ?? "nothing");
            ActivePaneView.FocusOnReturn();
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void OnFocusRequested(object? sender, string target)
    {
        // After a command on rows: back to the list only if the keyboard was left nowhere (the row
        // it was on deleted, a list rebuilt under it, 2026-10-06 Friction), never taken from
        // something the user moved to meanwhile. Once things settle, so the new rows are there.
        if (target == "grid-if-lost")
        {
            Dispatcher.BeginInvoke(
                () =>
                {
                    if (KeyboardIsLost())
                        ActivePaneView.FocusGrid();
                },
                System.Windows.Threading.DispatcherPriority.ContextIdle);
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (target == "pane")
                ActivePaneView.FocusOnReturn();
            else if (target == "address")
                ActivePaneView.FocusAddress();
            else if (target == "command")
                ActivePaneView.FocusCommandLine(_shell.TakeCommandLineDraft());
            else if (target == "locationMenu")
                ActivePaneView.OpenLocationMenu();
            else if (target == "palette")
            {
                PaletteQuery.Focus();
                PaletteQuery.SelectAll();
            }
            else
                ActivePaneView.FocusGrid();
        });
    }

    /// <summary>
    /// Whether the keyboard has nowhere useful to be in this window: on nothing, on the window
    /// itself, or on an element no longer on screen (a row container taken away with its row).
    /// </summary>
    private bool KeyboardIsLost()
    {
        if (!IsActive)
            return false;

        return Keyboard.FocusedElement is not DependencyObject focused
               || ReferenceEquals(focused, this)
               || PresentationSource.FromDependencyObject(focused) is null;
    }

    // ---- command palette ---------------------------------------------------------------------

    /// <summary>
    /// The palette's own keys. It is a text box, so the window's key router has already stood aside
    /// (a text input owns its keys, §1.2/7) and these are the only ones that reach it.
    /// </summary>
    private async void OnPaletteKeyDown(object sender, KeyEventArgs e)
    {
        var palette = _shell.Palette;

        switch (e.Key)
        {
            case Key.Down:
                palette.Move(1);
                break;
            case Key.Up:
                palette.Move(-1);
                break;
            // A page of this list, not of the pane behind it.
            case Key.PageDown:
                palette.Move(10);
                break;
            case Key.PageUp:
                palette.Move(-10);
                break;
            // Section to section. Tab would otherwise move focus out of the query box, which closes
            // the palette — so it is the one key here with nothing better to do.
            case Key.Tab:
                palette.JumpSection(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                break;
            case Key.Enter:
                e.Handled = true;
                _paletteFromAddress = null;
                await _shell.RunFromPaletteAsync();
                return;
            case Key.Escape:
                var back = _paletteFromAddress;
                _paletteFromAddress = null;
                _shell.ClosePalette();
                // Changed its mind, so back to the address as it was left. After the list has had
                // the keyboard the close asks for, which is queued already.
                if (back is { } from)
                    _ = Dispatcher.BeginInvoke(
                        () => from.Pane.ResumeAddress(from.Text, from.Start, from.Length),
                        System.Windows.Threading.DispatcherPriority.Background);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Clicking anywhere else dismisses it, without taking the keyboard back from wherever the
    /// click just went — that is what makes it transient rather than modal.
    /// </summary>
    private void OnPaletteLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _paletteFromAddress = null;
        _shell.Palette.Close();
    }

    private async void OnPaletteItemClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;
        if (TreeWalk.Ancestor<ListBoxItem>(source)?.DataContext is not CommandPaletteRow { IsHeader: false } row)
            return;

        e.Handled = true;
        _paletteFromAddress = null;
        _shell.Palette.Selected = row;
        await _shell.RunFromPaletteAsync();
    }

    /// <summary>
    /// The highlight is moved from the query box, so the list has to follow it. A row already drawn
    /// is brought into view at once, which is every arrow key. A row not drawn yet is the first of
    /// a list just replaced, and asking the virtualizing panel for it there is the suspect for the
    /// palette's slow opens (PLAN.md, "Palette, first open"): it can build and lay out rows on the
    /// spot, inside the palette's own build, before the frame does it all again. So that one waits
    /// until the frame has drawn the rows, when it costs nothing.
    /// </summary>
    private void OnPaletteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PaletteList.SelectedItem is not { } item)
            return;

        if (PaletteList.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement row)
            row.BringIntoView();
        else
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(PaletteList.SelectedItem, item))
                    PaletteList.ScrollIntoView(item);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Modifier state drives the legend, and modifiers only surface through key events — so both
    /// directions are tracked here rather than polled.
    /// </summary>
    private void UpdateHeldModifiers() => _shell.HeldModifiers = KeyStrokeMapper.From(Keyboard.Modifiers);

    /// <summary>
    /// Every key in the application, not only this window's, and the pointer while it is ours. A
    /// modifier let go while a context menu had the keyboard (a menu is a window of its own, so
    /// the key never reached PreviewKeyUp here), or during a drag (the drag loop takes the keys),
    /// left the legend showing that modifier's whole layer, several rows tall, until the next key.
    /// Reading the real state costs nothing, and setting an unchanged value does nothing.
    /// </summary>
    private void OnAnyInput(object sender, ProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is KeyEventArgs || (IsActive && e.StagingItem.Input is MouseEventArgs))
            UpdateHeldModifiers();

        // The bubbling events only: each press also comes through as its preview and as raw input.
        if (e.StagingItem.Input.RoutedEvent == Keyboard.KeyDownEvent || e.StagingItem.Input.RoutedEvent == Mouse.MouseDownEvent)
            _answers.Seen(e.StagingItem.Input);
    }

    /// <summary>Whether a click or key got its frame in time (<see cref="AnswerClock"/>).</summary>
    private readonly AnswerClock _answers = new();

    /// <summary>
    /// True for Alt pressed or released on its own. Windows delivers those as WM_SYSKEY*, which WPF
    /// surfaces as <see cref="Key.System"/> with the real key in <see cref="KeyEventArgs.SystemKey"/>.
    /// </summary>
    private static bool IsBareAlt(KeyEventArgs e) =>
        e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt;

    /// <summary>Commands whose key, pressed in the address bar, leaves it and runs as from the list.</summary>
    private static readonly HashSet<string> LeavesAddressBar = new(StringComparer.Ordinal)
    {
        "dc.palette", "dc.back", "dc.forward", "dc.up",
    };

    /// <summary>
    /// The palette was opened from the address bar: whose, and what was in it, for Escape to put
    /// back. Null once the palette closed any other way.
    /// </summary>
    private (PaneView Pane, string Text, int Start, int Length)? _paletteFromAddress;

    private static bool IsModifierKey(KeyEventArgs e) =>
        (e.Key == Key.System ? e.SystemKey : e.Key)
            is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    /// <summary>When Backspace emptied the quick filter, as a key event's timestamp; null once another key came.</summary>
    private int? _filterEmptiedAt;

    /// <summary>
    /// How long Backspace stays inert after emptying the filter. Long enough for the extra press in
    /// a burst, short enough that a deliberate "and now up" after a glance at the list still works.
    /// </summary>
    private const int FilterEmptiedGraceMs = 700;

    /// <summary>
    /// Tapping Alt with nothing after it normally puts Win32 into menu mode: the window waits for a
    /// menu that this application does not have, and swallows everything until Escape — which is
    /// exactly the stuck legend row. We have no menu bar and no access keys, so the tap is ours to
    /// eat. Alt+F4 and Alt+Space arrive with their own SystemKey and are deliberately left alone.
    /// </summary>
    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        UpdateHeldModifiers();
        if (IsBareAlt(e))
            e.Handled = true;
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        UpdateHeldModifiers();

        if (IsBareAlt(e))
        {
            e.Handled = true;
            return;
        }

        // Text inputs own their keys; the pane handles Enter and Escape locally (§1.2/7).
        // PasswordBox is not a TextBoxBase -- it derives straight from Control -- so it has to be
        // named explicitly. Leaving it out sent Enter in a password field to the global binding
        // for Enter, which is "open the selected file".
        // One exception: the key for "Go to location", pressed in the address bar, does what it does
        // in a browser and selects what is there. Text typed after it used to land beside the old
        // address and go to the two joined (2026-10-14). Asked of the registry, so a rebound key works.
        var fromAddress = false;
        if (Keyboard.FocusedElement is TextBox { Name: "AddressBox" } address &&
            KeyStrokeMapper.From(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers) is { } inAddress &&
            _shell.Commands.ForKey(inAddress, _shell.ActiveTab.Uri) is { } addressCommand)
        {
            if (addressCommand.Id == "dc.focusAddress")
            {
                e.Handled = true;
                address.SelectAll();
                return;
            }

            // The palette and the history keys work from the address bar too, after leaving it (2026-10-26).
            // Only on a chord with Ctrl or Alt: a plain key, Backspace for Up above all, is the
            // text's. The palette keeps what was typed for Escape to come back to; Back and Forward
            // let it go, as a browser does, since they say where to be instead.
            if ((inAddress.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != 0
                && LeavesAddressBar.Contains(addressCommand.Id)
                && AppliesNow(addressCommand))
            {
                _paletteFromAddress = addressCommand.Id == "dc.palette"
                    ? (ActivePaneView, address.Text, address.SelectionStart, address.SelectionLength)
                    : null;
                ActivePaneView.FocusGrid();
                fromAddress = true;
            }
        }

        // The command line's own key, pressed on the open line, steps to the next shell installed
        // here: the line opens in the configured one, and a pwsh line is two presses away. Not on
        // a held key's repeats, which landed in the box just opened and spun through the shells.
        if (Keyboard.FocusedElement is TextBox { Name: "CommandBox" } commandBox &&
            KeyStrokeMapper.From(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers) is { } inCommand &&
            _shell.Commands.ForKey(inCommand, _shell.ActiveTab.Uri) is { Id: "dc.commandLine" } &&
            TreeWalk.Ancestor<PaneView>(commandBox) is { } commandPane)
        {
            e.Handled = true;
            if (!e.IsRepeat)
                commandPane.NextCommandShell();
            return;
        }

        if (!fromAddress && Keyboard.FocusedElement is TextBoxBase or PasswordBox)
            return;

        // A filter in progress owns Backspace, the same way a text box owns its keys: while you
        // are typing into a listing, Backspace is editing what you typed and not leaving the
        // folder. Escape comes first in the dismiss chain, so there is always a way out before
        // Backspace means "up" again.
        if (e.Key == Key.Back && _shell.ActiveTab.IsFiltering)
        {
            e.Handled = true;
            _shell.ActiveTab.BackspaceFilter();
            if (!_shell.ActiveTab.IsFiltering)
                _filterEmptiedAt = e.Timestamp;
            return;
        }

        // The Backspace that empties a filter is often followed by one more, pressed in the same
        // burst, and that one took the user out of the folder (2026-10-26). So for a moment after,
        // Backspace does nothing; held down, it does nothing for as long as it is held.
        if (_filterEmptiedAt is { } emptied && e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None
            && (e.IsRepeat || e.Timestamp - emptied < FilterEmptiedGraceMs))
        {
            e.Handled = true;
            if (e.IsRepeat)
                _filterEmptiedAt = e.Timestamp;
            return;
        }
        if (!IsModifierKey(e))
            _filterEmptiedAt = null;

        // Space too, and for a less obvious reason: a ListView handles Space itself as a selection
        // key and marks it handled, so it never reaches the grid as text input at all. Typing a
        // filename with a space in it silently dropped the space. Claimed only while a filter is
        // running, so bare Space still belongs to the listing.
        if (e.Key == Key.Space && _shell.ActiveTab.IsFiltering && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _shell.ActiveTab.FilterBy(' ');
            return;
        }

        // A focused button owns Enter and Space — that is what focusing it was for. Without this,
        // the global binding for Enter ("open the selected file") fires while the user is trying
        // to answer a confirmation, and the prompt's own buttons can never be pressed.
        if (Keyboard.FocusedElement is ButtonBase && e.Key is Key.Enter or Key.Space)
            return;

        // An open preview owns Ctrl+C: what it copies is the lines picked out of the file, which
        // is what the preview is for. The grid's own "copy these files to the clipboard" is one
        // Escape away, and is what Ctrl+C means everywhere else in the window.
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && ActivePaneView.IsPreviewFocused)
            return;

        // Windows delivers anything with Alt held as WM_SYSKEY*, which WPF surfaces as Key.System
        // with the real key in SystemKey -- the same fact the bare-Alt handling above relies on.
        // Reading e.Key directly meant every Alt binding in the application silently did nothing.
        var pressed = e.Key == Key.System ? e.SystemKey : e.Key;

        if (KeyStrokeMapper.From(pressed, Keyboard.Modifiers) is not { } stroke)
            return;
        if (_shell.Commands.ForKey(stroke, _shell.ActiveTab.Uri) is not { } command)
            return;

        // A command that does not apply must not swallow the key: Escape with nothing to dismiss
        // still belongs to whatever has focus.
        if (!AppliesNow(command))
            return;

        e.Handled = true;

        // Pressed while an undo was still running, and only arrived now: a Ctrl+Z hammered while
        // waiting is not a request for one more, and the next undo ran on its own (2026-10-18).
        if (command.Id == "dc.undo" && _shell.PressedBeforeUndoEnded(e.Timestamp))
            return;

        await _shell.ExecuteAsync(command);
    }

    /// <summary>
    /// Whether a key's command applies here and now. Its check may not end the process: the key
    /// handler is async void, so anything thrown there reached the dispatcher (the Ctrl+T crash,
    /// 2026-10-01).
    /// </summary>
    private bool AppliesNow(CommandDefinition command)
    {
        try
        {
            return command.IsApplicable(_shell.CreateContext());
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not tell whether {Command} applies; the key was left alone", command.Id);
            return false;
        }
    }
}
