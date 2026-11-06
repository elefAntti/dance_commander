using DanceCommander.Core.Fs;
using DanceCommander.Providers.Local;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>about:app: the version, the licences and the folders, as rows that are the things themselves.</summary>
public sealed class AboutAppTests
{
    private static async Task<List<FsEntry>> ListAsync(FsUri page)
    {
        var rows = new List<FsEntry>();
        await foreach (var row in new AboutProvider().ListAsync(page, CancellationToken.None))
            rows.Add(row);
        return rows;
    }

    [Fact]
    public async Task ListsTheProgramWithItsVersionTheLicencesAndTheFolders()
    {
        var rows = await ListAsync(AboutPages.App);

        var program = rows.Single(r => r.Name == "Dance Commander");
        program.IsDirectory.ShouldBeTrue();
        program.Uri.ShouldBe(FsUri.Local(AppContext.BaseDirectory));
        program.Extra![AboutProvider.DescriptionColumn].ShouldStartWith("Version ");

        // The files beside the executable, wherever this build runs from.
        rows.Single(r => r.Name == "LICENSE.txt").Uri
            .ShouldBe(FsUri.Local(Path.Combine(AppContext.BaseDirectory, "LICENSE.txt")));
        rows.Single(r => r.Name == "THIRD-PARTY-NOTICES.txt").IsDirectory.ShouldBeFalse();
        rows.Single(r => r.Name == "README.md").IsDirectory.ShouldBeFalse();

        rows.Single(r => r.Name == "Settings").Uri.ShouldBe(FsUri.Local(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DanceCommander")));
        rows.Single(r => r.Name == "Logs").IsDirectory.ShouldBeTrue();
    }

    [Fact]
    public async Task TheComputerPageLeadsToItAndItIsAPlace()
    {
        (await ListAsync(FsUri.Computer)).ShouldContain(r => r.Uri == AboutPages.App && r.Name == "About Dance Commander");
        (await new AboutProvider().StatAsync(AboutPages.App, CancellationToken.None)).ShouldNotBeNull();
        FsUri.Parse("about:app").ShouldBe(AboutPages.App);
    }
}
