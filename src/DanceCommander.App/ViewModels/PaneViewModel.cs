using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Navigation;

namespace DanceCommander.App.ViewModels;

/// <summary>
/// One half of the window: a stack of tabs plus the chrome around them. Everything about a
/// location lives on <see cref="TabViewModel"/>; the pane only decides which one is showing.
/// </summary>
public sealed partial class PaneViewModel : ObservableObject
{
    private readonly Func<TabViewModel> _newTab;

    public PaneViewModel(Func<TabViewModel> newTab)
    {
        _newTab = newTab;
        // Straight into the field, so OnActiveTabChanged never ran for the first tab and it was
        // never marked current. Unseen with one tab (no strip), until a session came back with the
        // first of several tabs in front: assigning it again is no change, so its chip stayed
        // unlit while it was the tab on screen (2026-10-02).
        _activeTab = AddTab();
        _activeTab.IsCurrent = true;
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private TabViewModel _activeTab;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>
    /// The command line is transient: invoked with Ctrl+E, dismissed with Escape. Keeping it
    /// hidden by default is what lets it exist without spending one of the five chrome regions.
    /// </summary>
    [ObservableProperty]
    private bool _showCommandLine;

    // A shell stepped to with Ctrl+E is for the line it was chosen on: the next Ctrl+E opens
    // the configured one, so which shell a line goes to never depends on an earlier visit.
    partial void OnShowCommandLineChanged(bool value)
    {
        if (!value)
            foreach (var tab in Tabs)
                tab.UseConfiguredShell();
    }

    partial void OnActiveTabChanged(TabViewModel? oldValue, TabViewModel newValue)
    {
        if (oldValue is not null)
            oldValue.IsCurrent = false;
        newValue.IsCurrent = true;

        // A connection's question goes with the front: left behind, every other tab waiting for
        // that server said "Connecting…" with nothing to answer (2026-10-14). Only to a tab on that
        // server, though; one somewhere else is not waiting for it (2026-10-26). From any tab
        // behind, not just the one left: the question stays where it was when a local tab was
        // in between.
        if (newValue.Decision is null && newValue.Location is { } front
            && (oldValue is null ? Tabs : Tabs.Prepend(oldValue))
                .Select(tab => tab.Decision)
                .FirstOrDefault(d => d?.FollowsFront?.Invoke(front) == true) is { } waiting)
            newValue.Receive(waiting);

        // A tab restored from the last session lists the first time it comes to the front.
        _ = newValue.ShowAsync();
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Tabs opened, closed, brought to the front or sent somewhere: what the session keeps.</summary>
    public event EventHandler? SessionChanged;

    private void OnTabChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.Uri))
            SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Puts back the tabs of the last session, none of them listed yet: the one in front lists
    /// when the caller shows it, the rest when they are first brought forward.
    /// </summary>
    public void Restore(IReadOnlyList<(List<FsUri> History, int Position, string? Cursor)> tabs, int active)
    {
        for (var i = 0; i < tabs.Count; i++)
        {
            var tab = i < Tabs.Count ? Tabs[i] : AddTab();
            tab.Restore(tabs[i].History, tabs[i].Position, tabs[i].Cursor);
        }

        ActiveTab = Tabs[Math.Clamp(active, 0, Tabs.Count - 1)];
    }

    public PaneSession Snapshot()
    {
        var pane = new PaneSession();
        foreach (var tab in Tabs)
        {
            if (tab.Snapshot() is not { } kept)
                continue;
            if (ReferenceEquals(tab, ActiveTab))
                pane.Active = pane.Tabs.Count;
            pane.Tabs.Add(kept);
        }
        return pane;
    }

    /// <summary>A single tab needs no tab strip; showing one anyway is just chrome (§1.3).</summary>
    public bool ShowTabStrip => Tabs.Count > 1;

    public TabViewModel AddTab(FsUri? uri = null)
    {
        var tab = _newTab();
        tab.PropertyChanged += OnTabChanged;
        Tabs.Add(tab);
        OnPropertyChanged(nameof(ShowTabStrip));
        SessionChanged?.Invoke(this, EventArgs.Empty);

        if (uri is not null)
            _ = tab.NavigateAsync(uri);

        return tab;
    }

    public async Task OpenInNewTabAsync(FsUri uri)
    {
        var tab = AddTab();
        ActiveTab = tab;
        await tab.NavigateAsync(uri);
    }

    /// <summary>Several at once: the first in front, the others behind it, in selection order.</summary>
    public async Task OpenInNewTabsAsync(IReadOnlyList<FsUri> locations)
    {
        if (locations.Count == 0)
            return;

        var tabs = locations.Select(_ => AddTab()).ToList();
        ActiveTab = tabs[0];
        await Task.WhenAll(tabs.Zip(locations, (tab, uri) => tab.NavigateAsync(uri)));
    }

    public void CloseTab(TabViewModel tab)
    {
        // The last tab stays: a pane with no tab has nothing to show and nowhere to type.
        if (Tabs.Count <= 1)
            return;

        var index = Tabs.IndexOf(tab);
        if (index < 0)
            return;

        Tabs.RemoveAt(index);
        tab.PropertyChanged -= OnTabChanged;
        tab.Dispose();
        SessionChanged?.Invoke(this, EventArgs.Empty);

        if (ReferenceEquals(ActiveTab, tab))
            ActiveTab = Tabs[Math.Min(index, Tabs.Count - 1)];

        OnPropertyChanged(nameof(ShowTabStrip));
    }

    public void CloseActiveTab() => CloseTab(ActiveTab);

    /// <summary>
    /// Puts a tab at another place in the strip. <paramref name="before"/> is the index of the tab
    /// it goes in front of as the strip is now, or the count for the end, which is what a drop
    /// between two chips knows.
    /// </summary>
    public void MoveTab(TabViewModel tab, int before)
    {
        var from = Tabs.IndexOf(tab);
        if (from < 0)
            return;

        // Taken out first, everything after it shifts down by one.
        var to = Math.Clamp(before > from ? before - 1 : before, 0, Tabs.Count - 1);
        if (to == from)
            return;

        Tabs.Move(from, to);
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Lets a tab go to another pane, alive: unlike <see cref="CloseTab"/> nothing about it is
    /// stopped or disposed, so its listing, history and any question it is asking go with it.
    /// False for the last tab, which stays for the same reason it can't be closed.
    /// </summary>
    public bool Release(TabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0 || Tabs.Count <= 1)
            return false;

        Tabs.RemoveAt(index);
        tab.PropertyChanged -= OnTabChanged;
        if (ReferenceEquals(ActiveTab, tab))
            ActiveTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        // Not current here any more; the pane it goes to makes it current there.
        tab.IsCurrent = false;
        // A shell stepped to belonged to this pane's line, which the tab is leaving.
        tab.UseConfiguredShell();

        OnPropertyChanged(nameof(ShowTabStrip));
        SessionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Takes in a tab another pane let go of, in front of the tab at <paramref name="before"/>, and shows it.</summary>
    public void Adopt(TabViewModel tab, int? before = null)
    {
        tab.PropertyChanged += OnTabChanged;
        Tabs.Insert(Math.Clamp(before ?? Tabs.Count, 0, Tabs.Count), tab);
        OnPropertyChanged(nameof(ShowTabStrip));
        ActiveTab = tab;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }
}
