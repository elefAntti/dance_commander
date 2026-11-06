using DanceCommander.Core.Fs;
using Shouldly;

namespace DanceCommander.Tests;

public class FsUriTests
{
    [Theory]
    [InlineData(@"C:\src\app", "C:/src/app", @"C:\src\app")]
    [InlineData(@"c:\SRC\", "C:/SRC", @"C:\SRC")]
    [InlineData(@"C:\", "C:/", @"C:\")]
    [InlineData(@"\\?\C:\very\long", "C:/very/long", @"C:\very\long")]
    [InlineData(@"C:\a\..\b", "C:/b", @"C:\b")]
    [InlineData(@"C:\a\.\b", "C:/a/b", @"C:\a\b")]
    public void NormalisesLocalPaths(string input, string path, string display)
    {
        var uri = FsUri.Local(input);
        uri.Path.ShouldBe(path);
        uri.Display.ShouldBe(display);
        uri.IsLocal.ShouldBeTrue();
    }

    [Theory]
    [InlineData(@"C:\music\Wham!\Make It Big")]
    [InlineData(@"C:\a!b")]
    [InlineData(@"C:\a!!b!")]
    public void AFolderWithAnExclamationMarkSurvivesItsPersistedForm(string path)
    {
        // Read back as an archive and its entry, it could not be reached from history or bookmarks.
        var uri = FsUri.Local(path);
        var back = FsUri.Parse(uri.ToString());

        back.ShouldBe(uri);
        back.IsArchiveEntry.ShouldBeFalse();
        back.Display.ShouldBe(path);
    }

    [Fact]
    public void AnArchiveEntryWithAnExclamationMarkSurvivesToo()
    {
        var uri = FsUri.ArchiveEntry(FsUri.Local(@"C:\dl\Hey!.zip"), "/news!/today.txt");
        FsUri.Parse(uri.ToString()).ShouldBe(uri);
    }

    [Fact]
    public void ATypedPathWithAnExclamationMarkIsTheFolderWhenTheDiskSaysSo()
    {
        bool IsFile(string path) => path == @"C:\dl\rel.zip";
        bool Exists(string path) => path is @"C:\dl\rel.zip" or @"C:\music\Wham!";

        FsUri.TryParseTyped(@"C:\music\Wham!", IsFile, Exists, out var folder, out _).ShouldBeTrue();
        folder.ShouldBe(FsUri.Local(@"C:\music\Wham!"));

        FsUri.TryParseTyped(@"C:\dl\rel.zip!/bin", IsFile, Exists, out var entry, out _).ShouldBeTrue();
        entry!.IsArchiveEntry.ShouldBeTrue();
    }

    [Fact]
    public void OtherProgramsGetTheAddressAsItIsWritten()
    {
        // Doubled is the application's own form; handed to curl or a clipboard, it named another file.
        var uri = FsUri.Remote("sftp", "h", "/x/a!b");
        uri.ToString().ShouldBe("sftp://h/x/a!!b");
        uri.Text.ShouldBe("sftp://h/x/a!b");
    }

    [Fact]
    public void TextFromOutsideIsReadWholeWithItsExclamationMarks()
    {
        FsUri.TryParseWhole("sftp://h/x/a!!b", out var uri).ShouldBeTrue();
        uri!.Path.ShouldBe("/x/a!!b");
        uri.IsArchiveEntry.ShouldBeFalse();
    }

    [Fact]
    public void ATypedFolderWithTwoExclamationMarksIsThatFolder()
    {
        bool Exists(string path) => path is @"C:\x\a!!b" or @"C:\x\a!b";

        FsUri.TryParseTyped(@"C:\x\a!!b", _ => false, Exists, out var uri, out _).ShouldBeTrue();
        uri.ShouldBe(FsUri.Local(@"C:\x\a!!b"));
    }

    [Theory]
    [InlineData("file:///C:/src/app")]
    [InlineData("sftp://antti@build01:22/var/log")]
    [InlineData("davs://cloud.example.com/dav/files/antti")]
    [InlineData("file:///C:/dl/rel.zip!/bin")]
    [InlineData("file:///C:/dl/rel.zip!/bin/tools.tgz!/usr/bin")]
    public void RoundTripsThroughItsPersistedForm(string text)
    {
        var uri = FsUri.Parse(text);
        uri.ToString().ShouldBe(text);
        FsUri.Parse(uri.ToString()).ShouldBe(uri);
    }

    [Fact]
    public void ParsesRemoteAuthorityAndPath()
    {
        var uri = FsUri.Parse("sftp://antti@build01:22/var/log");
        uri.Scheme.ShouldBe("sftp");
        uri.Authority.ShouldBe("antti@build01:22");
        uri.Path.ShouldBe("/var/log");
        uri.Name.ShouldBe("log");
    }

    [Fact]
    public void NestsArchivesToAnyDepth()
    {
        var uri = FsUri.Parse("file:///C:/dl/rel.zip!/bin/tools.tgz!/usr/bin");

        uri.Scheme.ShouldBe(FsUri.ArchiveScheme);
        uri.Path.ShouldBe("/usr/bin");
        uri.Inner!.Path.ShouldBe("/bin/tools.tgz");
        uri.Inner.Inner!.Display.ShouldBe(@"C:\dl\rel.zip");
    }

    [Fact]
    public void LeavingAnArchiveRootLandsBesideTheArchiveFile()
    {
        var inside = FsUri.Parse("file:///C:/dl/rel.zip!/bin");

        inside.Parent!.Path.ShouldBe("/");        // the archive root
        inside.Parent.Parent!.ShouldBe(FsUri.Local(@"C:\dl"));
    }

    [Theory]
    [InlineData(@"C:\src\app", @"C:\src")]
    [InlineData(@"C:\src", @"C:\")]
    public void WalksUpToTheDriveRoot(string child, string parent)
    {
        FsUri.Local(child).Parent.ShouldBe(FsUri.Local(parent));
    }

    [Fact]
    public void StopsAtTheDriveRoot()
    {
        FsUri.Local(@"C:\").Parent.ShouldBeNull();
        FsUri.Local(@"C:\").IsRoot.ShouldBeTrue();
        FsUri.Local(@"C:\").Name.ShouldBe("C:");
    }

    [Fact]
    public void ChildAppendsWithoutDoublingTheRootSeparator()
    {
        FsUri.Local(@"C:\").Child("src").Display.ShouldBe(@"C:\src");
        FsUri.Local(@"C:\src").Child("app").Display.ShouldBe(@"C:\src\app");
        FsUri.Remote("sftp", "host", "/").Child("var").Path.ShouldBe("/var");
    }

    [Fact]
    public void LocalPathsCompareCaseInsensitivelyButRemoteOnesDoNot()
    {
        FsUri.Local(@"C:\Src").ShouldBe(FsUri.Local(@"c:\src"));
        FsUri.Local(@"C:\Src").GetHashCode().ShouldBe(FsUri.Local(@"c:\src").GetHashCode());
        FsUri.Remote("sftp", "host", "/Var").ShouldNotBe(FsUri.Remote("sftp", "host", "/var"));
    }

    [Fact]
    public void PrefixesOnlyPathsThatActuallyNeedLongPathSupport()
    {
        FsUri.Local(@"C:\short").ToLocalPath().ShouldBe(@"C:\short");

        var deep = FsUri.Local(@"C:\" + string.Join('\\', Enumerable.Repeat("segment", 40)));
        deep.ToLocalPath().ShouldStartWith(@"\\?\C:\");
    }

    [Theory]
    [InlineData(@"C:\x\notes.", @"\\?\C:\x\notes.")]
    [InlineData(@"C:\x\notes.\a.txt", @"\\?\C:\x\notes.\a.txt")]
    [InlineData(@"\\server\share\report \a", @"\\?\UNC\server\share\report \a")]
    [InlineData(@"C:\x\.git", @"C:\x\.git")]
    [InlineData(@"C:\x\a.b", @"C:\x\a.b")]
    public void PrefixesANameWin32WouldTrim(string path, string expected) =>
        FsUri.LocalExact(path).ToLocalPath().ShouldBe(expected);

    [Fact]
    public void AnExactPathKeepsItsLastSpaceAndItsShare()
    {
        FsUri.Local(@"\\?\C:\x\report ").Name.ShouldBe("report ");
        FsUri.LocalExact(@"C:\x\report ").Name.ShouldBe("report ");
        FsUri.Local(@"\\?\UNC\server\share\a").ShouldBe(FsUri.Local(@"\\server\share\a"));
        // Typed text is still trimmed.
        FsUri.Local(@"  C:\x\report ").Name.ShouldBe("report");
    }

    [Fact]
    public void OurOwnWrittenFormReadsBackTheNameWithItsSpace()
    {
        // The clipboard, a drag and a collection keep ToString(); trimmed on the way back, Ctrl+X
        // on "report " and Ctrl+V moved "report" (review, 2026-10-28).
        var spaced = FsUri.LocalExact(@"C:\x\report ");
        FsUri.Parse(spaced.ToString()).ShouldBe(spaced);
        FsUri.Parse(spaced.ToString()).Name.ShouldBe("report ");
    }

    [Theory]
    [InlineData(@"a\b.txt")]
    [InlineData("report ")]
    [InlineData(" leading")]
    public void AServersNameComesBackFromOurOwnFormAsItIs(string name)
    {
        // Cut on an SFTP a\b.txt and Paste moved /a/b.txt: the parse read \ as a separator.
        var file = FsUri.Remote("sftp", "x@host", "/srv").Child(name);
        FsUri.Parse(file.ToString()).ShouldBe(file);
        FsUri.Parse(file.ToString()).Name.ShouldBe(name);
    }

    [Fact]
    public void AShortNameBesideADottedOneIsNotExpandedThroughWin32()
    {
        // GetLongPathName reads notes. as notes, like any call without \\?\.
        var root = Directory.CreateTempSubdirectory("dc-short-name-folder-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "notes"));
            Directory.CreateDirectory(@"\\?\" + Path.Combine(root, "notes."));
            var parent = Path.GetDirectoryName(root)!;
            var shortRoot = Path.Combine(parent, "DC-SHO~1");
            if (!Directory.Exists(shortRoot))
                return; // no 8.3 names on this volume
            FsUri.LocalExact(Path.Combine(shortRoot, "notes.")).Name.ShouldBe("notes.");
        }
        finally
        {
            Directory.Delete(@"\\?\" + Path.Combine(root, "notes."));
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RejectsEmptyLocationsWithAMessage(string? text)
    {
        FsUri.TryParse(text, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("sftp://bob:hunter2@build01/var/log")]
    [InlineData("davs://bob@example.com:hunter2@cloud.example.com/dav")]
    [InlineData("flat:sftp://bob:hunter2@build01:2222/var")]
    // Cut off before its '@' by a '/' or by the archive separator, the rest reads as a port.
    [InlineData("sftp://bob:hunter/2@build01/var")]
    [InlineData("sftp://bob:hunter!2@build01/var")]
    [InlineData("sftp://bob:/hunter2@build01/var")]
    public void RefusesAPasswordInTheAddressWithoutRepeatingIt(string text)
    {
        // Taken for a user name, the password went to the log, history and the server's log.
        FsUri.TryParse(text, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
        error.ShouldNotContain("hunter2");
    }

    [Theory]
    [InlineData("sftp://build01:2222/var")]
    [InlineData("sftp://bob@[::1]:2222/var")]
    [InlineData("davs://bob@example.com@cloud.example.com/dav")]
    public void AColonAfterTheUserIsStillAPort(string text) =>
        FsUri.TryParse(text, out _, out _).ShouldBeTrue();

    // ---- views --------------------------------------------------------------------------------

    [Fact]
    public void AChildOfAViewStaysInThatView()
    {
        // It used to forget the parameter, so a "from:" address lost the listing it named.
        var view = FsUri.View("from", FsUri.Local(@"C:\src\app"), "worktrees");

        var child = view.Child("main");

        child.Parameter.ShouldBe("worktrees");
        child.Inner.ShouldBe(view.Inner);
    }

    [Fact]
    public void TheAnchorOfAViewIsTheFolderItIsAViewOf()
    {
        var folder = FsUri.Local(@"C:\src\app");

        FsUri.View("flat", folder).Anchor.ShouldBe(folder);
        // Through any number of them: a search over a flattened tree still writes into the folder.
        FsUri.View("search", FsUri.View("flat", folder), "*.cs").Anchor.ShouldBe(folder);
        folder.Anchor.ShouldBe(folder);
    }

    [Fact]
    public void AnArchiveEntryIsItsOwnAnchor()
    {
        // Not a view: its Inner is the file it lives in, not a folder it is a way of looking at.
        var entry = FsUri.ArchiveEntry(FsUri.Local(@"C:\dl\a.zip"), "/bin");

        entry.Anchor.ShouldBe(entry);
    }
}

/// <summary>
/// The Name column shows the stem and the Ext column the extension, so the two together have to
/// account for the whole filename exactly — anything else means the grid is lying about a name.
/// </summary>
public class EntryNameSplitTests
{
    private static FsEntry File(string name) =>
        new(FsUri.Local(@"C:\work").Child(name), name, IsDirectory: false, 0, null);

    private static FsEntry Directory(string name) =>
        new(FsUri.Local(@"C:\work").Child(name), name, IsDirectory: true, 0, null);

    [Theory]
    [InlineData("Program.cs", "Program", "cs")]
    [InlineData("archive.tar.gz", "archive.tar", "gz")]        // only the last dot splits
    [InlineData("README", "README", "")]                        // no extension at all
    [InlineData(".gitignore", ".gitignore", "")]                // a dotfile is all stem
    [InlineData("trailing.", "trailing.", "")]                  // a bare trailing dot is not one
    [InlineData("UPPER.TXT", "UPPER", "txt")]                   // extension lower-cased for sorting
    public void SplitsTheNameIntoStemAndExtension(string name, string stem, string extension)
    {
        var entry = File(name);
        entry.Stem.ShouldBe(stem);
        entry.Extension.ShouldBe(extension);
    }

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("archive.tar.gz")]
    [InlineData("README")]
    [InlineData(".gitignore")]
    [InlineData("UPPER.TXT")]
    public void StemAndExtensionAlwaysReconstructTheName(string name)
    {
        var entry = File(name);
        var rebuilt = entry.Extension.Length == 0 ? entry.Stem : $"{entry.Stem}.{entry.Extension}";
        rebuilt.ShouldBe(name, StringCompareShould.IgnoreCase);
    }

    [Fact]
    public void NeverSplitsADirectory()
    {
        var entry = Directory("my.folder.v2");
        entry.Stem.ShouldBe("my.folder.v2");
        entry.Extension.ShouldBe(string.Empty);
    }
}

/// <summary>Script locations (§6.16): a listing id, then crumbs the script printed.</summary>
public class ScriptAddressTests
{
    private const string Pipeline = "from:file:///C:/src/app?gitlab/12345/build-job";

    [Fact]
    public void TheIdStaysInTheParameterAndTheCrumbsGoInThePath()
    {
        var uri = FsUri.Parse(Pipeline);

        uri.Parameter.ShouldBe("gitlab");
        uri.Path.ShouldBe("/12345/build-job");
        uri.Inner.ShouldBe(FsUri.Local(@"C:\src\app"));
        uri.Name.ShouldBe("build-job");
        uri.ToString().ShouldBe(Pipeline);
        uri.Display.ShouldBe(@"from:C:\src\app?gitlab/12345/build-job");
    }

    [Fact]
    public void UpClimbsTheCrumbsBeforeLeavingTheView()
    {
        var job = FsUri.Parse(Pipeline);

        var pipeline = job.Parent!;
        pipeline.ToString().ShouldBe("from:file:///C:/src/app?gitlab/12345");
        pipeline.Parameter.ShouldBe("gitlab");

        var root = pipeline.Parent!;
        root.IsRoot.ShouldBeTrue();
        root.ToString().ShouldBe("from:file:///C:/src/app?gitlab");

        root.Parent.ShouldBe(FsUri.Local(@"C:\src\app"));
    }

    [Fact]
    public void AChildKeepsTheListingItBelongsTo() =>
        FsUri.Parse("from:file:///C:/src/app?gitlab").Child("12345").ToString()
            .ShouldBe("from:file:///C:/src/app?gitlab/12345");

    [Fact]
    public void CrumbsCompareExactlyWhileTheIdDoesNot()
    {
        // Two jobs whose names differ only in case are two jobs; the id is the user's own word.
        FsUri.Parse("from:C:/src?gitlab/1/Build").ShouldNotBe(FsUri.Parse("from:C:/src?gitlab/1/build"));
        FsUri.Parse("from:C:/src?GitLab/1").ShouldBe(FsUri.Parse("from:C:/src?gitlab/1"));
    }

    [Fact]
    public void AnArtifactZipOpensInPlace()
    {
        // The archive provider resolves Inner first, so a zip a script serves browses like any zip.
        var entry = FsUri.Parse(Pipeline + "/artifacts.zip!/bin/app.exe");

        entry.IsArchiveEntry.ShouldBeTrue();
        entry.Path.ShouldBe("/bin/app.exe");
        entry.Inner!.Scheme.ShouldBe("from");
        entry.Inner.Path.ShouldBe("/12345/build-job/artifacts.zip");
    }

    [Fact]
    public void ASearchQueryWithASlashIsStillJustAQuery()
    {
        var search = FsUri.Parse("search:C:/src?docs/api");

        search.Parameter.ShouldBe("docs/api");
        search.IsRoot.ShouldBeTrue();
    }
}

public class NestedViewAddressTests
{
    [Fact]
    public void AFlatViewOfAScriptLocationKeepsTheListingAndItsCrumbs()
    {
        var flat = FsUri.Parse("flat:from:file:///C:/src?gitlab/4711");

        flat.Scheme.ShouldBe("flat");
        flat.Parameter.ShouldBe(string.Empty);
        flat.Inner!.Parameter.ShouldBe("gitlab");
        flat.Inner.Path.ShouldBe("/4711");
        FsUri.Parse(flat.ToString()).ShouldBe(flat);
    }
}

public class PathTextTests
{
    private static string Part(string text, int index)
    {
        var (start, length) = PathText.SegmentAt(text, index);
        return text.Substring(start, length);
    }

    [Theory]
    [InlineData(@"C:\Users\me\Downloads", 5, "Users")]      // inside a part
    [InlineData(@"C:\Users\me\Downloads", 3, "Users")]      // at its first character
    [InlineData(@"C:\Users\me\Downloads", 8, "Users")]      // on the separator after it: the part to the left
    [InlineData(@"C:\Users\me\Downloads", 21, "Downloads")] // past the end
    [InlineData(@"C:\Users\me\Downloads", 0, "C")]
    [InlineData("flat:C:/src?gitlab/4711", 1, "flat")]
    [InlineData("flat:C:/src?gitlab/4711", 14, "gitlab")]
    [InlineData("sftp://host/var/log", 9, "host")]
    public void ADoubleClickSelectsThePathPartUnderIt(string text, int index, string expected) =>
        Part(text, index).ShouldBe(expected);

    [Fact]
    public void NothingToSelectInAnEmptyBoxOrBetweenSeparators()
    {
        PathText.SegmentAt("", 0).ShouldBe((0, 0));
        PathText.SegmentAt("sftp://host", 6).Length.ShouldBe(0); // between the two slashes
    }
}

public class AboutAddressTests
{
    [Fact]
    public void TheComputerPageIsTheTop()
    {
        var computer = FsUri.Parse("about:computer");

        computer.ShouldBe(FsUri.Computer);
        computer.ToString().ShouldBe("about:computer");
        computer.Display.ShouldBe("about:computer");
        computer.Name.ShouldBe("This computer");
        computer.Parent.ShouldBeNull();
        FsUri.Parse("About:Computer").ShouldBe(FsUri.Computer); // as the browser's are
        FsUri.Parse("about:").ShouldBe(FsUri.Computer);
    }

    [Fact]
    public void OtherPagesClimbToTheOneAboveThem()
    {
        var collection = FsUri.Parse("about:collections/release");

        collection.Name.ShouldBe("release");
        collection.Parent!.ToString().ShouldBe("about:collections");
        collection.Parent.Parent.ShouldBe(FsUri.Computer);
    }

    [Fact]
    public void UpFromADriveRootIsTheComputerPageButItsParentIsNothing()
    {
        // Up goes on to the computer page; the parent does not: code that builds an entry from its
        // parent, or asks what contains what, must not find about:computer there.
        var root = FsUri.Local(@"C:\");

        root.Up.ShouldBe(FsUri.Computer);
        root.Parent.ShouldBeNull();
        FsUri.Computer.Up.ShouldBeNull();
        FsUri.Local(@"C:\src").Up.ShouldBe(root);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData(@"..\..\Users\me\Documents")] // a legal folder name on a Linux server
    [InlineData("report.txt:hidden")] // an alternate data stream
    public void AChildOfALocalFolderIsOneNameNeverAPath(string name) =>
        Should.Throw<ArgumentException>(() => FsUri.Local(@"D:\dest").Child(name));

    [Fact]
    public void OnAServerABackslashIsJustACharacterOfTheName() =>
        FsUri.Remote("sftp", "host", "/srv").Child(@"a\b").Name.ShouldBe(@"a\b");

    [Theory]
    // A typo for about:, which read as the path \abou:bookmarks and then ended the application.
    [InlineData("abou:bookmarks")]
    [InlineData("sftp:host")]
    [InlineData("ab:")]
    public void AnUnknownKindOfAddressIsRefusedNotTakenForAPath(string typed)
    {
        FsUri.TryParse(typed, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("not a kind of address");
    }

    [Theory]
    [InlineData(@"C:\work")]
    [InlineData("C:")]
    [InlineData(@"\\?\C:\work")]
    [InlineData(@"C:\work\notes.txt:stream")]
    [InlineData(@"\\server\share")]
    public void PathsWithAColonInTheirPlaceStillParse(string typed) =>
        FsUri.TryParse(typed, out var uri, out _).ShouldBeTrue();
}
