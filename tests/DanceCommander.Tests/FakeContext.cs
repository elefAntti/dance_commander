using DanceCommander.Core.Commands;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;

namespace DanceCommander.Tests;

/// <summary>
/// A command context with no window behind it: set where it stands and what is selected, and read
/// back what the command asked, showed and followed.
/// </summary>
internal sealed class FakeContext : ICommandContext, IOutputHost
{
    public static readonly FsUri DefaultDirectory = FsUri.Local(@"C:\work");

    public FsUri Directory { get; init; } = DefaultDirectory;

    public IReadOnlyList<FsEntry> Selection { get; init; } = [];

    private readonly FsEntry? _focused;

    /// <summary>Defaults to the first selected entry, as the grid's cursor usually is.</summary>
    public FsEntry? Focused
    {
        get => _focused ?? Selection.FirstOrDefault();
        init => _focused = value;
    }

    public FsUri? OtherPaneDirectory { get; init; }

    public RepositoryInfo? Repository { get; init; }

    public ScriptedDecisions Scripted { get; } = new();

    public IDecisionHost Decisions => Scripted;

    public IOutputHost Output => this;

    /// <summary>For the few tests that resolve services; the rest are told plainly that they did.</summary>
    public IServiceProvider? ServiceProvider { get; init; }

    public IServiceProvider Services =>
        ServiceProvider ?? throw new NotSupportedException("Nothing in these tests resolves services.");

    /// <summary>Jobs the command asked the pane to follow.</summary>
    public List<Job> Followed { get; } = [];

    /// <summary>Output the command put in the pane, as (title, text).</summary>
    public List<(string Title, string Text)> Shown { get; } = [];

    public Task RefreshAsync() => Task.CompletedTask;

    public Task NavigateAsync(FsUri uri, string? selectName = null)
    {
        Navigated.Add(uri);
        return Task.CompletedTask;
    }

    /// <summary>Locations the command went to in this pane.</summary>
    public List<FsUri> Navigated { get; } = [];

    /// <summary>Locations the command asked to open in new tabs.</summary>
    public List<FsUri> OpenedInTabs { get; } = [];

    public Task OpenInNewTabsAsync(IReadOnlyList<FsUri> locations)
    {
        OpenedInTabs.AddRange(locations);
        return Task.CompletedTask;
    }

    public void ShowOutput(string title, string text, bool failed = false) => Shown.Add((title, text));

    public void Follow(Job job) => Followed.Add(job);

    public List<string> Done { get; } = [];

    public void ShowDone(string text) => Done.Add(text);

    /// <summary>An entry inside <see cref="DefaultDirectory"/>.</summary>
    public static FsEntry Entry(string name, bool directory = false) =>
        new(DefaultDirectory.Child(name), name, directory, directory ? 0 : 1, null);
}
