using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DanceCommander.App.Services;
using DanceCommander.App.ViewModels;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Transfer;

namespace DanceCommander.App.Views;

/// <param name="Target">The directory the files land in — a hovered folder, or the pane's own.</param>
/// <param name="Contents">
/// Files another program handed over as bytes, with no address (an Outlook attachment); then
/// <paramref name="Sources"/> is empty and the files are written into the target as new ones.
/// </param>
/// <param name="Text">Text dragged from another program, written into the target as a new file, as Ctrl+V does.</param>
public sealed record DropRequest(
    IReadOnlyList<FsUri> Sources, FsUri Target, DropAction Action, IReadOnlyList<VirtualFile>? Contents = null,
    string? Text = null);

/// <summary>
/// Drag and drop, in the three shapes that need no shell interop (§13): within the application
/// between panes and onto folders, in from Explorer, and out to Explorer when the selection is
/// local. Dragging a remote or in-archive entry <em>out</em> needs virtual files and is declined.
/// </summary>
public partial class PaneView
{
    /// <summary>Our own format, carrying URIs so a drag keeps full fidelity inside the app.</summary>
    private const string UriFormat = "DanceCommander.Uris";

    /// <summary>Marks a drag of the location icon: a place, which copies unless Shift says move.</summary>
    private const string LocationFormat = "DanceCommander.Location";

    /// <summary>Marks a drag off the shelf, which copies: a shelf hands out copies (ShelfWindow).</summary>
    internal const string ShelfFormat = "DanceCommander.Shelf";

    private Point _pressedAt;
    private bool _mayDrag;

    /// <summary>A press on one of several selected rows, waiting to learn whether it was a click.</summary>
    private FileRowViewModel? _clickOnRelease;
    private FileRowViewModel? _dropTarget;

    /// <summary>Raised when a drop is accepted; the window turns it into a transfer.</summary>
    public event EventHandler<DropRequest>? FilesDropped;

    private void OnGridPreviewLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Only a press that lands on a row can begin a drag. Pressing empty space is a rubber-band
        // selection as far as the grid is concerned, and stealing it would be worse than useless.
        _pressedAt = e.GetPosition(this);
        var row = e.OriginalSource is DependencyObject source ? FindRow(source) : null;
        _mayDrag = row is not null;
        _clickOnRelease = null;

        // A click without Shift is where the next Shift+click ranges from, also on the row the
        // cursor is already on: the keyboard doesn't move then, so the focus event that sets the
        // anchor never comes. Click A, Shift+click Z, click Z, Shift+click A ranged A to A
        // (the user, 2026-10-17).
        if (row is not null && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            StartRangeAt(row.Entry.Uri);

        // Empty space below the rows is still the list: a click there from the address bar or the
        // command line hands the keyboard back to it, as a click in the other pane's does. Not on
        // the scroll bar or a column header, which are for scrolling and sorting.
        if (row is null && !FileGrid.IsKeyboardFocusWithin && e.OriginalSource is DependencyObject pressed
            && TreeWalk.Ancestor<System.Windows.Controls.Primitives.ScrollBar>(pressed) is null
            && TreeWalk.Ancestor<GridViewColumnHeader>(pressed) is null)
        {
            _ = Dispatcher.BeginInvoke(() => FocusGrid(clicked: true), System.Windows.Threading.DispatcherPriority.Input);
        }

        // Explorer's rule. A plain press on a row that is one of several selected might be the
        // start of dragging all of them, so it decides nothing until the button comes up: a drag
        // takes the whole selection, a click selects just that row, as it always did. The list
        // decides on the press, so dragging three selected files took one (friction log,
        // 2026-10-01). Double-clicks and modified clicks are the list's own, as before.
        if (row is not null && e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None &&
            FileGrid.SelectedItems.Count > 1 && FileGrid.SelectedItems.Contains(row))
        {
            e.Handled = true;
            _clickOnRelease = row;
            MoveCursorTo(row);
            Mouse.Capture(this, CaptureMode.SubTree);
            return;
        }

        // Shift+click: the range from where it starts to the row clicked; with Ctrl too, added
        // to the marks already there.
        if (row is not null && e.ClickCount == 1 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0 &&
            MarkRangeTo(row, addToMarks: (Keyboard.Modifiers & ModifierKeys.Control) != 0))
        {
            e.Handled = true;
            _mayDrag = false;
        }
    }

    /// <summary>
    /// Every row from where the range starts (_rangeAnchorUri) to <paramref name="row"/>, in the
    /// list's order, marked, and the cursor on <paramref name="row"/>; for Shift+click and
    /// Shift+arrows. Done here rather than by the list, whose range starts from an anchor of its
    /// own that this pane's cursor moves never move. The cursor moves with Shift down, so the
    /// next one ranges from the same start, as in Explorer.
    /// </summary>
    /// <returns>False when there was no range to make (no rows, a row gone).</returns>
    /// <param name="keepMarks">Shift+arrows: the marks made before the range stay, as a commander's do.</param>
    private bool MarkRangeTo(FileRowViewModel row, bool addToMarks, bool keepMarks = false)
    {
        if (Tab is not { } tab)
            return false;

        var rows = tab.Rows;
        var anchor = rows.FirstOrDefault(r => r.Entry.Uri == _rangeAnchorUri)
                     ?? rows.FirstOrDefault(r => r.Entry.Uri == _cursorUri)
                     ?? row;
        var (from, to) = (IndexOf(rows, anchor), IndexOf(rows, row));
        if (from < 0 || to < 0)
            return false;

        var range = rows.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1).ToList();
        List<FileRowViewModel> marks;
        if (keepMarks)
        {
            // The marks made before this range, kept: the marks there now, less what this same
            // range added last time, so a range shrinks as it turns back and an Esc in between
            // is not undone. Insert on B, Down twice, Shift+Down dropped B (the user, 2026-10-30).
            // Only what it added: a mark made by hand inside the range stays as the range turns
            // away from it. The same range by its start as meant, even if that row has gone and
            // the cursor stands in: else the whole last range counted as marked before, and stayed
            // marked as the cursor turned back (review, 2026-10-30).
            var same = _lastRange.Anchor is { } last && last == _rangeAnchorUri;
            var before = tab.HasMarks
                ? FileGrid.SelectedItems.Cast<FileRowViewModel>()
                    .Where(r => !same || !_lastRange.Rows.Contains(r.Entry.Uri))
                    .ToList()
                : [];
            marks = before.Union(range).ToList();
            var kept = before.Select(r => r.Entry.Uri).ToHashSet();
            _lastRange = (_rangeAnchorUri, range.Select(r => r.Entry.Uri).Where(u => !kept.Contains(u)).ToHashSet());
        }
        else
        {
            var already = addToMarks && tab.HasMarks
                ? FileGrid.SelectedItems.Cast<FileRowViewModel>().ToList()
                : [];
            marks = already.Union(range).ToList();
            // Recorded as well, so Shift+arrows after a Shift+click turn its range back rather
            // than keep it all as marked before (review, 2026-10-30).
            var kept = already.Select(r => r.Entry.Uri).ToHashSet();
            _lastRange = (_rangeAnchorUri, range.Select(r => r.Entry.Uri).Where(u => !kept.Contains(u)).ToHashSet());
        }

        // Cursor first: focusing a row selects it alone, and the marks go on after it.
        MoveCursorTo(row);
        ReplaceSelection(marks, marks: true);
        return true;
    }

    /// <summary>
    /// The next range starts here, a new one: an old range's rows from the same start, remembered
    /// still, would take back marks made since.
    /// </summary>
    private void StartRangeAt(FsUri uri)
    {
        _rangeAnchorUri = uri;
        _lastRange = (null, []);
    }

    /// <summary>The rows a Shift+arrows range from this start added last, to take back as it turns.</summary>
    private (FsUri? Anchor, HashSet<FsUri> Rows) _lastRange = (null, []);

    /// <summary>
    /// Takes the mouse from the list once a press on a row has done its selecting. The list,
    /// holding the mouse, selects every row the pointer passes over with the button down; a drag
    /// only begins a row's travel later, so by then the selection -- and the focus -- had moved to
    /// a neighbour, and that was the file dragged. The list only does this while it holds the mouse
    /// itself, so the pane holding it instead keeps the pressed row the one that goes.
    /// </summary>
    private void OnGridPressHandled(object sender, MouseButtonEventArgs e)
    {
        if (_mayDrag && e.ClickCount == 1 && Mouse.Captured == FileGrid)
            Mouse.Capture(this, CaptureMode.SubTree);
    }

    private void OnPaneLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Released without dragging: it was a click after all.
        if (_clickOnRelease is { } clicked && _mayDrag)
            ReplaceSelection([clicked], marks: false);
        _clickOnRelease = null;
        _mayDrag = false;
        if (Mouse.Captured == this)
            ReleaseMouseCapture();
    }

    private void OnGridMouseMove(object sender, MouseEventArgs e)
    {
        if (!_mayDrag || e.LeftButton != MouseButtonState.Pressed || Tab is null)
            return;

        // Not yet a drag; a click that wobbles is still a click. The system's few pixels are too few
        // here: the neighbouring row is often a folder, and a slipped click would move the selection
        // into it. So a drag has to travel at least a row before it begins.
        var moved = e.GetPosition(this) - _pressedAt;
        var threshold = RowHeight();
        if (Math.Abs(moved.X) < Math.Max(threshold, SystemParameters.MinimumHorizontalDragDistance) &&
            Math.Abs(moved.Y) < Math.Max(threshold, SystemParameters.MinimumVerticalDragDistance))
        {
            return;
        }

        _mayDrag = false;
        _clickOnRelease = null;

        var sources = FileGrid.SelectedItems
            .OfType<FileRowViewModel>()
            .Select(row => row.Target)
            .ToList();

        if (sources.Count == 0)
            return;

        // Remembered rows are not handed to a transfer, which would move whatever is at those
        // addresses now (§1.6); the commands wait for the server instead, and a drag can't.
        if (Tab is { IsUnconfirmed: true } remembered)
        {
            remembered.ShowDone("These rows are as last listed; drag them once the folder has been checked.");
            return;
        }

        try
        {
            DragDrop.DoDragDrop(FileGrid, DragData(sources), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            ClearDropTarget();
        }
    }

    private void OnPaneDragOver(object sender, DragEventArgs e)
    {
        // Called back through COM by the program dragging: an exception here becomes a refused
        // drop and nothing else, so it is said in the log, once per drag.
        try
        {
            PaneDragOver(sender, e);
        }
        catch (Exception ex) when (!AlreadyLogged(e.Data))
        {
            Serilog.Log.Warning(ex, "A drag over a pane failed");
            throw;
        }
    }

    private void PaneDragOver(object sender, DragEventArgs e)
    {
        ScrollTabsUnderDrag(e);
        if (DraggedTab(e) is { } tab)
        {
            OnTabDragOver(e, tab);
            return;
        }

        NoteRoute(e.Data);

        e.Effects = Evaluate(e, out var target) switch
        {
            DropAction.Copy => DragDropEffects.Copy,
            DropAction.Move => DragDropEffects.Move,
            DropAction.Collect => DragDropEffects.Link,
            _ => DragDropEffects.None,
        };

        // Refused by us, or answered with an effect the source doesn't allow, which Windows shows
        // as refused all the same.
        if ((e.Effects & e.AllowedEffects) == DragDropEffects.None)
            LogRefusedDrag(e);

        // Highlight the folder the files would land in. Without it, dropping onto a row is aim
        // without a sight — the pointer is over a list of near-identical lines.
        HighlightDropTarget(e.Effects == DragDropEffects.None ? null : target);
        // A folder at the edge the list is scrolling past doesn't open: the rows are moving under
        // the pointer, and the one it is on now is not the one aimed at.
        var scrolling = AutoScroll(e);
        // A tab opens for any drag of files, since the folder showing may refuse them only for
        // being where they came from; a folder opens only if the files could go into it.
        SpringLoad(!CarriesFiles(e.Data) && !CarriesText(e.Data) ? null
            : (object?)TabUnder(e) ?? (object?)NavButtonUnder(e) ?? (e.Effects == DragDropEffects.None || scrolling ? null : target));

        e.Handled = true;
    }

    private void OnPaneDragLeave(object sender, DragEventArgs e)
    {
        // A drag cancelled after hovering leaves no route behind for the next one; moving between
        // elements inside the pane leaves too, and the DragOver after it notes the route again.
        _pathsAtDrop = false;
        ClearDropTarget();
        HideTabDropMark();
        StopTabEdgeDragScroll();
    }

    /// <summary>
    /// The drag whose refusal was last logged, so a drag held over the pane logs once. Weak: it is
    /// the other program's data object, and keeping it alive would keep that program's drag alive.
    /// </summary>
    private WeakReference<IDataObject>? _refusalLogged;

    /// <summary>True if this drag has been logged already; else notes it as logged now.</summary>
    private bool AlreadyLogged(IDataObject data)
    {
        if (_refusalLogged?.TryGetTarget(out var logged) == true && ReferenceEquals(logged, data))
            return true;
        _refusalLogged = new WeakReference<IDataObject>(data);
        return false;
    }

    /// <summary>
    /// Says in the log what a refused drag offered. Programs hand files over in more ways than
    /// Explorer's (new Outlook's attachments came in a shape this didn't know, 2026-10-13), and the
    /// formats are the only way to learn which one without guessing. Only for drags from outside:
    /// our own are understood or refused on purpose.
    /// </summary>
    private void LogRefusedDrag(DragEventArgs e)
    {
        var data = e.Data;
        try
        {
            if (data.GetDataPresent(UriFormat) || AlreadyLogged(data))
                return;

            var formats = string.Join(", ", data.GetFormats(autoConvert: false));
            var paths = data.GetDataPresent(DataFormats.FileDrop)
                ? (DroppedPaths(data) is { } list ? $"{list.Length} paths" : "no paths yet")
                : "none";
            Serilog.Log.Information(
                "Drag refused over a pane at {Uri}: we said {Effects}, the source allows {Allowed}; formats {Formats}; file list {Paths}",
                Tab?.Uri, e.Effects, e.AllowedEffects, formats, paths);
        }
        catch (Exception ex)
        {
            Serilog.Log.Information(ex, "Drag refused over a pane; its formats could not be read");
        }
    }

    // ---- spring-loading --------------------------------------------------------------------

    /// <summary>Held on a tab or a navigation button this long, a drag opens it.</summary>
    /// <remarks>0.6 s caught tabs only passed over on the way somewhere (the user, 2026-10-09).</remarks>
    private static readonly TimeSpan TabSpringDelay = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// Held on a folder row this long, a drag goes into it: longer than a tab, because rows are
    /// crossed on the way to others.
    /// </summary>
    private static readonly TimeSpan FolderSpringDelay = TimeSpan.FromMilliseconds(1200);

    private System.Windows.Threading.DispatcherTimer? _spring;
    private object? _springTarget;

    /// <summary>
    /// A drag held on another tab opens that tab, and held on a folder goes into it, so files can
    /// be dropped where the pane wasn't showing: between two tabs of one pane there was no way at
    /// all (friction log), and Finder has taught the gesture. Back, Forward and Up do what a click
    /// would, and go on doing it while the drag stays: held on Up, it climbs a folder at a time.
    /// Anything that moves the pointer to a different target starts the wait again; leaving or
    /// dropping stops it.
    /// </summary>
    private void SpringLoad(object? target)
    {
        if (target is TabViewModel tab && ReferenceEquals(tab, ViewModel?.ActiveTab))
            target = null;
        if (target is UIElement { IsEnabled: false })
            target = null;
        if (ReferenceEquals(target, _springTarget))
            return;

        StopSpring();
        _springTarget = target;
        if (target is null)
            return;

        _spring ??= new System.Windows.Threading.DispatcherTimer();
        _spring.Tick -= OnSpring;
        _spring.Tick += OnSpring;
        _spring.Interval = target is FileRowViewModel ? FolderSpringDelay : TabSpringDelay;
        _spring.Start();
        ShowSpringLine(target, _spring.Interval);
    }

    private void OnSpring(object? sender, EventArgs e)
    {
        var target = _springTarget;
        StopSpring();

        if (target is TabViewModel tab && ViewModel is { } pane && pane.Tabs.Contains(tab))
            pane.ActiveTab = tab;
        else if (target is FileRowViewModel { IsDirectory: true } row && Tab is { } here)
            _ = here.NavigateAsync(row.Target);
        else if (ReferenceEquals(target, BackButton) && Tab is { CanGoBack: true } back)
            _ = back.GoBackAsync();
        else if (ReferenceEquals(target, ForwardButton) && Tab is { CanGoForward: true } forward)
            _ = forward.GoForwardAsync();
        else if (ReferenceEquals(target, UpButton) && Tab is { } child)
            _ = child.GoUpAsync();
    }

    private void StopSpring()
    {
        _spring?.Stop();
        _springTarget = null;
        SpringFill.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        SpringLine.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Fills a line under what the drag is held on over the time it waits, so the wait is seen and
    /// can be left before it ends. Without it a tab or a folder opened out of nowhere, and the only
    /// way to learn the delay was to be surprised by it.
    /// </summary>
    private void ShowSpringLine(object target, TimeSpan wait)
    {
        var element = target switch
        {
            TabViewModel tab => TabStrip.ItemContainerGenerator.ContainerFromItem(tab) as FrameworkElement,
            FileRowViewModel row => FileGrid.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement,
            _ => target as FrameworkElement,
        };
        if (element is null || !element.IsVisible)
            return;

        var at = element.TranslatePoint(new Point(0, element.ActualHeight - SpringLine.Height), PaneRoot);
        SpringLine.Margin = new Thickness(at.X, at.Y, 0, 0);
        SpringLine.Width = element.ActualWidth;
        SpringLine.Visibility = Visibility.Visible;
        SpringFill.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, wait));
    }

    // ---- scrolling at the edges ------------------------------------------------------------

    /// <summary>How long a drag at the very edge waits between rows, and how long at the zone's inner side.</summary>
    private static readonly TimeSpan EdgeScrollFastest = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan EdgeScrollSlowest = TimeSpan.FromMilliseconds(350);

    private System.Windows.Threading.DispatcherTimer? _edgeScroll;

    /// <summary>+1 scrolling down, -1 up, 0 still.</summary>
    private int _edgeScrollDirection;

    private TimeSpan _edgeScrollWait;
    private DateTime _edgeScrolledAt;

    /// <summary>
    /// A drag held in the last row's height at the bottom of the list, or the first at the top,
    /// scrolls the list a row at a time, faster the nearer the edge, so a folder out of sight can
    /// be reached without letting go. Without it a drop or a spring-load could only aim at what
    /// already showed. True while it scrolls; false when the pointer is elsewhere or the list is at
    /// its end that way.
    /// </summary>
    private bool AutoScroll(DragEventArgs e)
    {
        var direction = 0;
        var depth = 0.0;
        if (GridScroller is { } scroller &&
            TreeWalk.Descendant<ScrollContentPresenter>(scroller) is { ActualHeight: > 0 } rows)
        {
            var point = e.GetPosition(rows);
            var zone = Math.Min(RowHeight(), rows.ActualHeight / 4);
            if (point.X >= 0 && point.X <= rows.ActualWidth && point.Y >= 0 && point.Y <= rows.ActualHeight)
            {
                if (point.Y > rows.ActualHeight - zone && scroller.VerticalOffset < scroller.ScrollableHeight)
                    (direction, depth) = (1, (point.Y - (rows.ActualHeight - zone)) / zone);
                else if (point.Y < zone && scroller.VerticalOffset > 0)
                    (direction, depth) = (-1, (zone - point.Y) / zone);
            }
        }

        if (direction == 0)
        {
            StopAutoScroll();
            return false;
        }

        // The timer ticks at a fixed short rate and the wait is counted here: setting a running
        // DispatcherTimer's Interval restarts it, and DragOver comes with every move of the
        // pointer, so a timer retuned there would never get to tick.
        _edgeScrollWait = EdgeScrollSlowest - (EdgeScrollSlowest - EdgeScrollFastest) * Math.Clamp(depth, 0, 1);
        if (_edgeScroll is null)
        {
            _edgeScroll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _edgeScroll.Tick += OnEdgeScroll;
        }

        if (_edgeScrollDirection != direction || !_edgeScroll.IsEnabled)
        {
            _edgeScrollDirection = direction;
            _edgeScrolledAt = DateTime.UtcNow;
            _edgeScroll.Start();
        }

        return true;
    }

    private void OnEdgeScroll(object? sender, EventArgs e)
    {
        if (GridScroller is not { } scroller)
        {
            StopAutoScroll();
            return;
        }

        if (DateTime.UtcNow - _edgeScrolledAt < _edgeScrollWait)
            return;

        _edgeScrolledAt = DateTime.UtcNow;
        if (_edgeScrollDirection > 0)
            scroller.LineDown();
        else
            scroller.LineUp();
    }

    private void StopAutoScroll()
    {
        _edgeScroll?.Stop();
        _edgeScrollDirection = 0;
    }

    /// <summary>Back or Forward, if the pointer is over one. Up takes its own drags (PaneView.UpButton.cs).</summary>
    private Button? NavButtonUnder(DragEventArgs e) =>
        new[] { BackButton, ForwardButton }.FirstOrDefault(button =>
        {
            var point = e.GetPosition(button);
            return point.X >= 0 && point.Y >= 0 && point.X <= button.ActualWidth && point.Y <= button.ActualHeight;
        });

    /// <summary>
    /// The tab whose header is under the pointer, if it is over the tab strip. Not one under a
    /// faded edge: there the strip is paging, and the tab under it a moment from now is another.
    /// </summary>
    private TabViewModel? TabUnder(DragEventArgs e)
    {
        if (TabEdgeUnder(e) != 0)
            return null;

        var point = e.GetPosition(TabStrip);
        if (point.X < 0 || point.Y < 0 || point.X > TabStrip.ActualWidth || point.Y > TabStrip.ActualHeight)
            return null;

        for (var at = System.Windows.Media.VisualTreeHelper.HitTest(TabStrip, point)?.VisualHit;
             at is not null && !ReferenceEquals(at, TabStrip);
             at = System.Windows.Media.VisualTreeHelper.GetParent(at))
        {
            if (at is FrameworkElement { DataContext: TabViewModel tab })
                return tab;
        }

        return null;
    }

    private void OnPaneDrop(object sender, DragEventArgs e)
    {
        try
        {
            PaneDrop(sender, e);
        }
        finally
        {
            _pathsAtDrop = false;
            StopTabEdgeDragScroll();
        }
    }

    private void PaneDrop(object sender, DragEventArgs e)
    {
        ClearDropTarget();
        if (DraggedTab(e) is { } tab)
        {
            OnTabDrop(e, tab);
            return;
        }

        var action = Evaluate(e, out _);
        e.Handled = true;
        e.Effects = DragDropEffects.None;

        if (action == DropAction.None || Tab is null)
            return;

        // A drag that was going to hand its files over at the drop goes that way now, though its
        // paths have just become readable: read again here, they sent it down the transfer.
        IReadOnlyList<FsUri> sources = _pathsAtDrop ? [] : SourcesFrom(e.Data);
        var onRow = TargetUnder(e);
        var target = onRow ?? PaneDropTarget;
        if (target is null)
            return;

        // A remembered folder row may be gone, or a file now. The folder the pane shows is an
        // address, judged by the server when the files arrive, so dropping there is still fine.
        if (onRow is not null && Tab.IsUnconfirmed)
        {
            Tab.ShowDone("That folder is as last listed; drop onto it once this folder has been checked.");
            return;
        }

        if (sources.Count == 0)
        {
            DropContents(e, target);
            return;
        }

        e.Effects = Reported(action);
        FilesDropped?.Invoke(this, new DropRequest(sources, target, action));
    }

    /// <summary>
    /// Files with no address, only bytes (an Outlook attachment), are read now, while the program
    /// that offered them is still bound to answer, and handed on to be written into the target.
    /// </summary>
    private void DropContents(DragEventArgs e, FsUri target)
    {
        IReadOnlyList<VirtualFile>? files;
        string? problem = null;
        if (VirtualFiles.IsPresent(e.Data))
        {
            Serilog.Log.Information("Drop of files with no paths: reading their contents");
            files = VirtualFiles.Read(e.Data, out problem);
        }
        else if (_pathsAtDrop)
        {
            // Chromium (new Outlook) won't render its file list while its drag is going, the drop
            // included. Told the drop is asynchronous, it downloads the attachment and answers once
            // the drop has returned, so the asking waits until then.
            var operation = VirtualFiles.StartAsync(e.Data);
            Serilog.Log.Information(
                "Drop of files rendered at the drop: {How}", operation is null ? "asking after the drop" : "asynchronous, as Explorer does");
            e.Effects = DragDropEffects.Copy;
            var data = e.Data;
            var taking = Dispatcher.InvokeAsync(
                () => TakeRendered(data, target, operation), System.Windows.Threading.DispatcherPriority.Background);

            // Closing before it runs drops the callback: the program is still told, so it doesn't
            // wait for ever with its download.
            if (operation is not null)
                taking.Aborted += (_, _) => operation.End(taken: false);
            return;
        }
        else if (CarriesText(e.Data))
        {
            DropText(e, target);
            return;
        }
        else
        {
            return;
        }

        if (files is null)
        {
            Tab?.Report(new Notice("Could not take what was dropped.", NoticeSeverity.Warning, problem));
            return;
        }

        e.Effects = DragDropEffects.Copy;
        FilesDropped?.Invoke(this, new DropRequest([], target, DropAction.Copy, files));
    }

    /// <summary>
    /// The second half of a drop whose files are rendered late. The paths are the program's own
    /// download folder, its to clean up once told the drop is over, so they are read whole first
    /// and the program told after; never handed to a transfer job that would read them later.
    /// </summary>
    private void TakeRendered(IDataObject data, FsUri target, VirtualFiles.AsyncDrop? operation)
    {
        // Anything at all is caught: this runs from the dispatcher after the drop, where an escaping
        // exception takes the whole window down, with whatever transfers it is running.
        IReadOnlyList<VirtualFile>? files = null;
        string? problem = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            files = DroppedPaths(data) is { } paths ? VirtualFiles.ReadPaths(paths, out problem) : null;
            problem ??= "The program did not hand the files over.";
        }
        catch (Exception ex)
        {
            files = null;
            problem = ex.Message;
        }
        finally
        {
            operation?.End(taken: files is not null);
        }

        // The program downloads while this waits, on the UI thread: how long it took says whether
        // that needs moving to a worker (review, 2026-10-13).
        Serilog.Log.Information(
            "Drop of files rendered at the drop: {Outcome} after {Elapsed} ms",
            files is null ? $"not taken: {problem}" : $"{files.Count} taken", clock.ElapsedMilliseconds);

        if (files is null)
        {
            Tab?.Report(new Notice("Could not take what was dropped.", NoticeSeverity.Warning, problem));
            return;
        }

        try
        {
            FilesDropped?.Invoke(this, new DropRequest([], target, DropAction.Copy, files));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Writing a drop failed");
            Tab?.Report(new Notice("Could not write what was dropped.", NoticeSeverity.Warning, ex.Message));
        }
    }

    /// <summary>
    /// Text dragged in, from an editor or a browser, becomes a file of its own as pasted text does.
    /// Read only now: hovering asks only whether there is text, as the program may make it on demand
    /// and a long selection would be fetched with every move of the pointer.
    /// </summary>
    private void DropText(DragEventArgs e, FsUri target)
    {
        string? text;
        try
        {
            text = e.Data.GetData(DataFormats.UnicodeText) as string ?? e.Data.GetData(DataFormats.Text) as string;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Tab?.Report(new Notice("Could not take what was dropped.", NoticeSeverity.Warning, ex.Message));
            return;
        }

        if (string.IsNullOrEmpty(text))
            return;

        e.Effects = DragDropEffects.Copy;
        FilesDropped?.Invoke(this, new DropRequest([], target, DropAction.Copy, Text: text));
    }

    /// <summary>
    /// Whether a drag offers text and nothing more. Files come first: a browser dragging an image
    /// offers its address as text too, and the image is what was meant. So does any file format,
    /// read here or not: an attachment offered in a shape this can't take (the ANSI descriptor, a
    /// file list that won't render yet) is refused as before, never saved as its mail's text
    /// while the user believes the attachment safe (review, 2026-10-15).
    /// </summary>
    private bool CarriesText(IDataObject data)
    {
        if (CarriesFiles(data))
            return false;
        try
        {
            foreach (var format in (string[])[DataFormats.FileDrop, "FileGroupDescriptorW", "FileGroupDescriptor", "FileContents"])
            {
                if (data.GetDataPresent(format))
                    return false;
            }

            return data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    /// <summary>Whether a drag carries files of any kind: addresses, paths, or bytes.</summary>
    private bool CarriesFiles(IDataObject data) =>
        _pathsAtDrop || SourcesFrom(data).Count > 0 || VirtualFiles.IsPresent(data);

    /// <summary>
    /// The drag being hovered promised a file list it wouldn't give yet (new Outlook). Decided while
    /// hovering and kept for the drop, not tested again there: at the drop the list becomes
    /// readable, and a second look sent the program's temporary copies down an ordinary transfer
    /// that would read them after the program may have cleaned them up (review, 2026-10-13).
    /// </summary>
    private bool _pathsAtDrop;

    private void NoteRoute(IDataObject data) =>
        _pathsAtDrop = SourcesFrom(data).Count == 0 && PathsLater(data);

    /// <summary>
    /// Files that can only be had by dropping them: bytes with no address (classic Outlook), or a
    /// file list the program renders only at the drop (new Outlook).
    /// </summary>
    private bool ContentsAtDrop(IDataObject data) =>
        _pathsAtDrop || (SourcesFrom(data).Count == 0 && VirtualFiles.IsPresent(data));

    /// <summary>A file list promised but not given while the drag moves.</summary>
    private static bool PathsLater(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop) && DroppedPaths(data) is null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    /// <summary>
    /// Where a drop onto the pane itself goes, rather than onto a row: the folder it shows or is a
    /// view of, or the collection or bookmarks it shows. A from: listing and the other pages have nowhere.
    /// </summary>
    private FsUri? PaneDropTarget => Tab?.Uri switch
    {
        { } here when AboutPages.HoldsFiles(here) || AboutPages.IsBookmarks(here) => here,
        { IsAbout: false } here when here.Scheme != FsUri.FromScheme => here.Anchor,
        _ => null,
    };

    /// <summary>Decides what this drop would do, and where it would land.</summary>
    private DropAction Evaluate(DragEventArgs e, out FileRowViewModel? row, FsUri? onto = null)
    {
        row = onto is null ? RowUnder(e) : null;

        // A pane showing a flattened tree or a search takes a drop into the folder it is a view of.
        // A from: listing has no such folder — its rows come from a command or a script — so only a
        // drop onto one of its rows that is a real folder (a worktree) has somewhere to go.
        if ((onto ?? row?.Target ?? PaneDropTarget) is not { } target)
            return DropAction.None;
        IReadOnlyList<FsUri> sources = _pathsAtDrop ? [] : SourcesFrom(e.Data);

        // Into a collection — its page, or its row in the list — or onto the bookmarks, a drop adds
        // a reference to each thing dropped and copies nothing.
        if (target == AboutPages.Shelf || AboutPages.CollectionName(target) is not null || AboutPages.IsBookmarks(target))
            return sources.Count > 0 && sources.All(s => !s.IsAbout) ? DropAction.Collect : DropAction.None;

        var modifiers = DragModifiers.None;
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0)
            modifiers |= DragModifiers.Control;
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0)
            modifiers |= DragModifiers.Shift;

        // An archive or a script location is browsable but not writable, and the transfer would fail
        // at the far end.
        var writable = !target.IsArchiveEntry && !target.IsAbout && target.Scheme != FsUri.FromScheme;

        // Bytes from another program, or files it renders only at the drop: there is nothing to
        // move or to leave in place, so they are always new files here, whatever the keys say.
        // A source that doesn't allow a copy is answered with none: Windows would show it refused anyway.
        if (sources.Count == 0 && ContentsAtDrop(e.Data))
            return writable && e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DropAction.Copy : DropAction.None;

        // Text is a new file here too, as Ctrl+V makes one of it. Copy, though the text may be
        // moved out of an editor: told Move, the editor would cut it from what it was editing.
        if (sources.Count == 0 && CarriesText(e.Data))
            return writable && e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DropAction.Copy : DropAction.None;

        var action = DropPolicy.Decide(
            sources, target, modifiers, writable,
            preferCopy: e.Data.GetDataPresent(LocationFormat) || e.Data.GetDataPresent(ShelfFormat));

        // The source says what it allows. The shelf offers copies only, and so do programs that
        // keep their files; Shift used to move the original out from under them anyway.
        if (action == DropAction.Move && !e.AllowedEffects.HasFlag(DragDropEffects.Move))
            action = e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DropAction.Copy : DropAction.None;
        return action;
    }

    /// <summary>
    /// What a drop tells the program the files came from. Never Move: in the shell's protocol a
    /// source that hears Move deletes its originals, and ours are moved by a transfer job that may
    /// not have read them yet. The job does the move, so to the source it was a copy. Left unset,
    /// WPF answered with everything the source allowed, Move included.
    /// </summary>
    private static DragDropEffects Reported(DropAction action) => action switch
    {
        DropAction.Copy or DropAction.Move => DragDropEffects.Copy,
        DropAction.Collect => DragDropEffects.Link,
        _ => DragDropEffects.None,
    };

    internal static IReadOnlyList<FsUri> SourcesFrom(IDataObject data)
    {
        // Our own format first: it survives archives and servers, which paths cannot describe.
        if (data.GetData(UriFormat) is string[] uris)
        {
            return uris
                .Select(text => FsUri.TryParse(text, out var uri, out _) ? uri : null)
                .OfType<FsUri>()
                .ToList();
        }

        if (DroppedPaths(data) is { } paths)
            return paths.Select(FsUri.LocalExact).ToList();

        return [];
    }

    /// <summary>
    /// The paths on a drag, or null. New Outlook offers a file list it won't render until the drop
    /// (DV_E_FORMATETC while the drag moves): it downloads the attachment only when asked at the
    /// right moment. The exception went back through COM as a silent refusal, 2026-10-13.
    /// </summary>
    private static string[]? DroppedPaths(IDataObject data)
    {
        try
        {
            return data.GetData(DataFormats.FileDrop) as string[];
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>The folder row under the pointer, or null when the drop belongs to the pane.</summary>
    private FileRowViewModel? RowUnder(DragEventArgs e)
    {
        var point = e.GetPosition(FileGrid);
        if (point.X < 0 || point.Y < 0 || point.X > FileGrid.ActualWidth || point.Y > FileGrid.ActualHeight)
            return null;

        var hit = System.Windows.Media.VisualTreeHelper.HitTest(FileGrid, point);
        if (hit?.VisualHit is not { } visual)
            return null;

        return FindRow(visual) is { IsDirectory: true } row ? row : null;
    }

    private FsUri? TargetUnder(DragEventArgs e) => RowUnder(e)?.Target;

    private void HighlightDropTarget(FileRowViewModel? row)
    {
        if (ReferenceEquals(_dropTarget, row))
            return;

        if (_dropTarget is not null)
            _dropTarget.IsDropTarget = false;

        _dropTarget = row;

        if (_dropTarget is not null)
            _dropTarget.IsDropTarget = true;
    }

    private void ClearDropTarget()
    {
        HighlightDropTarget(null);
        StopSpring();
        StopAutoScroll();
    }
}
