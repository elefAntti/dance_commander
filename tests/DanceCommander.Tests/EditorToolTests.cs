using DanceCommander.Core.Commands;
using DanceCommander.Core.Config;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Listing;
using DanceCommander.Core.Matching;
using DanceCommander.Extensibility;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>Tools marked "edits": what F4 asks before the text editor and the shell's own edit.</summary>
public class EditorToolTests
{
    private sealed class Store(ToolsFile file) : IConfigStore<ToolsFile>
    {
        public ToolsFile Current => file;

        public event EventHandler<ToolsFile>? Changed { add { } remove { } }

        public Task SaveAsync(ToolsFile value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static ToolCatalog Load(params ToolDefinition[] tools)
    {
        var catalog = new ToolCatalog(
            new Store(new ToolsFile { Tools = [.. tools] }), new CommandRegistry(),
            new ToolRunner(new Core.Operations.ActivityLog(), new JobService(), NullLogger<ToolRunner>.Instance),
            new CommandListingCatalog(), NullLogger<ToolCatalog>.Instance);
        catalog.Reload();
        return catalog;
    }

    private static FakeContext On(string name) => new()
    {
        Selection = [new FsEntry(FsUri.Local(@"C:\work\" + name), name, IsDirectory: false, 1, null)],
    };

    private static readonly ToolDefinition Code = new()
    {
        Id = "edit-in-code",
        Title = "Edit in VS Code",
        AppliesTo = "file|multi",
        Exec = "code",
        Args = ["${selection}"],
        When = new MatchRule { Glob = "*.{html,py}" },
        Edits = true,
    };

    [Fact]
    public void AnEditorAppliesOnlyToTheFilesItsConditionTakes()
    {
        var editor = Load(Code).Editors.ShouldHaveSingleItem();

        editor.IsApplicable(On("index.html")).ShouldBeTrue();
        editor.IsApplicable(On("photo.png")).ShouldBeFalse();
    }

    [Fact]
    public void AToolNotMarkedEditsIsNotAnEditor() =>
        Load(new ToolDefinition { Id = "code", Title = "Open with VS Code", Exec = "code", Args = ["${path}"] })
            .Editors.ShouldBeEmpty();

    [Fact]
    public void AnEditorWhoseExecIsTheFileIsRefused()
    {
        // It would run the script F4 was pressed on so as not to run it.
        var runsIt = new ToolDefinition { Id = "runs", Title = "Runs", Exec = "${path}", Args = ["${path}"], Edits = true };

        Load(runsIt).Editors.ShouldBeEmpty();
    }

    [Fact]
    public void AnEditorThatIsNeverGivenTheFileIsRefused()
    {
        var folder = new ToolDefinition { Id = "folder", Title = "Folder", Exec = "code", Args = ["${dir}"], Edits = true };

        Load(folder).Editors.ShouldBeEmpty();
    }

    [Fact]
    public void EditorsKeepTheFilesOrder()
    {
        var notepad = new ToolDefinition { Id = "np", Title = "Notepad++", Exec = "notepad++", Args = ["${path}"], Edits = true };

        Load(notepad, Code).Editors.Select(c => c.Id).ShouldBe(["tool.np", "tool.edit-in-code"]);
    }
}
