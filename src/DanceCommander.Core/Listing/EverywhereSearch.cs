using System.Runtime.CompilerServices;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Matching;
using DanceCommander.Core.Watching;

namespace DanceCommander.Core.Listing;

/// <summary>One place an everywhere search walks.</summary>
/// <param name="Label">What the Where column says of what is found there.</param>
/// <param name="Likelihood">How likely what is wanted is to be here: lower is likelier.</param>
public sealed record SearchArea(string Label, FsUri Root, int Likelihood);

/// <summary>The places on this machine an everywhere search walks, in the order it walks them.</summary>
/// <remarks>
/// The order is most of the speed. A whole disk takes minutes, the folder you are in and your own
/// folders take seconds, and the thing searched for is nearly always in one of those; so they go
/// first and their rows are on screen while Program Files and Windows are still being read.
/// </remarks>
public static class SearchAreas
{
    public const int Here = 0;
    public const int Yours = 1;
    public const int Home = 2;
    public const int ThisDrive = 3;
    public const int OtherDrives = 4;
    public const int Programs = 5;
    public const int Windows = 6;

    /// <summary>
    /// From where the pane stands, then your own folders, the rest of your home folder, what else is
    /// on the system drive, the other drives, and what is installed: Windows itself last. Network
    /// drives never: a slow share would hold the whole search up and is rarely where things are.
    /// </summary>
    public static IReadOnlyList<SearchArea> OfThisMachine(FsUri? here)
    {
        var areas = new List<SearchArea>();
        void Add(string label, string? path, int likelihood)
        {
            if (!string.IsNullOrEmpty(path))
                areas.Add(new SearchArea(label, FsUri.Local(path), likelihood));
        }

        if (here is { IsLocal: true, Inner: null })
            areas.Add(new SearchArea("Here", here, Here));

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Yours);
        Add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Yours);
        Add("Downloads", profile.Length == 0 ? null : Path.Combine(profile, "Downloads"), Yours);
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDrive"), Yours);
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDriveCommercial"), Yours);
        Add("Home", profile, Home);

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemDrive = windows.Length == 0 ? null : Path.GetPathRoot(windows);
        Add(DriveLabel(systemDrive), systemDrive, ThisDrive);
        Add("Users", profile.Length == 0 ? null : Path.GetDirectoryName(profile), ThisDrive);

        foreach (var drive in DrivesBesides(systemDrive))
            Add(DriveLabel(drive), drive, SearchAreas.OtherDrives);

        Add("Programs", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Programs);
        Add("Programs", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Programs);
        Add("Windows", windows, Windows);

        return Arrange(areas, uri => Directory.Exists(uri.ToLocalPath()));
    }

    /// <summary>
    /// Each place once, at its first and likeliest mention, and only if it is there: the folder you
    /// are in may be Documents itself, and a machine may have no OneDrive.
    /// </summary>
    public static IReadOnlyList<SearchArea> Arrange(IEnumerable<SearchArea> candidates, Func<FsUri, bool> exists)
    {
        var seen = new HashSet<FsUri>();
        return candidates.Where(area => seen.Add(area.Root) && exists(area.Root)).ToList();
    }

    /// <summary>Fixed disks, then removable ones, that are ready now. Not network or optical drives.</summary>
    private static IEnumerable<string> DrivesBesides(string? systemDrive)
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var type in new[] { DriveType.Fixed, DriveType.Removable })
        {
            foreach (var drive in drives)
            {
                if (drive.DriveType == type
                    && !string.Equals(drive.Name, systemDrive, StringComparison.OrdinalIgnoreCase)
                    && drive.IsReady)
                {
                    yield return drive.Name;
                }
            }
        }
    }

    private static string DriveLabel(string? root) => root?.TrimEnd('\\', '/') ?? string.Empty;
}

/// <summary>
/// A search of the whole computer, from where the pane stands outwards (<c>everywhere:</c>), as a
/// browser's address bar searches the web. Ctrl+S's <c>search:</c> stays the search of one folder.
/// </summary>
/// <remarks>
/// <para>Each area is walked as its own tree, fenced off from the others' roots, so nothing is
/// walked twice: the walk of Home does not go back into Documents, nor that of the C: drive into
/// Users or Windows. A matching folder at an area's root is still found, by the walk of the folder
/// above it.</para>
///
/// <para>Besides hidden folders it declines clutter (<see cref="PrunePolicy"/>), and says so; Ctrl+H
/// walks both. A folder it cannot read is counted, not fatal: a whole machine has plenty.</para>
/// </remarks>
public sealed class EverywhereListingSource : IListingSource
{
    private const int MaxDepth = 64;

    private readonly IProviderRegistry _providers;
    private readonly FsUri _start;
    private readonly NameMatcher _matcher;
    private readonly PrunePolicy _prune;
    private readonly IReadOnlyList<SearchArea>? _areas;
    private readonly DateTimeOffset _now = DateTimeOffset.Now;

    /// <param name="areas">Where to walk; this machine's places when not given.</param>
    public EverywhereListingSource(
        IProviderRegistry providers, FsUri uri, ListingFilter filter, IReadOnlyList<SearchArea>? areas = null)
    {
        _providers = providers;
        _start = uri.Inner ?? throw new InvalidOperationException("A search must say where it starts.");
        _prune = new PrunePolicy(filter, skipClutter: true);
        _matcher = NameQuery.Compile(uri.Parameter);
        _areas = areas;
        Uri = uri;
    }

    public FsUri Uri { get; }

    public string Title => $"everywhere: {Uri.Parameter}";

    public ColumnSet Columns => ColumnSet.OfEverywhere;

    public bool SupportsNavigation => true;

    public string? Omission => _prune.Omission;

    public bool RereadWhenFilterChanges => true;

    public IDirectoryWatcher? Watch() => null;

    public async IAsyncEnumerable<FsEntry> ListAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Asked here, on the walk's thread rather than the window's: whether a drive is ready can
        // take a moment.
        var areas = _areas ?? SearchAreas.OfThisMachine(_start);
        var roots = areas.Select(area => area.Root).ToHashSet();

        foreach (var area in areas)
        {
            var fenced = roots.Where(root => root != area.Root).ToHashSet();
            await foreach (var entry in TreeWalk.WalkAsync(
                _providers, area.Root, _prune, MaxDepth,
                (entry, prefix) => Select(area, entry, prefix),
                ct, fenced, keepGoing: true).ConfigureAwait(false))
            {
                yield return entry;
            }
        }
    }

    private FsEntry? Select(SearchArea area, FsEntry entry, string prefix)
    {
        if (!_matcher.Matches(entry))
            return null;

        var extra = entry.Extra is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(entry.Extra);

        // The folder in full: "src/app" says nothing once rows come from all over the machine.
        // Written as the address bar would write it, C:\src\app, not as the URI's C:/src/app.
        extra[ColumnIds.RelativePath] = entry.Uri.Parent?.Display ?? string.Empty;
        extra[ColumnIds.Where] = area.Label;
        extra[ColumnIds.Where + ColumnIds.SortKeySuffix] =
            Relevance.Key(area.Likelihood, _matcher.Rank(entry.Name), entry, prefix, _now);
        return entry with { Extra = extra };
    }
}

/// <summary>How likely a search's row is to be the one wanted, as a key that sorts likeliest first.</summary>
public static class Relevance
{
    /// <summary>
    /// Files no person made or opens: what a build, an installer or Windows leaves behind. They
    /// match a search as readily as anything, and bury what was meant under hundreds of rows.
    /// </summary>
    private static readonly HashSet<string> MadeByMachines = new(StringComparer.OrdinalIgnoreCase)
    {
        "dll", "sys", "mui", "pdb", "obj", "o", "a", "lib", "exp", "ilk", "pch", "ipch", "pyc", "pyo",
        "class", "cat", "manifest", "mum", "etl", "cache", "tmp", "idx", "pack", "map",
    };

    /// <summary>Folders that hold what a build made rather than what somebody wrote.</summary>
    private static readonly HashSet<string> BuildOutput = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "build", "dist", "out", "target", "Debug", "Release", "packages",
    };

    /// <summary>
    /// In order of weight: the area it was found in; whether a person made it, or a machine; how well
    /// the name matches; how recently it changed; how deep it lies in its area. Where the user keeps
    /// things is the strongest hint there is, and a perfect name match on a DLL is still a DLL.
    /// </summary>
    /// <param name="prefix">The folder it is in, relative to its area's root, '/'-separated.</param>
    public static string Key(int likelihood, int nameRank, FsEntry entry, string prefix, DateTimeOffset now)
    {
        var segments = prefix.Length == 0 ? [] : prefix.Split('/');
        var made = segments.Any(BuildOutput.Contains)
            || (!entry.IsDirectory && MadeByMachines.Contains(entry.Extension)) ? 1 : 0;

        var age = entry.Modified is not { } modified ? 2
            : now - modified < TimeSpan.FromDays(30) ? 0
            : now - modified < TimeSpan.FromDays(365) ? 1
            : 2;

        return $"{Math.Clamp(likelihood, 0, 9)}{made}{Math.Clamp(nameRank, 0, 9)}{age}{Math.Min(segments.Length, 99):D2}";
    }
}
