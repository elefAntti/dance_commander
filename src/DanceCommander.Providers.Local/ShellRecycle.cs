using System.Runtime.InteropServices;

namespace DanceCommander.Providers.Local;

/// <summary>
/// The shell's IFileOperation, for the one thing used of it: sending items to the Recycle Bin.
/// </summary>
/// <remarks>
/// SHFileOperation can only recycle with FOF_ALLOWUNDO, and that flag also puts the delete on the
/// shell's undo stack, which every program in the session shares: Ctrl+Z in Explorer put back what
/// was deleted here (2026-10-17). FOFX_RECYCLEONDELETE (Windows 8) recycles without it, so a delete
/// made here is undone here and nowhere else.
/// </remarks>
internal static class ShellRecycle
{
    /// <summary>
    /// What came of it: the first item that failed and why, or none; and the first one the shell
    /// said it removed with nothing put in the bin, which is deleted outright whatever it was asked.
    /// </summary>
    public readonly record struct Outcome(int Result, string? Failed, bool Refused, bool Aborted, string? NotRecycled = null);

    public static Task<Outcome> RecycleAsync(IReadOnlyList<string> paths) => RecycleAsync(paths, askBeforeDestroying: true);

    /// <summary>
    /// IFileOperation only works in a single-threaded apartment, and neither the thread pool nor a
    /// test runner is one, so it gets a thread of its own. <paramref name="askBeforeDestroying"/>
    /// false is for the torture run alone, on its own test volume: without Windows' box, it finds
    /// out whether the shell would destroy an item.
    /// </summary>
    internal static Task<Outcome> RecycleAsync(IReadOnlyList<string> paths, bool askBeforeDestroying)
    {
        var done = new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(Recycle(paths, askBeforeDestroying));
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Recycle Bin",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static Outcome Recycle(IReadOnlyList<string> paths, bool askBeforeDestroying)
    {
        IFileOperation? operation = null;
        var items = new List<IShellItem>();
        try
        {
            var sink = new Sink();
            uint cookie;
            try
            {
                operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidFileOperation, throwOnError: true)!)!;
                operation.SetOperationFlags(
                    FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOFX_RECYCLEONDELETE | (askBeforeDestroying ? FOF_WANTNUKEWARNING : 0));
                cookie = operation.Advise(sink);
                foreach (var path in paths)
                {
                    var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, IidShellItem, out var item);
                    if (hr < 0)
                        return new Outcome(hr, path, Refused: false, Aborted: false);
                    items.Add(item);
                    // The item the shell made must be the file asked for: its parser takes a
                    // trailing dot or space off a name, and notes. became the folder notes.
                    // Compared as files, not as text: the shell names a long path by its short
                    // names (SEGMEN~1\…), which refused every long recycle (the torture run,
                    // 2026-10-28). No answer is no news, the names that need this being refused before.
                    if (Sink.PathOf(item) is { } parsed && !string.Equals(Plain(parsed), Plain(path), StringComparison.OrdinalIgnoreCase)
                        && FileIdentity.Of(parsed) is { } named && FileIdentity.Of(path) is { } asked && named != asked)
                        return new Outcome(ErrorInvalidName, path, Refused: false, Aborted: false);
                    operation.DeleteItem(item, null);
                }
            }
            catch (COMException ex)
            {
                // Nothing is deleted yet: said as the shell's error, like any other.
                return new Outcome(ex.HResult, null, Refused: false, Aborted: false);
            }

            var result = operation.PerformOperations();
            var aborted = operation.GetAnyOperationsAborted();
            try
            {
                operation.Unadvise(cookie);
            }
            catch (COMException)
            {
                // Past the delete, a failure to stop listening must not read as the delete's.
            }

            if (sink.Refused is { } refused)
                return new Outcome(result, refused, Refused: true, aborted);
            if (sink.FailedResult < 0)
                return new Outcome(sink.FailedResult, sink.Failed, Refused: false, aborted, sink.NotRecycled);
            return new Outcome(result, null, Refused: false, aborted, sink.NotRecycled);
        }
        finally
        {
            foreach (var item in items)
                Marshal.FinalReleaseComObject(item);
            if (operation is not null)
                Marshal.FinalReleaseComObject(operation);
        }
    }

    /// <summary>
    /// Hears each item before and after. Before: the shell says whether it is about to recycle;
    /// one it would delete outright is refused here, so a flag that doesn't do what it says costs
    /// a delete, not the file. After: the first failure, with its path, for the words, and any
    /// item that went without reaching the bin.
    /// </summary>
    [ComVisible(true)]
    private sealed class Sink : IFileOperationProgressSink
    {
        public string? Refused { get; private set; }

        public string? Failed { get; private set; }

        public int FailedResult { get; private set; }

        public string? NotRecycled { get; private set; }

        public int PreDeleteItem(uint flags, IShellItem item)
        {
            if ((flags & TSF_DELETE_RECYCLE_IF_POSSIBLE) != 0)
                return 0;
            Refused ??= PathOf(item);
            return E_ABORT;
        }

        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? recycled)
        {
            if (result < 0 && FailedResult >= 0)
            {
                FailedResult = result;
                Failed = PathOf(item);
            }
            // Gone with nothing in the bin to show for it. A skip said as a success (the copy
            // engine has a few) lands here too, and the caller's look at what is still in place
            // sorts the two apart.
            if (result >= 0 && recycled is null)
                NotRecycled ??= PathOf(item) ?? "";
            return 0;
        }

        internal static string? PathOf(IShellItem item)
        {
            if (item.GetDisplayName(SIGDN_FILESYSPATH, out var name) < 0)
                return null;
            try
            {
                return Marshal.PtrToStringUni(name);
            }
            finally
            {
                Marshal.FreeCoTaskMem(name);
            }
        }

        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IShellItem item, IntPtr newName) => 0;
        public int PostRenameItem(uint flags, IShellItem item, IntPtr newName, int result, IShellItem? created) => 0;
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName) => 0;
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName, int result, IShellItem? created) => 0;
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName) => 0;
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName, int result, IShellItem? created) => 0;
        public int PreNewItem(uint flags, IShellItem destination, IntPtr newName) => 0;
        public int PostNewItem(uint flags, IShellItem destination, IntPtr newName, IntPtr template, uint attributes, int result, IShellItem? created) => 0;
        public int UpdateProgress(uint total, uint done) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    private const uint FOF_SILENT = 0x0004;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_NOERRORUI = 0x0400;

    /// <summary>Still warns when something is too large for the bin and would be destroyed instead.</summary>
    private const uint FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>Recycle, without FOF_ALLOWUNDO or FOFX_ADDUNDORECORD: no entry on the shell's undo stack.</summary>
    private const uint FOFX_RECYCLEONDELETE = 0x00080000;

    private const uint TSF_DELETE_RECYCLE_IF_POSSIBLE = 0x80;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int E_ABORT = unchecked((int)0x80004004);
    private const int ErrorInvalidName = unchecked((int)0x8007007B);

    private static string Plain(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
        : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
        : path;

    private static readonly Guid ClsidFileOperation = new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static readonly Guid IidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr bindContext, in Guid riid, out IShellItem item);

    // In vtable order. Only the slots used are typed; the others hold their place.
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        uint Advise(IFileOperationProgressSink sink);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage();
        void SetProgressDialog();
        void SetProperties();
        void SetOwnerWindow();
        void ApplyPropertiesToItem();
        void ApplyPropertiesToItems();
        void RenameItem();
        void RenameItems();
        void MoveItem();
        void MoveItems();
        void CopyItem();
        void CopyItems();
        void DeleteItem(IShellItem item, IFileOperationProgressSink? sink);
        void DeleteItems();
        void NewItem();
        [PreserveSig] int PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetAnyOperationsAborted();
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        void BindToHandler();
        void GetParent();
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
        void GetAttributes();
        void Compare();
    }

    [ComImport, Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, IntPtr newName);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, IntPtr newName, int result, IShellItem? created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName, int result, IShellItem? created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName, int result, IShellItem? created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? recycled);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, IntPtr newName);
        [PreserveSig] int PostNewItem(uint flags, IShellItem destination, IntPtr newName, IntPtr template, uint attributes, int result, IShellItem? created);
        [PreserveSig] int UpdateProgress(uint total, uint done);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
}
