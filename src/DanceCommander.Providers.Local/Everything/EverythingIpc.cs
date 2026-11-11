using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using DanceCommander.Core.Listing;

namespace DanceCommander.Providers.Local.Everything;

/// <summary>What Everything answered: how many it has in all, and the ones it sent.</summary>
internal sealed record EverythingReply(uint Total, IReadOnlyList<IndexedPath> Paths);

/// <summary>
/// Everything 1.4's own IPC, spoken directly rather than through its SDK's DLL: a query sent with
/// <c>WM_COPYDATA</c> to its window, the answer sent back the same way to a window of ours.
/// </summary>
/// <remarks>
/// <para>The answer is a message sent to our window, so it arrives only while the thread that made
/// the window is in a message call. That thread is this class's own, kept for the life of the
/// process, and it always waits by pumping messages, never by blocking: a late answer to a query
/// already given up on would otherwise hold Everything up until we pumped again.</para>
///
/// <para>One query at a time, each with a number of its own as the reply's message id, so an answer
/// arriving after its query timed out is not taken for the next one's.</para>
/// </remarks>
internal sealed class EverythingIpc
{
    private const string WindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";
    private const uint WM_COPYDATA = 0x004A;
    private const uint WM_USER = 0x0400;
    private const uint IsDbLoaded = 401;
    private const uint QueryCopyData2W = 18;
    private const uint RequestFullPath = 0x4;
    private const uint ItemFolder = 0x1;
    private const uint ItemDrive = 0x2;
    private const uint SmtoAbortIfHung = 0x2;

    /// <summary>Asking whether it is there, and handing it a query: it either takes them at once or is hung.</summary>
    private const uint SendTimeoutMs = 1_000;

    /// <summary>
    /// Long enough for a search of a whole disk, which is tens of milliseconds; anything longer is
    /// an Everything that is busy, and the walk does it instead.
    /// </summary>
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(5);

    public static EverythingIpc Shared { get; } = new();

    /// <summary>UTF-16 that refuses what it can't carry, rather than putting U+FFFD in its place.</summary>
    private static readonly UnicodeEncoding Strict = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);

    private readonly ConcurrentQueue<Query> _queries = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly WndProc _proc;
    private IntPtr _window;
    private uint _numbering = 0x44430000;
    private uint _awaited;
    private byte[]? _answer;

    private sealed record Query(string Search, uint Max, TaskCompletionSource<EverythingReply?> Done, CancellationToken Ct);

    private EverythingIpc()
    {
        _proc = Receive;
        new Thread(Run) { IsBackground = true, Name = "Everything IPC" }.Start();
    }

    /// <summary>Whether Everything is running here, as the process that owns its window.</summary>
    public static int? ProcessId()
    {
        var window = FindWindow(WindowClass, null);
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var id) == 0)
            return null;
        return (int)id;
    }

    /// <summary>
    /// Everything's answer to <paramref name="search"/> in its own syntax, at most
    /// <paramref name="max"/> paths of it; null when it is not running, not loaded, refuses or is slow.
    /// </summary>
    public Task<EverythingReply?> QueryAsync(string search, uint max, CancellationToken ct)
    {
        var done = new TaskCompletionSource<EverythingReply?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queries.Enqueue(new Query(search, max, done, ct));
        _wake.Set();
        return done.Task;
    }

    private void Run()
    {
        var cls = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "DanceCommander.Everything",
        };
        RegisterClassEx(ref cls);
        _window = CreateWindowEx(0, cls.lpszClassName, "", 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */,
            IntPtr.Zero, cls.hInstance, IntPtr.Zero);

        // An Everything at a lower integrity level than us may still answer: run elevated, we would
        // otherwise not be sent its reply.
        if (_window != IntPtr.Zero)
            ChangeWindowMessageFilterEx(_window, WM_COPYDATA, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);

        var handles = new[] { _wake.SafeWaitHandle.DangerousGetHandle() };
        while (true)
        {
            MsgWaitForMultipleObjectsEx(1, handles, uint.MaxValue, 0x04FF /* QS_ALLINPUT */, 0x4 /* MWMO_INPUTAVAILABLE */);
            try
            {
                Pump();
            }
            catch (Exception)
            {
                // Whatever a stray message did, the thread every search waits on carries on.
            }
            while (_queries.TryDequeue(out var query))
            {
                if (query.Ct.IsCancellationRequested)
                {
                    query.Done.TrySetCanceled(query.Ct);
                    continue;
                }

                try
                {
                    query.Done.TrySetResult(_window == IntPtr.Zero ? null : Ask(query));
                }
                catch (OperationCanceledException)
                {
                    query.Done.TrySetCanceled(query.Ct);
                }
                catch (Exception e)
                {
                    query.Done.TrySetException(e);
                }
            }
        }
    }

    private EverythingReply? Ask(Query query)
    {
        var target = FindWindow(WindowClass, null);
        if (target == IntPtr.Zero)
            return null;

        // Still reading its database after starting, it answers with nothing found.
        if (SendMessageTimeout(target, WM_USER, new IntPtr(IsDbLoaded), IntPtr.Zero, SmtoAbortIfHung,
                SendTimeoutMs, out var loaded) == IntPtr.Zero || loaded == IntPtr.Zero)
            return null;

        byte[] text;
        try
        {
            text = Strict.GetBytes(query.Search + "\0");
        }
        catch (EncoderFallbackException)
        {
            return null; // half a surrogate pair is no text it could match as we do
        }

        var id = ++_numbering;
        var body = new byte[7 * 4 + text.Length];
        void Put(int index, uint value) => BitConverter.TryWriteBytes(body.AsSpan(index * 4), value);
        Put(0, (uint)_window.ToInt64()); // window handles are 32 bits wide across processes
        Put(1, id);
        Put(2, 0); // no match case, whole word, path or regex: our rule decides those, after
        Put(3, 0); // from the first
        Put(4, query.Max);
        Put(5, RequestFullPath);
        Put(6, 1); // by name: the order it keeps, so the fastest
        text.CopyTo(body, 7 * 4);

        // Waited for only while this query is, on every way out, so a reply to a query given up on
        // finds nothing waiting for it (review, 2026-11-10).
        _awaited = id;
        _answer = null;
        try
        {
            var pinned = GCHandle.Alloc(body, GCHandleType.Pinned);
            try
            {
                var data = new COPYDATASTRUCT
                {
                    dwData = new IntPtr(QueryCopyData2W),
                    cbData = body.Length,
                    lpData = pinned.AddrOfPinnedObject(),
                };
                if (SendMessageTimeout(target, WM_COPYDATA, _window, ref data, SmtoAbortIfHung, SendTimeoutMs, out var taken) == IntPtr.Zero
                    || taken == IntPtr.Zero)
                    return null;
            }
            finally
            {
                pinned.Free();
            }

            var deadline = DateTime.UtcNow + AnswerTimeout;
            while (_answer is null)
            {
                query.Ct.ThrowIfCancellationRequested();
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero)
                    return null;

                MsgWaitForMultipleObjectsEx(0, null, (uint)Math.Min(left.TotalMilliseconds, 50), 0x04FF, 0x4);
                Pump();
            }

            return Parse(_answer);
        }
        finally
        {
            _awaited = 0;
        }
    }

    private static void Pump()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private IntPtr Receive(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message != WM_COPYDATA)
            return DefWindowProc(hwnd, message, wParam, lParam);

        // Any process on the desktop can send this window a message, and an exception thrown back
        // into DispatchMessage would end this thread or the app: whatever arrives, nothing escapes
        // (review, 2026-11-10).
        try
        {
            if (lParam == IntPtr.Zero)
                return IntPtr.Zero;

            var data = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
            if (_awaited != 0 && data.dwData == new IntPtr(_awaited) && data.cbData > 0 && data.lpData != IntPtr.Zero)
            {
                // Everything's memory, valid only during this call.
                var bytes = new byte[data.cbData];
                Marshal.Copy(data.lpData, bytes, 0, data.cbData);
                _answer = bytes;
            }
        }
        catch (Exception)
        {
            // Not an answer: the query waiting, if any, times out and the walk does it.
        }

        return new IntPtr(1);
    }

    /// <summary>
    /// <c>EVERYTHING_IPC_LIST2</c>: five DWORDs (total, count, offset, request flags, sort), then a
    /// (flags, data offset) pair per item; at an item's offset, its full path as a DWORD length and
    /// that many UTF-16 characters. Null for anything that doesn't add up, rather than a guess.
    /// </summary>
    internal static EverythingReply? Parse(byte[] reply)
    {
        if (reply.Length < 20)
            return null;

        uint U32(int at) => BitConverter.ToUInt32(reply, at);
        var total = U32(0);
        var count = U32(4);
        var request = U32(12);
        if (request != RequestFullPath || count > total || 20L + count * 8L > reply.Length)
            return null;

        var paths = new List<IndexedPath>((int)count);
        for (var i = 0; i < count; i++)
        {
            var flags = U32(20 + i * 8);
            var at = (long)U32(20 + i * 8 + 4);
            if (at + 4 > reply.Length)
                return null;

            var length = (long)U32((int)at);
            if (at + 4 + length * 2 > reply.Length)
                return null;

            string path;
            try
            {
                path = Strict.GetString(reply, (int)at + 4, (int)length * 2);
            }
            catch (DecoderFallbackException)
            {
                // A name with half a surrogate pair, which Windows allows: read loosely it would
                // be another name, and its file would be missing from the answer.
                return null;
            }

            paths.Add(new IndexedPath(path, (flags & (ItemFolder | ItemDrive)) != 0));
        }

        return new EverythingReply(total, paths);
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
        public uint lPrivate;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr filter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, ref COPYDATASTRUCT lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[]? handles, uint ms, uint wakeMask, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
