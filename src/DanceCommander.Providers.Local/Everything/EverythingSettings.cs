using System.Runtime.InteropServices;
using System.Text;

namespace DanceCommander.Providers.Local.Everything;

/// <summary>
/// Which folders Everything has all of, read from its settings file: a drive it indexes, with
/// nothing excluded anywhere.
/// </summary>
/// <remarks>
/// <para>An answer from an index that leaves something out looks like a complete one, so anything
/// short of "this whole drive, everything on it" is no: the walk answers instead. A drive that
/// isn't listed, an exclude list with anything in it, hidden or system files left out, a volume
/// indexed only in part. A setting this doesn't know is read as nothing excluded only for the keys
/// named here; Everything's default for each is to include.</para>
///
/// <para>A drive is known by its volume, not its letter: the settings name both, and a letter
/// Everything indexed once may now be another disk it has never seen (review, 2026-11-10). And
/// only while it watches the volume for changes: without that, its answer is as old as its last
/// rescan.</para>
///
/// <para>The file is <c>Everything.ini</c> beside the program, or in <c>%APPDATA%\Everything</c>
/// when that one says <c>app_data=1</c>, as an installed copy's does. Found again when Everything
/// is restarted, read again when it changes: Everything writes it when its options are changed.</para>
/// </remarks>
internal sealed class EverythingSettings
{
    private static readonly string[] MustBeEmpty =
    [
        "exclude_folders", "exclude_files", "include_only_files", "include_only_folders",
        "ntfs_volume_include_onlys", "refs_volume_include_onlys",
    ];

    private static readonly string[] MustBeOff = ["exclude_hidden_files_and_folders", "exclude_system_files_and_folders"];

    private readonly object _gate = new();
    private int _process;
    private string? _path;
    private DateTime _written;
    private Dictionary<string, string> _volumes = [];

    /// <summary>Whether Everything has every name under <paramref name="folder"/>, a Win32 path.</summary>
    public bool Covers(int processId, string folder)
    {
        if (folder.Length < 3 || folder[1] != ':' || folder[2] != '\\' || !char.IsAsciiLetter(folder[0]))
            return false; // a share, a \\?\ path: not a drive Everything indexes

        var drive = folder[..2].ToUpperInvariant();
        return Volumes(processId).TryGetValue(drive, out var volume)
            && VolumeOf(drive) is { } now && string.Equals(volume, now, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The drives indexed whole, each with its volume's GUID; none when anything is in doubt.</summary>
    private Dictionary<string, string> Volumes(int processId)
    {
        lock (_gate)
        {
            try
            {
                if (processId != _process || _path is null)
                {
                    _path = Locate(processId);
                    _process = processId;
                    _written = default;
                }

                if (_path is null)
                    return [];

                var written = File.GetLastWriteTimeUtc(_path);
                if (written != _written)
                {
                    _volumes = Read(File.ReadAllLines(_path));
                    _written = written;
                }

                return _volumes;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Being written as we read it, or gone with the program: covering nothing is safe,
                // and the next search looks again.
                _path = null;
                return [];
            }
        }
    }

    /// <summary>The GUID of the volume a drive letter stands for now, as in <c>\\?\Volume{…}\</c>.</summary>
    private static string? VolumeOf(string drive)
    {
        var name = new StringBuilder(64);
        return GetVolumeNameForVolumeMountPoint(drive + "\\", name, (uint)name.Capacity) ? Guid(name.ToString()) : null;
    }

    /// <summary>The <c>{…}</c> of a volume name, however its backslashes are written.</summary>
    private static string? Guid(string volume)
    {
        int open = volume.IndexOf('{'), close = volume.IndexOf('}');
        return open >= 0 && close > open ? volume[open..(close + 1)] : null;
    }

    /// <summary>Where the running Everything keeps its settings, found from its program's folder.</summary>
    private static string? Locate(int processId)
    {
        if (ProgramOf(processId) is not { } program || Path.GetDirectoryName(program) is not { } folder)
            return null;

        var beside = Path.Combine(folder, "Everything.ini");
        if (!File.Exists(beside))
            return null;

        if (Read(File.ReadAllLines(beside), "app_data") != "1")
            return beside;

        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Everything", "Everything.ini");
        return File.Exists(roaming) ? roaming : null;
    }

    /// <summary>
    /// The drives, as <c>C:</c>, that are indexed whole and watched, with the GUID of the volume
    /// each was; none when anything is excluded.
    /// </summary>
    internal static Dictionary<string, string> Read(IEnumerable<string> lines)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var equals = line.IndexOf('=');
            if (equals > 0 && !line.StartsWith(';') && !line.StartsWith('['))
                settings[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        string Value(string key) => settings.GetValueOrDefault(key, "");

        if (MustBeEmpty.Any(key => List(Value(key)).Any(item => item.Length > 0))
            || MustBeOff.Any(key => Value(key) is not ("" or "0")))
            return [];

        var drives = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in new[] { "ntfs", "refs" })
        {
            var paths = List(Value($"{kind}_volume_paths"));
            var guids = List(Value($"{kind}_volume_guids"));
            var includes = List(Value($"{kind}_volume_includes"));
            var monitors = List(Value($"{kind}_volume_monitors"));
            for (var i = 0; i < paths.Count; i++)
            {
                var drive = paths[i];
                if (drive.Length == 2 && char.IsAsciiLetter(drive[0]) && drive[1] == ':'
                    && i < includes.Count && includes[i] == "1"
                    && i < monitors.Count && monitors[i] == "1"
                    && i < guids.Count && Guid(guids[i]) is { } guid)
                    drives[drive.ToUpperInvariant()] = guid;
            }
        }

        return drives;
    }

    /// <summary>The program a process runs; any process, 32-bit or elevated, will say that much.</summary>
    private static string? ProgramOf(int processId)
    {
        var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)processId);
        if (process == IntPtr.Zero)
            return null;
        try
        {
            var name = new StringBuilder(1024);
            var length = (uint)name.Capacity;
            return QueryFullProcessImageName(process, 0, name, ref length) ? name.ToString(0, (int)length) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    internal static string? Read(IEnumerable<string> lines, string key) =>
        lines.Select(line => line.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair[1].Trim())
            .FirstOrDefault();

    /// <summary>
    /// A list as the file writes it: <c>"C:","D:"</c> for strings, <c>1,0</c> for numbers. Names
    /// with a comma or a quote in them are not a drive letter, which is all this has to read.
    /// </summary>
    private static List<string> List(string value) =>
        value.Length == 0 ? [] : value.Split(',').Select(item => item.Trim().Trim('"')).ToList();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint length);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, uint length);
}
