using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using DanceCommander.App.Services;
using DanceCommander.App.ViewModels;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Navigation;
using DcColumn = DanceCommander.Core.Columns.ColumnDefinition;

namespace DanceCommander.App.Views;

public partial class PaneView : UserControl
{
    private readonly Dictionary<GridViewColumn, string> _columnIds = [];
    private TabViewModel? _boundTab;
    private bool _suppressSelectionEvents;
    private GridViewColumn? _flexibleColumn;

    public PaneView()
    {
        InitializeComponent();

        // PaneSurfaceBrush, until the pane knows whether it is active (ShowActiveSurface). Set here
        // rather than as a <StaticResource> alias in the XAML dictionary, which WPF loads in a way
        // that broke the lookup of the resources around it.
        ShowActiveSurface();

        // A scheme switch repaints everything bound as a DynamicResource; the surface above is set
        // from code, and a row's marks come through converters, so those are redone by hand.
        Loaded += (_, _) => Services.ColourScheme.Changed += OnSchemeChanged;
        Unloaded += (_, _) => Services.ColourScheme.Changed -= OnSchemeChanged;
        HookSidewaysWheel();
        FileGrid.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));
        FileGrid.SizeChanged += (_, _) => ResizeFlexibleColumn();
        FileGrid.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnColumnDragStarted), handledEventsToo: true);
        FileGrid.AddHandler(Thumb.DragDeltaEvent, new DragDeltaEventHandler(OnColumnDragDelta), handledEventsToo: true);
        FileGrid.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnColumnDragCompleted), handledEventsToo: true);
        FileGrid.GotKeyboardFocus += OnGridGotKeyboardFocus;
        FileGrid.PreviewKeyDown += OnGridPreviewKeyDown;
        CommandBox.LostKeyboardFocus += OnCommandBoxLostFocus;
        // After the row has handled the press, which it marks handled (PaneView.DragDrop).
        FileGrid.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnGridPressHandled), handledEventsToo: true);
        PreviewMouseLeftButtonUp += OnPaneLeftButtonUp;
        // On the pane, not the grid: once the pane holds the mouse, a pointer that has left the
        // grid before travelling a row reaches the pane only.
        MouseMove += OnGridMouseMove;
        // A tab opened, closed or dragged to a new place moves the current one along the strip.
        TabStrip.ItemContainerGenerator.ItemsChanged += (_, _) => ShowBodyCorners();

        // An empty menu has to exist up front: WPF only raises ContextMenuOpening for elements
        // that already have one, and the contents are rebuilt per right-click anyway.
        FileGrid.ContextMenu = new ContextMenu();
        PopupToggle.Attach(
            BookmarksPopup, BookmarksButton,
            () => BookmarksList.ItemsSource = BookmarkItemViewModel.Arrange(Bookmarks));
        DataContextChanged += OnDataContextChanged;
    }

    public PaneViewModel? ViewModel => DataContext as PaneViewModel;

    public TabViewModel? Tab => ViewModel?.ActiveTab;

    /// <summary>Supplied by the window so the pane can resolve bookmarks and completions.</summary>
    public IHistoryService? History { get; set; }

    /// <summary>Supplied by the window: the folders on disk that carry on from a typed path.</summary>
    public FolderCompletion? Folders { get; set; }

    public IEnumerable<BookmarkItemViewModel> Bookmarks { get; set; } = [];

    /// <summary>Raised on double-click; the window turns it into the "open" command.</summary>
    public event EventHandler<FileRowViewModel>? Opened;

    /// <summary>Raised on a middle click on a row; the window runs the command settings.json names for it.</summary>
    public event EventHandler<FileRowViewModel>? MiddleClicked;

    /// <summary>Raised before the context menu shows, for the window to fill from the registry.</summary>
    public event EventHandler<ContextMenuRequest>? BuildingContextMenu;

    /// <summary>What the last right-click landed on, until the menu it opens has been built.</summary>
    private MenuTarget? _pointedAt;

    /// <param name="draft">A line to start from, the caret after it (Run with arguments).</param>
    public void FocusCommandLine(string? draft = null)
    {
        if (ViewModel is null)
            return;

        ViewModel.ShowCommandLine = true;
        Dispatcher.BeginInvoke(() =>
        {
            CommandBox.Focus();
            if (draft is null)
            {
                CommandBox.SelectAll();
                return;
            }

            _commandHistory = null;
            CommandBox.Text = draft;
            CommandBox.CaretIndex = draft.Length;
        });
    }

    /// <summary>
    /// Ctrl+E on the open line: the next shell installed here. History follows the shell, so Up
    /// starts again from that shell's newest line.
    /// </summary>
    public void NextCommandShell()
    {
        if (Tab?.NextShell() == true)
            _commandHistory = null;
    }

    // ---- focus --------------------------------------------------------------------------

    /// <summary>
    /// Whether the keyboard left this pane from its command line, and so goes back there when the
    /// pane is switched back to: it went to the list, and the line being typed sat there unfocused
    /// (friction log, 2026-10-01; still so between panes, the user said 2026-10-08).
    /// </summary>
    private bool _leftFromCommandLine;

    private void OnCommandBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        _leftFromCommandLine = e.NewFocus is not DependencyObject next || !TreeWalk.Within(this, next);

    /// <summary>
    /// Where the keyboard goes when this pane is switched to: the command line if that is where it
    /// left from and the line is still open, otherwise as <see cref="FocusGrid"/>.
    /// </summary>
    public void FocusOnReturn()
    {
        if (_leftFromCommandLine && ViewModel?.ShowCommandLine == true && Tab?.Decision is not { TakesFocus: true })
        {
            Dispatcher.BeginInvoke(() => CommandBox.Focus());
            return;
        }

        FocusGrid();
    }

    public void FocusGrid() => FocusGrid(clicked: false);

    /// <param name="clicked">
    /// The user clicked the list itself. That is a choice, and wins over a waiting question: the
    /// question stays where it is, and the list can be used meanwhile, Ctrl+T included (2026-10-14).
    /// </param>
    public void FocusGrid(bool clicked)
    {
        // A pane waiting for an answer takes the keyboard at its question, not its list: Tab back
        // to a pane with a password prompt landed on an empty listing, and from there Tab only
        // went between the two lists, never back to the prompt (2026-10-03). A question that
        // doesn't ask for the keyboard (an offer nobody requested) leaves it where it was going.
        if (!clicked && Tab?.Decision is { TakesFocus: true } waiting)
        {
            FocusDecision(waiting, attempt: 0, fresh: false);
            return;
        }

        if (FileGrid.SelectedItem is null && FileGrid.Items.Count > 0 && FileGrid.Items[0] is FileRowViewModel first)
            SelectOnly(first);

        // The item has to be scrolled into view *before* asking for its container: a row that was
        // selected while a preview covered the grid can end up virtualized away by the time the
        // preview closes
        if (FileGrid.SelectedItem is { } selected)
            FileGrid.ScrollIntoView(selected);

        var index = Math.Max(FileGrid.SelectedIndex, 0);
        var container = FileGrid.ItemContainerGenerator.ContainerFromIndex(index);

        // ScrollIntoView only schedules the scroll, so a row far down the list — the folder you just
        // came up out of — has no container yet. Falling back to focusing the list itself looked
        // harmless and was not: the selection showed on the right row, but the keyboard sat on the
        // list, and the list sends the first arrow key to the top. Lay out now and ask again.
        if (container is null && FileGrid.Items.Count > 0)
        {
            FileGrid.UpdateLayout();
            container = FileGrid.ItemContainerGenerator.ContainerFromIndex(index);
        }

        if (container is ListViewItem item)
            item.Focus();
        else
            FileGrid.Focus();
    }

    /// <summary>The row the keyboard is on in this pane's list, or null if it is not on one.</summary>
    public object? KeyboardRow =>
        Keyboard.FocusedElement is ListViewItem row && FileGrid.IsKeyboardFocusWithin ? row.DataContext : null;

    /// <summary>Whether the keyboard is anywhere in this pane's list, on a row or the list itself.</summary>
    public bool KeyboardInList => FileGrid.IsKeyboardFocusWithin;

    /// <summary>
    /// Back into the address bar with what was being typed when it was left for the palette, the
    /// caret and selection as they were. Not typing, so nothing is filled in.
    /// </summary>
    public void ResumeAddress(string text, int start, int length)
    {
        AddressBox.Focus();
        _completing = true;
        try
        {
            AddressBox.Text = text;
            AddressBox.Select(start, length);
        }
        finally
        {
            _completing = false;
        }
        _typed = text;
    }

    public void FocusAddress()
    {
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    // ---- binding ------------------------------------------------------------------------

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PaneViewModel old)
        {
            old.PropertyChanged -= OnPanePropertyChanged;
            old.Tabs.CollectionChanged -= OnTabsChanged;
        }
        if (e.NewValue is PaneViewModel added)
        {
            added.PropertyChanged += OnPanePropertyChanged;
            added.Tabs.CollectionChanged += OnTabsChanged;
            BindTab(added.ActiveTab);
        }
        ShowActiveSurface();
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.ActiveTab))
        {
            BindTab(ViewModel?.ActiveTab);
            if (ViewModel?.ActiveTab is { } front && _tabPlaces.TryGetValue(front, out var place))
                Dispatcher.BeginInvoke(() => ReturnTo(front, place), System.Windows.Threading.DispatcherPriority.Loaded);
            RevealActiveTab();
            ShowBodyCorners();
        }

        if (e.PropertyName == nameof(PaneViewModel.IsActive))
            ShowActiveSurface();

        // However the line closed, what was typed in it goes: Escape and Enter cleared it already,
        // and a line closed because its tab moved away was written for a folder no longer here.
        if (e.PropertyName == nameof(PaneViewModel.ShowCommandLine) && ViewModel?.ShowCommandLine == false)
        {
            CommandBox.Clear();
            _commandHistory = null;
        }
    }

    /// <summary>
    /// The pane's own surface colour, which everything drawn "as the pane" uses — the body, the
    /// current tab joined to it, the preview over it — so the inactive pane steps back as one piece.
    /// A local resource rather than a trigger on each, so there is one place that decides it.
    /// </summary>
    private void ShowActiveSurface()
    {
        Resources["PaneSurfaceBrush"] = FindResource(ViewModel?.IsActive == false ? "PaneInactiveBrush" : "PaneBrush");
        Resources["PaneAccentBrush"] = FindResource(_windowIsActive ? "AccentBrush" : "TextMutedBrush");
    }

    /// <summary>Whether the window has the keyboard, for the accent: the window says, see below.</summary>
    private bool _windowIsActive = true;

    /// <summary>
    /// The accent means "the keys go here", so it goes grey while they go to another window. Windows'
    /// own sign of that is a caption a shade dimmer, easy to miss, and a lost row cursor looks the
    /// same as an inactive window: the window looked focused when it wasn't (2026-10-09). Grey
    /// rather than gone, so it still says which pane the keys will come back to.
    /// </summary>
    public void ShowWindowActive(bool active)
    {
        _windowIsActive = active;
        ShowActiveSurface();
    }

    /// <summary>The rows rebuilt, not re-listed: their bindings run again against the new palette.</summary>
    private void OnSchemeChanged(object? sender, EventArgs e)
    {
        ShowActiveSurface();
        FileGrid.Items.Refresh();
    }

    /// <summary>
    /// Scrolls the tab strip so the active tab is on screen: switching with Ctrl+Tab to a tab past
    /// the edge would otherwise change the listing while the strip went on showing other tabs.
    /// After layout, because a tab just added has no container until the strip has measured it.
    /// It is brought clear of the faded edges, not just onto the screen: flush with the edge, its
    /// close button would sit under the edge, which pages instead.
    /// </summary>
    private void RevealActiveTab() =>
        Dispatcher.BeginInvoke(
            () =>
            {
                if (ViewModel?.ActiveTab is { } tab &&
                    TabStrip.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement chip)
                {
                    var edge = TabEdgeRight.Width;
                    chip.BringIntoView(new Rect(-edge, 0, chip.ActualWidth + 2 * edge, chip.ActualHeight));
                }
            },
            System.Windows.Threading.DispatcherPriority.Loaded);

    /// <summary>The wheel scrolls the strip sideways; it has no vertical to scroll.</summary>
    private void OnTabStripWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    /// <summary>
    /// Scrolling, resizing and a tab's title changing its width all move the current tab, and all
    /// come here: they change the offset, the viewport or the extent.
    /// </summary>
    private void OnTabStripScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The strip grown (a restored session's tabs arriving, a title getting longer) or the
        // window resized (restored from maximised, a splitter moved) can leave the current tab off
        // the edge with no tab change to bring it back (2026-10-14). A scroll by hand changes only
        // the offset, and is left where it was put.
        if (e.ViewportWidthChange != 0 || e.ExtentWidthChange != 0)
            RevealActiveTab();
        ShowBodyCorners();
    }

    /// <summary>
    /// Squares the pane's top corner where the current tab reaches that edge of the pane: the tab is
    /// square at the bottom, so a rounded corner under it left a notch between the two. Only there,
    /// though — a corner with no tab over it is the pane's own and stays rounded. After layout, so
    /// the tab's position is the one about to be drawn. The faded edges follow the same reckoning,
    /// so this is where they are shown too.
    /// </summary>
    private void ShowBodyCorners() =>
        Dispatcher.BeginInvoke(
            () =>
            {
                var radius = (CornerRadius)FindResource("RadiusM");
                var reachesLeft = false;
                var reachesRight = false;
                if (ViewModel is { ShowTabStrip: true, ActiveTab: { } tab } &&
                    TabStrip.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement chip)
                {
                    var left = chip.TranslatePoint(new Point(0, 0), TabScroller).X;
                    // The container includes the gap after the tab; the tab itself ends before it.
                    var gap = VisualTreeHelper.GetChildrenCount(chip) > 0 &&
                              VisualTreeHelper.GetChild(chip, 0) is FrameworkElement drawn
                        ? drawn.Margin.Right
                        : 0;
                    var right = left + chip.ActualWidth - gap;
                    var width = TabScroller.ViewportWidth;
                    const double Slack = 0.5;

                    // A tab half scrolled out of view is clipped at the edge, so it still reaches it.
                    reachesLeft = left <= Slack && right > 0;
                    reachesRight = right >= width - gap - Slack && left < width;
                    if (reachesLeft)
                        radius.TopLeft = 0;
                    if (reachesRight)
                        radius.TopRight = 0;
                }

                Body.CornerRadius = radius;
                ShowTabEdges(reachesLeft, reachesRight);
            },
            System.Windows.Threading.DispatcherPriority.Loaded);

    private void BindTab(TabViewModel? tab)
    {
        if (_boundTab is not null)
        {
            _boundTab.ColumnsChanged -= OnColumnsChanged;
            _boundTab.SelectionRequested -= OnSelectionRequested;
            _boundTab.SelectionOpRequested -= OnSelectionOpRequested;
            _boundTab.SelectionRestoreRequested -= OnSelectionRestoreRequested;
            _boundTab.ReorderedUnderCursor -= OnReorderedUnderCursor;
            _boundTab.ArrivalLanded -= OnArrivalLanded;
            _boundTab.LeavingAddress -= OnLeavingAddress;
            _boundTab.PropertyChanged -= OnTabPropertyChanged;
            // Behind another tab it is not being typed in, whatever the bar is doing now.
            _boundTab.IsEditingAddress = false;
        }

        _boundTab = tab;
        if (tab is not null)
            tab.IsEditingAddress = AddressBox.IsKeyboardFocusWithin;

        // A range belongs to the tab it was made in: with Shift still down from Ctrl+Shift+Tab,
        // the other tab's start carried over (review, 2026-10-30). The cursor's next move sets it.
        _rangeAnchorUri = null;
        _lastRange = (null, []);

        if (tab is not null)
        {
            tab.ColumnsChanged += OnColumnsChanged;
            tab.SelectionRequested += OnSelectionRequested;
            tab.SelectionOpRequested += OnSelectionOpRequested;
            tab.SelectionRestoreRequested += OnSelectionRestoreRequested;
            tab.ReorderedUnderCursor += OnReorderedUnderCursor;
            tab.ArrivalLanded += OnArrivalLanded;
            tab.LeavingAddress += OnLeavingAddress;
            tab.PropertyChanged += OnTabPropertyChanged;
            RebuildColumns();
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Going behind another tab. Said before the pane changes its front tab, so the list still
        // shows this one's rows, scrolled as they were.
        if (e.PropertyName == nameof(TabViewModel.IsCurrent) && sender is TabViewModel { IsCurrent: false } leaving)
            RememberPlace(leaving);

        if (e.PropertyName == nameof(TabViewModel.Rows))
        {
            RefreshHeaders();

            // The same place listed again — a watcher batch, badges landing, a sort — keeps its
            // scroll position; a new place starts where its listing puts it. Read now, before the
            // new rows have been laid out and the offset has moved with them.
            var sameLocation = Tab?.Uri is { } uri && uri == _rowsUri;

            // Leaving a place: where it was scrolled to is kept, so coming back (Back, Up, any way)
            // finds it as it was, not with the row you left from pinned to an edge (2026-10-07,
            // Friction). Read now, while the old rows are still laid out.
            if (!sameLocation && _rowsUri is { } left && GridScroller is { } scroller)
                RememberOffset(left, scroller.VerticalOffset);
            double? returning = !sameLocation && Tab?.Uri is { } arriving && _offsets.TryGetValue(arriving, out var was) ? was : null;

            _rowsUri = Tab?.Uri;
            FitColumnsToContent(grow: sameLocation);
            var offset = sameLocation ? GridScroller?.VerticalOffset : null;
            _scrollAskedFor = false;

            Dispatcher.BeginInvoke(
                () =>
                {
                    RestoreCursor();

                    // Unless something asked to show a particular row since — the quick filter
                    // going to its first match — in which case that row is where to be.
                    if (offset is { } kept && !_scrollAskedFor)
                        GridScroller?.ScrollToVerticalOffset(kept);
                    else if (returning is { } back && !_scrollAskedFor)
                        ScrollBackTo(back);
                },
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        if (e.PropertyName == nameof(TabViewModel.InFoundOrder))
            RefreshHeaders();

        if (e.PropertyName == nameof(TabViewModel.Decision) && Tab?.Decision is { TakesFocus: true } decision)
            FocusDecision(decision, attempt: 0, fresh: true);

        // A question answered or cancelled took the keyboard with it as it went: it was left on the
        // window, on nothing, until a key found its way back (2026-10-14). Back to the list, but
        // only when it is lost; where it went somewhere real, it stays.
        if (e.PropertyName == nameof(TabViewModel.Decision) && Tab?.Decision is null)
        {
            Dispatcher.BeginInvoke(
                () =>
                {
                    if (Keyboard.FocusedElement is null or Window or UIElement { IsVisible: false })
                        FocusGrid();
                },
                System.Windows.Threading.DispatcherPriority.Input);
        }

        // The preview is what the user just asked to look at, so it is what the keys should reach.
        if (e.PropertyName == nameof(TabViewModel.Preview) && Tab?.Preview is { IsText: true })
            Dispatcher.BeginInvoke(() => PreviewLines.Focus());
    }

    /// <summary>
    /// Puts the caret in the field a decision just raised, retrying until it takes.
    /// </summary>
    /// <remarks>
    /// The field becomes visible through a binding, and an element that is still collapsed cannot
    /// be focused -- the call quietly does nothing. One deferred attempt is usually enough, but
    /// "usually" is not good enough here: losing the race sends the user's password to the grid,
    /// which treats it as type-ahead and navigates somewhere. So it checks whether it worked.
    /// </remarks>
    /// <param name="fresh">
    /// The question has just appeared. Coming back to one already asked keeps what was typed into
    /// it: half a password is not something to wipe because the keyboard went away and returned.
    /// </param>
    private void FocusDecision(DecisionViewModel decision, int attempt, bool fresh)
    {
        const int MaxAttempts = 5;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (!ReferenceEquals(Tab?.Decision, decision))
                return; // Answered or replaced while we were waiting.

            var field = decision.IsTextInput
                ? decision.IsSecret ? (Control)DecisionSecret : DecisionText
                : DefaultOptionButton(decision);

            if (field is null || !field.IsVisible)
            {
                if (attempt < MaxAttempts)
                    FocusDecision(decision, attempt + 1, fresh);
                return;
            }

            if (decision.IsSecret && fresh)
                DecisionSecret.Clear();

            Keyboard.Focus(field);

            if (!field.IsKeyboardFocusWithin && attempt < MaxAttempts)
                FocusDecision(decision, attempt + 1, fresh);
            else if (field is TextBox box && fresh)
            {
                if (decision.SelectStemOnly)
                    box.Select(0, StemLength(box.Text));
                else
                    box.SelectAll();
            }
        });
    }

    /// <summary>
    /// Same rule as <see cref="FsEntry.Stem"/>: everything before the last dot, unless that dot is
    /// the first character (a dotfile) or the last one (a trailing dot, no extension to protect).
    /// </summary>
    private static int StemLength(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? dot : name.Length;
    }

    /// <summary>
    /// The button for the option marked default, so the keyboard starts on the safe answer.
    /// Falls back to the first option when nothing claims to be the default.
    /// </summary>
    private Button? DefaultOptionButton(DecisionViewModel decision)
    {
        if (decision.Options.Count == 0)
            return null;

        var wanted = decision.Options.FirstOrDefault(o => o.IsDefault) ?? decision.Options[0];
        var container = DecisionOptions.ItemContainerGenerator.ContainerFromItem(wanted);
        return container is null ? null : FindDescendant<Button>(container);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
            return match;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindDescendant<T>(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }

        return null;
    }

    private void OnColumnsChanged(object? sender, EventArgs e) => RebuildColumns();

    /// <summary>The location the current rows belong to, to tell a re-listing from a navigation.</summary>
    private FsUri? _rowsUri;

    /// <summary>Where each place this pane has left was scrolled to, the most recent last.</summary>
    private readonly Dictionary<FsUri, double> _offsets = [];

    private const int OffsetsKept = 200;

    private void RememberOffset(FsUri uri, double offset)
    {
        _offsets.Remove(uri);
        _offsets[uri] = offset;
        if (_offsets.Count > OffsetsKept)
            _offsets.Remove(_offsets.Keys.First());
    }

    /// <summary>
    /// Back where the place was scrolled to, as long as the row the cursor came back to is still in
    /// sight there: a list that changed since may have moved it, and then the row wins.
    /// </summary>
    private void ScrollBackTo(double offset)
    {
        if (GridScroller is not { } scroller)
            return;

        scroller.ScrollToVerticalOffset(offset);
        FileGrid.UpdateLayout();
        if (FileGrid.SelectedItem is { } row && FileGrid.ItemContainerGenerator.ContainerFromItem(row) is null)
            FileGrid.ScrollIntoView(row);
    }

    /// <summary>Where a tab was when it went behind another, by address: its rows may be listed again meanwhile.</summary>
    private sealed record TabPlace(FsUri? Uri, double Offset, FsUri? Cursor, IReadOnlySet<FsUri> Selected, bool Marks);

    /// <summary>
    /// Each tab's place in this pane. The tab keeps its selection itself, but the list changing
    /// its rows clears the list's selection, and that reaches whichever tab is bound at that
    /// moment: a tab switched away from and back came back on its first row, at the top
    /// (2026-10-15). Weak, so a closed tab takes its place with it.
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TabViewModel, TabPlace> _tabPlaces = new();

    /// <summary>
    /// A tab that leaves this pane's strip leaves its place behind. Used in the other pane, its
    /// marks and cursor are the ones made there; brought back here, the old ones put back on
    /// would be what the next Del took (review, 2026-10-15).
    /// </summary>
    private void OnTabsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel is not { } pane)
            return;
        foreach (var (tab, _) in _tabPlaces.ToList())
        {
            if (!pane.Tabs.Contains(tab))
                _tabPlaces.Remove(tab);
        }
    }

    private void RememberPlace(TabViewModel tab)
    {
        // Not one on its way out: a tab given to the other pane leaves the strip before the pane's
        // front changes, so it goes behind after it has gone.
        if (!ReferenceEquals(tab, _boundTab) || !ReferenceEquals(FileGrid.ItemsSource, tab.Rows) ||
            ViewModel?.Tabs.Contains(tab) != true)
            return;

        var selected = FileGrid.SelectedItems.Cast<FileRowViewModel>().Select(r => r.Entry.Uri).ToHashSet();
        _tabPlaces.AddOrUpdate(tab, new TabPlace(tab.Uri, GridScroller?.VerticalOffset ?? 0, tab.FocusedRow?.Entry.Uri, selected, tab.SelectionIsMarks));
    }

    /// <summary>
    /// A tab back in front, as it was left: scrolled the same, the cursor on its row, its marks
    /// marked. Only if it is still where it was left; a tab that went somewhere since starts as a
    /// navigation does.
    /// </summary>
    private void ReturnTo(TabViewModel tab, TabPlace place)
    {
        if (!ReferenceEquals(Tab, tab) || !ReferenceEquals(FileGrid.ItemsSource, tab.Rows) || tab.Uri != place.Uri)
            return;

        _rowsUri = tab.Uri;
        _cursorUri = place.Cursor;
        var cursor = tab.Rows.FirstOrDefault(r => r.Entry.Uri == place.Cursor);
        var selected = tab.Rows.Where(r => place.Selected.Contains(r.Entry.Uri)).ToList();

        GridScroller?.ScrollToVerticalOffset(place.Offset);
        FileGrid.UpdateLayout();

        // Cursor first: focusing a row selects it alone, and the marks go back on after it.
        if (cursor is not null && FileGrid.IsKeyboardFocusWithin)
            MoveCursorTo(cursor);
        if (selected.Count > 0)
            ReplaceSelection(selected, place.Marks);
        else if (cursor is not null)
            ReplaceSelection([cursor], marks: false);
    }

    /// <summary>Set when a row was explicitly brought into view after the rows last changed.</summary>
    private bool _scrollAskedFor;

    private ScrollViewer? _gridScroller;

    private ScrollViewer? GridScroller => _gridScroller ??= TreeWalk.Descendant<ScrollViewer>(FileGrid);

    /// <summary>The row the keyboard was last on, by address: by name, a flat view's second index.ts was as good as its first.</summary>
    private FsUri? _cursorUri;

    /// <summary>
    /// Where a Shift+click range starts: the row the cursor last came to without Shift held, by
    /// address. The list's own anchor moves only on its own clicks and arrows, and nearly every
    /// cursor move here goes around it (a click inside the selection, arrows with marks, Insert,
    /// a rename landing, rows rebuilt), so Shift+click ranged from a row nobody could see
    /// (2026-10-17). Ours follows the cursor wherever it goes.
    /// </summary>
    private FsUri? _rangeAnchorUri;

    private void OnGridGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is ListViewItem { DataContext: FileRowViewModel row })
        {
            _cursorUri = row.Entry.Uri;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                StartRangeAt(row.Entry.Uri);

            // The cursor is what Enter, F2 and F3 act on, so the tab hears when it moves even when
            // the selection does not — which, with marks, it no longer does.
            if (!_suppressSelectionEvents && Tab is { } tab)
                tab.SetSelection(FileGrid.SelectedItems.Cast<FileRowViewModel>(), row, tab.SelectionIsMarks);
        }
    }

    /// <summary>
    /// Puts the keyboard back on the row it was on after the row list was replaced.
    /// </summary>
    /// <remarks>
    /// Bringing an off-screen row back to focus it scrolls to it; the caller puts the scroll offset
    /// back afterwards, and WPF keeps a focused row's container alive however far it is scrolled
    /// away, so the next arrow key still starts from it.
    ///
    /// The list is rebuilt, not edited, whenever anything changes it: git badges arriving after a
    /// listing, every watcher batch, a sort. The selection was restored by name afterwards, but the
    /// keyboard was not — the focused row's container went with the old list and focus fell to the
    /// list itself, which sends the next arrow key to the top. So the selection looked right and the
    /// first Down landed on the first row. Only when focus actually fell: if it went anywhere else
    /// (the address bar, a decision) the user moved it, and this leaves it alone.
    /// </remarks>
    private void RestoreCursor()
    {
        if (!FileGrid.IsKeyboardFocused || Tab is null || _cursorUri is null)
            return;

        var row = Tab.Rows.FirstOrDefault(r => r.Entry.Uri == _cursorUri);
        if (row is null)
            return;

        if (FileGrid.ItemContainerGenerator.ContainerFromItem(row) is null)
        {
            FileGrid.ScrollIntoView(row);
            FileGrid.UpdateLayout();
        }

        if (FileGrid.ItemContainerGenerator.ContainerFromItem(row) is ListViewItem item)
        {
            // Focusing a row makes the list select it alone, so marks go back on after it, as the
            // arrows do: a watcher batch with the cursor off the marks used to take them all.
            var marked = Tab.HasMarks ? FileGrid.SelectedItems.Cast<FileRowViewModel>().ToList() : null;
            item.Focus();
            if (marked is not null)
                ReplaceSelection(marked, marks: true);
        }
    }

    /// <summary>
    /// A streamed walk has just been sorted: the row you were on is somewhere else now, so it is
    /// brought into view rather than the old scroll offset kept. Nobody on a row yet means nobody
    /// was reading anything, and the top of the sorted list is where to start.
    /// </summary>
    private void OnReorderedUnderCursor(object? sender, EventArgs e)
    {
        _scrollAskedFor = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                var row = Tab?.Rows.FirstOrDefault(r => r.Entry.Uri == _cursorUri);

                if (row is not null && FileGrid.IsKeyboardFocusWithin)
                    FileGrid.ScrollIntoView(row);
                else
                    GridScroller?.ScrollToVerticalOffset(0);
            },
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Something this application put here has landed: the cursor goes to it, so the eye can follow
    /// the files to where they went. Never with marks — someone mid-selection is busy, and moving
    /// their cursor would move what their next key acts on. Never taking the keyboard either: after
    /// F5 it stays in the pane you copied from, and this pane's cursor waits on the arrival for when
    /// you come over.
    /// </summary>
    private void OnArrivalLanded(object? sender, string name) =>
        Dispatcher.BeginInvoke(
            () =>
            {
                if (Tab is not { HasMarks: false } tab || RowNamed(name) is not { } row)
                    return;

                _scrollAskedFor = true;
                _cursorUri = row.Entry.Uri;

                if (FileGrid.IsKeyboardFocusWithin)
                {
                    // Cursor first, so the selection that follows it is taken with the cursor on it.
                    MoveCursorTo(row);
                    ReplaceSelection([row], marks: false);
                }
                else
                {
                    SelectOnly(row);
                    FileGrid.ScrollIntoView(row);
                }
            },
            System.Windows.Threading.DispatcherPriority.Loaded);

    /// <summary>
    /// The row a name means. Names repeat in a flat view or a search, and differ only by case on a
    /// server, so the row the cursor was on comes first, then the name in the folder the cursor was
    /// in (a file renamed there), then the exact name, and only then any case: the first
    /// "index.ts" took the cursor, and Del with it, after a Ctrl+R or a rename (2026-10-10).
    /// </summary>
    private FileRowViewModel? RowNamed(string name)
    {
        if (Tab is null)
            return null;
        var named = Tab.Rows.Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        var exact = named.Where(r => string.Equals(r.Name, name, StringComparison.Ordinal)).ToList();
        return named.FirstOrDefault(r => r.Entry.Uri == _cursorUri)
               ?? exact.FirstOrDefault(r => _cursorUri is not null && r.Entry.Uri.Parent == _cursorUri.Parent)
               ?? exact.FirstOrDefault()
               ?? named.FirstOrDefault();
    }

    private void OnSelectionRequested(object? sender, string name)
    {
        // A remembered row that has gone since: the first, rather than no row at all. Only when
        // nothing is selected, so a cursor already somewhere is not moved for a name that missed.
        var row = RowNamed(name)
            ?? (FileGrid.SelectedItem is null ? FileGrid.Items.OfType<FileRowViewModel>().FirstOrDefault() : null);
        if (row is null)
            return;

        _scrollAskedFor = true;

        // The keyboard only for the pane the commands act on, or one that has it already: a
        // refresh of the other pane after F5 into it took the keyboard there, while F5 and Del
        // still acted on this one. Elsewhere the row is selected and shown, and waits. So it does
        // while an address is being typed: Ctrl+T's tab opens there, and its rows arrive after.
        if ((ViewModel?.IsActive != true && !FileGrid.IsKeyboardFocusWithin) || AddressBox.IsKeyboardFocusWithin)
        {
            if (Tab is not { HasMarks: true })
                SelectOnly(row);
            FileGrid.ScrollIntoView(row);
            return;
        }

        // With marks, only the cursor goes there, as with the arrows: Ctrl+R putting the cursor
        // back on its row took the marks with it. Not FocusGrid, which goes to the first mark.
        if (Tab is { HasMarks: true } && Tab.Decision is not { TakesFocus: true })
        {
            var marked = FileGrid.SelectedItems.Cast<FileRowViewModel>().ToList();
            MoveCursorTo(row);
            ReplaceSelection(marked, marks: true);
            return;
        }

        SelectOnly(row);
        FileGrid.ScrollIntoView(row);
        FocusGrid();
    }

    /// <summary>
    /// Selection from the keyboard. The cursor is the focused row — the bar at its edge — and is not
    /// the same as being selected, so marking can move it on without disturbing what is marked.
    /// </summary>
    private void OnSelectionOpRequested(object? sender, SelectionOp op)
    {
        if (Tab is not { } tab)
            return;

        switch (op)
        {
            case SelectionOp.All:
                ReplaceSelection(tab.Rows, marks: true);
                break;

            case SelectionOp.None:
                // Back to no marks, which is the cursor's row alone: with nothing marked, that is
                // the row the commands act on, as it always was. A new range starts here: from the
                // old start, the next Shift+arrow put back what Esc had just taken off (review,
                // 2026-10-30).
                ReplaceSelection(CursorRow() is { } cursor ? [cursor] : [], marks: false);
                if (CursorRow() is { } here)
                    StartRangeAt(here.Entry.Uri);
                break;

            case SelectionOp.Invert:
                // Of the marks: with none, everything. The cursor's row standing in for them is
                // not one, so inverting used to leave it out.
                var marks = tab.HasMarks ? FileGrid.SelectedItems.Cast<FileRowViewModel>().ToHashSet() : [];
                ReplaceSelection(tab.Rows.Where(r => !marks.Contains(r)), marks: true);
                break;

            case SelectionOp.ToggleAndAdvance:
                if (CursorRow() is not { } row)
                    return;

                // Without marks the row under the cursor is selected just by being there, so
                // marking it means keeping it; otherwise the first Insert would unmark the very
                // row it was pressed on.
                var marked = tab.HasMarks ? FileGrid.SelectedItems.Cast<FileRowViewModel>().ToList() : [];
                if (!marked.Remove(row))
                    marked.Add(row);

                // Focusing a row makes the list select it alone, so the marks go back on after
                // the cursor has moved rather than being made before it.
                var next = IndexOf(tab.Rows, row) + 1;
                if (next < tab.Rows.Count)
                    MoveCursorTo(tab.Rows[next]);

                // The last mark taken off: back to the cursor's row standing in, as after Esc.
                if (marked.Count > 0)
                    ReplaceSelection(marked, marks: true);
                else
                    ReplaceSelection(CursorRow() is { } at ? [at] : [], marks: false);
                break;
        }
    }

    private static int IndexOf(IReadOnlyList<FileRowViewModel> rows, FileRowViewModel row)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], row))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// With marks, the arrows move the cursor and leave the marks alone, as in every commander;
    /// with none, they move the selection as they always did. Shift+arrows mark from the range's
    /// start to where the cursor goes, as Shift+click does: left to the list, they ranged from an
    /// anchor of its own that this pane's cursor moves never moved (2026-10-17).
    /// </summary>
    private void OnGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var shift = Keyboard.Modifiers == ModifierKeys.Shift;
        if ((Keyboard.Modifiers != ModifierKeys.None && !shift) || Tab is not { } tab || CursorRow() is not { } row)
            return;
        if (!shift && !tab.HasMarks)
            return;

        var rows = tab.Rows;
        var at = IndexOf(rows, row);
        var page = Math.Max(1, (int)((GridScroller?.ViewportHeight ?? 0) / Math.Max(1, RowHeight())) - 1);
        var target = e.Key switch
        {
            Key.Up => at - 1,
            Key.Down => at + 1,
            Key.PageUp => at - page,
            Key.PageDown => at + page,
            Key.Home => 0,
            Key.End => rows.Count - 1,
            _ => -2,
        };
        if (target == -2)
            return;

        e.Handled = true;
        target = Math.Clamp(target, 0, rows.Count - 1);
        if (shift)
        {
            MarkRangeTo(rows[target], addToMarks: false, keepMarks: true);
            return;
        }
        if (target == at)
            return;

        var marked = FileGrid.SelectedItems.Cast<FileRowViewModel>().ToList();
        MoveCursorTo(rows[target]);
        ReplaceSelection(marked, marks: true);
    }

    private double RowHeight() =>
        FileGrid.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem { ActualHeight: > 0 } item ? item.ActualHeight : 31;

    /// <summary>The row with the cursor: the focused one, or the selected one when focus is elsewhere.</summary>
    private FileRowViewModel? CursorRow() =>
        Keyboard.FocusedElement is ListViewItem { DataContext: FileRowViewModel focused } ? focused
        : FileGrid.SelectedItem as FileRowViewModel;

    /// <summary>Moves the cursor without selecting, as Ctrl+arrow does in the list.</summary>
    private void MoveCursorTo(FileRowViewModel row)
    {
        FileGrid.ScrollIntoView(row);
        FileGrid.UpdateLayout();
        (FileGrid.ItemContainerGenerator.ContainerFromItem(row) as ListViewItem)?.Focus();
    }

    /// <summary>Sets the whole selection at once, telling the tab once rather than per row.</summary>
    /// <param name="marks">Whether these are marks, or only the cursor's row standing in for them.</param>
    private void ReplaceSelection(IEnumerable<FileRowViewModel> rows, bool marks)
    {
        _suppressSelectionEvents = true;
        try
        {
            FileGrid.SelectedItems.Clear();
            foreach (var row in rows)
                FileGrid.SelectedItems.Add(row);
        }
        finally
        {
            _suppressSelectionEvents = false;
            // In the finally: an error part way, now carried past (ErrorReports), left the tab
            // with the old marks for Del while the list drew others (review, 2026-10-30). The
            // tab takes whatever the list holds, so what is drawn is what is acted on.
            Tab?.SetSelection(FileGrid.SelectedItems.Cast<FileRowViewModel>(), CursorRow(), marks);
        }
    }

    /// <summary>
    /// A row this application chose (the file just renamed or pasted, the first one): selected
    /// alone, and no mark. Said rather than left to <see cref="OnSelectionChanged"/>, which reads
    /// the modifier keys, and Ctrl is often still down from the Ctrl+V that brought the file.
    /// </summary>
    private void SelectOnly(FileRowViewModel row)
    {
        // Cleared and added, not SelectedItem = row: that does nothing when the row already is the
        // first of several selected, and the others stayed drawn as marks the tab no longer had.
        _suppressSelectionEvents = true;
        try
        {
            FileGrid.SelectedItems.Clear();
            FileGrid.SelectedItems.Add(row);
        }
        finally
        {
            _suppressSelectionEvents = false;
            // What the list holds, even after an error part way (see ReplaceSelection).
            Tab?.SetSelection(FileGrid.SelectedItems.Cast<FileRowViewModel>(), CursorRow(), marks: false);
        }
    }

    private void OnSelectionRestoreRequested(object? sender, SelectionRestore restore)
    {
        if (Tab is null)
            return;

        // By address, compared as its provider compares names (FsUri.Equals): see _selectedUris.
        var wanted = restore.Uris.ToHashSet();

        // Re-selecting would otherwise echo back into the view model and clear what we are
        // restoring, so the round trip is suppressed while we do it.
        _suppressSelectionEvents = true;
        try
        {
            FileGrid.SelectedItems.Clear();
            foreach (var row in Tab.Rows)
            {
                if (wanted.Contains(row.Entry.Uri))
                    FileGrid.SelectedItems.Add(row);
            }
        }
        finally
        {
            _suppressSelectionEvents = false;
            // The new rows, and marks still marks: the tab had kept the replaced rows, and a single
            // mark came back as the cursor's row. What the list holds, even after an error part way
            // (see ReplaceSelection).
            Tab?.SetSelection(FileGrid.SelectedItems.Cast<FileRowViewModel>(), CursorRow(), restore.Marks);
        }
    }

    // ---- columns ------------------------------------------------------------------------

    private void RebuildColumns()
    {
        if (Tab is null)
            return;

        // The descriptor holds on to every column it watches; a listing's columns go when it does.
        if (FileGrid.View is GridView old)
        {
            foreach (var column in old.Columns)
                ColumnWidth.RemoveValueChanged(column, OnFixedColumnResized);
        }

        _columnIds.Clear();
        _flexibleColumn = null;
        _resizedByHand.Clear();
        var view = new GridView
        {
            AllowsColumnReorder = true,
            ColumnHeaderContainerStyle = (Style)FindResource("ColumnHeaderStyle"),
        };

        // The name and the extension are shown in one column or two, never both: repeating the
        // extension in a column beside the name it is already part of states the same fact twice.


        foreach (var column in Tab.Columns.Columns)
        {
            var item = new GridViewColumn
            {
                Header = HeaderText(column),
                Width = column.DefaultWidth,
                CellTemplate = BuildCellTemplate(column),
            };
            if (column.Alignment == ColumnAlignment.Right)
                item.HeaderContainerStyle = (Style)FindResource("ColumnHeaderRightStyle");

            if (column.Flexible)
                _flexibleColumn = item;
            else
                ColumnWidth.AddValueChanged(item, OnFixedColumnResized);

            _columnIds[item] = column.Id;
            view.Columns.Add(item);
        }

        FileGrid.View = view;
        FitColumnsToContent(grow: false);
    }

    private static readonly DependencyPropertyDescriptor ColumnWidth =
        DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn));

    /// <summary>Columns dragged to a width by hand, which fitting leaves alone from then on.</summary>
    private readonly HashSet<GridViewColumn> _resizedByHand = [];

    /// <summary>Setting widths from here, which is not the user dragging a header.</summary>
    private bool _fitting;

    /// <summary>
    /// A fixed column changed width: the flexible one takes up the difference, so widening one
    /// column narrows the slack rather than pushing the listing past the pane's edge.
    /// </summary>
    private void OnFixedColumnResized(object? sender, EventArgs e)
    {
        if (!_fitting && sender is GridViewColumn column)
            _resizedByHand.Add(column);
        ResizeFlexibleColumn();
    }

    /// <summary>More rows than this and the defaults stand: measuring every value would cost more than it frees.</summary>
    private const int FitLimit = 2000;

    /// <summary>
    /// Narrows the fixed text columns to what they hold, never past their default width, so the
    /// flexible one gets the room: a worktree list whose Path says "C:\work" on every row, and
    /// whose Note is empty, should give that width to the branch names. Within one location it only
    /// widens (<paramref name="grow"/>), so narrowing the quick filter does not make columns jump.
    /// </summary>
    private void FitColumnsToContent(bool grow)
    {
        if (Tab is null || FileGrid.View is not GridView view)
            return;

        var rows = Tab.Rows;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(FileGrid.FontFamily, FileGrid.FontStyle, FileGrid.FontWeight, FileGrid.FontStretch);
        double Measure(string text) => text.Length == 0
            ? 0
            : new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, FileGrid.FontSize, Brushes.Black, dpi).WidthIncludingTrailingWhitespace;

        _fitting = true;
        try
        {
            foreach (var column in view.Columns)
            {
                if (column == _flexibleColumn
                    || _resizedByHand.Contains(column)
                    || !_columnIds.TryGetValue(column, out var id)
                    || Tab.Columns.Find(id) is not { Kind: ColumnKind.Text or ColumnKind.Path } definition)
                    continue;

                if (rows.Count > FitLimit)
                {
                    column.Width = definition.DefaultWidth;
                    continue;
                }

                // The cell's margin either side (ours, 8, and the 6 GridViewRowPresenter puts around
                // every cell), and the header's room for its sort arrow. Without the presenter's 12,
                // a column fitted to "drwxr-xr-x" came out just too narrow for it.
                const double CellPadding = 28, HeaderPadding = 34;
                var needed = Math.Max(
                    Measure(definition.Header) + HeaderPadding,
                    rows.Count == 0 ? 0 : rows.Max(row => Measure(row[id])) + CellPadding);
                var width = Math.Min(definition.DefaultWidth, Math.Ceiling(needed));
                column.Width = grow ? Math.Max(column.Width, width) : width;
            }
        }
        finally
        {
            _fitting = false;
        }

        ResizeFlexibleColumn();
    }

    /// <summary>
    /// Gives the flexible column whatever the fixed ones leave over. GridView has no star widths,
    /// so this is the substitute; it runs on every layout pass, which means dragging the splitter
    /// between panes reflows the listing rather than leaving a gap at its right edge.
    /// </summary>
    private void ResizeFlexibleColumn()
    {
        var viewport = ViewportWidth();
        if (_flexibleColumn is null || viewport <= 0)
            return;

        var fixedWidth = FileGrid.View is GridView view
            ? view.Columns.Where(c => c != _flexibleColumn).Sum(c => c.Width)
            : 0;
        _flexibleColumn.Width = Math.Max(viewport - fixedWidth - RoundingRoom, 140);
    }

    /// <summary>A pixel or two that rounding at other scales may add to the columns' sum.</summary>
    private const double RoundingRoom = 2;

    private ScrollContentPresenter? _viewport;

    /// <summary>
    /// The width the rows have: the list less its scroll bar while it has one. A fixed 22 pixels
    /// was kept back for a bar that may come, twice the width of ours and there whether or not it
    /// came (the user, 2026-10-22); the rows' area is watched instead, and the flexible column
    /// gives up the room when the bar appears.
    /// </summary>
    private double ViewportWidth()
    {
        // Looked for again if the list's template was put on afresh and the old one let go.
        if (_viewport is not null && !_viewport.IsDescendantOf(FileGrid))
            _viewport = null;

        if (_viewport is null
            && TreeWalk.Descendant<ScrollViewer>(FileGrid) is { } scroller
            && scroller.Template?.FindName("PART_ScrollContentPresenter", scroller) is ScrollContentPresenter rows)
        {
            _viewport = rows;
            _viewport.SizeChanged += (_, _) => ResizeFlexibleColumn();
        }

        return _viewport is { ActualWidth: > 0 } area ? area.ActualWidth : FileGrid.ActualWidth;
    }

    // ---- dragging a column's edge -------------------------------------------------------

    /// <summary>A column edge being dragged, and what the drag started from.</summary>
    /// <param name="Partner">The column on the other side of the edge that gives or takes the
    /// difference, or null when the flexible one does.</param>
    private sealed record ColumnDrag(
        GridViewColumn Column, double StartWidth, GridViewColumn? Partner, double PartnerStart, double StartX);

    private ColumnDrag? _drag;

    private const double MinColumnWidth = 24;

    /// <summary>The column whose edge <paramref name="source"/> is, if it is a header's edge at all.</summary>
    private static GridViewColumn? DraggedColumn(object source) =>
        source is Thumb { TemplatedParent: GridViewColumnHeader { Column: { } column } } ? column : null;

    /// <remarks>
    /// The width is worked out here, from how far the pointer is from where the drag began. WPF
    /// works it out from how far the pointer is from the edge, which only holds while the edge
    /// follows the pointer. With the flexible column taking up every change, an edge to its right
    /// stayed where it was, the distance never shrank, and each layout added it again: the column
    /// grew with the mouse still, or shrank to nothing (the user, 2026-10-22).
    ///
    /// So the edge follows the pointer. Left of the flexible column, that column takes up the
    /// difference as before; at it or right of it, the next column does, as a splitter between the
    /// two would. The last column's right edge is the pane's, so there the flexible one gives way.
    /// </remarks>
    private void OnColumnDragStarted(object sender, DragStartedEventArgs e)
    {
        if (DraggedColumn(e.OriginalSource) is not { } column || FileGrid.View is not GridView view)
            return;

        var at = view.Columns.IndexOf(column);
        var flexibleAt = _flexibleColumn is null ? -1 : view.Columns.IndexOf(_flexibleColumn);
        var partner = flexibleAt > at || at + 1 >= view.Columns.Count ? null : view.Columns[at + 1];

        _drag = new ColumnDrag(column, column.Width, partner, partner?.Width ?? 0, Mouse.GetPosition(FileGrid).X);
    }

    private void OnColumnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_drag is not { } drag || DraggedColumn(e.OriginalSource) != drag.Column)
            return;

        // Runs after the header's own handler, so what it set is replaced rather than added to.
        var moved = Mouse.GetPosition(FileGrid).X - drag.StartX;
        if (drag.Partner is not null)
            moved = Math.Min(moved, drag.PartnerStart - MinColumnWidth);

        var width = Math.Max(MinColumnWidth, drag.StartWidth + moved);
        drag.Column.Width = width;
        if (drag.Partner is { } partner)
            partner.Width = drag.PartnerStart - (width - drag.StartWidth);
    }

    private void OnColumnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_drag is not { } drag)
            return;

        _drag = null;
        if (e.Canceled)
        {
            drag.Column.Width = drag.StartWidth;
            if (drag.Partner is { } partner)
                partner.Width = drag.PartnerStart;
        }
    }

    private string HeaderText(DcColumn column)
    {
        if (Tab is null)
            return column.Header;

        // Found order is no column's order; an arrow would claim one the list is not in.
        if (Tab.InFoundOrder)
            return column.Header;

        var arrow = Tab.SortDescending ? "  ↓" : "  ↑";

        if (Tab.SortColumnId == column.Id)
            return column.Header + arrow;

        // Sorting by something with no column of its own — type, since Ctrl+F4 can ask for it and
        // there is no Ext column to put an arrow on. A listing in an order nothing on screen
        // explains looks like a listing in no order at all, so the name column says it instead.
        if (column.Kind == ColumnKind.Name && Tab.Columns.Find(Tab.SortColumnId) is null)
            return $"{column.Header} (by {SortLabel(Tab.SortColumnId)}){arrow}";

        return column.Header;
    }

    private static string SortLabel(string columnId) => columnId switch
    {
        ColumnIds.Extension => "type",
        ColumnIds.Modified => "date",
        ColumnIds.Size => "size",
        ColumnIds.Attributes => "attributes",
        _ => columnId,
    };

    private void RefreshHeaders()
    {
        if (FileGrid.View is not GridView view || Tab is null)
            return;

        foreach (var column in view.Columns)
        {
            if (_columnIds.TryGetValue(column, out var id) && Tab.Columns.Find(id) is { } definition)
                column.Header = HeaderText(definition);
        }
    }

    private static DataTemplate BuildCellTemplate(DcColumn column)
    {
        const string Ns = """
            xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
            xmlns:views="clr-namespace:DanceCommander.App.Views;assembly=DanceCommander"
            xmlns:conv="clr-namespace:DanceCommander.App.Converters;assembly=DanceCommander"
            """;

        var numeric = column.Kind is ColumnKind.Size or ColumnKind.Date or ColumnKind.Attributes
            ? " Typography.NumeralAlignment=\"Tabular\""
            : string.Empty;

        var alignment = column.Alignment == ColumnAlignment.Right ? "Right" : "Left";

        // The attribute letters are the one column whose content is a code rather than a value.
        if (column.Kind == ColumnKind.Attributes)
            return (DataTemplate)XamlReader.Parse($$"""
                <DataTemplate {{Ns}}>
                  <TextBlock Text="{Binding [{{column.Id}}]}" Margin="8,0" VerticalAlignment="Center"
                             Typography.NumeralAlignment="Tabular"
                             ToolTip="{Binding AttributeNames}" />
                </DataTemplate>
                """);

        // Only the flexible column is narrowed by its neighbours, so it is the only one whose value
        // can be trimmed away; a tip on the rest would fire on values you can already read.
        var tip = column.Flexible
            ? $" ToolTip=\"{{Binding [{column.Id}]}}\""
            : string.Empty;


        // Number and unit in columns of their own, both right-aligned: the numbers line up with each
        // other, and the units line up at the column's right edge, as in Explorer. As one string
        // the numbers could not line up (K is narrower than M); with the unit left-aligned in its
        // slot, the right edge was ragged.
        if (column.Kind == ColumnKind.Size)
            return (DataTemplate)XamlReader.Parse($$"""
                <DataTemplate {{Ns}}>
                  <Grid Margin="8,0,4,0" VerticalAlignment="Center">
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width="*" />
                      <ColumnDefinition Width="21" />
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column="0" Text="{Binding SizeNumber}" TextAlignment="Right"
                               Typography.NumeralAlignment="Tabular" TextTrimming="None" />
                    <TextBlock Grid.Column="1" Text="{Binding SizeUnit}" Margin="0"
                               TextAlignment="Right" TextTrimming="None" />
                  </Grid>
                </DataTemplate>
                """);

        // Date and time apart, so "Today" can stand in for the date and the time still sit where every
        // other row's does. The date's slot is as wide as the widest thing it can hold on any row: the
        // date itself, which stays in it hidden under "Today", and a hidden "Yesterday" in case a font
        // makes that the wider. The time's leading space is the one the single string had, so with the
        // setting off the column reads exactly as it did. Both right-aligned in the slot, against the
        // time: left-aligned, every "Today" left a hole before its time and the column zigzagged
        // between rows with a gap and rows without (the user, 2026-11-08).
        if (column.Kind == ColumnKind.Date && column.Id == ColumnIds.Modified)
            return (DataTemplate)XamlReader.Parse($$"""
                <DataTemplate {{Ns}}>
                  <Grid Margin="8,0" VerticalAlignment="Center">
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width="Auto" />
                      <ColumnDefinition Width="*" />
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column="0" Text="Yesterday" Visibility="Hidden" />
                    <!-- Shown when the settings ask (FriendlyDateVisibility) and the day is one of the two. -->
                    <TextBlock x:Name="Friendly" Grid.Column="0" Text="{Binding ModifiedFriendlyDay}" HorizontalAlignment="Right"
                               TextTrimming="None" ToolTip="{Binding ModifiedDay}">
                      <TextBlock.Style>
                        <Style TargetType="TextBlock">
                          <Style.BasedOn>
                            <StaticResource ResourceKey="{x:Type TextBlock}" />
                          </Style.BasedOn>
                          <Setter Property="Visibility" Value="{DynamicResource FriendlyDateVisibility}" />
                          <Style.Triggers>
                            <DataTrigger Binding="{Binding ModifiedFriendlyDay}" Value="{x:Null}">
                              <Setter Property="Visibility" Value="Collapsed" />
                            </DataTrigger>
                          </Style.Triggers>
                        </Style>
                      </TextBlock.Style>
                    </TextBlock>
                    <TextBlock Grid.Column="0" Text="{Binding ModifiedDay}" HorizontalAlignment="Right" TextTrimming="None"
                               Typography.NumeralAlignment="Tabular">
                      <TextBlock.Style>
                        <Style TargetType="TextBlock">
                          <Style.BasedOn>
                            <StaticResource ResourceKey="{x:Type TextBlock}" />
                          </Style.BasedOn>
                          <Style.Triggers>
                            <DataTrigger Binding="{Binding Visibility, ElementName=Friendly}" Value="Visible">
                              <Setter Property="Visibility" Value="Hidden" />
                            </DataTrigger>
                          </Style.Triggers>
                        </Style>
                      </TextBlock.Style>
                    </TextBlock>
                    <TextBlock Grid.Column="1" TextTrimming="CharacterEllipsis"
                               Typography.NumeralAlignment="Tabular"><Run Text=" " /><Run Text="{Binding ModifiedTime, Mode=OneWay}" /></TextBlock>
                  </Grid>
                </DataTemplate>
                """);

        if (column.Kind == ColumnKind.Path)
            return (DataTemplate)XamlReader.Parse($$"""
                <DataTemplate {{Ns}}>
                  <TextBlock views:PathTrim.Source="{Binding [{{column.Id}}]}" Margin="8,0"
                             VerticalAlignment="Center" HorizontalAlignment="Stretch"
                             TextTrimming="CharacterEllipsis" ToolTip="{Binding [{{column.Id}}]}" />
                </DataTemplate>
                """);

        var xaml = column.Kind == ColumnKind.Name
            ? $$"""
                <DataTemplate {{Ns}}>
                  <DataTemplate.Resources>
                    <conv:MarkerBrushConverter x:Key="MarkerBrush" />
                    <conv:HighlightBrushConverter x:Key="FadedBrush" Fade="0.55" />
                    <conv:NullToVisibilityConverter x:Key="NullToVisibility" />
                    <!--
                      A part of the row that sets its own colour (the git letter, the extension,
                      the glyphs): on a selected row in a contrast theme it takes the row's
                      HighlightText instead, or it would sit on the theme's Highlight in a colour
                      never meant for it (FileRowStyle, Controls.xaml). Its own colour goes in a
                      Setter of a style based on this one; set on the element, it would outrank
                      the trigger. Here rather than in the theme, so the lookup can't miss.
                    -->
                    <Style x:Key="RowPartTextStyle" TargetType="TextBlock">
                      <Style.Triggers>
                        <MultiDataTrigger>
                          <MultiDataTrigger.Conditions>
                            <Condition Value="True">
                              <Condition.Binding>
                                <Binding Path="IsSelected">
                                  <Binding.RelativeSource>
                                    <RelativeSource Mode="FindAncestor" AncestorType="ListViewItem" />
                                  </Binding.RelativeSource>
                                </Binding>
                              </Condition.Binding>
                            </Condition>
                            <Condition Binding="{Binding Path=(SystemParameters.HighContrast)}" Value="True" />
                          </MultiDataTrigger.Conditions>
                          <Setter Property="Foreground">
                            <Setter.Value>
                              <DynamicResource ResourceKey="{x:Static SystemColors.HighlightTextBrushKey}" />
                            </Setter.Value>
                          </Setter>
                        </MultiDataTrigger>
                      </Style.Triggers>
                    </Style>
                  </DataTemplate.Resources>
                  <Grid Margin="8,0">
                    <Grid.ColumnDefinitions>
                      <!--
                        The status gutter, ahead of the icon and always the same width whether
                        there is a mark in it or not. Between the icon and the name it prised the
                        two apart, and a gutter that collapses when empty makes the icons of one
                        listing sit in a different place from the icons of the next. Wide enough
                        for a bold "M", which is the widest of the letters git uses; at twelve it
                        was the narrowest, and every M came out as an ellipsis.
                      -->
                      <ColumnDefinition Width="18" />
                      <ColumnDefinition Width="Auto" />
                      <ColumnDefinition Width="*" MinWidth="90" />
                    </Grid.ColumnDefinitions>
                    <Image Grid.Column="1" Source="{Binding Icon}" Width="16" Height="16"
                           Margin="0,0,7,0" SnapsToDevicePixels="True" />
                    <!--
                      Git's own letter for the state, in git's own colour. It inherits the row's
                      font and size deliberately: vertical centring aligns the centres of two
                      boxes, not their baselines, so a smaller font here sat visibly higher than
                      the name beside it. Same size, same box, same baseline, no nudge factor.

                      Right-aligned, so the gap between the mark and the icon is the same for every
                      letter. Aligned left it was the column width minus the glyph, which made a
                      narrow "?" float away from its icon while a wide "M" pressed against one.
                      The ragged edge moves to the outside, against the pane, where a couple of
                      pixels go unnoticed.
                    -->
                    <TextBlock Grid.Column="0" Text="{Binding Marker.Glyph}"
                               VerticalAlignment="Center" FontWeight="SemiBold"
                               TextAlignment="Right" Margin="0,0,4,0" TextTrimming="None"
                               ToolTip="{Binding Marker.Meaning}">
                      <!-- Property-element syntax because attribute syntax would end in two
                           consecutive braces, which a $$ raw string reads as an interpolation. In a
                           style's setter, so a selected row in a contrast theme can override it
                           (RowPartTextStyle); the same for the parts below that colour themselves. -->
                      <TextBlock.Style>
                        <Style TargetType="TextBlock" BasedOn="{StaticResource RowPartTextStyle}">
                          <Setter Property="Foreground">
                            <Setter.Value>
                              <Binding Path="Marker" Converter="{StaticResource MarkerBrush}" />
                            </Setter.Value>
                          </Setter>
                        </Style>
                      </TextBlock.Style>
                    </TextBlock>
                    <!--
                      Stem and extension as two blocks in a panel that trims the stem and keeps the
                      extension against it, so a long name loses its middle and still says what kind
                      of file it is (StemSuffixPanel).
                    -->
                    <views:StemSuffixPanel Grid.Column="2" VerticalAlignment="Center">
                      <!-- Struck through while a Del has it (FileRowViewModel.IsLeaving). -->
                      <TextBlock Text="{Binding Stem}" TextTrimming="CharacterEllipsis">
                        <TextBlock.Style>
                          <!-- On the default text style, which a style of its own would otherwise
                               replace; property-element syntax for the same two braces as above. -->
                          <Style TargetType="TextBlock">
                            <Style.BasedOn>
                              <StaticResource ResourceKey="{x:Type TextBlock}" />
                            </Style.BasedOn>
                            <Style.Triggers>
                              <DataTrigger Binding="{Binding IsLeaving}" Value="True">
                                <Setter Property="TextDecorations" Value="Strikethrough" />
                              </DataTrigger>
                            </Style.Triggers>
                          </Style>
                        </TextBlock.Style>
                      </TextBlock>
                      <TextBlock Text="{Binding Suffix}" TextTrimming="CharacterEllipsis">
                        <TextBlock.Style>
                          <Style TargetType="TextBlock" BasedOn="{StaticResource RowPartTextStyle}">
                            <Setter Property="Foreground">
                              <Setter.Value>
                                <Binding Path="Highlight" Converter="{StaticResource FadedBrush}" />
                              </Setter.Value>
                            </Setter>
                            <Style.Triggers>
                              <DataTrigger Binding="{Binding IsLeaving}" Value="True">
                                <Setter Property="TextDecorations" Value="Strikethrough" />
                              </DataTrigger>
                            </Style.Triggers>
                          </Style>
                        </TextBlock.Style>
                      </TextBlock>
                      <!-- Link and read-only, as glyphs right after the name: what the Attr column
                           said in letters, where the eye already is, and absent on nearly every row. -->
                      <TextBlock Text="{Binding NameGlyphs}" Margin="6,1,0,0" VerticalAlignment="Center"
                                 FontFamily="{DynamicResource GlyphFontFamily}" FontSize="{DynamicResource FontSizeSmall}"
                                 TextTrimming="None" ToolTip="{Binding NameGlyphTip}">
                        <TextBlock.Style>
                          <Style TargetType="TextBlock" BasedOn="{StaticResource RowPartTextStyle}">
                            <Setter Property="Foreground" Value="{DynamicResource TextMutedBrush}" />
                          </Style>
                        </TextBlock.Style>
                      </TextBlock>
                      <!-- OneDrive's state, on the cursor row unless the settings say every row
                           (CloudStatusVisibility). Collapsed when there is none, so moving the
                           cursor onto an ordinary row never nudges the badge after it. -->
                      <TextBlock Text="{Binding CloudGlyph}" Margin="0,1,0,0" VerticalAlignment="Center"
                                 FontFamily="{DynamicResource GlyphFontFamily}" FontSize="{DynamicResource FontSizeSmall}"
                                 TextTrimming="None" ToolTip="{Binding NameGlyphTip}">
                        <TextBlock.Style>
                          <Style TargetType="TextBlock" BasedOn="{StaticResource RowPartTextStyle}">
                            <Setter Property="Foreground" Value="{DynamicResource TextMutedBrush}" />
                            <Setter Property="Visibility" Value="{DynamicResource CloudStatusVisibility}" />
                            <Style.Triggers>
                              <DataTrigger Value="True">
                                <DataTrigger.Binding>
                                  <Binding Path="IsKeyboardFocused">
                                    <Binding.RelativeSource>
                                      <RelativeSource Mode="FindAncestor" AncestorType="ListViewItem" />
                                    </Binding.RelativeSource>
                                  </Binding>
                                </DataTrigger.Binding>
                                <Setter Property="Visibility" Value="Visible" />
                              </DataTrigger>
                              <DataTrigger Binding="{Binding CloudGlyph}" Value="">
                                <Setter Property="Visibility" Value="Collapsed" />
                              </DataTrigger>
                            </Style.Triggers>
                          </Style>
                        </TextBlock.Style>
                      </TextBlock>
                      <!--
                        The branch of a repository folder, as the same pill the footer uses for the
                        branch you are in: one fact, one look. Right after the name, like the glyphs;
                        at the far end of the column it belonged to no row in particular. Capped, so a
                        long branch trims before the name has to. Dimmed with a brush, never with
                        Opacity: WPF drops ClearType for anything drawn translucent.
                      -->
                      <Border Margin="8,0,0,0" Padding="6,0" VerticalAlignment="Center"
                              CornerRadius="{DynamicResource RadiusS}" Background="{DynamicResource HoverBrush}"
                              ToolTip="{Binding Badge}">
                        <Border.Visibility>
                          <Binding Path="Badge" Converter="{StaticResource NullToVisibility}" />
                        </Border.Visibility>
                        <TextBlock FontSize="{DynamicResource FontSizeSmall}" Foreground="{DynamicResource TextSecondaryBrush}" MaxWidth="160" TextTrimming="CharacterEllipsis"><Run Text="⎇ " /><Run Text="{Binding Badge, Mode=OneWay}" /></TextBlock>
                      </Border>
                    </views:StemSuffixPanel>
                  </Grid>
                </DataTemplate>
                """
            : $$"""
                <DataTemplate {{Ns}}>
                  <TextBlock Text="{Binding [{{column.Id}}]}" Margin="8,0" VerticalAlignment="Center"
                             HorizontalAlignment="Stretch" TextTrimming="CharacterEllipsis"
                             TextAlignment="{{alignment}}"{{numeric}}{{tip}} />
                </DataTemplate>
                """;

        return (DataTemplate)XamlReader.Parse(xaml);
    }

    // ---- grid ---------------------------------------------------------------------------

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column })
            return;
        if (!_columnIds.TryGetValue(column, out var id))
            return;

        Tab?.SortBy(id);
        RefreshHeaders();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents)
            return;

        // The list's own selecting, by mouse or Shift+arrows. Ctrl or Shift held, or more than one
        // row, is marking; a plain click selects that row alone, which as in Explorer leaves no
        // marks, just the cursor there. What this application selects itself says which it is
        // (ReplaceSelection, SelectOnly), so a key still down from a command can't make a mark.
        var selected = FileGrid.SelectedItems.Cast<FileRowViewModel>().ToList();
        var marking = selected.Count > 1 || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        Tab?.SetSelection(selected, CursorRow(), marking);
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        if (FindRow(source) is { } row)
        {
            Opened?.Invoke(this, row);
            return;
        }

        // Empty space below the rows: up, which is what double-clicking ".." gave a mouse user. Not
        // on a column header or the scroll bar, which are also "not a row".
        if (TreeWalk.Ancestor<GridViewColumnHeader>(source) is null &&
            TreeWalk.Ancestor<System.Windows.Controls.Primitives.ScrollBar>(source) is null)
        {
            _ = Tab?.GoUpAsync();
        }
    }

    /// <summary>The row the middle button went down on, until it comes up.</summary>
    private FileRowViewModel? _middlePressed;

    /// <summary>
    /// A middle click is a press and a release on the same row, as a browser's is on a link: one
    /// that slides off to another row opens neither. The cursor and the marks stay where they were,
    /// as a link's page stays where it was.
    /// </summary>
    private void OnGridMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
            return;
        _middlePressed = e.OriginalSource is DependencyObject source ? FindRow(source) : null;
        e.Handled = _middlePressed is not null;
    }

    private void OnGridMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
            return;
        var pressed = _middlePressed;
        _middlePressed = null;
        if (pressed is not null && e.OriginalSource is DependencyObject source && ReferenceEquals(FindRow(source), pressed))
        {
            e.Handled = true;
            MiddleClicked?.Invoke(this, pressed);
        }
    }

    private static FileRowViewModel? FindRow(DependencyObject source) =>
        TreeWalk.Ancestor<ListViewItem>(source)?.DataContext as FileRowViewModel;

    // ---- toolbar ------------------------------------------------------------------------

    // A click that is the end of a hold which opened a menu is not a click (PaneView.HistoryMenus.cs).
    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (!TakeHeldMenu())
            _ = Tab?.GoBackAsync();
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        if (!TakeHeldMenu())
            _ = Tab?.GoForwardAsync();
    }

    private void OnUpClick(object sender, RoutedEventArgs e)
    {
        if (!TakeHeldMenu())
            _ = Tab?.GoUpAsync();
    }

    private void OnClosePreview(object sender, RoutedEventArgs e)
    {
        if (Tab is null)
            return;

        // The keyboard was in the preview, which is about to collapse; left there it goes nowhere.
        // After layout, because the grid is under the preview until the binding has hidden it.
        Tab.Preview = null;
        Dispatcher.BeginInvoke(() => FocusGrid(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnCopyOutput(object sender, RoutedEventArgs e)
    {
        if (Tab?.Output is not { } output)
            return;

        Tab.Report(WindowsClipboard.PutText(output.Text)
            ? new Notice("Output copied to the clipboard.")
            : WindowsClipboard.CouldNotCopy);
    }

    /// <summary>
    /// Follows the end of a job's output the way a terminal does: stays at the bottom while you
    /// are at the bottom, and leaves you alone once you scroll up to read something.
    /// </summary>
    private void OnOutputTextUpdated(object? sender, System.Windows.Data.DataTransferEventArgs e)
    {
        // Measured before the new text is laid out, so this is "were you at the end of what was
        // there", which is the question that matters. A panel that just appeared counts as at the
        // end, so a build opens showing its latest line rather than its first.
        var atEnd = OutputScroller.VerticalOffset >= OutputScroller.ScrollableHeight - 2;
        if (atEnd)
            Dispatcher.InvokeAsync(OutputScroller.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnDismissOutput(object sender, RoutedEventArgs e)
    {
        if (Tab is not null)
            Tab.Output = null;
    }

    /// <summary>
    /// Right-clicking something outside the selection makes it the selection first, which is what
    /// every file manager does and what stops a menu acting on entries you cannot see.
    /// </summary>
    /// <remarks>
    /// Empty space opens the folder's menu and leaves the selection alone (§6.18). It used to build
    /// the selection's menu instead, because nothing recorded that the click missed every row — so
    /// with anything selected there was no way to reach the folder's commands with the mouse.
    /// </remarks>
    private void OnGridRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || FindRow(source) is not { } row)
        {
            _pointedAt = MenuTarget.Location;
            return;
        }

        _pointedAt = MenuTarget.Selection;

        // As Explorer: a row outside the selection is the menu's alone, and the marks go.
        if (!FileGrid.SelectedItems.Contains(row))
            SelectOnly(row);
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = FileGrid.ContextMenu ??= new ContextMenu();
        menu.Items.Clear();

        // From the keyboard (Shift+F10, the Menu key) there was no click to go by, so the menu is
        // about whatever the keyboard is on: the selection, the ".." row, or else the folder.
        var target = _pointedAt ?? (Tab?.SelectedEntries.Count > 0 ? MenuTarget.Selection : MenuTarget.Location);
        _pointedAt = null;

        BuildingContextMenu?.Invoke(this, new ContextMenuRequest(menu, target));

        if (menu.Items.Count == 0)
            e.Handled = true; // nothing applies: no empty box
    }

    /// <summary>Where Up and Down are in the line history, from the moment the first is pressed.</summary>
    private Core.Navigation.HistoryCursor? _commandHistory;

    private void OnCommandKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is null)
            return;

        switch (e.Key)
        {
            // Earlier lines typed to the same shell, as in every shell (COMMAND_LINE_PLAN.md C).
            case Key.Up or Key.Down when Tab is not null:
                e.Handled = true;
                _commandHistory ??= new Core.Navigation.HistoryCursor(Tab.CommandLines(), CommandBox.Text);
                if ((e.Key == Key.Up ? _commandHistory.Older() : _commandHistory.Newer()) is { } recalled)
                {
                    CommandBox.Text = recalled;
                    CommandBox.CaretIndex = recalled.Length;
                }
                break;

            case Key.Enter:
                e.Handled = true;
                _commandHistory = null;
                var line = CommandBox.Text.Trim();
                CommandBox.Clear();
                if (line.Length == 0)
                    break;

                // One command, then back to the files: the output is what you look at next, and
                // Ctrl+E brings the line straight back for another. Sent before the line closes,
                // because closing it goes back to the configured shell, and the line was typed to
                // the one the prompt showed. FocusGrid still lets a question it asks take the keys.
                CommandSubmitted?.Invoke(this, line);
                ViewModel.ShowCommandLine = false;
                FocusGrid();
                break;

            case Key.Escape:
                e.Handled = true;
                _commandHistory = null;
                CommandBox.Clear();
                ViewModel.ShowCommandLine = false;
                FocusGrid();
                break;
        }
    }

    /// <summary>A command line entered by the user; the window runs it in the current directory.</summary>
    public event EventHandler<string>? CommandSubmitted;

    private void OnDismissNotice(object sender, RoutedEventArgs e)
    {
        if (Tab is not null)
            Tab.Notice = null;
    }

    /// <summary>
    /// Typed characters narrow the listing instead of jumping the cursor through it.
    /// </summary>
    /// <remarks>
    /// This is where the two readings of "start typing" are decided. Type-ahead moves the cursor to
    /// one row; filtering shows every row that matches with the first already selected, so Enter
    /// still opens what type-ahead would have found and the rest are there to see. The second is
    /// the first plus information, which is why there is no setting.
    /// </remarks>
    private void OnGridTextInput(object sender, TextCompositionEventArgs e)
    {
        if (Tab is null || e.Text.Length == 0)
            return;

        foreach (var character in e.Text)
        {
            // A filter never starts with a space. Bare Space belongs to the listing, and nobody
            // means to type a leading one; once a filter is under way a space is part of a name.
            if (char.IsControl(character) || (character == ' ' && !Tab.IsFiltering))
                return;
        }

        e.Handled = true;
        foreach (var character in e.Text)
            Tab.FilterBy(character);
    }

    private void OnCopyPreview(object sender, RoutedEventArgs e)
    {
        // Left uncaught, a clipboard another program was holding took the whole window down here
        // (2026-10-20).
        if (Tab?.Preview is { IsText: true } preview
            && !WindowsClipboard.PutText(preview.TextOf(PreviewLines.SelectedItems.Cast<PreviewLine>())))
            Tab.Report(WindowsClipboard.CouldNotCopy);
    }

    /// <summary>
    /// True while the keyboard is inside an open preview, which is what decides who owns Ctrl+C.
    /// </summary>
    public bool IsPreviewFocused => Tab?.Preview is not null && PreviewLines.IsKeyboardFocusWithin;

    /// <summary>
    /// Ctrl+C in a preview copies the lines you picked out, not the file you are standing on.
    /// </summary>
    /// <remarks>
    /// The window's global handler defers to this (see MainWindow.OnPreviewKeyDown) rather than
    /// the other way round, because a tunnelled key that has already been marked handled never
    /// reaches this control at all. Taking a few lines out of something you are not going to open
    /// is the reason the preview has a Copy button; the key that everyone reaches for first should
    /// do the same thing the button does.
    /// </remarks>
    private void OnPreviewLinesKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control)
            return;

        e.Handled = true;
        OnCopyPreview(sender, e);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _ = Tab?.RefreshAsync();
        FocusGrid();
    }

    private void OnStopLoading(object sender, RoutedEventArgs e)
    {
        Tab?.StopLoading();
        FocusGrid();
    }

    private void OnCloseTabClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && sender is FrameworkElement { DataContext: TabViewModel tab })
            ViewModel.CloseTab(tab);
        e.Handled = true;
    }

    /// <summary>Middle-click closes a tab, as in a browser: the close button shows only on hover.</summary>
    private void OnTabMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
            return;
        if (ViewModel is not null && sender is FrameworkElement { DataContext: TabViewModel tab })
            ViewModel.CloseTab(tab);
        e.Handled = true;
    }

    private async void OnBookmarkChosen(object sender, RoutedEventArgs e)
    {
        BookmarksPopup.IsOpen = false;
        if (sender is not FrameworkElement { Tag: FsUri uri } || Tab is not { } tab)
            return;

        // The keyboard was in the popup, which has closed. Back to the listing you just went to,
        // so typing filters it and the arrows move in it.
        await tab.NavigateAsync(uri);
        FocusGrid();
    }

    private async void OnManageBookmarksClick(object sender, RoutedEventArgs e)
    {
        BookmarksPopup.IsOpen = false;
        if (Tab is not { } tab)
            return;

        await tab.NavigateAsync(AboutPages.Bookmarks);
        FocusGrid();
    }

    private void OnEditBookmarksClick(object sender, RoutedEventArgs e)
    {
        BookmarksPopup.IsOpen = false;
        var path = System.IO.Path.Combine(App.DataDirectory, "bookmarks.json");
        if (System.IO.File.Exists(path))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ---- address bar --------------------------------------------------------------------

    /// <summary>
    /// Clicks in the address bar, as Explorer has them: the first click into it selects the whole
    /// address (to type over it), a click after that places the caret, a double-click selects the
    /// path part under the pointer, a triple-click the whole address again.
    /// </summary>
    /// <remarks>
    /// Selecting on focus alone does not survive a mouse click: the text box places its caret after
    /// the focus handler has selected everything, so the first click has to be taken over here.
    /// </remarks>
    private void OnAddressMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!AddressBox.IsKeyboardFocusWithin)
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
            e.Handled = true;
            return;
        }

        switch (e.ClickCount)
        {
            case 2:
                var index = AddressBox.GetCharacterIndexFromPoint(e.GetPosition(AddressBox), snapToText: true);
                var (start, length) = PathText.SegmentAt(AddressBox.Text, index < 0 ? AddressBox.Text.Length : index);
                AddressBox.Select(start, length);
                e.Handled = true;
                break;

            case >= 3:
                AddressBox.SelectAll();
                e.Handled = true;
                break;
        }
    }

    private void OnAddressFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Back from its own right-click menu is not arriving. The menu hands the keyboard back
        // before its command runs, so selecting everything here made Copy copy the whole address
        // instead of the part selected.
        if (!IsOwnMenu(e.OldFocus))
            AddressBox.SelectAll();
        if (Tab is { } tab)
            tab.IsEditingAddress = true;
        ShowAddressDisplay();
    }

    private void OnAddressLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        SuggestionPopup.IsOpen = false;
        // Let go of it here, not when it returns: coming back to the same folder before then
        // would otherwise take it for still on its way and never list.
        _folderListing?.Cancel();
        _folderListing = null;
        _folderListed = null;
        _stepped = null;
        Folders?.Forget();

        // Still editing while its menu is open: the muted rendering would lie over the selection
        // the menu is about to act on.
        if (IsOwnMenu(e.NewFocus))
            return;

        // Left for somewhere else in the window, the bar says where the pane is again: a path
        // stepped to or half typed, left standing, reads as the pane's location, and a copy to
        // "the other pane" goes somewhere else (review, 2026-10-12). Not when Enter is on its way
        // there, nor when the whole window lost the keyboard: back from fetching a name to paste,
        // what was typed is still there.
        if (IsInThisWindow(e.NewFocus) && Tab is { } tab)
        {
            // The tab may write the address again: where it is, or, after Enter, where it is going.
            tab.IsEditingAddress = false;
            if (!_accepting && tab.Location is { } here)
                AddressBox.Text = here.Display;
        }
        ShowAddressDisplay();
    }

    // Leaving the bar is what lets the tab write the address again (OnAddressLostFocus): the
    // keyboard goes to the list, where it lands after the same move made with a key. With the
    // window not holding the keyboard, what was typed is let go of all the same.
    private void OnLeavingAddress(object? sender, EventArgs e)
    {
        if (AddressBox.IsKeyboardFocusWithin)
            FocusGrid();
        else if (sender is TabViewModel tab)
            tab.IsEditingAddress = false;
    }

    private bool IsInThisWindow(IInputElement? element) =>
        element is DependencyObject node && Window.GetWindow(node) is { } window && window == Window.GetWindow(this);

    private bool IsOwnMenu(IInputElement? element) =>
        element is DependencyObject node
        && TreeWalk.Ancestor<ContextMenu>(node) is { } menu
        && ReferenceEquals(menu.PlacementTarget, AddressBox);

    private void OnAddressSizeChanged(object sender, SizeChangedEventArgs e) => ShowAddressDisplay();

    /// <summary>A scheme of two letters or more — so "C:" is a drive, not a scheme — then the rest.</summary>
    private static readonly System.Text.RegularExpressions.Regex AddressParts = new(
        @"^(?<scheme>[A-Za-z][A-Za-z0-9+.\-]+:(?://)?)(?<body>[^?]*)(?<parameter>\?.*)?$");

    /// <summary>
    /// Shows the muted-scheme rendering over the address box when it is not being edited and the
    /// address has a scheme; otherwise the box shows its own text as usual.
    /// </summary>
    private void ShowAddressDisplay()
    {
        var match = AddressParts.Match(AddressBox.Text);
        var show = match.Success && !AddressBox.IsKeyboardFocusWithin && AddressBox.IsLoaded;

        if (!show)
        {
            AddressDisplay.Visibility = Visibility.Collapsed;
            AddressBox.ClearValue(ForegroundProperty);
            return;
        }

        AddressScheme.Text = match.Groups["scheme"].Value;
        AddressBody.Text = match.Groups["body"].Value;
        AddressParameter.Text = match.Groups["parameter"].Value;

        // On the box's own first character, wherever its template puts it.
        AddressBox.UpdateLayout();
        var first = AddressBox.GetRectFromCharacterIndex(0);
        if (first.IsEmpty)
        {
            AddressDisplay.Visibility = Visibility.Collapsed;
            AddressBox.ClearValue(ForegroundProperty);
            return;
        }

        AddressDisplay.Margin = new Thickness(first.Left, first.Top, 0, 0);
        AddressDisplay.MaxWidth = Math.Max(0, AddressBox.ActualWidth - first.Left - 8);
        AddressDisplay.Visibility = Visibility.Visible;
        AddressBox.Foreground = Brushes.Transparent;
    }

    /// <summary>Filling in a completion, which changes the text but is not typing.</summary>
    private bool _completing;

    private void OnAddressTextChanged(object sender, TextChangedEventArgs e)
    {
        ShowAddressDisplay();

        if (_completing)
            return;

        if (_accepting || !AddressBox.IsKeyboardFocusWithin || History is null)
        {
            SuggestionPopup.IsOpen = false;
            return;
        }

        var typed = AddressBox.Text;
        _typed = typed;
        _stepped = null;
        _dismissed = false;
        _completedTo = null;
        ShowSuggestions(typed);
        CompleteInline(typed, e);
    }

    /// <summary>
    /// What was last actually typed, as opposed to filled in: the arrow keys write each suggestion
    /// into the box, and the search entry stands for this text rather than for an address.
    /// </summary>
    private string _typed = string.Empty;

    /// <summary>The "Search for" entry in the list now on offer, if there is one.</summary>
    private Suggestion? _searchSuggestion;

    /// <summary>
    /// The place the arrow keys last wrote into the box, until anything is typed. Enter goes to it
    /// rather than reading the text back: a name with "!", "%" or a trailing space does not parse
    /// back to the folder it came from.
    /// </summary>
    private Suggestion? _stepped;

    /// <summary>Escape closed the list; a listing that arrives later does not open it again.</summary>
    private bool _dismissed;

    /// <summary>The disk being asked for a folder's folders; cancelled by the next one.</summary>
    private CancellationTokenSource? _folderListing;

    /// <summary>Which folder that is, so that the letters typed meanwhile do not start it again.</summary>
    private FsUri? _folderListed;

    private void ShowSuggestions(string text)
    {
        if (History is null)
            return;

        List<Suggestion> suggestions = [];
        if (!AddressSearch.IsExplicit(text))
        {
            // The folders that exist first: a path being typed is after the next name in it, and
            // these are exactly those. Places visited follow, less any that are already there.
            if (Folders is { } folders && FolderCompletion.Split(text) is { } split)
            {
                if (folders.Cached(split.Folder, split.Prefix) is { } found)
                    suggestions.AddRange(found.Select(uri => new Suggestion(uri.Display, string.Empty, uri, IsBookmark: false)));
                else
                    ListFolders(folders, split.Folder);
            }

            suggestions.AddRange(History.Suggest(text).Where(s => suggestions.All(f => f.Uri != s.Uri)));
        }

        // Last, so it never moves the places above it out from under the arrow keys, and never
        // the one Enter takes unless it is the only thing on offer.
        _searchSuggestion = SearchFor(text) is { } search
            ? new Suggestion(
                $"Search for “{search.Parameter}”",
                search.Scheme == FsUri.EverywhereScheme
                    ? $"everywhere, from {search.Inner!.Display}"
                    : $"in {search.Inner!.Display}",
                search,
                IsBookmark: false)
            : null;
        if (_searchSuggestion is not null)
            suggestions.Add(_searchSuggestion);

        SuggestionList.ItemsSource = suggestions;
        SuggestionPopup.IsOpen = suggestions.Count > 0;
    }

    /// <summary>
    /// Goes to the disk for a folder not listed yet, and shows the list again once it is in. The
    /// places from history are on show meanwhile, so typing never waits on the disk.
    /// </summary>
    private async void ListFolders(FolderCompletion folders, FsUri folder)
    {
        if (_folderListing is not null && _folderListed == folder)
            return;

        _folderListing?.Cancel();
        var listing = _folderListing = new CancellationTokenSource();
        _folderListed = folder;
        try
        {
            await folders.ListAsync(folder, string.Empty, listing.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_folderListing, listing))
            {
                _folderListing = null;
                _folderListed = null;
            }
            listing.Dispose();
        }

        // Only while what is typed is still in that folder, and not once the arrow keys are in
        // the list: rebuilding it would move the highlight out from under them (§1.3).
        if (FolderCompletion.Split(_typed)?.Folder == folder
            && !_accepting && !_dismissed && AddressBox.IsKeyboardFocusWithin && SuggestionList.SelectedIndex < 0)
            ShowSuggestions(_typed);
    }

    /// <summary>The search the text stands for from here, or null when it is an address.</summary>
    private FsUri? SearchFor(string text) =>
        Tab?.Uri is { } here && AddressSearch.QueryIn(text) is { } query ? AddressSearch.Everywhere(here, query) : null;

    /// <summary>
    /// The address last filled in by <see cref="CompleteInline"/>, while its selected tail is still
    /// there to accept. Null once anything else changes the text.
    /// </summary>
    private string? _completedTo;

    /// <summary>
    /// Takes a completion still on offer: the caret goes to the end, and the list catches up with
    /// the longer address, ready for the next separator. False when there is none, so Tab keeps
    /// its usual meaning.
    /// </summary>
    private bool AcceptCompletion()
    {
        var text = AddressBox.Text;
        if (_completedTo is null
            || text != _completedTo
            || AddressBox.SelectionLength == 0
            || AddressBox.SelectionStart + AddressBox.SelectionLength != text.Length)
            return false;

        _completedTo = null;
        _typed = text;
        AddressBox.Select(text.Length, 0);
        ShowSuggestions(text);
        return true;
    }

    /// <summary>
    /// Fills in the rest of the address after the caret, selected, as a browser does: Enter takes
    /// it, typing on replaces it, Backspace or Delete drops it. Only when something was just typed
    /// at the end — completing after a deletion would put straight back what was just removed.
    /// </summary>
    private void CompleteInline(string typed, TextChangedEventArgs e)
    {
        if (!e.Changes.Any(change => change.AddedLength > 0)
            || AddressBox.CaretIndex != typed.Length
            || AddressBox.SelectionLength > 0
            || History?.Complete(typed) is not { } completion
            || completion.Length <= typed.Length
            || IsGoneFromItsFolder(typed, completion))
            return;

        // The whole text rather than just the rest, so the case is the address's own: "/Var" on a
        // server that has "/var" would otherwise complete to a place that does not exist.
        _completing = true;
        try
        {
            AddressBox.Text = completion;
            AddressBox.Select(typed.Length, completion.Length - typed.Length);
            _completedTo = completion;
        }
        finally
        {
            _completing = false;
        }
    }

    /// <summary>
    /// Whether the folder already listed for the dropdown says the completion's next step isn't
    /// there: history remembers folders deleted since, and filling one in sent Enter to it
    /// (2026-10-14). Only from a listing already in hand, never by going to the disk while typing,
    /// and only when the listing is short enough to have named every match.
    /// </summary>
    private bool IsGoneFromItsFolder(string typed, string completion)
    {
        if (Folders is not { } folders
            || FolderCompletion.Split(typed) is not { } split
            || folders.Cached(split.Folder, split.Prefix) is not { Count: < FolderCompletion.Max } there
            || !FsUri.TryParse(Environment.ExpandEnvironmentVariables(completion), out var target, out _))
            return false;

        for (var at = target; at?.Parent is { } parent; at = parent)
        {
            if (parent == split.Folder)
                return !there.Contains(at);
        }
        return false;
    }

    /// <summary>
    /// Takes the suggestion under the pointer rather than the selected one: selection follows the
    /// keyboard here, and a click should act on what was clicked whether or not it was highlighted
    /// first.
    /// </summary>
    private void OnSuggestionClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        var item = source as ListBoxItem ?? FindAncestor<ListBoxItem>(source);
        if (item?.DataContext is Suggestion chosen)
        {
            e.Handled = true;
            Accept(chosen.Uri);
        }
    }

    private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject =>
        TreeWalk.Ancestor<T>(source);

    /// <summary>
    /// Moves the highlight without moving focus, wrapping at both ends, and writes the suggestion
    /// into the box with the caret after it: typing on carries on from there, a separator and the
    /// start of the next name.
    /// </summary>
    private void StepSuggestion(int direction)
    {
        var count = SuggestionList.Items.Count;
        if (count == 0)
            return;

        // From "nothing highlighted", Down takes the first and Up takes the last.
        var index = SuggestionList.SelectedIndex < 0
            ? (direction > 0 ? 0 : count - 1)
            : (SuggestionList.SelectedIndex + direction + count) % count;

        SuggestionList.SelectedIndex = index;
        SuggestionList.ScrollIntoView(SuggestionList.Items[index]);

        if (SuggestionList.Items[index] is not Suggestion chosen)
            return;

        // The search stands for the words typed, not for an address to edit: those go back.
        var isSearch = ReferenceEquals(chosen, _searchSuggestion);
        var text = isSearch ? _typed : chosen.Uri.Display;
        _stepped = isSearch ? null : chosen;

        // Not typing, so the list stays as it is under the arrow keys.
        _completing = true;
        try
        {
            AddressBox.Text = text;
            AddressBox.Select(text.Length, 0);
            _completedTo = null;
        }
        finally
        {
            _completing = false;
        }
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (Tab is null)
            return;

        var suggesting = SuggestionPopup.IsOpen && SuggestionList.Items.Count > 0;

        switch (e.Key)
        {
            case Key.Down when suggesting:
                e.Handled = true;
                StepSuggestion(1);
                break;

            case Key.Up when suggesting:
                e.Handled = true;
                StepSuggestion(-1);
                break;

            // As in a shell: Tab takes the completion and leaves the caret after it, to type on.
            case Key.Tab when Keyboard.Modifiers == ModifierKeys.None && AcceptCompletion():
                e.Handled = true;
                break;

            // With nothing left to complete, Tab stays put while an address is being typed. It
            // used to move on, and leaving the bar puts back where the pane is, so one Tab too
            // many threw away the whole path typed (2026-10-26).
            case Key.Tab when Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift
                && AddressBox.Text != (Tab.Location?.Display ?? string.Empty):
                e.Handled = true;
                break;

            // A highlighted suggestion wins; otherwise Enter takes what was actually typed.
            case Key.Enter when suggesting && SuggestionList.SelectedItem is Suggestion chosen:
                e.Handled = true;
                Accept(chosen.Uri);
                break;

            // Stepped to, then the list closed with Escape: still that place, not its text re-read.
            case Key.Enter when _stepped is { } stepped:
                e.Handled = true;
                Accept(stepped.Uri);
                break;

            case Key.Enter:
                e.Handled = true;
                Navigate(AddressBox.Text);
                break;

            // First Escape dismisses the suggestions, second leaves the address bar. Escape should
            // undo one thing at a time.
            case Key.Escape when SuggestionPopup.IsOpen:
                e.Handled = true;
                SuggestionPopup.IsOpen = false;
                _dismissed = true;
                break;

            case Key.Escape:
                e.Handled = true;
                AddressBox.Text = Tab.Uri?.Display ?? string.Empty;
                FocusGrid();
                break;
        }
    }

    /// <summary>A path, a full URI, or a bookmark name — the address bar takes all three (§6.2).</summary>
    private void Navigate(string text)
    {
        if (Tab is null)
            return;

        // Expanded on the way in, not while typing: rewriting the box under the caret as somebody
        // is still typing "%LOCALAPPDATA%" would be the editor fighting them for it. Explorer's
        // address bar does the same, and %VAR% is how Windows spells this everywhere else.
        var typed = Environment.ExpandEnvironmentVariables(text.Trim());

        if (AddressSearch.IsExplicit(typed))
        {
            if (SearchFor(typed) is { } asked)
                Accept(asked);
            return;
        }

        if (Bookmarks.FirstOrDefault(b => string.Equals(b.Name, typed, StringComparison.OrdinalIgnoreCase))
            is { } bookmark)
        {
            Accept(bookmark.Uri);
            return;
        }

        // The start of a bookmark's name is enough: "dow" and Enter is Downloads, the one the
        // suggestions put first. Only for text that cannot be a path — no separator, no drive or
        // scheme colon — so a folder name typed on purpose is never taken for a bookmark.
        if (typed.IndexOfAny(['\\', '/', ':']) < 0
            && History?.Suggest(typed).FirstOrDefault() is { IsBookmark: true } first
            && first.Label.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
        {
            Accept(first.Uri);
            return;
        }

        // A bare word that is no bookmark's name: never an address, so a search of this folder.
        if (SearchFor(typed) is { } search)
        {
            Accept(search);
            return;
        }

        // Typed, so a '!' may be part of a folder's name rather than a step into an archive: the
        // disk says which.
        if (FsUri.TryParseTyped(typed, System.IO.File.Exists, path => System.IO.File.Exists(path) || System.IO.Directory.Exists(path), out var uri, out var error))
            Accept(uri);
        else
            Tab.Report(new Notice(error ?? "That is not a location Dance Commander can open.", NoticeSeverity.Error));
    }

    /// <summary>Navigating from the address bar: the address it is heading for.</summary>
    private bool _accepting;

    /// <summary>
    /// Goes where the address bar says, and hands the keyboard to the listing: you typed or picked
    /// a place in order to browse it. The list of suggestions closes and stays closed — the address
    /// text changing on arrival is navigation, not typing, and used to open it again.
    /// </summary>
    private async void Accept(FsUri uri)
    {
        SuggestionPopup.IsOpen = false;
        _accepting = true;
        FocusGrid();
        try
        {
            if (Tab is { } tab)
                await tab.GoToAsync(uri);
        }
        finally
        {
            _accepting = false;
        }

        // Again once the new rows are in, so the cursor is on the listing that arrived.
        FocusGrid();
    }

    /// <summary>
    /// A PasswordBox deliberately does not expose its contents as a bindable property, so the
    /// value is pushed across by hand. It still never lands anywhere observable: the decision
    /// holds it only until the prompt completes.
    /// </summary>
    private void OnDecisionSecretChanged(object sender, RoutedEventArgs e)
    {
        if (Tab?.Decision is { IsSecret: true } decision)
            decision.Text = DecisionSecret.Password;
    }

    private void OnDecisionTextKeyDown(object sender, KeyEventArgs e)
    {
        var decision = Tab?.Decision;
        if (decision is null)
            return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            decision.ConfirmCommand.Execute(null);
            FocusGrid();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            decision.Cancel();
            FocusGrid();
        }
    }

    /// <summary>
    /// The box is one line, and WPF keeps only the first line of a paste into it: the rest of a
    /// friction note pasted from a chat was dropped unsaid (BUGS.md 50). The lines are joined with
    /// spaces instead, and a line break copied along at the end is dropped.
    /// </summary>
    private void OnDecisionTextPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || text.IndexOfAny(['\r', '\n']) < 0)
            return;

        var joined = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, joined);
        e.DataObject = data;
    }
}

public sealed record ContextMenuRequest(ContextMenu Menu, MenuTarget Target);
