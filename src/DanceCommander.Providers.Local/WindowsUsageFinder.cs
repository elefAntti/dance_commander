using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DanceCommander.Core.Operations;

namespace DanceCommander.Providers.Local;

/// <summary>
/// Who has a path open (DESIGN.md §1.3: an error names its cause), asked in two ways.
/// </summary>
/// <remarks>
/// Restart Manager first: the API installers use for "close Word to continue". It is fast and
/// knows applications by name, but it only sees open files, and the commonest holder of a folder
/// has none: a shell whose working directory is inside it. Explorer says only "open in another
/// program" for exactly that reason. So when Restart Manager finds nobody, the handles of the
/// user's own processes are read and matched by path. That took 2.7 s in the case that prompted
/// it (a PowerShell tab in Windows Terminal), which is why this only ever runs after a failure.
/// </remarks>
public sealed class WindowsUsageFinder : IUsageFinder
{
    /// <summary>Files asked about per folder: enough for a project, not a disk.</summary>
    private const int MaxFiles = 2000;

    public Task<IReadOnlyList<Holder>> FindAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        Task.Run(() => Find(paths, ct), ct);

    private static IReadOnlyList<Holder> Find(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var full = paths.Select(p => Path.GetFullPath(p).TrimEnd('\\')).ToList();

        var pids = RestartManager.Holders(FilesUnder(full));
        if (pids.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            pids = Handles.Holders(full, ct);
        }

        return pids.Distinct().Select(pid => new Holder(pid, Describe(pid))).ToList();
    }

    private static List<string> FilesUnder(IReadOnlyList<string> paths)
    {
        var files = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var path in paths)
        {
            if (File.Exists(path))
                files.Add(path);
            else if (Directory.Exists(path))
                files.AddRange(Directory.EnumerateFiles(path, "*", options).Take(MaxFiles - files.Count));
            if (files.Count >= MaxFiles)
                break;
        }
        return files;
    }

    /// <summary>
    /// "Windows PowerShell in Windows Terminal Host (process 33536)". A shell has no window of its
    /// own: what the user knows it by is the terminal or editor it runs in, so that is named too.
    /// </summary>
    internal static string Describe(int pid)
    {
        if (pid == Environment.ProcessId)
            return "Dance Commander itself";

        var self = Name(pid) ?? "a program";
        if (!HasWindow(pid) && WindowedAncestor(pid) is { } host && Name(host) is { } hostName && hostName != self)
            return $"{self} in {hostName} (process {pid})";
        return $"{self} (process {pid})";
    }

    private static string? Name(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            try
            {
                if (process.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } description)
                    return description;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Elevated or protected: its name is still known, only not its description.
            }
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null; // gone since
        }
    }

    private static bool HasWindow(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainWindowHandle != IntPtr.Zero;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static int? WindowedAncestor(int pid)
    {
        for (var (child, depth) = (pid, 0); depth < 6; depth++)
        {
            if (Parent(child) is not { } parent)
                return null;
            if (HasWindow(parent))
                return parent;
            child = parent;
        }
        return null;
    }

    /// <summary>The parent, if it is still the one that started this process (ids are reused).</summary>
    private static int? Parent(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
                return null;

            var parent = (int)info.InheritedFromUniqueProcessId;
            using var child = Process.GetProcessById(pid);
            using var candidate = Process.GetProcessById(parent);
            return candidate.StartTime <= child.StartTime ? parent : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static class RestartManager
    {
        public static List<int> Holders(IReadOnlyList<string> files)
        {
            var pids = new List<int>();
            if (files.Count == 0 || RmStartSession(out var session, 0, new StringBuilder(64)) != 0)
                return pids;
            try
            {
                if (RmRegisterResources(session, (uint)files.Count, files.ToArray(), 0, IntPtr.Zero, 0, null) != 0)
                    return pids;

                uint count = 0, reasons = 0;
                var list = Array.Empty<RmProcessInfo>();
                int result;
                do
                {
                    result = RmGetList(session, out var needed, ref count, list, ref reasons);
                    if (result == ErrorMoreData)
                    {
                        list = new RmProcessInfo[needed];
                        count = needed;
                    }
                }
                while (result == ErrorMoreData);

                if (result == 0)
                    pids.AddRange(list.Take((int)count).Select(p => p.Process.ProcessId));
            }
            finally
            {
                RmEndSession(session);
            }
            return pids;
        }

        private const int ErrorMoreData = 234;

        [StructLayout(LayoutKind.Sequential)]
        private struct RmUniqueProcess
        {
            public int ProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RmProcessInfo
        {
            public RmUniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint SessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint session, int flags, StringBuilder key);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint session);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(
            uint session, uint files, string[] names, uint applications, IntPtr processes, uint services, string[]? serviceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(
            uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[] list, ref uint rebootReasons);
    }

    /// <summary>
    /// Every handle in the system, kept to the file handles of processes we may open, and asked
    /// for their paths. Only files on disk are asked: GetFinalPathNameByHandle on some pipes can
    /// wait forever, and GetFileType answers without waiting.
    /// </summary>
    private static class Handles
    {
        public static List<int> Holders(IReadOnlyList<string> paths, CancellationToken ct)
        {
            var pids = new List<int>();

            // Matched in the kernel's spelling (\Device\HarddiskVolume3\...): asking for drive
            // letters makes every one of thousands of lookups consult the mount manager. A path
            // with no such spelling (a network drive) falls back to drive letters for all of them.
            var nt = paths.Select(ToNtPath).ToList();
            var flags = nt.All(p => p is not null) ? VolumeNameNt : VolumeNameDos;
            var wanted = flags == VolumeNameNt ? nt.Select(p => p!).ToList() : paths.ToList();

            // A file handle of our own, open before the snapshot so that it is in it: its type
            // index is what keeps the scan to files instead of duplicating every handle there is.
            using var probe = Environment.ProcessPath is { } exe ? File.OpenHandle(exe) : null;
            var buffer = Snapshot();
            if (buffer == IntPtr.Zero)
                return pids;

            var processes = new Dictionary<int, IntPtr>();
            try
            {
                var count = Marshal.ReadIntPtr(buffer).ToInt64();
                var size = Marshal.SizeOf<HandleEntry>();
                var first = buffer + IntPtr.Size * 2;
                var fileType = probe is null ? null : FileTypeIndex(probe.DangerousGetHandle(), first, count, size);
                var self = GetCurrentProcess();
                var found = new HashSet<int>();
                var path = new StringBuilder(1024);

                for (long i = 0; i < count; i++)
                {
                    if ((i & 0xFFF) == 0)
                        ct.ThrowIfCancellationRequested();

                    var entry = Marshal.PtrToStructure<HandleEntry>(first + (int)(i * size));
                    var pid = (int)entry.ProcessId;
                    if ((fileType is { } type && entry.TypeIndex != type) || found.Contains(pid))
                        continue;

                    if (!processes.TryGetValue(pid, out var process))
                        processes[pid] = process = OpenProcess(ProcessDuplicateHandle, false, pid);
                    if (process == IntPtr.Zero ||
                        !DuplicateHandle(process, entry.Handle, self, out var copy, 0, false, DuplicateSameAccess))
                        continue;

                    try
                    {
                        path.Clear();
                        if (GetFileType(copy) != FileTypeDisk || GetFinalPathNameByHandle(copy, path, 1024, flags) == 0)
                            continue;
                        if (IsUnder(path.ToString(), wanted))
                        {
                            pids.Add(pid);
                            found.Add(pid);
                        }
                    }
                    finally
                    {
                        CloseHandle(copy);
                    }
                }
            }
            finally
            {
                foreach (var process in processes.Values)
                    if (process != IntPtr.Zero)
                        CloseHandle(process);
                Marshal.FreeHGlobal(buffer);
            }
            return pids;
        }

        private static bool IsUnder(string handlePath, IReadOnlyList<string> paths)
        {
            const string LongPrefix = @"\\?\";
            var open = handlePath.StartsWith(LongPrefix, StringComparison.Ordinal) ? handlePath[LongPrefix.Length..] : handlePath;
            return paths.Any(p =>
                open.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                open.StartsWith(p + '\\', StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>"C:\x" as "\Device\HarddiskVolume3\x"; null for anything not on a drive letter.</summary>
        private static string? ToNtPath(string path)
        {
            if (path.Length < 2 || path[1] != ':')
                return null;
            var device = new StringBuilder(1024);
            if (QueryDosDevice(path[..2], device, device.Capacity) == 0)
                return null;
            var target = device.ToString();
            // A subst drive maps to \??\C:\...: another drive letter, not a device.
            return target.StartsWith(@"\??\", StringComparison.Ordinal) ? null : target + path[2..];
        }

        /// <summary>The type index of file handles, read off one of our own; null if not found.</summary>
        private static ushort? FileTypeIndex(IntPtr value, IntPtr first, long count, int size)
        {
            var self = Environment.ProcessId;
            for (long i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<HandleEntry>(first + (int)(i * size));
                if ((int)entry.ProcessId == self && entry.Handle == value)
                    return entry.TypeIndex;
            }
            return null;
        }

        private static IntPtr Snapshot()
        {
            var size = 1 << 22;
            while (true)
            {
                var buffer = Marshal.AllocHGlobal(size);
                var status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, out var needed);
                if (status == 0)
                    return buffer;
                Marshal.FreeHGlobal(buffer);
                if (status != StatusInfoLengthMismatch || size > 1 << 28)
                    return IntPtr.Zero;
                size = Math.Max(needed + (1 << 20), size * 2);
            }
        }

        private const int SystemExtendedHandleInformation = 64;
        private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
        private const uint FileTypeDisk = 1;
        private const uint VolumeNameDos = 0x0;
        private const uint VolumeNameNt = 0x2;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint QueryDosDevice(string device, StringBuilder target, int length);
        private const uint DuplicateSameAccess = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct HandleEntry
        {
            public IntPtr Object;
            public IntPtr ProcessId;
            public IntPtr Handle;
            public uint GrantedAccess;
            public ushort CreatorBackTraceIndex;
            public ushort TypeIndex;
            public uint Attributes;
            public uint Reserved;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int needed);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(
            IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, bool inherit, uint options);

        [DllImport("kernel32.dll")]
        private static extern uint GetFileType(IntPtr file);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(IntPtr file, StringBuilder path, uint length, uint flags);
    }

    private const uint ProcessDuplicateHandle = 0x0040;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr process, int infoClass, ref ProcessBasicInformation info, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
