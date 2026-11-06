using DanceCommander.Core.Fs;
using DanceCommander.Core.Navigation;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// Text in the address bar is a search only when it could never have been an address, so the bar
/// stays an address bar first.
/// </summary>
public sealed class AddressSearchTests
{
    [Theory]
    [InlineData("disk mon", "disk mon")]
    [InlineData("  Projects ", "Projects")]
    [InlineData("?dow", "dow")]            // asked for outright, even if a bookmark starts so
    [InlineData(@"? C:\src", @"C:\src")]// ...and then even what looks like a path
    public void ABareWordOrAQuestionMarkIsASearch(string text, string query) =>
        AddressSearch.QueryIn(text).ShouldBe(query);

    [Theory]
    [InlineData(@"C:\Proj")]               // a mistyped path should say so, not search for itself
    [InlineData("src/app")]
    [InlineData("sftp://build01/var")]
    [InlineData("about:computer")]
    [InlineData("")]
    [InlineData("?")]
    [InlineData("  ?  ")]
    public void AnythingThatCouldBeAnAddressIsNot(string text) =>
        AddressSearch.QueryIn(text).ShouldBeNull();

    [Fact]
    public void ItSearchesFromTheFolderEvenWhenStandingInAView()
    {
        var folder = FsUri.Local(@"C:\work");
        var earlier = FsUri.View("search", folder, "old");

        AddressSearch.From(earlier, " new ").ShouldBe(FsUri.View("search", folder, "new"));
        AddressSearch.From(folder, "new").ShouldBe(FsUri.View("search", folder, "new"));
    }
}
