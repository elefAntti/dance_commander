using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DanceCommander.App.Commands;
using DanceCommander.App.Services;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Config;
using DanceCommander.Core.Decorations;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Listing;
using DanceCommander.Core.Matching;
using DanceCommander.Core.Navigation;
using DanceCommander.Core.Operations;
using DanceCommander.Core.Watching;
using Microsoft.Extensions.Logging;
using DanceCommander.Providers.Archive;
using DanceCommander.Providers.Local;

namespace DanceCommander.App.ViewModels;

/// <summary>
/// One location, with its own history, view state and live watch. A pane is a stack of these.
/// </summary>
public sealed partial class TabViewModel : ObservableObject, IDecisionHost, IOutputHost, ILaunchHost, IDisposable
{
    private readonly IListingSourceFactory _listings;
    private readonly IIconService _icons;
    private readonly IViewStateStore _viewState;
    private readonly IHistoryService _history;
    private readonly IReadOnlyList<IRowDecorator> _decorators;
    private readonly IGitService _git;
    private readonly IGitStatusService _gitStatus;
    private readonly IProviderRegistry _providers;
    private readonly ILogger<TabViewModel> _log;
    private readonly IFileOperationService? _operations;
    private FileSystemWatcher? _headWatcher;
    private FileSystemWatcher? _refsWatcher;
    private readonly Dispatcher _dispatcher;

    private readonly Dictionary<string, FileRowViewModel> _byName = new(StringComparer.OrdinalIgnoreCase);
    private List<FileRowViewModel> _allRows = [];

    /// <summary>
    /// <see cref="_allRows"/> as shown without the quick filter: hidden ones out, in the listing's
    /// order. Kept so that a key typed into the filter only filters it, and does not sort again.
    /// Null when it has to be worked out afresh.
    /// </summary>
    private List<FileRowViewModel>? _ordered;

    private List<FileRowViewModel> _selection = [];

    /// <summary>
    /// What a Del has, struck through until it goes (<see cref="FileRowViewModel.IsLeaving"/>).
    /// Kept here, not only on the rows: a listing refreshed meanwhile makes new ones. Counted, as
    /// two deletes may share an item. Read off the UI thread while a listing is collected.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<FsUri, int> _leaving = new();

    /// <summary>Strikes these rows through until the returned handle is disposed.</summary>
    public IDisposable MarkLeaving(IReadOnlyCollection<FsUri> items)
    {
        foreach (var item in items)
            _leaving.AddOrUpdate(item, 1, (_, count) => count + 1);
        ShowLeaving(items);
        return new Unmark(this, items);
    }

    private void ShowLeaving(IReadOnlyCollection<FsUri> items)
    {
        var set = items.ToHashSet();
        foreach (var row in _allRows)
        {
            if (set.Contains(row.Target))
                row.IsLeaving = _leaving.ContainsKey(row.Target);
        }
    }

    private sealed class Unmark(TabViewModel tab, IReadOnlyCollection<FsUri> items) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
                return;
            _done = true;
            foreach (var item in items)
            {
                if (tab._leaving.AddOrUpdate(item, 0, (_, count) => count - 1) <= 0)
                    tab._leaving.TryRemove(item, out _);
            }
            tab.ShowLeaving(items);
        }
    }
    /// <summary>
    /// The selection by address, to put back when the rows are replaced. Not by name: a flat view or
    /// a search holds many files of one name, a server folder can hold "a.txt" and "A.txt", and
    /// rows restored by name came back as every one of them, which Del then acted on (2026-10-10).
    /// An address also stays in its folder, where names carried marks into the next one.
    /// </summary>
    private IReadOnlyList<FsUri> _selectedUris = [];

    private CancellationTokenSource? _listing;
    private bool _stopRequested;

    /// <summary>What the listing left out on the filter's behalf, e.g. folders it declined to walk.</summary>
    private string? _omission;

    /// <summary>True when Ctrl+H has to list again rather than re-filter (a pruned walk).</summary>
    private bool _rereadOnFilterChange;
    private IDirectoryWatcher? _watcher;
    private DispatcherTimer? _decorationDue;
    private int _hiddenCount;

    private readonly ListingMemory? _memory;
    private readonly ListingPrefetcher? _prefetcher;

    /// <summary>How long the keyboard cursor rests on a server folder before it is fetched ahead.</summary>
    private static readonly TimeSpan RestBeforeFetching = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The cursor is on <paramref name="row"/>: a server folder is fetched ahead, at once when it was
    /// clicked (a click is a choice, and a double-click's second half joins it), after a rest when
    /// the keyboard got there (arrows pass over far more folders than they open).
    /// </summary>
    private void LookAhead(FileRowViewModel? row)
    {
        if (_prefetcher is null)
            return;

        if (row is { IsDirectory: true } && Uri is { IsView: false })
        {
            var clicked = System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed;
            _prefetcher.Ahead(row.Entry.Uri, clicked ? TimeSpan.Zero : RestBeforeFetching);
        }
        else
        {
            _prefetcher.Cancel();
        }
    }

    /// <summary>
    /// The rows came from <see cref="ListingMemory"/> and the folder is being listed again: they
    /// say what was there when it was last listed, which may not be what is there now.
    /// </summary>
    [ObservableProperty]
    private bool _isUnconfirmed;

    /// <summary>When the rows on screen were listed, while <see cref="IsUnconfirmed"/>.</summary>
    private DateTime _recalledFrom;

    /// <summary>Settled true when the fresh listing replaces recalled rows, false when it does not.</summary>
    private TaskCompletionSource<bool>? _confirming;

    /// <summary>
    /// Whether the rows on screen are the folder as it is: at once when they were never recalled,
    /// otherwise when the listing under way arrives. False when it failed, was stopped, or the tab
    /// went somewhere else first.
    /// </summary>
    public Task<bool> ConfirmedAsync() =>
        !IsUnconfirmed ? Task.FromResult(true) : _confirming?.Task ?? Task.FromResult(false);

    private void Unconfirmed(DateTime listedAt)
    {
        _confirming?.TrySetResult(false);
        _confirming = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _recalledFrom = listedAt;
        IsUnconfirmed = true;
    }

    private void Confirmed(bool confirmed)
    {
        if (confirmed)
            IsUnconfirmed = false;
        _confirming?.TrySetResult(confirmed);
        _confirming = null;
        UpdateStatus();
    }

    partial void OnIsUnconfirmedChanged(bool value) => UpdateStatus();

    public TabViewModel(
        IListingSourceFactory listings,
        IIconService icons,
        IViewStateStore viewState,
        IHistoryService history,
        IEnumerable<IRowDecorator> decorators,
        IGitService git,
        IGitStatusService gitStatus,
        IProviderRegistry providers,
        ILogger<TabViewModel> log,
        IArrivalFeed? arrivals = null,
        CommandShells? shells = null,
        CommandHistory? commandHistory = null,
        IConfigStore<SettingsFile>? settings = null,
        ListingMemory? memory = null,
        ListingPrefetcher? prefetcher = null,
        IFileOperationService? operations = null)
    {
        _operations = operations;
        _memory = memory;
        _prefetcher = prefetcher;
        // Where a tab starts before anything is inherited or remembered (settings.json).
        if (settings is not null)
            Order = new ListingOrder(DirectoriesFirst: settings.Current.View.DirectoriesFirst);
        _commandHistory = commandHistory;
        _shells = shells;
        if (shells is not null)
            shells.Changed += OnShellsChanged;
        _listings = listings;
        _icons = icons;
        // The address bar asks for its icon once per place; if the shell refused it then, this is
        // the second chance (a folder icon failing early in a launch, 2026-10-01).
        _icons.Recovered += OnIconsRecovered;
        _viewState = viewState;
        _history = history;
        _decorators = decorators.ToList();
        _git = git;
        _gitStatus = gitStatus;
        _providers = providers;
        _log = log;
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _arrivals = arrivals;
        if (arrivals is not null)
            arrivals.Arrived += OnArrived;
    }

    public NavigationHistory History { get; } = new();

    partial void OnUriChanged(FsUri? value)
    {
        OnPropertyChanged(nameof(CommandPrompt));
        OnPropertyChanged(nameof(LocationIcon));
    }

    /// <summary>
    /// The icon at the left of the address bar: this place, drawn the way its bookmark would be
    /// (§6.18). A flattened tree or a search shows the folder it was made from, because that is
    /// the folder its menu is about and the folder a drag of it carries.
    /// </summary>
    public System.Windows.Media.ImageSource? LocationIcon =>
        Location is { } uri ? _icons.GetLocationIcon(uri.Anchor) : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffersRefresh))]
    private FsUri? _uri;

    [ObservableProperty]
    private string _addressText = string.Empty;

    /// <summary>
    /// The address bar has the keyboard and is being typed in. Set by the view. While it is, the tab
    /// leaves <see cref="AddressText"/> alone: a refresh landing mid-word (the watcher, a folder
    /// gone) wrote the current folder over what was being typed, and the rest of it went after it.
    /// The view puts the address back when the bar is left.
    /// </summary>
    public bool IsEditingAddress { get; set; }

    /// <summary>
    /// Back, Forward or Up was asked for while the address bar had the keyboard: by a toolbar
    /// button, which never takes it, its menu or a mouse button. The view leaves the bar, as
    /// those keys already do, so the bar says where the pane is going instead of keeping what was
    /// in it (2026-11-02). Not for the tab's own moves, a folder gone above all, which must not
    /// throw away what is being typed.
    /// </summary>
    public event EventHandler? LeavingAddress;

    public void LeaveAddress()
    {
        if (IsEditingAddress)
            LeavingAddress?.Invoke(this, EventArgs.Empty);
    }

    private void ShowAddress(string address)
    {
        if (!IsEditingAddress)
            AddressText = address;
    }

    [ObservableProperty]
    private string _title = "New tab";

    [ObservableProperty]
    private IReadOnlyList<FileRowViewModel> _rows = [];

    [ObservableProperty]
    private ColumnSet _columns = ColumnSet.Standard;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffersRefresh))]
    private bool _isBusy;

    /// <summary>
    /// How many rows the listing has found so far, while it is still finding them.
    /// </summary>
    /// <remarks>
    /// A walk of a whole tree has no total to divide by, so there is no honest percentage to show.
    /// A number that visibly climbs says more than a bar pretending to know how far along it is:
    /// it shows that work is happening, how fast, and roughly how much you are in for.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingText))]
    private int _loadedCount;

    /// <summary>
    /// The server this listing is still connecting to, such as "user@host", and null once it has
    /// something to read. A first listing on a server spends its wait on the handshake, and
    /// "0 found" during it said the folder was being read and was empty so far, which it was not.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingText))]
    private string? _connectingTo;

    /// <summary>What the loading bar says the wait is for.</summary>
    public string LoadingText => ConnectingTo is { } server
        ? $"Connecting to {server}…"
        : $"Reading… {LoadedCount:N0} found";

    /// <summary>
    /// True when the listing on screen is what a stopped walk had found, not the whole location.
    /// </summary>
    [ObservableProperty]
    private bool _isPartial;

    /// <summary>
    /// What was typed into the listing to narrow it. Empty means everything is showing.
    /// </summary>
    /// <remarks>
    /// This replaces the grid's type-ahead rather than sitting beside it, because it is strictly
    /// the better version of the same gesture: typing three letters used to move the cursor to one
    /// row, and now it shows you every row those letters match with the first one already
    /// selected — so Enter still opens what type-ahead would have found, and you can see what else
    /// was there.
    ///
    /// Deliberately not part of <see cref="ListingFilter"/>: that is remembered per location
    /// (§1.2/5), and a filter that was still in force when you came back to a folder tomorrow
    /// would be a folder that had quietly lost most of its files.
    /// </remarks>
    [ObservableProperty]
    private string _quickFilter = string.Empty;

    /// <summary>How many rows the quick filter is holding back. Zero when it is not in use.</summary>
    [ObservableProperty]
    private int _filteredOut;

    partial void OnQuickFilterChanged(string value)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        ApplyView(reorder: false);
        var handed = System.Diagnostics.Stopwatch.GetTimestamp();
        OnPropertyChanged(nameof(IsFiltering));

        // The cursor goes to the first match, so that Enter still opens what you were reaching for.
        // That is what makes this a replacement for type-ahead rather than a different feature
        // wearing its keys.
        if (Rows.FirstOrDefault() is { } first)
            SelectionRequested?.Invoke(this, first.Name);

        TimeFilter(started, handed, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    /// <summary>When <see cref="ApplyView"/> had the rows ready, before handing them to the grid.</summary>
    private long _arrangedAt;

    /// <summary>Keys typed into the filter so far, to tell a measurement that the next key got into.</summary>
    private int _filterKeys;

    /// <summary>
    /// A line in the log for a key that took long enough to feel, from the key to the grid laid out
    /// with what it left, split by who spent it. Quiet otherwise.
    /// </summary>
    /// <remarks>
    /// Measured at Loaded priority, which runs after the layout and render pass that the new rows
    /// queued. Keys are Input priority, higher than that, so a key typed meanwhile is handled first
    /// and its time lands in this one's last part; the line says when that happened.
    /// </remarks>
    private void TimeFilter(long started, long handed, long landed)
    {
        var shown = Rows.Count;
        var arranged = _arrangedAt;
        var key = ++_filterKeys;
        _dispatcher.InvokeAsync(() =>
        {
            var total = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            double Ms(long from, long to) => System.Diagnostics.Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
            if (Budgets.Verdict(("filter", total, Budgets.Filter)) is { } verdict)
                _log.LogInformation(
                    "Slow filter: {Total:N0} ms to lay out: {Arranged:N0} ms arranging {All:N0} rows to {Shown:N0}, " +
                    "{Handed:N0} ms handing them to the grid, {Landed:N0} ms putting the cursor on the first, " +
                    "{Rest:N0} ms laying out{Queued}, {Verdict}",
                    total, Ms(started, arranged), _ordered?.Count ?? 0, shown, Ms(arranged, handed), Ms(handed, landed),
                    System.Diagnostics.Stopwatch.GetElapsedTime(landed).TotalMilliseconds,
                    key == _filterKeys ? "" : " (with the next key in it)", verdict);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Types a character into the filter, or starts one.</summary>
    public void FilterBy(char character) => QuickFilter += character;

    /// <summary>Removes the last character, ending the filter when it empties.</summary>
    public void BackspaceFilter()
    {
        if (QuickFilter.Length > 0)
            QuickFilter = QuickFilter[..^1];
    }

    public bool IsFiltering => QuickFilter.Length > 0;

    /// <summary>Filtering, and nothing survived it.</summary>
    public bool HasNoMatches => IsFiltering && Rows.Count == 0;

    /// <summary>
    /// What an empty listing says instead of being a blank rectangle, which reads as "still loading"
    /// or "broken". The ".." row used to fill that space by accident. Null when there are rows, or
    /// when the quick filter has its own line to say it.
    /// </summary>
    public string? EmptyText =>
        Rows.Count > 0 || IsFiltering || Uri is null ? null
        : _allRows.Count > 0 ? "Everything here is hidden. Ctrl+H shows it."
        : Uri.Scheme switch
        {
            "search" or FsUri.EverywhereScheme => "Nothing found.",
            "flat" => "No files below this folder.",
            FsUri.FromScheme => "Nothing listed.",
            _ => "This folder is empty.",
        };

    public void ClearFilter() => QuickFilter = string.Empty;

    /// <summary>
    /// Whether to show the loading bar, which is not the same question as whether we are loading.
    /// </summary>
    /// <remarks>
    /// Ordinary navigation finishes in a few milliseconds. A bar that appeared for two frames on
    /// every keystroke would push the listing down and let it spring back, which reads as a glitch
    /// rather than as progress -- so nothing is shown until a load has proved itself slow.
    /// </remarks>
    [ObservableProperty]
    private bool _showLoading;

    /// <summary>How long a listing may take before it owes the user an explanation.</summary>
    private static readonly TimeSpan PatienceBeforeSaying = TimeSpan.FromMilliseconds(250);

    /// <summary>True for the tab its pane is currently showing.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>The file being previewed over this pane, or null when the listing is showing.</summary>
    [ObservableProperty]
    private PreviewViewModel? _preview;

    [ObservableProperty]
    private DecisionViewModel? _decision;

    [ObservableProperty]
    private Notice? _notice;

    /// <summary>Captured output from a tool, shown in the pane until dismissed.</summary>
    [ObservableProperty]
    private ToolOutputViewModel? _output;

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    /// <summary>True when this location reports changes; false means Ctrl+R is the only refresh.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffersRefresh))]
    private bool _isLive;

    /// <summary>
    /// The footer offers Ctrl+R where it says "live" elsewhere: a place that does not report its
    /// changes is the one place the key is needed, so that is where it is shown, and the legend
    /// does not spend a slot on it. Not while loading, when the watcher is not up yet either.
    /// </summary>
    public bool OffersRefresh => !IsLive && !IsBusy && Uri is not null;

    /// <summary>The branch of the repository this directory is in, if any (§6.3).</summary>
    [ObservableProperty]
    private string? _branch;

    /// <summary>
    /// The repository this location is in, as the location tokens see it (§6.6). Set alongside
    /// <see cref="Branch"/> from the same read, so the chip and the tokens can never disagree.
    /// </summary>
    public RepositoryInfo? Repository { get; private set; }

    // The key the status service files this repository's answers under.
    private string? _workTreeRoot;

    /// <summary>
    /// <see cref="Repository"/> with its conflicts as the rows' marks last showed them: a lookup,
    /// not a git run, so a menu opening does not wait on one (§1.5).
    /// </summary>
    public RepositoryInfo? RepositoryWithConflicts() =>
        Repository is { } repository && _workTreeRoot is { } root && _gitStatus.Last(root) is { } status
            ? repository with { Conflicts = status.Conflicts }
            : Repository;

    /// <summary>
    /// What the command line prompts with, and therefore where a typed command will run. A bare
    /// ">" means this machine; a remote location says so, because running a command against a
    /// path that only exists on a server should not look identical to running it here.
    /// </summary>
    /// <summary>
    /// Which shell a typed line goes to, said quietly at the start of the command line: "bash",
    /// "cmd", or "zsh on claude@host" once a server has said. Not the path, which the address bar
    /// already shows (it used to be repeated here in full, in accent blue, and cut off).
    /// </summary>
    public string CommandPrompt =>
        Uri is { } uri && !uri.IsLocal && !uri.IsArchiveEntry
            ? _shells?.Known(uri) is { Name: { } name } ? $"{name} on {uri.Authority}" : $"on {uri.Authority}"
            : LocalShell.Name;

    private readonly CommandShells? _shells;

    private bool OnServer => Uri is { } uri && !uri.IsLocal && !uri.IsArchiveEntry;

    /// <summary>A shell stepped to with Ctrl+E on the open line; null is the configured one.</summary>
    private CommandShell? _chosenShell;

    /// <summary>The shell a line typed here goes to while the pane is local.</summary>
    public CommandShell LocalShell => _chosenShell ?? _shells?.Local ?? CommandShells.Cmd;

    /// <summary>
    /// Steps the command line to the next shell installed here, round to the configured one again.
    /// False on a server, which has the one shell its account logs in to.
    /// </summary>
    public bool NextShell()
    {
        if (OnServer || _shells is null)
            return false;

        var choices = _shells.LocalChoices();
        var at = choices.ToList().FindIndex(shell => shell.Key == LocalShell.Key);
        var next = choices[(at + 1) % choices.Count];
        _chosenShell = next.Key == _shells.Local.Key ? null : next;
        OnPropertyChanged(nameof(CommandPrompt));
        return true;
    }

    /// <summary>The line closed: the next one starts in the configured shell again.</summary>
    public void UseConfiguredShell()
    {
        if (_chosenShell is null)
            return;
        _chosenShell = null;
        OnPropertyChanged(nameof(CommandPrompt));
    }

    private readonly CommandHistory? _commandHistory;

    /// <summary>
    /// Whom a line typed here goes to, for its history: the local shell ("bash"), or the server
    /// account ("claude@host"), since a line written for one rarely means anything to another.
    /// </summary>
    public string CommandHistoryKey => HistoryKeyFor(LocalShell);

    private string HistoryKeyFor(CommandShell local) =>
        Location is { } uri && !uri.IsLocal && !uri.IsArchiveEntry ? uri.Authority : local.Key;

    /// <summary>Earlier lines typed to the same shell, oldest first.</summary>
    public IReadOnlyList<string> CommandLines() => _commandHistory?.For(CommandHistoryKey) ?? [];

    /// <summary>Records a line as typed, before any placeholder in it was filled in.</summary>
    /// <param name="local">The local shell it ran in, which a Run may have taken before Ctrl+E moved on.</param>
    public void RememberCommand(string line, CommandShell local) => _commandHistory?.Add(HistoryKeyFor(local), line);

    /// <summary>"✓ touch foo" on the command line's row, for a moment (IOutputHost.ShowDone).</summary>
    [ObservableProperty]
    private string? _commandEcho;

    private DispatcherTimer? _echoTimer;

    public void ShowDone(string text) => _dispatcher.InvokeAsync(() =>
    {
        // A finished panel from an earlier line would sit beside it, answering a question nobody
        // just asked; one still following a running job stays.
        if (Output is { Job: null })
            Output = null;

        CommandEcho = text;
        _echoTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background,
            (_, _) => { _echoTimer!.Stop(); CommandEcho = null; }, _dispatcher);
        _echoTimer.Stop();
        _echoTimer.Start();
    });

    private void OnShellsChanged(object? sender, EventArgs e) =>
        _dispatcher.InvokeAsync(() => OnPropertyChanged(nameof(CommandPrompt)));

    /// <summary>
    /// What the archive we are inside actually turned out to be, e.g. "tar.gz" for a file
    /// named only ".gz". Null when this location is not inside an archive.
    /// </summary>
    [ObservableProperty]
    private string? _archiveLabel;

    public ListingOrder Order { get; private set; } = new();

    public ListingFilter Filter { get; private set; } = new();

    public string SortColumnId => Order.ColumnId;

    public bool SortDescending => Order.Descending;

    public event EventHandler? ColumnsChanged;

    public event EventHandler<string>? SelectionRequested;

    /// <summary>Asks the view to change the selection: the grid owns it, so the grid carries it out.</summary>
    public event EventHandler<SelectionOp>? SelectionOpRequested;

    public void RequestSelection(SelectionOp op) => SelectionOpRequested?.Invoke(this, op);

    /// <summary>Asks the view to re-select these names after the rows were replaced, as marks or as the cursor's row.</summary>
    public event EventHandler<SelectionRestore>? SelectionRestoreRequested;

    public IReadOnlyList<FsEntry> SelectedEntries =>
        _selection.Select(r => r.Entry).ToList();

    /// <summary>The row with the cursor: what Enter, F2 and F3 act on.</summary>
    public FileRowViewModel? FocusedRow { get; private set; }

    /// <summary>
    /// Whether the selection is marks the user made, rather than the cursor's row standing in for
    /// them. Told, never worked out: "one row selected, under the cursor" is both a single mark the
    /// cursor came back to and no marks at all, and reading it as the second lost the mark once the
    /// cursor moved on, and F5 or Del then acted on the cursor's row instead (2026-10-10).
    /// </summary>
    public bool SelectionIsMarks { get; private set; }

    /// <summary>
    /// Whether anything is marked. Without marks, the cursor's row is the selection, and moving
    /// the cursor moves it; marks stay where they are while the cursor moves.
    /// </summary>
    public bool HasMarks => SelectionIsMarks && _selection.Count > 0;

    // ---- navigation ---------------------------------------------------------------------

    public Task NavigateAsync(FsUri uri, string? selectName = null) =>
        NavigateCoreAsync(uri, selectName, recordHistory: true);

    /// <summary>
    /// A place someone gave, typed or on the command line, which may be a file: an archive opens
    /// as a folder, as Enter on it does, and anything else opens where it is with the cursor on it.
    /// Listing a file as a folder said only Windows' "The parameter is incorrect" (§4:
    /// <c>DanceCommander pack.zip</c>, and the same path typed in the address bar, 2026-10-15).
    /// </summary>
    public async Task GoToAsync(FsUri given, string? selectName = null)
    {
        if (given.IsLocal && !given.IsView)
        {
            // Asking can take an offline network drive's timeout, and the tab is usable meanwhile:
            // gone somewhere else or closed by the answer, it stays where it was put (review,
            // 2026-10-15).
            var asked = _navigations;
            var from = Uri;
            bool Superseded() => _navigations != asked || Uri != from || IsClosed;

            // Off the UI thread when the answer can be slow.
            var path = given.ToLocalPath();
            var isFile = CheapToCheck(given) ? File.Exists(path) : await Task.Run(() => File.Exists(path));
            if (Superseded())
                return;
            if (isFile)
            {
                var browsable = !ZipDocuments.IsDocument(given.Name) &&
                                await ArchiveDetector.IsBrowsableAsync(given, _providers, CancellationToken.None);
                if (Superseded())
                    return;
                if (browsable)
                {
                    await NavigateAsync(FsUri.ArchiveEntry(given, "/"));
                    return;
                }

                if (given.Parent is { } folder)
                {
                    await NavigateAsync(folder, given.Name);
                    return;
                }
            }
        }

        await NavigateAsync(given, selectName);
    }

    // ---- session ------------------------------------------------------------------------

    /// <summary>A tab put back from the last session and not shown yet: where it lists when it is.</summary>
    private (FsUri Uri, string? Cursor)? _deferred;

    private Task? _shown;

    /// <summary>"Starting Word…" while something opened from here is on its way (Launcher).</summary>
    [ObservableProperty]
    private string? _startingText;

    public void ShowStarting(string? text) => _dispatcher.InvokeAsync(() => StartingText = text);

    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The first time this tab has rows to show or a question to ask: what launch waits for. Not
    /// the end of its listing, which for a restored SFTP pane is whenever the user types the
    /// password, and launch used to wait for that (no warm-ups, no session saving, no shelf key).
    /// </summary>
    public Task Settled => _settled.Task;

    /// <summary>
    /// Where this tab is: restored and not yet shown, arrived, or on its way there (a connection
    /// waiting for its password, or one cancelled before it arrived). Null only for a tab that has
    /// been sent nowhere, which nothing should make: commands, menus and the address bar's icon all
    /// ask this, and a tab with no answer crashed the location menu (2026-10-01).
    /// </summary>
    public FsUri? Location => _deferred?.Uri ?? Uri ?? _goingTo ?? History.Current;

    private FsUri? _goingTo;

    /// <summary>
    /// Puts back a tab from the last session without listing it: that waits until it is first shown,
    /// so tabs behind the front one cost nothing at startup — no disk, no server, no password prompt.
    /// </summary>
    public void Restore(IReadOnlyList<FsUri> history, int position, string? cursor)
    {
        History.Restore(history, position);
        if (History.Current is not { } uri)
            return;

        _deferred = (uri, cursor);
        _shown = null;
        ShowAddress(uri.Display);
        Title = uri.Name.Length > 0 ? uri.Name : uri.Display;
        UpdateHistoryState();
    }

    /// <summary>Lists a restored tab, the first time it is shown; nothing for any other.</summary>
    public Task ShowAsync()
    {
        if (_deferred is not { } deferred)
            return _shown ?? Task.CompletedTask;

        _deferred = null;
        return _shown = OpenRestoredAsync(deferred.Uri, deferred.Cursor);
    }

    private async Task OpenRestoredAsync(FsUri saved, string? cursor)
    {
        // Off the UI thread only when the answer can be slow: a mapped drive that is offline takes
        // seconds to say so. Otherwise here, at once. The hop to a worker came back to a UI thread
        // that at launch is building the window, so a restored pane began listing only once that
        // was shown, ~150-250 ms after a fresh start's would have (DESIGN.md §1.5). Nothing that is
        // not a local folder is checked at all, so for an archive or a view it was pure delay.
        var place = CheapToCheck(saved)
            ? SessionPlaces.NearestExisting(saved, Directory.Exists)
            : await Task.Run(() => SessionPlaces.NearestExisting(saved, Directory.Exists));
        if (place == saved)
        {
            await NavigateAsync(saved, cursor);
            return;
        }

        // Said, not silently swapped: a folder gone since yesterday is worth knowing about, and
        // the parent is the clue to where it was.
        await NavigateAsync(place);
        Report(GoneNotice(saved));
    }

    /// <summary>
    /// By name only: two full paths made the notice longer than the pane (2026-10-26), and where it
    /// landed instead is already in the address bar (§1.3: nothing is stated twice).
    /// </summary>
    private static Notice GoneNotice(FsUri gone) =>
        new($"{gone.Name} is gone — showing the nearest folder above it.");

    /// <summary>
    /// The folder this tab showed has gone: the nearest one above it that still exists, said in a
    /// notice, as for a session restored to a folder deleted since.
    /// </summary>
    private async Task GoToNearestExistingAsync(FsUri gone)
    {
        if (Uri != gone || IsClosed)
            return;

        var place = await Task.Run(async () =>
        {
            // A folder renamed away and made again at once (mv and mkdir, a build, a checkout) is
            // back within a moment. Looked for once, straight away, it read as gone and the pane
            // went up for good (driven, 2026-10-28), so it is given a second to come back.
            for (var tries = 0; gone.IsLocal && tries < 10; tries++)
            {
                if (Directory.Exists(gone.ToLocalPath()))
                    return gone;
                await Task.Delay(100).ConfigureAwait(false);
            }
            return SessionPlaces.NearestExisting(gone, Directory.Exists);
        });
        if (Uri != gone)
            return;

        if (place == gone)
        {
            // Back already: deleted and made again (a script rebuilding it, a checkout). The failed
            // listing left its red "no longer exists" over the old rows, and the watcher stopped;
            // listing it again clears both. Not again within a few seconds: a failure of that
            // listing comes back here, and a folder that keeps vanishing must not loop.
            if (_recheckedGone is { } last && last.Uri == gone && Environment.TickCount64 - last.At < 5000)
                return;
            _recheckedGone = (gone, Environment.TickCount64);
            await RefreshAsync();
            return;
        }

        await NavigateAsync(place, selectName: gone.Name);
        Report(GoneNotice(gone));
    }

    /// <summary>
    /// A local folder asked for that isn't there: make it, or go to the nearest folder above it
    /// that is. A folder an app makes only once it needs one (an optional fonts folder, say) is
    /// one Enter from existing, rather than a trip to its parent and F7.
    /// </summary>
    /// <remarks>
    /// Asked, not done: a typo made into a folder is clutter. Going up is the default, because
    /// Enter on it changes nothing on disk; making it is the shared New folder operation, so it is
    /// undoable and in the activity log like any other.
    /// </remarks>
    private async Task OfferMissingAsync(FsUri missing, Exception ex)
    {
        var asked = _navigations;
        var from = Uri;
        bool Superseded() => _navigations != asked || Uri != from || IsClosed;

        // Off this thread when it can be slow: an offline network drive takes its timeout to say so.
        var nearest = CheapToCheck(missing)
            ? SessionPlaces.NearestExisting(missing, Directory.Exists)
            : await Task.Run(() => SessionPlaces.NearestExisting(missing, Directory.Exists));
        if (Superseded())
            return;

        // Nothing above it on its own drive or share exists either, so there is nothing to offer.
        // Nor for "foo\bar" typed without a drive: Windows reads it from the root of whatever drive
        // the process is on, which is not a place anyone asked for (review, 2026-10-29).
        if (nearest == missing || !Path.IsPathFullyQualified(missing.Path.Replace('/', '\\')))
        {
            Notice = Describe(ex, missing, shown: false);
            return;
        }

        var levels = new List<string>();
        for (var at = missing; at is not null && at != nearest; at = at.Parent)
            levels.Add(at.Name);
        levels.Reverse();

        // Made only from names New folder would take as they are. Its parser trims each name and
        // drops empty ones, so "a \b" would have made a\b; and a trailing dot or space is a typo
        // Windows strips everywhere else, which made here would be a folder Explorer cannot open.
        var creatable = _operations is not null && levels.All(name =>
            name == name.Trim() && !FsUri.HasNameWin32Trims(name) && BuiltInCommands.ValidateFolderPath(name) is null);

        var above = string.IsNullOrEmpty(nearest.Name) ? nearest.Display : nearest.Name;
        List<DecisionOption> options = [new("up", $"Go to {above}", IsDefault: true)];
        if (creatable)
            options.Add(new("create", levels.Count == 1 ? "Create it" : $"Create {levels.Count} folders"));
        options.Add(new("cancel", "Cancel", IsCancel: true));

        var answer = await AskAsync(
            new ChoiceRequest(
                $"{missing.Display} does not exist.",
                options,
                // More than one missing level is said, so making them is not a surprise.
                !creatable || levels.Count == 1 ? null : $"{string.Join('\\', levels)} would be made in {nearest.Display}."),
            CancellationToken.None);

        // Acted on even if the tab has been somewhere else since: the question stays up while the
        // user browses (§1.2/2), and an answer given to it is still what they asked for.
        if (IsClosed)
            return;

        if (answer == "up")
        {
            await NavigateAsync(nearest);
        }
        else if (answer == "create" && _operations is not null)
        {
            FsUri? created;
            try
            {
                created = await _operations.CreateFolderAsync(nearest, string.Join('/', levels), CancellationToken.None);
            }
            catch (Exception failed)
            {
                // Nobody awaits this: what isn't said here is lost.
                _log.LogError(failed, "Creating {Uri} failed", missing);
                Notice = new Notice($"Could not create {missing.Display}.", NoticeSeverity.Error, failed.Message);
                return;
            }

            if (created is not null)
                await NavigateAsync(created);
        }
    }

    /// <summary>The folder <see cref="GoToNearestExistingAsync"/> last listed again because it was back, and when.</summary>
    private (FsUri Uri, long At)? _recheckedGone;

    /// <summary>
    /// The place answered that it isn't there, rather than that it couldn't be asked: a local
    /// folder whose parent still exists (a stick pulled out says "not found" too), or a server's
    /// own "no such file".
    /// </summary>
    private static bool IsNotThere(Exception ex, FsUri uri) => ex switch
    {
        DirectoryNotFoundException when uri.IsLocal && !uri.IsView =>
            uri.Parent is { IsLocal: true } parent && Directory.Exists(parent.ToLocalPath()),
        FileNotFoundException when !uri.IsLocal && !uri.IsView => true,
        _ => false,
    };

    private static bool IsAtOrUnder(FsUri candidate, FsUri root)
    {
        for (var at = candidate; at is not null; at = at.Parent)
        {
            if (at == root)
                return true;
        }
        return false;
    }

    /// <summary>Not a local folder (nothing to check), one on a fixed disk, or no real path at all.</summary>
    private static bool CheapToCheck(FsUri uri)
    {
        if (!uri.IsLocal || uri.IsView)
            return true;

        // A network share can be slow to answer, a drive is asked its type, and anything else is
        // no path Windows can reach, so nothing about it can be slow: "abou:bookmarks", a typo
        // for about:, is a local path whose "root" is abou:, and handed to DriveInfo that ended
        // the application (2026-10-17). Its navigation says the address is wrong, as before.
        var root = Path.GetPathRoot(uri.ToLocalPath());
        if (root is null || root.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        if (root.Length < 2 || root[1] != ':' || !char.IsAsciiLetter(root[0]) || (root.Length > 2 && root[2] is not ('\\' or '/')))
            return true;

        // GetDriveType: answered from the drive letter, without touching a network drive.
        return new DriveInfo(root[..2]).DriveType is DriveType.Fixed;
    }

    /// <summary>What the session keeps of this tab, or null for one that has been nowhere.</summary>
    public TabSession? Snapshot()
    {
        if (History.Position < 0)
            return null;
        return TabSession.Of(History.Entries, History.Position, _deferred is { } deferred ? deferred.Cursor : FocusedRow?.Name);
    }

    public Task GoBackAsync() =>
        History.Back() is { } uri ? GoByHistoryAsync(uri) : Task.CompletedTask;

    public Task GoForwardAsync() =>
        History.Forward() is { } uri ? GoByHistoryAsync(uri) : Task.CompletedTask;

    /// <summary>One entry of the Back or Forward menu: all those steps at once, the entries between stay in the history.</summary>
    public Task GoToHistoryAsync(int index) =>
        History.GoTo(index) is { } uri ? GoByHistoryAsync(uri) : Task.CompletedTask;

    private Task GoByHistoryAsync(FsUri uri)
    {
        LeaveAddress();
        return NavigateCoreAsync(uri, null, recordHistory: false);
    }

    public Task RefreshAsync() =>
        Uri is null ? Task.CompletedTask : NavigateCoreAsync(Uri, null, recordHistory: false);

    /// <summary>
    /// Up, which from a drive root goes on to the computer page, with the cursor on that drive —
    /// rather than stopping, with no way to see what other drives there are.
    /// </summary>
    /// <remarks>
    /// From where the tab is going, while a listing is on its way: Backspace pressed several times
    /// in quick succession is several levels up. From where it is, every press after the first
    /// asked for the same parent again, restarting the listing it was waiting for.
    /// </remarks>
    public Task GoUpAsync()
    {
        var from = _listing is not null && _goingTo is { } going ? going : Location;
        if (from?.Up is not { } parent)
            return Task.CompletedTask;
        LeaveAddress();
        return NavigateAsync(parent, selectName: from.Name);
    }

    /// <summary>
    /// Abandons the listing in progress and keeps whatever it found (§6.1).
    /// </summary>
    /// <remarks>
    /// Flattening a home folder is a minute of walking, which is a long time to be unable to change
    /// your mind. Throwing the result away on the way out would make stopping cost as much as
    /// waiting, so a stopped walk commits what it has and the pane says the listing is partial.
    /// </remarks>
    public void StopLoading()
    {
        if (!IsBusy)
            return;

        _stopRequested = true;
        _listing?.Cancel();
    }

    private async Task NavigateCoreAsync(FsUri uri, string? selectName, bool recordHistory)
    {
        // Sent somewhere before it was ever shown: that is where it is now.
        _deferred = null;
        _goingTo = uri;
        OnPropertyChanged(nameof(LocationIcon));

        // A password or host-key question raised while listing here is asked here, not in
        // whichever pane happens to be active (DecisionScope).
        DecisionScope.Current = this;

        // Arriving in the parent of where we were puts the cursor on the folder we came out of —
        // whichever way we got here: "..", Up, Backspace, the address bar, or Back. Only Backspace
        // and Up used to ask for it, so Enter on ".." dropped you at the top of the parent with no
        // sign of where you had been. Leaving an archive lands on the archive, whose root is named
        // after it; leaving a view has nothing to point at, since the view is not in the folder.
        if (selectName is null && Uri is { IsView: false } previous && previous.Parent == uri)
            selectName = previous.Name;

        _navigations++;
        _listing?.Cancel();
        var cts = new CancellationTokenSource();
        _listing = cts;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var rowsAt = TimeSpan.Zero;
        var paintedAt = TimeSpan.Zero;
        var atLaunch = false;
        _stopRequested = false;

        SaveViewState();
        StopWatching();


        IsBusy = true;
        LoadedCount = 0;

        // Where we are going, from the moment we set off, the way a browser shows the address it is
        // loading. Only arriving used to set it, so a first listing on a server showed an empty
        // address for as long as the handshake and the password prompt took.
        ShowAddress(uri.Display);
        ConnectingTo = _providers.TryResolve(uri, out var connecting) && connecting is IConnectingProvider remote
            ? remote.ConnectingTo(uri)
            : null;
        IsPartial = false;
        Notice = null;
        QuickFilter = string.Empty;
        AnnounceIfSlow(cts);
        RememberedListing? recalled = null;
        var recalledAt = TimeSpan.Zero;

        try
        {
            // Read before listing, not after: a walk that prunes has to know whether hidden entries
            // are wanted before it decides which folders to open. Somewhere we have been before
            // comes back the way we left it; somewhere new inherits how this tab is currently set
            // up, so a preference carries as you browse (§1.2/5).
            var remembered = _viewState.Get(uri);
            if (remembered is not null)
            {
                Order = remembered.Order;
                Filter = remembered.Filter;
            }
            _chosenHere = remembered?.Chosen ?? false;

            var source = _listings.Create(uri, Filter);

            // A server folder visited lately: its rows at once, as they were, while it is listed
            // again. Not for the place already on screen (Ctrl+R, the refresh after a command),
            // whose rows are newer than anything remembered.
            var memorable = _memory is not null && source is DirectoryListingSource && ListingMemory.Covers(uri, connecting);

            // Taken before the request goes, so a listing remembered from it is never stamped newer
            // than what the server was asked: a slower, older answer must not pass for a newer one.
            var askedAt = DateTime.UtcNow;

            var stopped = false;
            var streamed = false;
            var finished = false;

            // Remembered rows, shown as unconfirmed until this navigation's own listing arrives.
            // Never as the listing itself, however young: the app's own writes don't clear what is
            // remembered, a fetch ahead may have been asked before one, and a clock set back makes
            // any age look young. Only a request started now is the folder as it is now.
            void ShowRemembered(RememberedListing memory)
            {
                if (finished || streamed || !ReferenceEquals(_listing, cts) || memory.ListedAt <= recalled?.ListedAt)
                    return;

                var first = recalled is null;
                recalled = memory;

                // Still the same check under way: a command already waiting on it keeps waiting.
                if (first)
                    Unconfirmed(memory.ListedAt);
                else
                    _recalledFrom = memory.ListedAt;
                Arrive(uri, source);
                InFoundOrder = false;
                SetRows(memory.Entries
                    .Select(e => new FileRowViewModel(e, _icons) { IsLeaving = _leaving.ContainsKey(e.Uri) })
                    .ToList());
                recalledAt = clock.Elapsed;
                _ = FillIconsAsync(_allRows, cts.Token);
                _ = DecorateAsync(_allRows, uri, cts.Token);
                if (!first)
                    return; // rows replaced under a cursor already placed; the restore keeps it

                // Where it was gone to, even if the listing that confirms it fails: the rows were
                // shown, and Back has to come back here.
                if (recordHistory)
                {
                    History.Visit(uri);
                    _history.Record(uri);
                    recordHistory = false;
                }
                UpdateHistoryState();

                if ((selectName ?? remembered?.FocusedName ?? Rows.FirstOrDefault()?.Name) is { } at)
                    SelectionRequested?.Invoke(this, at);
            }

            if (memorable && uri != Uri)
            {
                if (_memory!.Recall(uri) is { } memory)
                    ShowRemembered(memory);

                // Fetched ahead and still on its way: its rows are shown when they come, if this
                // listing has not come first. Taken over, so the cursor leaving does not cancel it.
                if (_prefetcher?.Join(uri, cts.Token) is { } fetching)
                    _ = fetching.ContinueWith(
                        done =>
                        {
                            if (done is { IsCompletedSuccessfully: true, Result: { } fetched })
                                _dispatcher.InvokeAsync(() => ShowRemembered(fetched));
                        },
                        TaskScheduler.Default);
            }

            // Constructed here so it captures the UI thread: the walk runs on a worker and this is
            // the only thing it touches on the way.
            var found = new Progress<int>(n =>
            {
                LoadedCount = n;
                ConnectingTo = null;
            });

            // What has been found so far, shown in the order found (PLAN.md section 13, "Streaming
            // long walks"). Runs on the UI thread; the first call is also the arrival.
            void ShowSoFar(List<FileRowViewModel> soFar)
            {
                // A snapshot that reaches the UI after the walk ended would put a partial, unsorted
                // list over the finished one.
                // Remembered rows are already up, in order; found order over them would be churn.
                if (finished || recalled is not null || (cts.IsCancellationRequested && !_stopRequested))
                    return;

                if (!streamed)
                {
                    streamed = true;
                    Arrive(uri, source);
                    InFoundOrder = true;
                }

                SetRows(soFar);
            }

            var rows = await Task.Run(async () =>
            {
                var collected = new List<FileRowViewModel>(1024);

                var clock = System.Diagnostics.Stopwatch.StartNew();
                TimeSpan? shownAt = null;
                var pause = StreamAfter;
                var took = TimeSpan.Zero;
                Task showing = Task.CompletedTask;

                try
                {
                    await foreach (var batch in source
                        .ListAsync(cts.Token)
                        .InBatchesAsync(512, TimeSpan.FromMilliseconds(50), cts.Token)
                        .ConfigureAwait(false))
                    {
                        foreach (var entry in batch)
                            collected.Add(new FileRowViewModel(entry, _icons) { IsLeaving = _leaving.ContainsKey(entry.Uri) });

                        ((IProgress<int>)found).Report(collected.Count);

                        // A walk that is over within a second is presented once, as before: a list
                        // that assembles itself in front of you for 300 ms is a flash, not progress.
                        // A long one shows its first screenful, then less and less often -- the
                        // point is to finish, and every showing of 190,000 rows costs the UI thread.
                        // Never waited for: the walk goes on while the pane catches up, and a
                        // showing still in progress means the next one is skipped, not queued.
                        if (clock.Elapsed >= StreamAfter && showing.IsCompleted &&
                            (shownAt is null || clock.Elapsed - shownAt >= pause))
                        {
                            shownAt = clock.Elapsed;
                            var soFar = collected.ToList();
                            showing = _dispatcher.InvokeAsync(() =>
                            {
                                var started = System.Diagnostics.Stopwatch.StartNew();
                                ShowSoFar(soFar);
                                took = started.Elapsed;
                            }).Task;

                            // Doubling from a second up to StreamAtMost, and never less than four
                            // times what the last showing took, so the keys stay answered.
                            pause = TimeSpan.FromTicks(Math.Max(
                                Math.Min(pause.Ticks * 2, StreamAtMost.Ticks), took.Ticks * 4));
                        }
                    }
                }
                catch (OperationCanceledException) when (Volatile.Read(ref _stopRequested))
                {
                    // Asked to stop, not superseded. What has been collected is the answer.
                    stopped = true;
                }

                return collected;
            }, CancellationToken.None).ConfigureAwait(true);

            if (cts.IsCancellationRequested && !stopped)
                return;

            // Stopped before the server answered: what it had found is less than what was already
            // up. The remembered rows stay, unconfirmed (settled in finally).
            if (stopped && recalled is not null)
                return;

            IsPartial = stopped;
            finished = true;

            if (!streamed)
                Arrive(uri, source);

            // A stopped walk still has to finish presenting what it found, so the rest of the work
            // runs untokened; a superseded one returned above and never gets here.
            var rest = stopped ? CancellationToken.None : cts.Token;

            // Rows first, decoration second. Decorating before showing them meant a git directory
            // waited on a "git status" process before anything appeared, which held the loading bar
            // up past its quarter-second and shoved the listing down as it arrived. The rows are
            // already correct and complete; a badge landing a moment later is what a decoration is.
            //
            // A streamed walk gets its one reorder here, and the pane keeps the cursor row in view
            // across it -- or goes to the top, if nobody has been anywhere in the list yet.
            var sortingNow = InFoundOrder;
            InFoundOrder = false;
            SetRows(rows);
            Confirmed(true);
            if (memorable && !stopped)
                _memory!.Remember(uri, rows.Select(r => r.Entry).ToList(), askedAt);
            rowsAt = clock.Elapsed;
            // Taken now: the line below is written after decorating, by when the launch is over.
            atLaunch = StartupClock.Launching;
            StartupClock.RowsSet(this, rows.Count);
            _settled.TrySetResult();
            _ = FillIconsAsync(rows, rest);
            if (sortingNow)
                ReorderedUnderCursor?.Invoke(this, EventArgs.Empty);
            IsBusy = false;
            ShowLoading = false;

            WatchHead(uri);
            await DescribeArchiveAsync(uri, rest);

            if (recordHistory)
            {
                History.Visit(uri);
                _history.Record(uri);
            }
            UpdateHistoryState();

            // Not over a choice made while it streamed: whoever picked rows during the walk is
            // somewhere already, and being moved to where they were last time would undo that.
            // With nowhere remembered, the first row: a folder entered for the first time left the
            // keyboard on the list with no row, so F5 or Del had nothing to act on until a Down.
            // After remembered rows, the cursor has been somewhere since and the new rows kept it
            // there (by address); only if its row is gone does it need a place.
            var focus = selectName ?? remembered?.FocusedName ?? Rows.FirstOrDefault()?.Name;
            var placed = (recalled is not null || streamed) && _selectedUris.Count > 0;
            if (focus is not null && !placed)
                SelectionRequested?.Invoke(this, focus);

            if (!stopped)
                StartWatching(source);

            // Rows read just now are marked from an answer asked just now. The cached one is dropped
            // only when this tab's watcher reports a change, and a paste or a rename navigates here
            // straight after writing, which stops the watcher before its batch is out: the new file
            // was listed but marked from a status taken before it existed, and nothing came along to
            // correct it. Files written while we were elsewhere in the repository were the same.
            // The last answer still shows at once while git is asked again.
            //
            // After the rows have been laid out and drawn, not before: the first part of decorating
            // (finding the repository, painting the last marks, sorting again) runs on this thread
            // before its first await, and the new rows waited behind it for their first frame.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            if (cts.IsCancellationRequested && !stopped)
                return;
            paintedAt = clock.Elapsed;
            ForgetGitStatus();
            await DecorateAsync(rows, uri, rest);

            // Listing has to stay fast, and the only way it stays fast is being told when it is not.
            // Quiet within budget (DESIGN.md §1.5), which is nearly everything local. One at launch
            // says so: the Startup line has already timed it, cold, and it is not a slow folder.
            // Rows are timed to being drawn, not to being handed to the grid: the 23 ms this used to
            // report for a folder that felt sticky left out the layout and the first frame.
            var verdict = Budgets.Verdict(
                ("rows", paintedAt.TotalMilliseconds, uri.IsLocal ? Budgets.LocalRows : Budgets.OtherRows),
                ("decorated", (clock.Elapsed - rowsAt).TotalMilliseconds, Budgets.Decorated));
            if (verdict is not null || LogEveryListing)
                _log.LogInformation(
                    "Slow listing{AtLaunch}: {Uri}, {Count:N0} rows on screen after {Rows:N0} ms{Recalled}, decorated after {Done:N0} ms, {Verdict}",
                    atLaunch ? " at launch" : "", uri.Display, rows.Count, paintedAt.TotalMilliseconds,
                    // The wait the user actually had, when remembered rows stood in for it.
                    recalled is null ? "" : $" (remembered ones after {recalledAt.TotalMilliseconds:N0} ms)",
                    clock.Elapsed.TotalMilliseconds, verdict ?? "within budget");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (!ReferenceEquals(_listing, cts) || ex is Providers.Sftp.SftpAbortedException)
        {
            // Superseded: another navigation is under way, and this one's failure is about a place
            // the tab has left. Going to a second server cancelled the first one's password prompt,
            // and its red "could not open" landed over the new connection (2026-10-01). Or the user
            // cancelled the prompt: a choice, not a failure (§1.3: red is rare), said quietly.
            if (ReferenceEquals(_listing, cts))
            {
                Notice = new Notice(
                    $"Not connected to {uri.Authority}: the password prompt was cancelled.",
                    NoticeSeverity.Info,
                    "Ctrl+R tries again.");
                if (Uri is { } here)
                    ShowAddress(here.Display);
            }
        }
        catch (Exception ex)
        {
            // The notice tells the user what failed; the log is the only place the reason survives,
            // and a navigation that fails without a stack trace is a bug report nobody can act on.
            _log.LogError(ex, "Navigating to {Uri} failed", uri);

            // Somewhere new that isn't there -- typed, or a folder an app makes only when it needs
            // it -- is asked about rather than reported: make it, or go to what does exist. One the
            // tab was showing is gone instead, and is followed up below.
            if (ex is DirectoryNotFoundException && uri != Uri && uri.IsLocal && !uri.IsView)
                _ = _dispatcher.InvokeAsync(() => OfferMissingAsync(uri, ex));
            else
                Notice = Describe(ex, uri, shown: uri == Uri);

            // Not shown first another time: whatever the rows were, the server no longer stands by them.
            _memory?.Forget(uri);

            if (IsNotThere(ex, uri))
            {
                // Not offered again: the address bar filled in a folder long gone, and Enter took
                // it there (2026-10-14). With it go the places under it.
                _history.Forget(_history.Visits.Select(v => v.Uri).Where(v => IsAtOrUnder(v, uri)).ToList());

                // Its own folder gone under it, found by a refresh: somewhere that still exists.
                if (uri == Uri && uri.IsLocal && !uri.IsView)
                    _ = _dispatcher.InvokeAsync(() => GoToNearestExistingAsync(uri));
            }

            // Back to where the listing still is. With nowhere yet, the address that failed stays,
            // which is the one worth correcting.
            if (Uri is { } here)
                ShowAddress(here.Display);
        }
        finally
        {
            if (ReferenceEquals(_listing, cts))
            {
                IsBusy = false;
                ShowLoading = false;
                ConnectingTo = null;
                _listing = null;

                // Failed, stopped, or the password not given: whatever is on screen is still
                // unconfirmed, and a command waiting on it is told so rather than left waiting.
                if (IsUnconfirmed)
                    Confirmed(false);
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// Fetches the icons the rows were made without, off the UI thread, one shell call per kind of
    /// file rather than per row, and hands each row its icon when it arrives — rows first, icons a
    /// moment later, the same order the git badges already keep.
    /// </summary>
    /// <remarks>
    /// In two passes. Kinds first, none of which opens a file, with the kind's icon standing in for
    /// files that carry their own picture. Then those pictures one file at a time, each handed over
    /// as it comes: reading one means opening the file, and a download Defender has not seen yet is
    /// scanned first. Asked all together and handed over at the end, the newest installer in
    /// Downloads held back every row's icon, 38 programs behind it (2026-10-28).
    /// </remarks>
    private async Task FillIconsAsync(IReadOnlyList<FileRowViewModel> rows, CancellationToken ct)
    {
        var waiting = rows.Where(r => r.NeedsIcon).ToList();
        if (waiting.Count == 0)
            return;

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var at = Uri;
        var own = waiting.Where(r => _icons.ReadsOwnIcon(r.Entry)).ToList();
        var kinds = waiting.Except(own).ToList();
        var bare = own.Where(r => r.Icon is null).ToList();
        var slowest = (Name: "", Ms: 0.0);

        try
        {
            var fetched = await Task.Run(
                () => kinds
                    // By the icon service's own key, not by extension: Desktop and Downloads are
                    // both folders, and each has an icon of its own.
                    .GroupBy(r => _icons.IconKey(r.Entry), StringComparer.OrdinalIgnoreCase)
                    .Select(kind => (Rows: kind.ToList(), Icon: _icons.GetIcon(kind.First().Entry)))
                    .ToList(),
                ct).ConfigureAwait(true);
            var standIns = await Task.Run(
                () => bare
                    .GroupBy(r => r.Entry.Extension, StringComparer.OrdinalIgnoreCase)
                    .Select(kind => (Rows: kind.ToList(), Icon: _icons.GetTypeIcon(kind.First().Entry)))
                    .ToList(),
                ct).ConfigureAwait(true);

            foreach (var (kindRows, icon) in fetched)
                foreach (var row in kindRows)
                    row.ReceiveIcon(icon);
            foreach (var (kindRows, icon) in standIns)
                foreach (var row in kindRows)
                    row.ReceiveStandIn(icon);

            foreach (var row in NearestCursorFirst(own))
            {
                // Rows replaced or the tab closed: whatever is left is for rows nobody will see, and
                // each one holds the shell's one lookup at a time away from the panes that are seen.
                if (ct.IsCancellationRequested || IsClosed || !ReferenceEquals(_allRows, rows))
                    return;

                var asked = System.Diagnostics.Stopwatch.GetTimestamp();
                var icon = await Task.Run(() => _icons.GetIcon(row.Entry), ct).ConfigureAwait(true);
                row.ReceiveIcon(icon);

                var ms = System.Diagnostics.Stopwatch.GetElapsedTime(asked).TotalMilliseconds;
                if (ms > slowest.Ms)
                    slowest = (row.Name, ms);
            }
        }
        catch (OperationCanceledException)
        {
            // Navigated away; these rows are not on screen any more.
            return;
        }

        // Not part of the "decorated" time in the Slow listing line, which these run alongside and
        // used to be invisible to: icons late by seconds left no trace in the log.
        var total = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (Budgets.Verdict(("icons", total, Budgets.Decorated)) is { } verdict)
            _log.LogInformation(
                "Slow icons: {Uri}, {Kinds:N0} rows by kind and {Own:N0} with their own icon after {Ms:N0} ms, slowest {Slowest} at {SlowestMs:N0} ms, {Verdict}",
                at?.Display, kinds.Count, own.Count, total, slowest.Name, slowest.Ms, verdict);
    }

    /// <summary>
    /// The rows ordered by how far they are from the cursor's, as shown; those filtered out of
    /// view come last. Without a cursor, from the top.
    /// </summary>
    private List<FileRowViewModel> NearestCursorFirst(IReadOnlyList<FileRowViewModel> rows)
    {
        var left = rows.ToHashSet();
        var shown = Rows;
        var cursor = 0;
        for (var i = 0; i < shown.Count; i++)
        {
            if (ReferenceEquals(shown[i], FocusedRow))
            {
                cursor = i;
                break;
            }
        }

        var ordered = shown
            .Select((row, index) => (Row: row, Index: index))
            .Where(p => left.Contains(p.Row))
            .OrderBy(p => Math.Abs(p.Index - cursor))
            .Select(p => p.Row)
            .ToList();
        left.ExceptWith(ordered);
        ordered.AddRange(left);
        return ordered;
    }

    /// <summary>
    /// Becomes the location being listed: address, title, columns. Once per navigation -- when the
    /// first rows are shown, which for a streamed walk is before the walk has finished.
    /// </summary>
    private void Arrive(FsUri uri, IListingSource source)
    {
        // Marks belong to the place they were made. By address they could no longer match another
        // folder's files, but they still followed the same files into another view of them (a flat
        // view, a search), arriving marked off screen. On arrival rather than on setting out: a
        // navigation cancelled or failed leaves the marks where they were. The same place listed
        // again keeps them.
        if (uri != Uri)
        {
            _selectedUris = [];
            SelectionIsMarks = false;
            KeepConnectionTo(uri);
        }

        Uri = uri;
        ShowAddress(uri.Display);
        Title = string.IsNullOrEmpty(source.Title) ? uri.Display : source.Title;

        if (!Equals(Columns, source.Columns))
        {
            Columns = source.Columns;
            Order = Order with
            {
                ColumnId = source.Columns.DefaultSortColumnId,
                Descending = source.Columns.DefaultDescending,
            };
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
        }

        _omission = source.Omission;
        _rereadOnFilterChange = source.RereadWhenFilterChanges;
    }

    /// <summary>The server this tab shows, kept connected while it does; null anywhere local.</summary>
    private IDisposable? _keptOpen;

    /// <summary>
    /// The new hold is taken before the old one goes, so moving between folders on one server
    /// never leaves it unheld for the idle reaper to find.
    /// </summary>
    private void KeepConnectionTo(FsUri uri)
    {
        var previous = _keptOpen;
        _keptOpen = _providers.TryResolve(uri, out var provider) && provider is IConnectingProvider remote
            ? remote.KeepOpen(uri)
            : null;
        previous?.Dispose();
    }

    /// <summary>DC_LISTING_LOG=all logs every listing, over budget or not, for measuring rather than for use.</summary>
    private static readonly bool LogEveryListing =
        Environment.GetEnvironmentVariable("DC_LISTING_LOG") == "all";

    /// <summary>How long a walk runs before its rows are shown as they arrive.</summary>
    private static readonly TimeSpan StreamAfter = TimeSpan.FromSeconds(1);

    /// <summary>The longest a streaming walk goes between showings of what it has found.</summary>
    private static readonly TimeSpan StreamAtMost = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Rows shown in the order the walk found them, not sorted: a streaming walk, until it ends or
    /// a column is clicked. Sorting as rows arrive would put each one between rows being read, so
    /// the top of the list would churn under the reader; the headers drop their arrow instead, so
    /// the list does not claim an order it is not in.
    /// </summary>
    [ObservableProperty]
    private bool _inFoundOrder;

    /// <summary>The rows were just sorted from found order; the pane keeps the cursor in view.</summary>
    public event EventHandler? ReorderedUnderCursor;

    /// <summary>Raises the loading bar only if this listing is still running a moment from now.</summary>
    private void AnnounceIfSlow(CancellationTokenSource cts) =>
        _ = Task.Delay(PatienceBeforeSaying).ContinueWith(
            _ => ShowLoading = IsBusy && ReferenceEquals(_listing, cts),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());

    private void UpdateHistoryState()
    {
        CanGoBack = History.CanGoBack;
        CanGoForward = History.CanGoForward;
    }

    /// <param name="shown">
    /// Whether the tab was showing <paramref name="uri"/> when it failed. Only then has a folder
    /// that isn't there gone: one typed into the address bar may never have existed at all.
    /// </param>
    private static Notice Describe(Exception ex, FsUri uri, bool shown) => ex switch
    {
        UnauthorizedAccessException =>
            new Notice($"Access denied. You do not have permission to read {uri.Display}.", NoticeSeverity.Error),
        DirectoryNotFoundException =>
            new Notice($"{uri.Display} {(shown ? "no longer exists" : "does not exist")}.", NoticeSeverity.Error),
        IOException io =>
            new Notice($"Could not read {uri.Display}.", NoticeSeverity.Error, io.Message),
        NotSupportedException ns =>
            new Notice(ns.Message, NoticeSeverity.Error),
        _ => new Notice($"Could not open {uri.Display}.", NoticeSeverity.Error, ex.Message),
    };

    // ---- live updates -------------------------------------------------------------------

    private void StartWatching(IListingSource source)
    {
        _watcher = source.Watch();
        IsLive = _watcher is not null;
        if (_watcher is null)
            return;

        _watcher.Changed += OnWatcherChanged;
        _watcher.ResyncRequired += OnResyncRequired;
        _watcher.Moved += OnFolderMoved;
        _watcher.Start();
    }

    private void StopWatching()
    {
        if (_watcher is null)
            return;

        _watcher.Changed -= OnWatcherChanged;
        _watcher.ResyncRequired -= OnResyncRequired;
        _watcher.Moved -= OnFolderMoved;
        _watcher.Dispose();
        _watcher = null;
        IsLive = false;
    }

    /// <summary>How many listings this tab has started; a resync asked for before the latest one is moot.</summary>
    private int _navigations;

    private void OnWatcherChanged(object? sender, DirectoryChangeBatch batch) =>
        _dispatcher.InvokeAsync(() => ApplyChanges(batch));

    /// <summary>This tab's folder was renamed, deleted or moved away while it was shown.</summary>
    private void OnFolderMoved(object? sender, FsUri? to)
    {
        var asked = _navigations;
        var was = Uri;
        _dispatcher.InvokeAsync(async () =>
        {
            if (_navigations != asked || was is null || Uri != was)
                return;

            if (to is null)
            {
                await GoToNearestExistingAsync(was);
                return;
            }

            if (to != was)
            {
                // The same folder under its new name, where the user already is.
                await NavigateAsync(to, FocusedRow?.Name);
                Report(new Notice($"{was.Name} was renamed to {to.Name}."));
            }
        });
    }

    private void OnResyncRequired(object? sender, EventArgs e)
    {
        // The OS dropped events, so the only honest response is to re-read the directory — unless
        // a listing has started since it asked, which reads the directory as it is now anyway. Re-
        // reading after that one would also put the cursor back where it was, over the row the
        // navigation meant to land on: a collection just made with F7.
        var asked = _navigations;
        _dispatcher.InvokeAsync(() =>
        {
            if (_navigations == asked)
                _ = RefreshAsync();
        });
    }

    private void ApplyChanges(DirectoryChangeBatch batch)
    {
        var touched = false;

        foreach (var change in batch.Changes)
        {
            switch (change.Kind)
            {
                case DirectoryChangeKind.Removed:
                    if (_byName.Remove(change.Name, out var gone))
                    {
                        _allRows.Remove(gone);
                        touched = true;
                    }
                    break;

                case DirectoryChangeKind.Added or DirectoryChangeKind.Updated when change.Entry is { } entry:
                    var row = new FileRowViewModel(entry, _icons) { IsLeaving = _leaving.ContainsKey(entry.Uri) };
                    if (_byName.TryGetValue(change.Name, out var existing))
                    {
                        _allRows[_allRows.IndexOf(existing)] = row;
                    }
                    else
                    {
                        _allRows.Add(row);
                    }
                    _byName[change.Name] = row;
                    touched = true;
                    break;
            }
        }

        if (touched)
        {
            // Changed in place, so the kept order no longer holds every row, nor only rows that exist.
            _ordered = null;
            ScheduleDecoration();
            _ = FillIconsAsync(_allRows, CancellationToken.None);
        }
    }

    // ---- decoration ----------------------------------------------------------------------

    /// <summary>
    /// Runs the decorator pipeline (§6.5) and sorts the view as soon as the order can be known.
    /// Decorators assign on this thread, so anything slow inside one is its own business to offload.
    /// </summary>
    /// <remarks>
    /// All start at once, and the view is applied when the ones that answer on the spot have: the
    /// highlight rules, which are all the sort order depends on. Git status, waiting on a process,
    /// lands its marks when git replies. Sorting after it made a highlighted row jump into place
    /// seconds after the listing — 2.5 s on a large repository (DESIGN.md §1.5).
    /// </remarks>
    private async Task DecorateAsync(IReadOnlyList<FileRowViewModel> rows, FsUri directory, CancellationToken ct)
    {
        var sinks = rows.Cast<IRowDecorationSink>().ToList();
        var pending = new List<Task>();
        if (sinks.Count > 0)
        {
            foreach (var decorator in _decorators)
            {
                var running = DecorateWith(decorator, sinks, directory, ct);
                if (!running.IsCompleted)
                    pending.Add(running);
            }
        }

        ApplyView();
        await Task.WhenAll(pending);
    }

    private static async Task DecorateWith(
        IRowDecorator decorator, IReadOnlyList<IRowDecorationSink> rows, FsUri directory, CancellationToken ct)
    {
        try
        {
            await decorator.DecorateAsync(rows, directory, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A decorator is an embellishment. One that fails must not cost you the listing.
        }
    }

    /// <summary>
    /// Labels the pane with the archive's real format once we are inside one. This is where a
    /// misnamed file gets corrected out loud: open something called "logs.gz" and the footer says
    /// tar.gz, because that is what the bytes said.
    /// </summary>
    private async Task DescribeArchiveAsync(FsUri uri, CancellationToken ct)
    {
        if (!uri.IsArchiveEntry)
        {
            ArchiveLabel = null;
            return;
        }

        try
        {
            var provider = _providers.Resolve(uri);
            ArchiveLabel = provider is ArchiveProvider archive
                ? (await archive.DescribeAsync(uri, ct).ConfigureAwait(true)).Label
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            // A label is a courtesy; failing to produce one must not disturb the listing.
            ArchiveLabel = null;
        }
    }

    /// <summary>
    /// Watches the repository's HEAD so switching branches in a terminal updates the pane at once,
    /// rather than the next time you happen to navigate.
    /// </summary>
    private void WatchHead(FsUri directory)
    {
        StopWatchingHead();

        // A flattened tree or a search is still somewhere in particular: the folder it was made
        // from. Asking git about "flat:C:\src" itself would get no answer, and a command about
        // the repository would vanish the moment you pressed Ctrl+Shift+F.
        var anchor = directory.Anchor;
        var info = _git.Describe(anchor);
        SetRepository(info);
        if (info is null || !Directory.Exists(info.GitDirectory))
            return;

        _headWatcher = new FileSystemWatcher(info.GitDirectory)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };

        // HEAD for a branch switch, index for anything staged, committed or stashed. A commit does
        // not rewrite HEAD -- it still says "ref: refs/heads/main" -- so watching HEAD alone would
        // leave every row marked modified after the change that unmodified them.
        _headWatcher.Filters.Add("HEAD");
        _headWatcher.Filters.Add("index");

        var gitDirectory = info.GitDirectory;
        void OnHeadChanged(object _, EventArgs __) =>
            _dispatcher.InvokeAsync(() =>
            {
                _git.Invalidate(gitDirectory);
                ForgetGitStatus();

                if (Uri is not { } current)
                    return;

                SetRepository(_git.Describe(current.Anchor));
                ScheduleDecoration();
            });

        _headWatcher.Changed += OnHeadChanged;
        _headWatcher.Created += OnHeadChanged;
        // Git does not write HEAD in place. It writes HEAD.lock and renames it over the top, which
        // arrives as Renamed and as nothing else -- so without this line a branch switch made in a
        // terminal left the chip showing the branch you were on before it.
        _headWatcher.Renamed += OnHeadChanged;
        _headWatcher.EnableRaisingEvents = true;

        // And the branches. A commit of what was already staged need not rewrite the index at all
        // -- only the branch's ref moves -- so the rows kept the "added" and "modified" of a change
        // that was now committed. A worktree's refs are not in its own git directory but in the
        // repository's, which its commondir file names.
        var refs = Path.Combine(CommonGitDirectory(gitDirectory), "refs", "heads");
        if (!Directory.Exists(refs))
            return;
        _refsWatcher = new FileSystemWatcher(refs)
        {
            IncludeSubdirectories = true, // feature/x is a folder
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
        };
        _refsWatcher.Changed += OnHeadChanged;
        _refsWatcher.Created += OnHeadChanged;
        _refsWatcher.Renamed += OnHeadChanged;
        _refsWatcher.EnableRaisingEvents = true;
    }

    private static string CommonGitDirectory(string gitDirectory)
    {
        try
        {
            var file = Path.Combine(gitDirectory, "commondir");
            return File.Exists(file)
                ? Path.GetFullPath(Path.Combine(gitDirectory, File.ReadAllText(file).Trim()))
                : gitDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return gitDirectory;
        }
    }

    private void SetRepository(GitInfo? info)
    {
        Repository = info is null
            ? null
            : new RepositoryInfo(
                FsUri.Local(info.WorkTreeRoot), info.Detached ? null : info.Branch, info.RemoteUrl);
        _workTreeRoot = info?.WorkTreeRoot;
        Branch = info?.Label;
    }

    /// <summary>
    /// Re-decorates once the changes stop arriving, rather than once per change.
    /// </summary>
    /// <remarks>
    /// A branch switch rewrites thousands of files, and the watcher reports them in batches for
    /// several seconds. Decorating per batch would start a <c>git status</c> process per batch —
    /// hundreds of them, each one obsolete before it finished, on a repository that is mid-checkout
    /// and will answer differently a moment later. So the timer restarts on every batch and only
    /// the trailing edge does any work: the answer we want is the one about the tree as it ends up.
    ///
    /// The rows themselves are never debounced. Those are already correct and the user is looking
    /// at them; it is only the annotations that are worth waiting to get right.
    /// </remarks>
    private void ScheduleDecoration()
    {
        _decorationDue ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };

        _decorationDue.Tick -= OnDecorationDue;
        _decorationDue.Tick += OnDecorationDue;
        _decorationDue.Stop();
        _decorationDue.Start();
    }

    private void OnDecorationDue(object? sender, EventArgs e)
    {
        _decorationDue?.Stop();

        if (Uri is not { } directory)
            return;

        // Whatever git said before the storm was about a tree that no longer exists.
        ForgetGitStatus();
        _ = DecorateAsync(_allRows, directory, CancellationToken.None);
    }

    /// <summary>Drops what git said about the repository this tab is in, if it is in one.</summary>
    private void ForgetGitStatus()
    {
        if (Uri is { } uri && _git.Describe(uri) is { } repository)
            _gitStatus.Invalidate(repository.WorkTreeRoot);
    }

    private void StopWatchingHead()
    {
        _headWatcher?.Dispose();
        _headWatcher = null;
        _refsWatcher?.Dispose();
        _refsWatcher = null;
    }

    // ---- view state, sorting, filtering --------------------------------------------------

    private void SetRows(List<FileRowViewModel> rows)
    {
        _allRows = rows;
        _byName.Clear();
        foreach (var row in rows)
            _byName[row.Name] = row;
        ApplyView();
    }

    public void SortBy(string columnId)
    {
        // Asking for an order during a walk is asking for the churn that comes with it.
        InFoundOrder = false;
        Order = Order.ToggledBy(columnId);
        RememberChoice();
        ApplyView();
    }

    public void ToggleHidden()
    {
        Filter = Filter.Toggled();
        RememberChoice();

        // A pruned walk never collected the hidden part, so there is nothing to re-filter; showing
        // the same rows again would look like the key had stopped working.
        if (_rereadOnFilterChange)
        {
            _ = RefreshAsync();
            return;
        }

        ApplyView();
    }

    public void ToggleDirectoriesFirst()
    {
        Order = Order with { DirectoriesFirst = !Order.DirectoriesFirst };
        RememberChoice();
        ApplyView();
    }

    /// <param name="reorder">
    /// False when only the quick filter changed, so the order kept from last time still holds.
    /// </param>
    private void ApplyView(bool reorder = true)
    {
        if (reorder || _ordered is null)
        {
            var body = _allRows;
            var ordered = body.Where(r => Filter.Matches(r.Entry)).ToList();
            _hiddenCount = body.Count - ordered.Count;

            if (!InFoundOrder)
                ordered.Sort((a, b) =>
            {
                // A highlight rule can pin its matches to the top; otherwise ordering is untouched.
                if (a.SortGroup != b.SortGroup)
                    return b.SortGroup.CompareTo(a.SortGroup);
                return Order.Compare(a.Entry, b.Entry);
            });

            _ordered = ordered;
        }

        var visible = _ordered;
        if (QuickFilter.Length > 0)
        {
            // The best matches come first — the name starting with what was typed, above all — so
            // the cursor, which goes to the first match, lands where type-ahead would have put it.
            // Not during a walk, whose rows stay in the order found until it ends.
            var matcher = NameQuery.Compile(QuickFilter);
            visible = InFoundOrder
                ? _ordered.Where(r => matcher.Matches(r.Entry)).ToList()
                : matcher.Arrange(_ordered, r => r.Entry);
            FilteredOut = _ordered.Count - visible.Count;
        }
        else
        {
            FilteredOut = 0;
        }

        _arrangedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        // Taken before the rows go: the list may say its selection emptied as they do.
        var keep = new SelectionRestore(_selectedUris, SelectionIsMarks);
        Rows = visible;
        var landed = Land(visible);
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(EmptyText));
        UpdateStatus();

        // Replacing the row list would otherwise drop the selection every time a watcher batch
        // arrives, which is intolerable while you are picking files.
        if (keep.Uris.Count > 0)
            SelectionRestoreRequested?.Invoke(this, keep);

        // After the selection is back, so the cursor move is the last word.
        if (landed is not null)
            ArrivalLanded?.Invoke(this, landed);
    }

    // ---- arrivals -------------------------------------------------------------------------

    private readonly IArrivalFeed? _arrivals;

    /// <summary>
    /// Things this application put in a folder, by name: where, when it was reported, and when its
    /// row first showed (null until it has). The report and the row come separately — the watcher
    /// may add the row before the job says it has finished, or a refresh long after — so each waits
    /// for the other here.
    /// </summary>
    private readonly Dictionary<string, (FsUri Folder, DateTime Reported, DateTime? Shown)> _landing =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a report waits for its row: a remote folder shows it only when next listed.</summary>
    private static readonly TimeSpan LandingWait = TimeSpan.FromSeconds(20);

    private DispatcherTimer? _glow;

    /// <summary>
    /// The arrivals have landed: the name of the first of them in the listing's order, for the view
    /// to put the cursor on. Raised once per arrival, never for what the watcher alone saw.
    /// </summary>
    public event EventHandler<string>? ArrivalLanded;

    private void OnArrived(object? sender, IReadOnlyList<FsUri> placed) =>
        _dispatcher.InvokeAsync(() =>
        {
            if (Uri is not { IsView: false } here)
                return;

            var now = DateTime.UtcNow;
            var ours = false;
            foreach (var uri in placed)
            {
                if (uri.Parent != here)
                    continue;
                _landing[uri.Name] = (here, now, null);
                ours = true;
            }

            // The rows may be in already; if not, the listing that brings them lands them.
            if (ours)
                ApplyView();
        });

    /// <summary>Starts the glow on arrivals whose rows are now in; returns the first one to go to.</summary>
    private string? Land(List<FileRowViewModel> visible)
    {
        if (_landing.Count == 0)
            return null;

        var now = DateTime.UtcNow;
        List<string>? landed = null;
        foreach (var (name, arrival) in _landing.ToList())
        {
            if (arrival.Shown is not null || arrival.Folder != Uri)
                continue;
            if (now - arrival.Reported > LandingWait)
            {
                _landing.Remove(name);
                continue;
            }
            if (!_byName.ContainsKey(name))
                continue;

            _landing[name] = arrival with { Shown = now };
            (landed ??= []).Add(name);
        }

        if (landed is null)
        {
            // A glowing row replaced mid-fade — a late watcher update, a refresh — carries on
            // from where its predecessor was rather than going dark.
            Glow(now);
            return null;
        }

        Glow(now);
        if (_glow is null)
        {
            _glow = new DispatcherTimer(DispatcherPriority.Render, _dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
            _glow.Tick += (_, _) => Glow(DateTime.UtcNow);
        }
        _glow.Start();

        var set = landed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return visible.FirstOrDefault(r => set.Contains(r.Name))?.Name;
    }

    /// <summary>Sets every arriving row's glow for this moment; stops the clock once all have faded.</summary>
    private void Glow(DateTime now)
    {
        foreach (var (name, arrival) in _landing.ToList())
        {
            if (arrival.Shown is not { } shown)
                continue;

            var since = now - shown;
            if (_byName.TryGetValue(name, out var row) && arrival.Folder == Uri)
                row.Arrival = ArrivalGlow.At(since);
            if (since >= ArrivalGlow.Total)
                _landing.Remove(name);
        }

        if (!_landing.Values.Any(a => a.Shown is not null))
            _glow?.Stop();
    }

    /// <summary>
    /// settings.json's "folders before files" changed: followed here unless this location's order
    /// was chosen on purpose, which a default does not overrule. Saved as inherited, as any view
    /// passed through is.
    /// </summary>
    public void FollowDefaultOrder(bool directoriesFirst)
    {
        if (_chosenHere || Order.DirectoriesFirst == directoriesFirst)
            return;
        Order = Order with { DirectoriesFirst = directoriesFirst };
        SaveViewState();
        ApplyView();
    }

    private void SaveViewState()
    {
        if (Uri is { } uri)
            _viewState.Set(uri, new ViewState(Order, Filter, FocusedRow?.Name, _chosenHere));
    }

    /// <summary>Whether this location's order or filter was set here, rather than inherited.</summary>
    private bool _chosenHere;

    /// <summary>
    /// Records a choice made here, at once rather than on leaving: a sort set just before closing
    /// the window should still be there next time.
    /// </summary>
    private void RememberChoice()
    {
        _chosenHere = true;
        SaveViewState();
    }

    /// <param name="marks">Whether <paramref name="selected"/> are marks, or only the cursor's row.</param>
    public void SetSelection(IEnumerable<FileRowViewModel> selected, FileRowViewModel? focused, bool marks)
    {
        _selection = [.. selected];
        _selectedUris = _selection.Select(r => r.Entry.Uri).ToList();
        SelectionIsMarks = marks;
        if (!ReferenceEquals(FocusedRow, focused))
            LookAhead(focused);
        FocusedRow = focused;
        OnPropertyChanged(nameof(SelectedEntries));
        UpdateStatus();
    }

    partial void OnIsPartialChanged(bool value) => UpdateStatus();

    private void UpdateStatus()
    {
        var items = Rows.Count;
        var picked = _selection.Count;
        // Both halves of "what you are not seeing" read as one clause under one mention of the
        // key that undoes them; naming Ctrl+H twice in one status line is noise, not emphasis.
        var concealed = new List<string>(2);
        if (_hiddenCount > 0)
            concealed.Add($"{_hiddenCount:N0} hidden");
        if (_omission is not null)
            concealed.Add(_omission);

        var hidden = concealed.Count == 0
            ? string.Empty
            : $" — {string.Join(", ", concealed)} (Ctrl+H)";

        // The filter says how much it is holding back in its own bar; the footer says it too,
        // because the footer is where "what am I looking at" is answered and a count that only
        // appears in one place is a count somebody will miss.
        var filtered = FilteredOut > 0 ? $" — {FilteredOut:N0} filtered out" : string.Empty;
        var partial = IsPartial ? " — stopped, partial (Ctrl+R to finish)" : string.Empty;

        // Remembered rows say how old they are, and whether anything is still checking them.
        if (IsUnconfirmed)
            partial += _confirming is null
                ? $" — as listed {Ago(_recalledFrom)}, not checked (Ctrl+R)"
                : $" — as listed {Ago(_recalledFrom)}, checking";

        if (picked == 0)
        {
            StatusText = $"{Count(items, "item")}{hidden}{filtered}{partial}";
            return;
        }

        // A byte total for a folder selection is noise, not information.
        var files = _selection.Where(r => !r.IsDirectory).ToList();
        var size = files.Count > 0 ? $" ({Format.Size(files.Sum(r => r.Entry.Size))})" : string.Empty;
        StatusText = $"{Count(items, "item")} — {picked:N0} selected{size}{hidden}{filtered}{partial}";
    }

    /// <summary>"just now", "40 s ago", "3 min ago", "2 h ago": how long since a remembered listing.</summary>
    private static string Ago(DateTime utc)
    {
        var since = DateTime.UtcNow - utc;
        return since.TotalSeconds < 10 ? "just now"
            : since.TotalMinutes < 1 ? $"{since.TotalSeconds:0} s ago"
            : since.TotalHours < 1 ? $"{since.TotalMinutes:0} min ago"
            : $"{since.TotalHours:0} h ago";
    }

    /// <summary>"1 item", "2 items" — a search that finds one thing should not say "1 items".</summary>
    private static string Count(int n, string noun) => $"{n:N0} {noun}{(n == 1 ? "" : "s")}";

    // ---- IDecisionHost ------------------------------------------------------------------

    public Task<string?> AskAsync(ChoiceRequest request, CancellationToken ct) =>
        PresentAsync(new DecisionViewModel(request), ct);

    public Task<string?> AskTextAsync(TextRequest request, CancellationToken ct) =>
        PresentAsync(new DecisionViewModel(request), ct);

    public async Task<SecretAnswer?> AskSecretAsync(SecretRequest request, CancellationToken ct)
    {
        var decision = new DecisionViewModel(request);
        var secret = await PresentAsync(decision, ct).ConfigureAwait(true);
        return secret is null ? null : new SecretAnswer(secret, decision.Remember);
    }

    public void Report(Notice notice) => _dispatcher.InvokeAsync(() => Notice = notice);

    /// <summary>Takes over a question that follows the front tab (<see cref="ChoiceRequest.FollowsFront"/>), on the UI thread.</summary>
    internal void Receive(DecisionViewModel decision)
    {
        if (decision.Holder is { } from && ReferenceEquals(from.Decision, decision))
            from.Decision = null;
        decision.Holder = this;
        Decision = decision;
    }

    public void ShowOutput(string title, string text, bool failed = false) =>
        _dispatcher.InvokeAsync(() => Output = new ToolOutputViewModel(title, text, failed));

    public void Follow(Job job) =>
        _dispatcher.InvokeAsync(() => Output = new ToolOutputViewModel(job, _dispatcher));

    /// <summary>A panel that is no longer on screen stops following its job; the job carries on.</summary>
    partial void OnOutputChanged(ToolOutputViewModel? oldValue, ToolOutputViewModel? newValue) =>
        oldValue?.Dispose();

    /// <summary>
    /// Shows a decision and waits for the answer.
    /// </summary>
    /// <remarks>
    /// Callable from any thread, and it has to be: a provider raises these from wherever its work
    /// happens — an SFTP password prompt comes from the listing worker, not from a command — and
    /// no provider should have to know which thread this application's UI lives on. Publishing
    /// the decision is therefore marshalled here rather than being every caller's problem.
    /// </remarks>
    private async Task<string?> PresentAsync(DecisionViewModel decision, CancellationToken ct)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            Decision?.Cancel();
            decision.Holder = this;
            Decision = decision;
            StartupClock.PaneAsks(this);
            _settled.TrySetResult();
        });

        await using var registration = ct.Register(decision.Cancel);
        try
        {
            return await decision.Completion.ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                // Wherever it is now: one that followed the front tab is cleared there.
                var holder = decision.Holder ?? this;
                if (ReferenceEquals(holder.Decision, decision))
                    holder.Decision = null;
            });
        }
    }

    private void OnIconsRecovered(object? sender, EventArgs e) =>
        _dispatcher.InvokeAsync(() => OnPropertyChanged(nameof(LocationIcon)));

    /// <summary>Closed for good, as opposed to moved to the other pane, which keeps it alive.</summary>
    public bool IsClosed { get; private set; }

    public void Dispose()
    {
        // Once: the container keeps every tab and disposes it again at exit, after the pane did
        // when it was closed, and a second Cancel on a listing's disposed token threw there,
        // which stopped the container before history and view state were saved (review,
        // 2026-10-30).
        if (IsClosed)
            return;
        IsClosed = true;

        // A question left open in a tab closed for good is never answered, and what asked it (a
        // Del, Ctrl+Z held "still going" behind it) waited for ever (review, 2026-10-18). Closed,
        // it is answered as Cancel, as dismissing it would be.
        if (Decision is { } open && ReferenceEquals(open.Holder ?? this, this))
            open.Cancel();

        _icons.Recovered -= OnIconsRecovered;
        if (_shells is not null)
            _shells.Changed -= OnShellsChanged;
        if (_arrivals is not null)
            _arrivals.Arrived -= OnArrived;
        _glow?.Stop();
        SaveViewState();
        StopWatching();
        StopWatchingHead();
        _keptOpen?.Dispose();
        _keptOpen = null;
        Output = null;
        _listing?.Cancel();
        _listing?.Dispose();
    }
}

/// <summary>A selection to put back after the rows were replaced: by address, and whether they were marks.</summary>
public sealed record SelectionRestore(IReadOnlyList<FsUri> Uris, bool Marks);

/// <summary>The keyboard's ways of changing the selection.</summary>
public enum SelectionOp
{
    /// <summary>Insert or Space, as in every commander: mark or unmark the row under the cursor, and move on.</summary>
    ToggleAndAdvance,
    All,
    None,
    Invert,
}
