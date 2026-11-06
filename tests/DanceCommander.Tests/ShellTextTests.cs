using DanceCommander.Core.Fs;
using DanceCommander.Core.Operations;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>The shell line the activity log records has to do what was done, if someone runs it.</summary>
public sealed class ShellTextTests
{
    [Fact]
    public void APermanentlyDeletedFileIsForcedSoReadOnlyIsNoObstacle() =>
        ShellText.DeletePermanently(FsUri.Local(@"C:\work\notes.txt"), isDirectory: false).ShouldBe(@"del /f C:\work\notes.txt");

    [Fact]
    public void APermanentlyDeletedFolderGoesWithAllItHolds() =>
        ShellText.DeletePermanently(FsUri.Local(@"C:\work\old build"), isDirectory: true).ShouldBe(@"rmdir /s /q ""C:\work\old build""");
}
