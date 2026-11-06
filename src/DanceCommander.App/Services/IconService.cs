using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DanceCommander.Core.Fs;

namespace DanceCommander.App.Services;

public interface IIconService
{
    ImageSource? GetIcon(FsEntry entry);

    /// <summary>
    /// An icon the shell refused earlier has now been given. Anything that asked once and kept the
    /// answer (the address bar) asks again. Raised on whichever thread got the icon.
    /// </summary>
    event EventHandler? Recovered;

    /// <summary>The icon if it is already known, without asking the shell; false when it would have to.</summary>
    bool TryGetCachedIcon(FsEntry entry, out ImageSource? icon);

    /// <summary>
    /// Which icon an entry gets: entries with equal keys share one. Rows waiting for icons are
    /// grouped by this, so one shell call serves each group.
    /// </summary>
    string IconKey(FsEntry entry);

    /// <summary>
    /// The icon is read out of the file itself (a program, an icon file), which means opening it:
    /// a lookup that can take as long as a virus scan of the file, not a cache's milliseconds.
    /// </summary>
    bool ReadsOwnIcon(FsEntry entry);

    /// <summary>The icon of the entry's kind, from its extension alone: what stands in while its own is read.</summary>
    ImageSource? GetTypeIcon(FsEntry entry);

    /// <summary>The kind's icon if it is already known, without asking the shell.</summary>
    bool TryGetCachedTypeIcon(FsEntry entry, out ImageSource? icon);

    /// <summary>An icon for a place rather than a file: a folder, or a network location.</summary>
    ImageSource? GetLocationIcon(FsUri uri);
}

/// <summary>
/// Shell icons, cached by <em>extension</em> rather than by path (§5.4). Using
/// SHGFI_USEFILEATTRIBUTES means the shell answers from the file association alone and never
/// touches the disk, so this is also correct for remote and in-archive entries where the path
/// does not exist locally. Results are frozen, so rows can be built on a worker thread.
/// </summary>
public sealed class IconService : IIconService
{
    private const string DirectoryKey = "\0dir";

    private const string RemoteKey = " remote";

    // Only icons the shell actually gave. A failure used to be kept too, so one refused lookup left
    // that kind blank (folders, in practice) in every pane until the app was restarted.
    private readonly ConcurrentDictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, bool> _reportedFailures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One shell icon lookup at a time. The shell refuses lookups made from several threads before
    /// its icon machinery has settled, which is what both panes filling their icons at once at launch
    /// did: folders, then .zip, .gz and .py, blank for the session (2026-10-01). Measured in fresh
    /// processes, 16 threads at once: 4 to 43 of 640 lookups failed, whether or not the threads had
    /// COM set up; one at a time, none did, in every run. Each kind is asked once and then cached,
    /// so the wait this costs is a few milliseconds at most.
    /// </summary>
    private static readonly object ShellGate = new();

    public ImageSource? GetIcon(FsEntry entry) => Get(KeyOf(entry), LoadFor);

    public bool TryGetCachedIcon(FsEntry entry, out ImageSource? icon)
    {
        var found = _cache.TryGetValue(KeyOf(entry), out var cached);
        icon = cached;
        return found;
    }

    private ImageSource? Get(string key, Func<string, ImageSource?> load)
    {
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        ImageSource? icon;
        lock (ShellGate)
        {
            // Another thread may have fetched it while this one waited.
            if (_cache.TryGetValue(key, out cached))
                return cached;
            icon = load(key);
        }

        if (icon is not null)
        {
            var kept = _cache.GetOrAdd(key, icon);
            if (_reportedFailures.ContainsKey(key))
                Recovered?.Invoke(this, EventArgs.Empty);
            return kept;
        }

        // Once per kind: enough to tell, next time, that the shell had no icon to give.
        if (_reportedFailures.TryAdd(key, true))
            Serilog.Log.Warning(
                "The shell gave no icon for {Key} ({Apartment} thread {Thread}, {Elapsed} ms after start); asking again later",
                key.Trim(), Thread.CurrentThread.GetApartmentState(), Environment.CurrentManagedThreadId, StartupClock.Now);
        return null;
    }

    public event EventHandler? Recovered;

    /// <summary>
    /// A drive or a special folder is its own kind — its real icon, the one the address bar and the
    /// bookmarks show — wherever it is listed: on the computer page, and as Desktop or Documents in
    /// the home folder. So is a program or an icon file on the local disk, which carries its own
    /// picture. Everything else is keyed by extension.
    /// </summary>
    public string IconKey(FsEntry entry) => KeyOf(entry);

    private static string KeyOf(FsEntry entry)
    {
        if (entry.IsDirectory)
        {
            return entry.Uri.IsLocal && SpecialFolders.Contains(entry.Uri.ToLocalPath().TrimEnd('\\') + "\\")
                ? SpecialKey + entry.Uri.ToLocalPath()
                : DirectoryKey;
        }

        return entry.Uri.IsLocal && OwnIconExtensions.Contains(entry.Extension)
            ? OwnKey + entry.Uri.ToLocalPath()
            : TypeKeyOf(entry);
    }

    private static string TypeKeyOf(FsEntry entry) => entry.IsDirectory ? DirectoryKey : "." + entry.Extension;

    public bool ReadsOwnIcon(FsEntry entry) => KeyOf(entry).StartsWith(OwnKey, StringComparison.Ordinal);

    public ImageSource? GetTypeIcon(FsEntry entry) => Get(TypeKeyOf(entry), Load);

    public bool TryGetCachedTypeIcon(FsEntry entry, out ImageSource? icon)
    {
        var found = _cache.TryGetValue(TypeKeyOf(entry), out var cached);
        icon = cached;
        return found;
    }

    private static ImageSource? LoadFor(string key)
    {
        if (key.StartsWith(SpecialKey, StringComparison.Ordinal))
            return LoadReal(key[SpecialKey.Length..]) ?? Load(DirectoryKey);

        // A program whose icon can't be read (no icon resource, a share that went away) still looks
        // like a program rather than a blank.
        if (key.StartsWith(OwnKey, StringComparison.Ordinal))
        {
            var path = key[OwnKey.Length..];
            return LoadReal(path) ?? Load(System.IO.Path.GetExtension(path));
        }

        return Load(key);
    }

    private const string OwnKey = " own:";

    /// <summary>
    /// Files Explorer draws with a picture read out of the file itself. A shortcut (.lnk) would
    /// belong here too, but its icon is the target's, and a target on a share that is not there
    /// can keep the shell busy for seconds, holding every other lookup behind <see cref="ShellGate"/>.
    /// </summary>
    private static readonly HashSet<string> OwnIconExtensions = new(StringComparer.OrdinalIgnoreCase) { "exe", "ico" };

    public ImageSource? GetLocationIcon(FsUri uri)
    {
        if (uri.IsLocal && SpecialFolders.Contains(uri.ToLocalPath().TrimEnd('\\') + "\\"))
            return Get(SpecialKey + uri.ToLocalPath(), LoadFor);

        return uri.IsLocal || uri.IsArchiveEntry
            ? Get(DirectoryKey, Load)
            : Get(RemoteKey, _ => LoadStock(SIID_DRIVENET));
    }

    private const string SpecialKey = " special:";

    /// <summary>
    /// Downloads has no <see cref="Environment.SpecialFolder"/>; the known-folder API has it. Declared
    /// above <see cref="SpecialFolders"/> on purpose: static initialisers run in textual order, and
    /// below it this was still an empty Guid when the set was built, so Downloads quietly kept the
    /// generic icon.
    /// </summary>
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>
    /// The folders Windows draws with an icon of their own — home, Desktop, Documents, Downloads and
    /// the rest — and the drive roots. Found once; kept with a trailing separator so "C:\" and
    /// "C:\Users\me" compare alike.
    /// </summary>
    /// <remarks>
    /// Everything else gets the one generic folder icon from the file association, which never
    /// touches the disk. These few are asked for their real icon, which means the shell reading
    /// each one's desktop.ini: worth it for a handful of bookmarks that are otherwise identical
    /// yellow folders, and paid once per folder per session by the cache, not per popup.
    /// </remarks>
    private static readonly HashSet<string> SpecialFolders = FindSpecialFolders();

    private static HashSet<string> FindSpecialFolders()
    {
        var paths = new[]
            {
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.Desktop,
                Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.MyMusic,
                Environment.SpecialFolder.MyPictures,
                Environment.SpecialFolder.MyVideos,
            }
            .Select(Environment.GetFolderPath)
            .Append(KnownFolder(DownloadsFolderId))
            .Concat(Environment.GetLogicalDrives());

        return paths
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!.TrimEnd('\\') + "\\")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var path) != 0)
            return null;

        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    /// <summary>The icon of an actual folder or file on disk, as Explorer would draw it; null if the shell has none.</summary>
    private static ImageSource? LoadReal(string path)
    {
        var info = default(SHFILEINFO);
        var result = SHGetFileInfo(
            path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    /// <summary>A shell stock icon, used for locations that have no file behind them.</summary>
    private static ImageSource? LoadStock(uint stockIconId)
    {
        var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
        if (SHGetStockIconInfo(stockIconId, SHGSI_ICON | SHGSI_SMALLICON, ref info) != 0 ||
            info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private static ImageSource? Load(string key)
    {
        var isDirectory = key == DirectoryKey;
        var pseudoPath = isDirectory ? "folder" : "file" + key;
        var attributes = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

        var info = default(SHFILEINFO);
        var result = SHGetFileInfo(
            pseudoPath, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

        // A folder without its icon is the one row kind nobody can do without, so a shell that
        // will not say (seen while Explorer was crashing) gets the stock folder, asked another way.
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return isDirectory ? LoadStock(SIID_FOLDER) : null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private const uint SIID_FOLDER = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint SHGSI_ICON = 0x000000100;
    private const uint SHGSI_SMALLICON = 0x000000001;
    private const uint SIID_DRIVENET = 9;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHSTOCKICONINFO
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("shell32.dll")]
    private static extern int SHGetStockIconInfo(uint siid, uint uFlags, ref SHSTOCKICONINFO psii);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
