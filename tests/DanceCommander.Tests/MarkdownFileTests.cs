using DanceCommander.Core.Preview;
using Shouldly;

namespace DanceCommander.Tests;

public sealed class MarkdownFileTests
{
    private const string Folder = @"C:\repo\docs";

    [Theory]
    [InlineData("README.md", true)]
    [InlineData("notes.MARKDOWN", true)]
    [InlineData("a.mdown", true)]
    [InlineData("readme.txt", false)]
    [InlineData("md", false)]
    public void KnowsMarkdownByItsName(string name, bool expected) =>
        MarkdownFile.IsMarkdown(name).ShouldBe(expected);

    [Theory]
    [InlineData("shot.png", @"C:\repo\docs\shot.png")]
    [InlineData("./img/shot.png", @"C:\repo\docs\img\shot.png")]
    [InlineData("../assets/logo.png", @"C:\repo\assets\logo.png")]
    [InlineData("my%20shot.png", @"C:\repo\docs\my shot.png")]
    [InlineData("shot.png?raw=true", @"C:\repo\docs\shot.png")]
    [InlineData("shot.png#dark", @"C:\repo\docs\shot.png")]
    public void ReadsImagesBesideTheFile(string url, string expected) =>
        MarkdownFile.ResolveImage(Folder, url).ShouldBe(expected);

    [Theory]
    [InlineData("https://example.com/a.png")]
    [InlineData("http://example.com/a.png")]
    [InlineData("file:///C:/a.png")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData(@"\\server\share\a.png")]
    [InlineData("//server/share/a.png")]
    [InlineData("%5C%5Cserver%5Cshare%5Ca.png")]
    [InlineData(@"C:\Windows\a.png")]
    [InlineData("/a.png")]
    [InlineData("")]
    [InlineData(null)]
    public void NeverReachesTheNetworkOrFarAway(string? url) =>
        MarkdownFile.ResolveImage(Folder, url).ShouldBeNull();

    [Fact]
    public void AFileOnAShareShowsNoImages() =>
        MarkdownFile.ResolveImage(@"\\server\share\docs", "shot.png").ShouldBeNull();

    [Fact]
    public void AFileNotOnALocalDriveShowsNoImages() =>
        MarkdownFile.ResolveImage(null, "shot.png").ShouldBeNull();
}
