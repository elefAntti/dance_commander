using DanceCommander.Core.Navigation;
using Shouldly;

namespace DanceCommander.Tests;

public class CommandHistoryTests
{
    [Fact]
    public void EachShellAndServerHasItsOwnList()
    {
        var history = new CommandHistory();
        history.Add("bash", "git status");
        history.Add("claude@host", "systemctl status nginx");

        history.For("bash").ShouldBe(["git status"]);
        history.For("claude@host").ShouldBe(["systemctl status nginx"]);
        history.For("cmd").ShouldBeEmpty();
    }

    [Fact]
    public void ARepeatedLineMovesToTheEndInsteadOfAppearingTwice()
    {
        var history = new CommandHistory();
        history.Add("bash", "make");
        history.Add("bash", "make test");
        history.Add("bash", "make");

        history.For("bash").ShouldBe(["make test", "make"]);
    }

    [Fact]
    public void TheOldestGoPastCapacity()
    {
        var history = new CommandHistory();
        for (var i = 0; i < CommandHistory.Capacity + 5; i++)
            history.Add("bash", $"echo {i}");

        var lines = history.For("bash");
        lines.Count.ShouldBe(CommandHistory.Capacity);
        lines[0].ShouldBe("echo 5");
    }

    [Fact]
    public void UpAndDownWalkTheListAndGiveTheDraftBack()
    {
        var cursor = new HistoryCursor(["one", "two"], draft: "half-typ");

        cursor.Older().ShouldBe("two");
        cursor.Older().ShouldBe("one");
        cursor.Older().ShouldBeNull();
        cursor.Newer().ShouldBe("two");
        cursor.Newer().ShouldBe("half-typ");
        cursor.Newer().ShouldBeNull();
    }
}
