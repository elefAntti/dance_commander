using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DanceCommander.App.Commands;
using DanceCommander.App.Services;
using DanceCommander.Core.Collections;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Config;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Listing;
using DanceCommander.Core.Navigation;
using DanceCommander.Core.Startup;
using DanceCommander.Providers.Local;
using DanceCommander.Core.Operations;
using DanceCommander.Core.Transfer;
using DanceCommander.Extensibility;
using DanceCommander.Providers.Sftp;
using Microsoft.Extensions.DependencyInjection;

namespace DanceCommander.App.ViewModels;

/// <summary>One slot on the F-key legend. A null command renders as an empty key.</summary>
/// <param name="Group">The command's kind; the legend keeps a kind together and spaces the kinds apart.</param>
/// <param name="StartsLine">Begins a line of its own, whatever room is left on the one before.</param>
public sealed record LegendSlot(
    string Key, string Title, string CommandId, string? Group = null, bool StartsLine = false);

public sealed partial class ShellViewModel : ObservableObject, IEcho
{
    private readonly IServiceProvider _services;

    private readonly IIconService _icons;
    private readonly ITransferService _transfers;
    private readonly IJobService _jobs;

    public ShellViewModel(
        IServiceProvider services,
        ICommandRegistry commands,
        IBookmarkService bookmarks,
        IIconService icons,
        ITransferService transfers,
        IJobService jobs,
        IActivityLog activityLog,
        IUndoStack undo,
        ICommandListingCatalog listings)
    {
        _services = services;
        _icons = icons;
        _transfers = transfers;
        _jobs = jobs;
        Commands = commands;
        BookmarkService = bookmarks;
        ActivityLog = activityLog;
        Undo = undo;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        jobs.JobsChanged += (_, _) =>
            dispatcher?.InvokeAsync(RefreshJobs);
        jobs.JobFinished += (_, job) =>
            dispatcher?.InvokeAsync(() => OnJobFinished(job));
        activityLog.Changed += (_, _) =>
            dispatcher?.InvokeAsync(() =>
            {
                OnPropertyChanged(nameof(Activity));
                OnPropertyChanged(nameof(HasNoActivity));
                EchoLatestActivity();
            });
        // Not for a permanent delete: on the stack only so that Ctrl+Z says it can't be undone,
        // it was offered as "Ctrl+Z undoes" beside "Deleted … permanently" (2026-10-17).
        undo.Pushed += (_, operation) =>
        {
            // Only when Ctrl+Z would undo it now: with another operation still under way it
            // answered "Still copying" to the offer (review, 2026-10-30). Asked once dispatched,
            // by when the operation that pushed it has usually let go.
            if (operation is not Irreversible)
            {
                dispatcher?.InvokeAsync(() =>
                {
                    if (ReferenceEquals(undo.Pending.FirstOrDefault(), operation) && !undo.Waits)
                        OfferUndoInEcho();
                });
            }
        };
        // Noted as it is raised, not dispatched later: the end of an undo is what a key press
        // waiting in the queue is measured against, and a little late only drops a little more.
        undo.Changed += (_, _) => NoteUndoEnd();
        undo.Changed += (_, _) =>
            dispatcher?.InvokeAsync(() =>
            {
                OnPropertyChanged(nameof(UndoDescription));
                OnPropertyChanged(nameof(HasUndo));
                EchoUndoing();
            });

        Left = services.GetRequiredService<PaneViewModel>();
        Right = services.GetRequiredService<PaneViewModel>();
        _activePane = Left;
        Left.IsActive = true;

        Palette = new CommandPaletteViewModel(commands, CreateContext, () => new PaletteLocation(
            ActiveTab.Title, ActiveTab.Uri is { } here ? _icons.GetLocationIcon(here) : null, ActiveTab.Branch),
            icons.GetIcon, services.GetService<CommandUsage>());

        BuiltInCommands.RegisterAll(commands);
        CollectionCommands.RegisterAll(commands);
        BookmarkCommands.RegisterAll(commands);
        RegisterShellCommands(commands, listings);
        if (services.GetService<IConfigStore<SettingsFile>>() is { } settings)
        {
            SettingsCommands.RegisterAll(commands, settings, services.GetRequiredService<SettingsWriter>(),
                () => SettingsRequested?.Invoke(this, null), services.GetService<PackDetector>()?.Settings);

            // How a tab sorts by default: tabs on a view nobody chose follow a change at once, so
            // the setting shows what it does while the window is still open.
            settings.Changed += (_, file) => dispatcher?.InvokeAsync(() =>
            {
                foreach (var tab in Left.Tabs.Concat(Right.Tabs))
                    tab.FollowDefaultOrder(file.View.DirectoriesFirst);
            });
        }
        UpdateLegend();

        // A key chosen in settings.json moves a command on the legend as much as a tool's does.
        commands.BindingsChanged += (_, _) => dispatcher?.InvokeAsync(UpdateLegend);

        // Tools register after the shell exists, and again on every edit of tools.json. Without
        // this the legend was drawn before any of them arrived and never again at rest, so a tool
        // bound to F9 worked but did not appear on the F-key row until a modifier was pressed.
        if (services.GetService<ToolCatalog>() is { } tools)
            tools.Changed += (_, _) => dispatcher?.InvokeAsync(UpdateLegend);

        bookmarks.Changed += (_, _) => RefreshBookmarks();

        // A server that went away is said in the panes showing it, when it happens, rather than
        // when the next click there fails (2026-10-03). Amber, not red: the next listing or
        // Ctrl+R connects again on its own.
        if (services.GetService<Providers.Sftp.ISftpConnections>() is { } connections)
            connections.Lost += (_, target) => dispatcher?.InvokeAsync(() =>
            {
                foreach (var tab in Left.Tabs.Concat(Right.Tabs))
                {
                    if (tab.Uri is { } at && string.Equals(at.Scheme, "sftp", StringComparison.OrdinalIgnoreCase) &&
                        Providers.Sftp.SftpTarget.From(at) == target)
                    {
                        tab.Report(new Notice($"Lost the connection to {target}.", NoticeSeverity.Warning, "Ctrl+R connects again."));
                    }
                }
            });
        if (services.GetService<IShelf>() is { } shelf)
        {
            shelf.Changed += (_, _) => dispatcher?.InvokeAsync(() =>
            {
                OnPropertyChanged(nameof(ShelfCount));
                OnPropertyChanged(nameof(HasShelf));
            });
        }
        RefreshBookmarks();

        // The title follows the active tab, so switching panes or tabs retitles the window.
        foreach (var pane in new[] { Left, Right })
        {
            pane.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(PaneViewModel.ActiveTab) && ReferenceEquals(pane, ActivePane))
                    RewireTitle();
            };
        }

        RewireTitle();

        // What the session keeps changes: saved two seconds after the last change and again on
        // the way out, so a crash loses at most those two seconds.
        foreach (var pane in new[] { Left, Right })
            pane.SessionChanged += (_, _) => ScheduleSessionSave();
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(IsDualPane) or nameof(ActivePane))
                ScheduleSessionSave();
        };
    }

    // ---- session ------------------------------------------------------------------------

    private readonly Core.Config.Debouncer _sessionSave = new(TimeSpan.FromSeconds(2));

    /// <summary>Where the session is kept; null until it has been restored, and in a --new-window copy.</summary>
    private Core.Config.IConfigStore<SessionFile>? _session;

    private void ScheduleSessionSave()
    {
        if (_session is not null)
            _sessionSave.Schedule(() =>
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => _ = SaveSessionAsync()));
    }

    private SessionFile SessionSnapshot() => new()
    {
        DualPane = IsDualPane,
        RightActive = ReferenceEquals(ActivePane, Right),
        Left = Left.Snapshot(),
        Right = Right.Snapshot(),
    };

    private Task SaveSessionAsync() =>
        _session?.SaveAsync(SessionSnapshot()) ?? Task.CompletedTask;

    /// <summary>On the way out: written before the process can end under it, but never held up for long.</summary>
    public void SaveSessionNow()
    {
        if (_session is not { } store)
            return;
        var snapshot = SessionSnapshot();
        Task.Run(() => store.SaveAsync(snapshot)).Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Puts back the panes and tabs of the last session, none of them listed yet. False when there
    /// is nothing to put back: a first run, or a file with nothing readable left in it.
    /// </summary>
    private bool RestoreSession(SessionFile file, FsUri home, FsUri docs)
    {
        var left = Read(file.Left, out var leftActive);
        var right = Read(file.Right, out var rightActive);
        if (left.Count == 0 && right.Count == 0)
            return false;

        Left.Restore(left.Count > 0 ? left : [([home], 0, null)], leftActive);
        Right.Restore(right.Count > 0 ? right : [([docs], 0, null)], rightActive);
        IsDualPane = file.DualPane;
        if (file.RightActive)
            Activate(Right);
        return true;

        static List<(List<FsUri> History, int Position, string? Cursor)> Read(PaneSession pane, out int active)
        {
            var tabs = new List<(List<FsUri> History, int Position, string? Cursor)>();
            active = 0;
            for (var i = 0; i < pane.Tabs.Count; i++)
            {
                if (pane.Tabs[i].Read() is not { } read)
                    continue;
                if (i == pane.Active)
                    active = tabs.Count;
                tabs.Add((read.History, read.Position, pane.Tabs[i].Cursor));
            }
            return tabs;
        }
    }

    public PaneViewModel Left { get; }

    public PaneViewModel Right { get; }

    public ICommandRegistry Commands { get; }

    /// <summary>Every command that applies here, searchable — a renderer over the registry (§1.2/1).</summary>
    public CommandPaletteViewModel Palette { get; }

    public IBookmarkService BookmarkService { get; }

    public ObservableCollection<BookmarkItemViewModel> Bookmarks { get; } = [];

    public IActivityLog ActivityLog { get; }

    public IUndoStack Undo { get; }

    /// <summary>
    /// Transfers and builds, shown in the strip at the bottom (§6.17): running, recently failed,
    /// and finished jobs that have something to offer.
    /// </summary>
    public ObservableCollection<Job> Jobs { get; } = [];

    /// <summary>What the app has done to your files, newest first (§1.2/6).</summary>
    public IReadOnlyList<ActivityEntry> Activity => ActivityLog.Entries;

    public string? UndoDescription => Undo.NextDescription;

    public bool HasNoActivity => ActivityLog.Entries.Count == 0;

    [ObservableProperty]
    private bool _hasJobs;

    /// <summary>How long a forgotten job stays in the strip while it fades; matches the view's fade.</summary>
    private static readonly TimeSpan JobFadesFor = TimeSpan.FromMilliseconds(400);

    private void RefreshJobs()
    {
        var current = _jobs.Jobs;

        // Leaving rather than removed: a finished row that vanishes between one frame and the next
        // reads as a glitch. It stays for the fade and goes after.
        foreach (var job in Jobs.Where(j => !j.IsLeaving && !current.Contains(j)).ToList())
        {
            job.IsLeaving = true;
            _ = RemoveAfterFadeAsync(job);
        }

        foreach (var job in current.Where(j => !Jobs.Contains(j)))
        {
            job.PropertyChanged += OnJobProgressed;
            if (!job.IsFinished && job.WaitingFor is null)
            {
                job.IsHeld = true;
                _ = ShowIfStillGoingAsync(job);
            }
            Jobs.Add(job);
        }

        UpdateHasJobs();
        UpdateTaskbar();
    }

    /// <summary>
    /// How long a job runs before the strip shows it. A move on one drive, a small copy, a quick
    /// tool are over before then, and appeared in the strip only to leave it again: motion for
    /// something the user just watched happen (§1.3).
    /// </summary>
    private static readonly TimeSpan JobShowsAfter = TimeSpan.FromMilliseconds(500);

    private async Task ShowIfStillGoingAsync(Job job)
    {
        await Task.Delay(JobShowsAfter);
        if (job.IsHeld && WorthShowing(job))
            ReleaseJob(job);
    }

    /// <summary>
    /// Anything still running, waiting on an answer, or ended badly or with something to open.
    /// A job that simply finished has said all it had to in the activity log and the arrivals.
    /// </summary>
    private static bool WorthShowing(Job job) =>
        !job.IsFinished || job.State != JobState.Completed || job.HasResult;

    private void ReleaseJob(Job job)
    {
        job.IsHeld = false;
        UpdateHasJobs();
    }

    /// <summary>The strip is up while it has a row to show; held jobs are not rows yet.</summary>
    private void UpdateHasJobs() => HasJobs = Jobs.Any(j => !j.IsHeld);

    private async Task RemoveAfterFadeAsync(Job job)
    {
        // Never shown, so nothing to fade.
        if (!job.IsHeld)
            await Task.Delay(JobFadesFor);
        job.PropertyChanged -= OnJobProgressed;
        Jobs.Remove(job);
        UpdateHasJobs();
    }

    private void OnJobProgressed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(Job.Fraction) or nameof(Job.State) or nameof(Job.IsIndeterminate)
            or nameof(Job.WaitingFor) or nameof(Job.Title)))
            return;

        // Jobs report from their worker thread, and Jobs is a UI-owned collection.
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            // A question or a failure is shown at once, however soon it comes.
            if (sender is Job { IsHeld: true } held && (held.WaitingFor is not null || (held.IsFinished && WorthShowing(held))))
                ReleaseJob(held);
            UpdateTaskbar();
            if (e.PropertyName == nameof(Job.State) && sender is Job { IsFinished: true } job)
                _ = RefreshAffectedPanesAsync(job);
        });
    }

    /// <summary>
    /// Raised when a job whose ending is news has ended, for the window to announce — it alone
    /// knows whether anybody is looking at it.
    /// </summary>
    public event EventHandler<Job>? JobAnnounced;

    private void OnJobFinished(Job job)
    {
        if (job.AnnouncesCompletion)
            JobAnnounced?.Invoke(this, job);
    }

    /// <summary>Puts a job's output in the active pane, following it if it is still running.</summary>
    public void ShowJobOutput(Job job)
    {
        if (job.Output is not null)
            ActiveTab.Follow(job);
    }

    /// <summary>
    /// The file-manager ending to a build: its output directory, in the other pane (§6.17). The
    /// other pane, because the one you are in is where you started the build from and are
    /// presumably still working; the result is somewhere to look, not somewhere to be moved to.
    /// </summary>
    public async Task OpenJobResultAsync(Job job)
    {
        if (job.ResultLocation is not { } location)
            return;

        IsDualPane = true;
        await InactivePane.ActiveTab.NavigateAsync(location);
    }

    /// <summary>
    /// Re-lists any tab showing somewhere the job just changed.
    /// </summary>
    /// <remarks>
    /// A local folder notices on its own -- it has a watcher -- so this exists for the places that
    /// cannot: an SFTP directory has no change notification, and without this a file you just
    /// copied there simply does not appear until you press Ctrl+R, which makes the transfer look
    /// like it failed. Live locations are skipped so a local copy does not get listed twice.
    /// </remarks>
    private async Task RefreshAffectedPanesAsync(Job job)
    {
        await FollowMovedFoldersAsync(job);

        var touched = new List<FsUri>();
        if (job.ResultLocation is { } result)
            touched.Add(result);

        if (job is TransferJob transfer)
        {
            // A move empties the source directories as well as filling the target.
            touched.Add(transfer.TargetDirectory);
            foreach (var source in transfer.Sources)
            {
                if (source.Parent is { } parent)
                    touched.Add(parent);
            }
        }

        if (touched.Count == 0)
            return;

        foreach (var tab in new[] { Left, Right }.SelectMany(pane => pane.Tabs))
        {
            if (tab.IsLive || tab.Uri is not { } uri)
                continue;
            if (!touched.Any(where => where == uri))
                continue;

            await tab.RefreshAsync();
        }
    }

    /// <summary>
    /// A tab showing a folder that a move just carried away goes with it, rather than being left on
    /// a path that no longer exists.
    /// </summary>
    /// <remarks>
    /// Moving the folder you are looking at — dragging the location icon with Shift, F6 on a folder
    /// open in the other pane — used to end with "no longer exists" in that pane, which is true and
    /// useless: the folder does exist, and the application is the one that moved it. A move lands
    /// each source under the target by its own name, so the new place of anything inside it is that
    /// plus the same path below. Only for moves this application made and finished; something moved
    /// away by another program still reports that it has gone, because we do not know where to.
    /// </remarks>
    private async Task FollowMovedFoldersAsync(Job job)
    {
        if (job is not TransferJob { Mode: TransferMode.Move, State: JobState.Completed } move)
            return;

        foreach (var tab in new[] { Left, Right }.SelectMany(pane => pane.Tabs))
        {
            if (tab.Uri is not { IsView: false } here)
                continue;

            foreach (var source in move.Sources)
            {
                // A name the target cannot hold was left out of the move, so nothing went there.
                FsUri landed;
                try
                {
                    landed = move.TargetDirectory.Child(source.Name);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (Relocated(here, source, landed) is not { } moved)
                    continue;

                // Only if it really went, and really arrived. A folder skipped at the "already
                // there" question was never moved, and jumping to its namesake would be wrong.
                if (await ExistsAsync(here) || !await ExistsAsync(moved))
                    break;

                await tab.NavigateAsync(moved);
                break;
            }
        }

        async Task<bool> ExistsAsync(FsUri uri)
        {
            try
            {
                var provider = _services.GetRequiredService<IProviderRegistry>().Resolve(uri);
                return await provider.StatAsync(uri, CancellationToken.None) is { IsDirectory: true };
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // The path below the moved folder, re-rooted at where it went. Null when "here" is not
        // the folder or inside it.
        static FsUri? Relocated(FsUri here, FsUri source, FsUri destination)
        {
            var below = new Stack<string>();
            for (var at = here; at is not null; at = at.Parent)
            {
                if (at == source)
                {
                    var result = destination;
                    while (below.Count > 0)
                        result = result.Child(below.Pop());
                    return result;
                }

                below.Push(at.Name);
            }

            return null;
        }
    }

    /// <summary>
    /// What the window reports to the OS about work in progress. A long copy shows on the taskbar
    /// button exactly as Explorer's does, so the app does not have to be on screen to be watched.
    /// </summary>
    [ObservableProperty]
    private double _taskbarProgress;

    [ObservableProperty]
    private bool _taskbarBusy;

    [ObservableProperty]
    private bool _taskbarFailed;

    /// <summary>Busy with nothing that can say how far along it is: a build, but no copy.</summary>
    [ObservableProperty]
    private bool _taskbarIndeterminate;

    private void UpdateTaskbar()
    {
        var running = Jobs.Where(j => !j.IsFinished).ToList();
        var staying = Jobs.Where(j => !j.IsLeaving).ToList();
        // A build has no fraction to average in; letting it count as 0% would drag a copy
        // running beside it backwards on the taskbar.
        var measured = running.Where(j => !j.IsIndeterminate).ToList();

        TaskbarFailed = staying.Any(j => j.State == JobState.Failed);
        TaskbarBusy = running.Count > 0;
        TaskbarIndeterminate = running.Count > 0 && measured.Count == 0;
        TaskbarProgress = measured.Count == 0 ? 0 : measured.Average(j => j.Fraction);

        WorkingReason = Describe(running.Where(j => !j.IsWaiting).ToList());
        UnfinishedReason = Describe(running);

        static string? Describe(List<Job> jobs) => jobs.Count switch
        {
            0 => null,
            1 => jobs[0].Title,
            _ => $"{jobs.Count:N0} jobs are running",
        };
    }

    /// <summary>
    /// What Windows is told keeps the machine awake (<see cref="Services.SessionGuard"/>): jobs
    /// doing work, not ones queued or waiting on an answer. Null for none.
    /// </summary>
    [ObservableProperty]
    private string? _workingReason;

    /// <summary>
    /// Jobs not yet finished, from the job service itself rather than the strip: the strip hears of
    /// a new job a moment later, and the end of a session can come in that moment. Any thread.
    /// </summary>
    public IReadOnlyList<Job> UnfinishedJobs => _jobs.Jobs.Where(j => !j.IsFinished).ToList();

    /// <summary>What Windows is told a shutdown would stop: every job not yet finished. Null for none.</summary>
    [ObservableProperty]
    private string? _unfinishedReason;

    // ---- window title ---------------------------------------------------------------------

    private TabViewModel? _titledTab;

    /// <summary>
    /// Explorer's convention: the folder you are looking at, then the application. With two panes
    /// the honest answer is the *active* one — that is what the keyboard is pointed at.
    /// </summary>
    /// <summary>
    /// What the listing calls itself, which for a directory is its name and for a generated
    /// location is whatever generated it — the window and the tab strip should not disagree about
    /// where you are, and the listing is the one that knows.
    /// </summary>
    public string WindowTitle =>
        ActiveTab.Uri is null ? "Dance Commander" : $"{ActiveTab.Title} — Dance Commander";

    private void RewireTitle()
    {
        if (_titledTab is not null)
            _titledTab.PropertyChanged -= OnTitledTabChanged;

        _titledTab = ActiveTab;
        _titledTab.PropertyChanged += OnTitledTabChanged;
        OnPropertyChanged(nameof(WindowTitle));
        UpdateLegend();
    }

    private void OnTitledTabChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.Uri) or nameof(TabViewModel.Title))
            OnPropertyChanged(nameof(WindowTitle));

        // What the legend offers depends on where you are: a read-only place offers no Rename. And
        // again once the listing is in: until then the cursor is on a row of the place just left,
        // and in a collection it is the rows, not the page, that can be renamed.
        if (e.PropertyName is nameof(TabViewModel.Uri) or nameof(TabViewModel.IsBusy))
            UpdateLegend();
    }

    /// <summary>Something in the job strip that Escape can dismiss (dc.dismiss).</summary>
    private bool HasFinishedJobs => Jobs.Any(j => j.IsFinished && !j.IsLeaving && !j.IsHeld);

    /// <summary>
    /// Gone at once, not faded: the fade is for a job leaving by itself, so its going does not read
    /// as a glitch. One the user dismissed slowly dissolving under the pointer reads as a lag.
    /// </summary>
    public void DismissJob(Job job)
    {
        _jobs.Forget(job);
        if (Jobs.Remove(job))
        {
            job.PropertyChanged -= OnJobProgressed;
            UpdateHasJobs();
            UpdateTaskbar();
        }
    }

    /// <summary>Starts a transfer on behalf of something outside the command registry, e.g. a drop.</summary>
    public void EnqueueTransfer(IReadOnlyList<FsUri> sources, FsUri target, TransferMode mode) =>
        _transfers.Enqueue(sources, target, mode, ActiveTab);

    /// <summary>
    /// Writes files another program handed over as bytes (an Outlook attachment) into
    /// <paramref name="target"/>, each as a new file: one already there of that name is stepped
    /// around, never written over, and each can be undone like a pasted file.
    /// </summary>
    public async Task WriteDroppedAsync(IReadOnlyList<VirtualFile> files, FsUri target)
    {
        var operations = _services.GetRequiredService<IFileOperationService>();
        var tab = ActiveTab;
        var failed = new List<string>();

        // What arrived but not quite as asked. Said, because the next thing a user does with a saved
        // attachment is often delete the email, and the file they check has to be the one that came.
        var remarks = new List<string>();
        var worrying = false;

        foreach (var file in files)
        {
            var name = ForeignName.ToFileName(file.Name);
            FsUri? created;
            try
            {
                // No command fetches an attachment out of a mail program; the log says where it came from.
                created = await operations.CreateFileAsync(
                    target, name, file.Content, $"rem {name}: dropped from another program", CancellationToken.None);
            }
            catch (Exception ex)
            {
                failed.Add($"{name}: {ex.Message}");
                continue;
            }

            if (created is null)
                continue;
            if (created.Name != name)
                remarks.Add($"{name} was already there, so this one is {created.Name}.");
            if (file.SizeDiffers)
            {
                worrying = true;
                remarks.Add($"{created.Name} came as {file.Content.LongLength:N0} bytes where {file.DeclaredSize:N0} were promised. Open it before deleting where it came from.");
            }

            if (created.IsLocal && !VirtualFiles.MarkFromInternet(created.ToLocalPath()))
            {
                worrying = true;
                remarks.Add($"{created.Name} could not be marked as from the internet, so Windows will not warn before opening it.");
            }
        }

        if (failed.Count > 0)
        {
            tab.Report(new Notice(
                failed.Count == files.Count ? "Could not write what was dropped." : "Could not write some of what was dropped.",
                NoticeSeverity.Warning, string.Join(Environment.NewLine, failed.Concat(remarks))));
        }
        else if (remarks.Count > 0)
        {
            tab.Report(new Notice(
                remarks.Count == 1 ? remarks[0] : "Dropped, with something to know.",
                worrying ? NoticeSeverity.Warning : NoticeSeverity.Info, remarks.Count == 1 ? null : string.Join(Environment.NewLine, remarks)));
        }
    }

    /// <summary>
    /// Writes text dragged in from another program into <paramref name="target"/> as a new file,
    /// named and encoded as pasted text is (BuiltInCommands.PasteAsync), and undone the same way.
    /// Dropped onto the folder showing, the cursor goes to it, as after a paste; dropped onto a
    /// folder row, the cursor stays, as it does after files are dropped there.
    /// </summary>
    public async Task WriteDroppedTextAsync(string text, FsUri target)
    {
        var operations = _services.GetRequiredService<IFileOperationService>();
        var tab = ActiveTab;
        var name = $"Dropped text {BuiltInCommands.Stamp()}.txt";
        FsUri? created;
        try
        {
            // No command fetches a drag; the log says where the text came from.
            created = await operations.CreateFileAsync(
                target, name, BuiltInCommands.TextFileContent(text), $"rem {name}: text dropped from another program",
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            tab.Report(new Notice("Could not write what was dropped.", NoticeSeverity.Warning, ex.Message));
            return;
        }

        if (created is not null && !tab.IsClosed && tab.Uri == target)
            await tab.NavigateAsync(target, created.Name);
    }

    private Task AddToShelf(IReadOnlyList<FsUri> sources)
    {
        var added = Shelf.Add(sources);
        Say(added == 0 ? "Already on the shelf" : $"Added {added:N0} to the shelf");
        return Task.CompletedTask;
    }

    /// <summary>The session's shelf, counted at the foot of the window (§6.19).</summary>
    public IShelf Shelf => _services.GetRequiredService<IShelf>();

    public int ShelfCount => Shelf.Items.Count;

    public bool HasShelf => ShelfCount > 0;

    /// <summary>Opens the shelf's page in the active pane.</summary>
    public async Task ShowShelfPageAsync()
    {
        await ActiveTab.NavigateAsync(AboutPages.Shelf);
        FocusRequested?.Invoke(this, "grid");
    }

    /// <summary>The shelf in a window of its own, above the others; the window owns it (ShelfWindow).</summary>
    public event EventHandler? ShelfWindowRequested;

    /// <summary>Ctrl+, : the window opens the settings window, or brings it forward.</summary>
    public event EventHandler<string?>? SettingsRequested;

    /// <summary>
    /// A drop onto one of the application's own pages (§6.19): it adds references, and copies
    /// nothing — to a collection, or as bookmarks into the group the page shows.
    /// </summary>
    public Task CollectAsync(IReadOnlyList<FsUri> sources, FsUri page) =>
        page == AboutPages.Shelf ? AddToShelf(sources)
        : AboutPages.CollectionName(page) is { } collection
            ? CollectionCommands.Collect(_services, ActiveTab, sources, collection, CancellationToken.None)
            : BookmarkCommands.Bookmark(_services, ActiveTab, sources, AboutPages.BookmarkGroupName(page), CancellationToken.None);

    /// <summary>
    /// The legend re-labels as modifiers are held, which is what makes it teach the app instead
    /// of merely decorating the bottom of the window (§1.2/1).
    /// </summary>
    public ObservableCollection<LegendSlot> Legend { get; } = [];

    [ObservableProperty]
    private PaneViewModel _activePane;

    [ObservableProperty]
    private bool _isDualPane = true;

    [ObservableProperty]
    private KeyModifiers _heldModifiers = KeyModifiers.None;

    public PaneViewModel InactivePane => ReferenceEquals(ActivePane, Left) ? Right : Left;

    public TabViewModel ActiveTab => ActivePane.ActiveTab;

    /// <summary>
    /// Where a connection's question goes when no listing claimed it (a copy, a drag, a preview):
    /// the front tab of a pane showing that server, the active one first, else the active tab.
    /// Asking in the active pane put the password for a drag out of an SFTP pane over the local
    /// listing it was dropped on.
    /// </summary>
    public TabViewModel TabShowing(Func<FsUri, bool> place)
    {
        if (ActiveTab.Location is { } here && place(here))
            return ActiveTab;
        if (IsDualPane && InactivePane.ActiveTab is { Location: { } there } other && place(there))
            return other;
        return ActiveTab;
    }

    public event EventHandler<string>? FocusRequested;

    partial void OnHeldModifiersChanged(KeyModifiers value)
    {
        UpdateLegend();
        OnPropertyChanged(nameof(HeldModifiersLabel));
    }

    /// <summary>
    /// A pane's part of the launch is over when it has rows or is asking something, or its listing
    /// ended without either. "Startup must not wait on the user", and a pane waiting for a
    /// password is waiting on the user.
    /// </summary>
    private static Task Settled(TabViewModel tab, Task listing) => Task.WhenAny(listing, tab.Settled);

    public async Task InitializeAsync(LaunchRequest? request = null)
    {
        var home = FsUri.Local(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var docs = FsUri.Local(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

        // A --new-window copy starts fresh and keeps nothing: one session, one writer.
        var store = request is { NewWindow: true }
            ? null
            : _services.GetService<Core.Config.IConfigStore<SessionFile>>();

        if (store is not null && RestoreSession(store.Current, home, docs))
        {
            if (request is not null)
                ApplyPanes(request);

            // The front tab of each pane, both at once; the others wait until they are shown.
            StartupClock.ExpectRows(2);
            await Task.WhenAll(
                Settled(Left.ActiveTab, Left.ActiveTab.ShowAsync()
                    .ContinueWith(_ => StartupClock.Mark("left rows"), TaskScheduler.Current)),
                Settled(Right.ActiveTab, Right.ActiveTab.ShowAsync()
                    .ContinueWith(_ => StartupClock.Mark("right rows"), TaskScheduler.Current)));
            StartupClock.ListingsEnded();

            // A command line opens in a new tab of the restored window, as the hand-off does when
            // a window is already up: what it asks for joins where you were rather than replacing it.
            if (request is { HasWork: true })
                await OpenAsync(request);
            else if (request is { Problems.Count: > 0 })
                Report(request);
        }
        else
        {
            // Before listing, not after it: a listing can wait on a password prompt indefinitely,
            // and --one-pane was ignored until it ended.
            if (request is not null)
                ApplyPanes(request);

            StartupClock.ExpectRows(2);

            // Both at once. One after the other, the right pane waited for the left before it
            // began, and the right one is Documents — on OneDrive, for many, and the slower of the two.
            await Task.WhenAll(
                Settled(Left.ActiveTab, Left.ActiveTab.GoToAsync(request?.Left ?? home, request?.Select)
                    .ContinueWith(_ => StartupClock.Mark("left rows"), TaskScheduler.Current)),
                Settled(Right.ActiveTab, Right.ActiveTab.GoToAsync(request?.Right ?? docs, null)
                    .ContinueWith(_ => StartupClock.Mark("right rows"), TaskScheduler.Current)));
            StartupClock.ListingsEnded();

            // After, because navigating clears the pane's notice.
            if (request is { Problems.Count: > 0 })
                Report(request);
        }

        // Only now: saving a half-restored window would write over the session being restored.
        _session = store;

        WatchConfigFiles();

        // Not awaited, and only now that the panes have listed: it looks at the disk, and the tools
        // it finds were already there from packs.json's last answer.
        var packs = _services.GetService<PackDetector>();
        var looked = Task.CompletedTask;
        if (packs is not null)
        {
            packs.Changed += (_, e) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => AnnouncePacksAsync(e.Arrived));
            looked = packs.DetectAsync();
        }

        if (BookmarkService is Core.Navigation.BookmarkService { FirstRun: { } firstRun })
            _ = AnnounceFirstRunAsync(firstRun, looked, packs);
    }

    /// <summary>
    /// What a first run set up from this computer, said once, in the pane and without taking the
    /// keyboard (SETTINGS_PLAN.md §8): one notice, not a wizard. After the first look for tool packs,
    /// so it can name what was found; that first look is otherwise silent, being the starting point
    /// rather than news.
    /// </summary>
    private async Task AnnounceFirstRunAsync(Task<int> firstRun, Task looked, PackDetector? packs)
    {
        var fromExplorer = await firstRun;
        await looked;

        var settings = _services.GetService<IConfigStore<SettingsFile>>()?.Current;
        var found = packs is null ? [] : ToolPacks.All
            .Where(p => packs.Found.ContainsKey(p.Id) && (settings is null || p.IsOn(settings)))
            .Select(p => p.Title)
            .ToList();

        // Counts in the headline, which way to look in the detail: naming every program the tools
        // were for ran the line past the pane's edge on a new machine (2026-10-26), and Settings
        // lists them anyway.
        var count = BookmarkService.Bookmarks.Count;
        var headline = $"Set up from this computer: {count} {(count == 1 ? "bookmark" : "bookmarks")}"
            + found.Count switch
            {
                0 => ".",
                1 => $" and tools for {found[0]}.",
                _ => $" and tools for {found.Count} programs.",
            };
        var detail = (fromExplorer > 0 ? $"{fromExplorer} of them are Explorer's Quick access pins, in their own group. " : string.Empty)
            + "Settings has the tools, the colours and the keys.";

        var answer = await ActiveTab.AskAsync(
            new ChoiceRequest(
                headline,
                [
                    new DecisionOption("ok", "OK", IsDefault: true, IsCancel: true),
                    new DecisionOption("bookmarks", "Show bookmarks"),
                    new DecisionOption("settings", "Settings…"),
                ],
                detail,
                TakesFocus: false),
            CancellationToken.None);

        if (answer == "bookmarks")
            await ActiveTab.NavigateAsync(Core.Fs.AboutPages.Bookmarks);
        else if (answer == "settings")
            SettingsRequested?.Invoke(this, null);
    }

    /// <summary>
    /// A program installed since the last look brings its tools along, and says so once, in the
    /// pane and without taking the keyboard (SETTINGS_PLAN.md §4): a default that changes the menus
    /// is never invisible (§1.3). Nothing to answer; Settings is there for switching them off.
    /// </summary>
    private async Task AnnouncePacksAsync(IReadOnlyList<ToolPack> arrived)
    {
        var settings = _services.GetService<IConfigStore<SettingsFile>>()?.Current;
        var on = arrived.Where(p => settings is null || p.IsOn(settings)).ToList();
        if (on.Count == 0)
            return;

        var tools = on.SelectMany(p => p.Sample).Select(t => t.Title).ToList();
        var answer = await ActiveTab.AskAsync(
            new ChoiceRequest(
                $"{string.Join(" and ", on.Select(p => p.Title))} found: {string.Join(", ", tools)} "
                + (tools.Count == 1 ? "is" : "are") + " in the menus now.",
                [
                    new DecisionOption("ok", "OK", IsDefault: true, IsCancel: true),
                    new DecisionOption("settings", "Settings…"),
                ],
                "Each program's tools can be switched off in Settings, under Tools.",
                TakesFocus: false),
            CancellationToken.None);

        if (answer == "settings")
            SettingsRequested?.Invoke(this, SettingsViewModel.Tools);
    }

    /// <summary>
    /// A config file with a mistake in it is said out loud, in the pane, with the line to look at.
    /// </summary>
    /// <remarks>
    /// It used to reach only the log. The file had been edited by hand — that is what these files
    /// are for (§7) — so the person who needs to know is the one looking at the window, and a tools
    /// menu that is silently missing half its entries reads as a bug in the application rather than
    /// as a comma in the file. Reported after the first navigation, which clears the notice bar.
    /// </remarks>
    private void WatchConfigFiles()
    {
        var files = _services.GetServices<Core.Config.IConfigFile>().ToList();

        var broken = files.Select(f => f.Problem).OfType<Core.Config.ConfigProblem>().ToList();
        if (broken.Count > 0)
            ActiveTab.Report(Describe(broken));

        foreach (var file in files)
            file.ProblemFound += (_, problem) => ActiveTab.Report(Describe([problem]));

        static Notice Describe(IReadOnlyList<Core.Config.ConfigProblem> problems) => new(
            string.Join(" ", problems.Select(p => p.Message)),
            NoticeSeverity.Warning,
            string.Join(Environment.NewLine, problems.Select(p => $"{p.FileName}: {p.Detail}")));
    }

    /// <summary>
    /// Opens what a later invocation asked for, in the window that is already up (§6.11).
    /// </summary>
    /// <remarks>
    /// In a new tab rather than in place. The command line arrives while somebody is in the middle
    /// of something — that is the whole situation it exists for — and replacing what they were
    /// looking at to show them a finished download would lose their place to tell them something
    /// they did not ask about just then.
    /// </remarks>
    public async Task OpenAsync(LaunchRequest request)
    {
        // The window takes its shape first and both panes list at once, for the same reasons as at
        // startup: a listing may be waiting on a password, and nothing else should wait with it.
        var navigations = new List<Task>(2);

        // A bare path goes where you are looking; --left and --right say which pane, and mean it
        // (BUGS.md 34: with the right pane in use, --left X opened X on the right).
        var first = request.Placed ? Left : ActivePane;
        var second = ReferenceEquals(first, Left) ? Right : Left;

        if (request.Left is { } left)
        {
            first.ActiveTab = first.AddTab();
            navigations.Add(first.ActiveTab.GoToAsync(left, request.Select));
            Activate(first);
        }

        if (request.Right is { } right)
        {
            IsDualPane = true;
            second.ActiveTab = second.AddTab();
            navigations.Add(second.ActiveTab.GoToAsync(right, null));
        }

        ApplyPanes(request);
        FocusRequested?.Invoke(this, "grid");
        await Task.WhenAll(navigations);

        // After, because navigating clears the pane's notice.
        if (request.Problems.Count > 0)
            Report(request);
    }

    private void ApplyPanes(LaunchRequest request)
    {
        if (request.Panes == PaneRequest.One)
            IsDualPane = false;
        else if (request.Panes == PaneRequest.Two)
            IsDualPane = true;
    }

    /// <summary>
    /// An argument we could not read is said out loud. A launcher passing the wrong thing is a bug
    /// in the launcher, and silently opening the home folder instead hides it from whoever can fix
    /// it.
    /// </summary>
    internal void Report(LaunchRequest request) =>
        ActiveTab.Report(new Notice(
            "Some of the command line could not be read.",
            NoticeSeverity.Warning,
            string.Join(" ", request.Problems)));

    public void Activate(PaneViewModel pane)
    {
        if (ReferenceEquals(ActivePane, pane))
            return;
        ActivePane.IsActive = false;
        ActivePane = pane;
        pane.IsActive = true;
        OnPropertyChanged(nameof(InactivePane));
        OnPropertyChanged(nameof(ActiveTab));
        RewireTitle();
    }

    /// <summary>
    /// Moves a tab to the other pane, alive, and the keyboard with it (PLAN.md §3, decided
    /// 2026-10-10). Its history, listing and any question it is asking go along: the tab is the
    /// question's host, and jobs hold the addresses they started with, so nothing is sent anywhere
    /// new. The last tab of a pane stays, and so does a tab with the other pane off screen.
    /// </summary>
    /// <param name="before">Where in the strip, as <see cref="PaneViewModel.Adopt"/>; the end if null.</param>
    public Task MoveTabToPane(TabViewModel tab, PaneViewModel to, int? before = null)
    {
        var from = ReferenceEquals(to, Left) ? Right : Left;
        if (!from.Tabs.Contains(tab))
            return Task.CompletedTask;

        if (!IsDualPane)
        {
            tab.Report(new Notice(
                "There is no other pane on screen to move the tab to.", NoticeSeverity.Warning,
                "Ctrl+2 shows both panes."));
            return Task.CompletedTask;
        }

        if (from.Tabs.Count <= 1)
        {
            tab.Report(new Notice(
                "The last tab stays in its pane.", NoticeSeverity.Warning,
                "Ctrl+T opens another tab here first."));
            return Task.CompletedTask;
        }

        // The line being typed is the pane's, written for the folder that is leaving. Left open it
        // would run in whatever the pane shows next, so it goes; one is seldom open for long, and
        // nobody arranges tabs mid-command (the user, 2026-10-10). The same goes for a line open in
        // the pane it arrives in: the folder it was written for goes behind the tab coming in.
        if (ReferenceEquals(from.ActiveTab, tab))
            from.ShowCommandLine = false;
        to.ShowCommandLine = false;

        from.Release(tab);
        to.Adopt(tab, before);
        Activate(to);
        // Its question, if it is asking one, takes the keyboard: PaneView.FocusGrid sees to that.
        FocusRequested?.Invoke(this, "grid");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs a typed line in the active pane's directory. Unlike a tool, a command line is meant to
    /// be shell syntax, so this one genuinely goes through cmd — and it reuses the tool runner, so
    /// the output lands in the pane and the activity log records it like everything else.
    /// </summary>
    /// <param name="shell">The local shell the line was written for; the tab's, as its prompt says, if not given.</param>
    public async Task RunCommandLineAsync(string line, CommandShell? shell = null)
    {
        var tab = ActiveTab;
        var remote = _services.GetRequiredService<IRemoteCommandService>();

        // Read before anything is awaited: the line closing goes back to the configured shell, and
        // a line typed at a pwsh prompt must not run in bash because a question came first.
        var local = shell ?? tab.LocalShell;

        // As typed, before anything is filled in, so Up brings back the line and not an answer
        // given to one of its ${prompt:…}s.
        tab.RememberCommand(line, local);

        // A cd lasts only for its own line, so on its own it would run, change nothing and vanish
        // (COMMAND_LINE_PLAN.md E). Said instead, and nothing run.
        if (LoneCd().IsMatch(line))
        {
            tab.ShowDone("cd only lasts for its own line. Ctrl+L goes somewhere.");
            return;
        }

        // A command line acts on where you are, and where you are may be a server. Sending it to
        // cmd.exe while the pane shows a remote directory would run it against a path that does
        // not exist on this machine. Where, and with which rows, is read once, here: questions
        // follow, the pane can move while they are open, and a line asked about here must not run
        // in whatever folder, or on whatever server, it shows by then.
        var context = (PaneCommandContext)CreateContext();

        // ${selection} and ${name} come from the rows, and remembered ones may not be what is there.
        if (context.TakenUnconfirmed)
        {
            if (await ConfirmedFirstAsync("the command line", context) is not { } confirmed)
                return;
            context = confirmed;
        }

        var here = tab.Uri;
        var onServer = here is { } at && remote.CanRun(at);
        var shells = _services.GetRequiredService<CommandShells>();

        // A flattened tree or a search runs in the folder it is a view of. Somewhere with no folder
        // on this machine (an archive, a from: listing of a server, a page) runs nothing: such a line
        // used to start wherever the application had, and `rm -rf *` there is someone else's folder.
        if (!onServer && tab.Location?.Anchor is not { IsLocal: true })
        {
            tab.ShowOutput(
                line,
                "A command line runs in a folder, and there is none here on this machine. Go to a folder first.",
                failed: true);
            return;
        }

        // On a server, its account's own shell, asked once: quoting a line for bash and handing it
        // to a Windows server's cmd.exe would run something other than what was typed.
        if (onServer && !(await shells.AskAsync(remote, here!, CancellationToken.None)).IsPosix)
        {
            tab.ShowOutput(
                $"{here!.Authority}: {line}",
                "This server's shell is not a POSIX one (a Windows server's cmd.exe or PowerShell, by the look of it). " +
                "Running lines there is not supported yet; a terminal over ssh is the way for now.",
                failed: true);
            return;
        }

        // The same placeholders tools use, so ${selection} means the same thing at the prompt (SS6.6)
        // -- but quoted for the shell that will read the line, so a name with a space in it is still
        // one word there.
        var target = new ToolTarget(
            context.Directory, context.Selection, context.Focused, context.OtherPaneDirectory, context.Repository);
        // A trailing & sends the line to the job strip at once. Taken off the line: left on, bash
        // would put the command in its own background and exit, and the job would end at once.
        var background = false;
        if (line.TrimEnd() is var trimmed && trimmed.EndsWith('&') && !trimmed.EndsWith("&&"))
        {
            line = trimmed[..^1].TrimEnd();
            background = true;
        }

        var expanded = await ToolPlaceholders.ExpandOneAsync(
            line, target, context.Decisions, shell: onServer ? ShellQuoting.Posix : local.Quoting);
        if (expanded is null)
            return; // a ${prompt:...} the user cancelled

        // vim, top or a bare python want a terminal; run here they would hang or quit at once. The
        // line is offered to a real one instead (COMMAND_LINE_PLAN.md J).
        if (TerminalLauncher.NeedsTerminal(expanded) is { } program)
        {
            var open = await tab.AskAsync(new ChoiceRequest(
                $"{program} needs a terminal",
                [
                    new DecisionOption("open", "Open in Windows Terminal", IsDefault: true),
                    new DecisionOption("cancel", "Cancel", IsCancel: true),
                ],
                "The command line runs a line and shows what it printed; this one wants to draw and read keys."),
                CancellationToken.None);
            if (open != "open")
                return;
            if (onServer)
                TerminalLauncher.OpenRemote(here!, posix: true, expanded);
            else
                TerminalLauncher.OpenLocal(context.Directory.Anchor.ToLocalPath(), local, expanded);
            return;
        }

        if (onServer)
        {
            await RunRemoteAsync(remote, tab, here!, expanded, background);
            return;
        }

        // In the shell settings.json names (Git Bash by default when installed, else cmd.exe), or
        // the one Ctrl+E stepped to.
        var tool = local.ToolFor(expanded);
        if (background)
            tool.BackgroundAfter = TimeSpan.Zero;

        await _services.GetRequiredService<IToolRunner>()
            .RunAsync(tool, context, CancellationToken.None);
    }

    /// <summary>A line that is a cd and nothing else: no operator that could make it part of more.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*cd(\s+[^;&|]*)?$")]
    private static partial System.Text.RegularExpressions.Regex LoneCd();

    private async Task RunRemoteAsync(
        IRemoteCommandService remote, TabViewModel tab, FsUri directory, string line, bool background)
    {
        // A job from the start, as a local line is: answered in place if it ends within the
        // patience, moved to the strip if it doesn't (or at once, for a line ending in &), so a
        // long one never holds the prompt and never has to be started again (COMMAND_LINE_PLAN.md F).
        var job = remote.Start(directory, line);
        var running = Task.Run(job.RunAsync);

        if (!background && await Task.WhenAny(running, Task.Delay(CommandShells.BackgroundAfter)) == running)
        {
            var text = job.Output.Text.TrimEnd();
            if (job.State == JobState.Completed && text.Length == 0)
                tab.ShowDone($"✓ {line}"); // nothing to say, nothing to dismiss
            else if (job.ExitStatus is null && job.State == JobState.Failed)
                tab.Report(new Notice($"Could not run that on {directory.Authority}.", NoticeSeverity.Error, job.Error));
            else
                tab.ShowOutput(
                    job.State == JobState.Completed ? line : $"{line} — exit status {job.ExitStatus}",
                    text.Length > 0 ? text : "(no output)",
                    job.State != JobState.Completed);
        }
        else
        {
            // The strip's row says it, and opens its output; a panel too was the same thing twice.
            _jobs.Add(job);
            await running;
        }

        // The command may well have changed the directory we are looking at, and a remote
        // location has no watcher to notice.
        if (tab.Uri == directory)
            await tab.RefreshAsync();
    }

    public ICommandContext CreateContext() =>
        new PaneCommandContext(ActivePane, InactivePane, _services, otherVisible: IsDualPane);

    /// <summary>
    /// A context standing on one particular entry rather than on the selection: the ".." row, whose
    /// menu is about the parent folder and which the selection never contains.
    /// </summary>
    public ICommandContext CreateContext(FsEntry standingOn) =>
        new PaneCommandContext(ActivePane, InactivePane, _services, standingOn, IsDualPane);

    /// <summary>The icon a location is drawn with — the same one its bookmark shows (§6.18).</summary>
    public System.Windows.Media.ImageSource? IconFor(FsUri location) => _icons.GetLocationIcon(location);

    public Task ExecuteAsync(CommandDefinition command) => ExecuteAsync(command, CreateContext());

    /// <summary>Runs a command against a context already built, e.g. the one its menu was built from.</summary>
    public async Task ExecuteAsync(CommandDefinition command, ICommandContext context)
    {
        if (!command.IsApplicable(context))
            return;

        if (context is PaneCommandContext pane && (pane.TakenUnconfirmed || pane.Tab.IsUnconfirmed)
            && !SafeOnRememberedRows(command))
        {
            if (await ConfirmedFirstAsync(command.Title, pane) is not { } confirmed || !command.IsApplicable(confirmed))
                return;
            context = confirmed;
        }

        // What a friction note says led up to it; not the note itself, and not opening the palette,
        // which every note made from the palette would otherwise end with.
        if (command.Id is not (NoteFrictionId or PaletteId))
            FrictionNotes.Ran(command.Title);

        try
        {
            await command.ExecuteAsync(context, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Logged as well: said only in the pane, a Compress cut off by a lost connection left
            // nothing to read afterwards (2026-11-03).
            Serilog.Log.Warning(ex, "{Command} did not complete", command.Id);
            ActiveTab.Report(new Notice($"{command.Title} did not complete.", NoticeSeverity.Error, ex.Message));
        }

        // A command on rows may have taken away the one the keyboard was on (a delete, a move out
        // of a list): the window puts it back on the list if it was left nowhere, and only then.
        if ((command.Scope & (CommandScope.File | CommandScope.Directory | CommandScope.Multiple)) != 0)
            FocusRequested?.Invoke(this, "grid-if-lost");
    }

    /// <summary>
    /// What may run on rows recalled from <see cref="ListingMemory"/> before the server has
    /// confirmed them: going somewhere, looking, marking, and taking a name or an address. Nothing
    /// that writes, and nothing a tool might write with. Listed this way round so that a command
    /// added later waits unless someone decides it need not.
    /// </summary>
    private static readonly HashSet<string> SafeOnRemembered = new(StringComparer.Ordinal)
    {
        "dc.open", "dc.openInNewTab", "dc.openInOtherPane", "dc.up", "dc.back", "dc.forward",
        "dc.refresh", "dc.preview", "dc.search", "dc.flatten", "dc.bookmark",
        "dc.mark", "dc.markWithSpace", "dc.selectAll", "dc.selectNone", "dc.invertSelection",
        "dc.toggleHidden", "dc.toggleDirectoriesFirst",
        "dc.copyName", "dc.copyPath", "dc.copyWebLink", "dc.locationMenu", "dc.revealParent",
    };

    private static bool SafeOnRememberedRows(CommandDefinition command) =>
        command.Subject == CommandSubject.Window || SafeOnRemembered.Contains(command.Id);

    /// <summary>
    /// Waits for the listing under way to confirm the rows a command was given, and gives the
    /// command back its context as it is then, or null with the reason said.
    /// </summary>
    /// <remarks>
    /// §1.6: a remembered row may be a file that has since gone, or been replaced by another of the
    /// same name, or a folder now. So the command acts only on what the server has just listed, and
    /// only if that is what it was given: the same files, each the same kind, size and date, the same
    /// cursor row, and the same other pane, which may have moved while it waited. Anything changed
    /// is not guessed at, it is handed back.
    /// </remarks>
    private async Task<PaneCommandContext?> ConfirmedFirstAsync(string title, PaneCommandContext context)
    {
        var tab = context.Tab;
        tab.ShowDone($"Checking this folder before {title}…");
        if (!await tab.ConfirmedAsync().ConfigureAwait(true))
        {
            tab.Report(new Notice(
                $"{title} was not done: these rows are as last listed and could not be checked.",
                NoticeSeverity.Info,
                "Ctrl+R lists the folder again."));
            return null;
        }

        var renewed = context.Renewed();
        if (!ReferenceEquals(renewed.Tab, tab) || !renewed.SameAs(context))
        {
            tab.Report(new Notice(
                $"{title} was not done: something it was about changed while this folder was checked.",
                NoticeSeverity.Info,
                "Look again and try again."));
            return null;
        }

        tab.ShowDone(string.Empty);
        return renewed;
    }

    /// <summary>
    /// Takes the highlighted command from the palette and runs it.
    /// </summary>
    /// <remarks>
    /// The palette closes <em>first</em>. Half of what is in it raises an inline prompt in the pane
    /// — rename, search, bookmark — and running it with the palette still on screen would put the
    /// question behind the thing that asked it.
    /// </remarks>
    public async Task RunFromPaletteAsync()
    {
        var command = Palette.Take();
        ClosePalette();

        if (command is not null)
            await ExecuteAsync(command);
    }

    /// <summary>Dismisses the palette and hands the keyboard back to the listing.</summary>
    public void ClosePalette()
    {
        if (!Palette.IsOpen)
            return;

        Palette.Close();
        FocusRequested?.Invoke(this, "grid");
    }

    public Task ExecuteAsync(string commandId) =>
        Commands.Find(commandId) is { } command ? ExecuteAsync(command) : Task.CompletedTask;

    /// <summary>
    /// The middle button on a row: the command settings.json chooses, standing on that row alone,
    /// as a browser's middle click is about the link under the pointer and not the page's selection.
    /// </summary>
    public Task MiddleClickAsync(FsEntry row)
    {
        var settings = _services.GetService<IConfigStore<SettingsFile>>()?.Current ?? new SettingsFile();
        return Commands.Find(SettingsCatalog.MiddleClickCommand(settings)) is { } command
            ? ExecuteAsync(command, CreateContext(row))
            : Task.CompletedTask;
    }

    private static (string? Group, string Name) SplitGroup(string typed)
    {
        var slash = typed.LastIndexOf('/');
        if (slash <= 0 || slash == typed.Length - 1)
            return (null, typed);
        return (typed[..slash].Trim(), typed[(slash + 1)..].Trim());
    }

    private void RefreshBookmarks()
    {
        Bookmarks.Clear();
        foreach (var bookmark in BookmarkService.Bookmarks)
        {
            // A bookmark whose URI no longer parses is skipped rather than crashing the popup;
            // the file is hand-editable, so malformed entries are expected.
            if (FsUri.TryParse(bookmark.Uri, out var uri, out _))
                Bookmarks.Add(new BookmarkItemViewModel(bookmark, uri, _icons, _services.GetService<IGitService>()));
        }
    }

    private void UpdateLegend()
    {
        Legend.Clear();

        if (HeldModifiers == KeyModifiers.None)
        {
            // At rest it is the classic function-key row. Only populated keys are shown: an honest
            // short row beats twelve slots of which ten are blank.
            // Only what applies here: in an archive or a script location F2 Rename, F6 Move and F7
            // MkDir are keys that would do nothing, and a legend listing them is lying about the place.
            var here = LegendContext();
            var slots = Commands.Legend(KeyModifiers.None, at: ActiveTab?.Uri);
            for (var i = 0; i < slots.Count; i++)
            {
                if (slots[i] is { } command && command.InLegend && (here is null || command.AppliesAt(here)))
                    Legend.Add(new LegendSlot($"F{i + 1}", command.Legend, command.Id));
            }

            return;
        }

        // Hold a modifier and the row becomes a view of that whole layer of the keymap — Ctrl+T,
        // Ctrl+S and the rest, not just function keys. That is what makes it teach the app. Kind by
        // kind, so the tab keys read as one thing and the sorts as another; the modifier is said
        // once at the head of the row (LegendBar) rather than in front of every key.
        var context = LegendContext();
        var layer = Commands.BoundWith(HeldModifiers, ActiveTab?.Uri)
            .Where(pair => pair.Command.InLegend && (context is null || pair.Command.AppliesAt(context)))
            .Select((pair, index) => (pair.Stroke, pair.Command, Index: index))
            // The function keys first, and on a line of their own: that row is where the eye
            // already is, since it is all the legend shows at rest. Ctrl+F3 sits where F3 did.
            .OrderBy(pair => pair.Stroke.FunctionKeyNumber ?? int.MaxValue)
            .ThenBy(pair => LegendRank(pair.Command.Group))
            // Within a kind, in the order the commands were declared, which is the order they make
            // sense in: New tab, Close tab, Next, Previous, not Enter, PageDown, PageUp, T, W.
            .ThenBy(pair => DeclaredAt(pair.Command))
            .ThenBy(pair => pair.Index);
        var afterFunctionKeys = false;
        foreach (var (stroke, command, _) in layer)
        {
            var isFunctionKey = stroke.FunctionKeyNumber is not null;
            Legend.Add(new LegendSlot(
                LegendKey(stroke.Key), command.Legend, command.Id,
                isFunctionKey ? "F-keys" : command.Group,
                StartsLine: afterFunctionKeys && !isFunctionKey));
            afterFunctionKeys = isFunctionKey;
        }
    }

    private int DeclaredAt(CommandDefinition command)
    {
        var all = Commands.All;
        for (var i = 0; i < all.Count; i++)
        {
            if (ReferenceEquals(all[i], command))
                return i;
        }

        return int.MaxValue;
    }

    /// <summary>The key as a keycap says it, where the legend's name for it is longer than it needs to be.</summary>
    private static string LegendKey(string key) => key switch
    {
        "PageDown" => "PgDn",
        "PageUp" => "PgUp",
        _ => key,
    };

    /// <summary>
    /// The order kinds appear in under a held modifier: where you are and where you go first, then
    /// what you see, then what you do to it. Tools, whose groups are the user's, follow in the order
    /// they were declared.
    /// </summary>
    private static readonly string[] LegendKinds =
        ["Navigate", "Tabs", "View", "Sort", "Selection", "Edit", "Clipboard", "File", "Collections"];

    private static int LegendRank(string? group) =>
        Array.IndexOf(LegendKinds, group) is var rank and >= 0 ? rank : LegendKinds.Length;

    /// <summary>
    /// The held modifiers, said once at the head of the legend: "Ctrl+", "Ctrl+Shift+". The trailing
    /// plus is what makes it read as the start of every key after it.
    /// </summary>
    public string? HeldModifiersLabel =>
        HeldModifiers == KeyModifiers.None ? null : new KeyStroke(string.Empty, HeldModifiers).ToString();

    /// <summary>The active pane's context, once there is a location to ask about; null before that.</summary>
    private ICommandContext? LegendContext() =>
        Left is null || Right is null || ActiveTab?.Uri is null ? null : CreateContext();

}

/// <summary>
/// What a command acts on, read once when the command starts (or its menu opens) and fixed from
/// then on. It used to be read live on every access, and questions do not block the panes: a
/// command that asked a name and then read the selection grouped whatever was selected by the
/// time Enter was pressed, in whatever folder the pane had moved to meanwhile.
/// </summary>
internal sealed class PaneCommandContext : ICommandContext
{
    private readonly PaneViewModel _pane;
    private readonly PaneViewModel _other;
    private readonly FsEntry? _standingOn;
    private readonly bool _otherVisible;
    private readonly IServiceProvider _services;
    private readonly FsUri? _directory;

    /// <summary>
    /// The same context taken again: the pane's selection and location as they are now. A context
    /// standing on one entry stands on it still, so it is only renewed as far as the rest goes.
    /// </summary>
    public PaneCommandContext Renewed() => new(_pane, _other, _services, _standingOn, _otherVisible);

    /// <summary>
    /// The rows this was taken from were remembered ones (<see cref="TabViewModel.IsUnconfirmed"/>).
    /// Kept, not read from the tab when the command runs: a menu opened over remembered rows can be
    /// answered after the fresh ones arrived, and its entries would still be the remembered ones.
    /// </summary>
    public bool TakenUnconfirmed { get; }

    /// <summary>
    /// Whether everything a command reads that remembered rows could have got wrong is the same in
    /// both: where, where to, and each file it acts on, as the same kind, size and date. Compared as
    /// values rather than as joined text, which a name with a newline in it could forge.
    /// </summary>
    public bool SameAs(PaneCommandContext other) =>
        _directory == other._directory
        && OtherPaneDirectory == other.OtherPaneDirectory
        && Of(Focused) == Of(other.Focused)
        && Selection.Select(Of).ToHashSet().SetEquals(other.Selection.Select(Of));

    private static (FsUri Uri, bool IsDirectory, long Size, DateTimeOffset? Modified)? Of(FsEntry? e) =>
        e is null ? null : (e.Uri, e.IsDirectory, e.Size, e.Modified);

    /// <param name="otherVisible">
    /// Whether the other pane is on screen. With one pane showing, F5 and F6 went to wherever the
    /// hidden pane had been left, which the user could not see.
    /// </param>
    public PaneCommandContext(
        PaneViewModel pane, PaneViewModel other, IServiceProvider services, FsEntry? standingOn = null,
        bool otherVisible = true)
    {
        _pane = pane;
        _other = other;
        _standingOn = standingOn;
        _otherVisible = otherVisible;
        _services = services;
        Tab = pane.ActiveTab;
        TakenUnconfirmed = Tab.IsUnconfirmed;
        // Where the tab is, or, while its first listing is still on its way, where it is going: a
        // restored SFTP pane waiting for its password has a place in its history before it has rows.
        _directory = Tab.Location;
        Selection = standingOn is null ? Tab.SelectedEntries : [standingOn];
        Focused = standingOn ?? Tab.FocusedRow?.Entry;
        OtherPaneDirectory = otherVisible ? other.ActiveTab.Uri : null;
        Repository = Tab.RepositoryWithConflicts();
    }

    public TabViewModel Tab { get; }

    public PaneViewModel Pane => _pane;

    // Ctrl+T pressed with no location yet crashed the app on 2026-10-01 when this threw at once.
    public FsUri Directory => _directory ?? throw new InvalidOperationException("The tab has no location yet.");

    public IReadOnlyList<FsEntry> Selection { get; }

    public FsEntry? Focused { get; }

    public FsUri? OtherPaneDirectory { get; }

    /// <summary>Free: the branch decoration already had to work this out to draw the chip.</summary>
    public RepositoryInfo? Repository { get; }

    public IDecisionHost Decisions => Tab;

    public IOutputHost Output => Tab;

    public IServiceProvider Services => _services;

    // Not for a tab closed while the command ran: refreshing or navigating it started a listing
    // and a watcher on a tab nobody can see, and nothing ever stopped them. A tab moved to the
    // other pane meanwhile is still open, and still the one the command was for.
    private bool TabIsOpen => !Tab.IsClosed;

    public Task RefreshAsync() => OnUi(() => TabIsOpen ? Tab.RefreshAsync() : Task.CompletedTask);

    /// <summary>On the UI thread, whichever thread the delete is on: the rows are the window's.</summary>
    public IDisposable MarkLeaving(IReadOnlyCollection<FsUri> items)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            return Tab.MarkLeaving(items);

        var mark = dispatcher.Invoke(() => Tab.MarkLeaving(items));
        return new OnUiThread(mark, dispatcher);
    }

    private sealed class OnUiThread(IDisposable inner, System.Windows.Threading.Dispatcher dispatcher) : IDisposable
    {
        public void Dispose()
        {
            if (dispatcher.CheckAccess())
                inner.Dispose();
            else
                dispatcher.Invoke(inner.Dispose);
        }
    }

    public Task NavigateAsync(FsUri uri, string? selectName = null) =>
        OnUi(() => TabIsOpen ? Tab.NavigateAsync(uri, selectName) : Task.CompletedTask);

    public Task GoUpAsync() => OnUi(() => TabIsOpen ? Tab.GoUpAsync() : Task.CompletedTask);

    public Task OpenInNewTabsAsync(IReadOnlyList<FsUri> locations) => OnUi(() => _pane.OpenInNewTabsAsync(locations));

    // Commands await with ConfigureAwait(false), so by the time they refresh they may be on a pool
    // thread: Compress on a WebDAV folder made its zip and then said "did not complete", because
    // the listing it refreshed touched the rows from there (2026-10-14). Locally the awaits had
    // happened to finish on the UI thread.
    private static Task OnUi(Func<Task> work)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.CheckAccess() ? work() : dispatcher.InvokeAsync(work).Task.Unwrap();
    }
}
