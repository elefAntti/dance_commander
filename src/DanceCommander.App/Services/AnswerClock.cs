using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Media;
using Serilog;

namespace DanceCommander.App.Services;

/// <summary>
/// How long a click or a key waited for the first frame after it: whether the window heard you.
/// </summary>
/// <remarks>
/// The other timing lines start when our code starts, so a stall before that leaves none. An Up
/// clicked after a night away took several seconds with nothing on screen and nothing in the log
/// (2026-10-28): neither the listing clock nor the loading bar, which both run on the UI thread,
/// had begun. The input's own timestamp is the moment Windows took it, so measured from there the
/// wait includes the time the thread could not run at all. Being away is reported, and on a late
/// answer only, the memory paged back in since: the two causes this machine has, a working set
/// the system took while the window was idle and a processor queue too long to get a core, look
/// alike from inside and want different things done.
/// </remarks>
public sealed class AnswerClock
{
    /// <summary>When the last answered input happened (Windows ticks), for how long the user was away.</summary>
    private int _lastInput = Environment.TickCount;

    /// <summary>The input waiting for its frame, if one is.</summary>
    private (int At, string What, int Away)? _waiting;

    /// <summary>Inputs from before this tick queued up behind one already reported: the same stall.</summary>
    private int _reportedUpTo = Environment.TickCount;

    /// <summary>Page faults and working set when the window was left; null once back and answered.</summary>
    private (uint PageFaults, ulong WorkingSet)? _onLeaving;

    /// <summary>The window lost the foreground: what memory looked like, for a slow return.</summary>
    public void Left() => _onLeaving = Memory();

    /// <summary>A key or button went down; times it to the next frame.</summary>
    public void Seen(InputEventArgs input)
    {
        // Keys held down repeat, and while a frame is awaited the next input would be timed from
        // its own later timestamp anyway; the first one is the one that was felt.
        if (_waiting is not null || input is KeyEventArgs { IsRepeat: true })
            return;

        var what = input switch
        {
            KeyEventArgs => "a key",
            MouseButtonEventArgs => "a click",
            _ => null,
        };
        if (what is null)
            return;

        _waiting = (input.Timestamp, what, unchecked(input.Timestamp - _lastInput));
        _lastInput = input.Timestamp;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnFrame;
        if (_waiting is not { } input)
            return;

        _waiting = null;
        var leaving = _onLeaving;
        _onLeaving = null;
        var now = Environment.TickCount;
        var waited = unchecked(now - input.At);

        // Queued behind one already reported, or before it: the same stall, said once.
        if (unchecked(input.At - _reportedUpTo) < 0)
            return;

        var verdict = Budgets.Verdict((Budgets.AnswerName, waited, Budgets.Answer));
        if (verdict is null)
            return;

        _reportedUpTo = now;
        var away = TimeSpan.FromMilliseconds(input.Away);
        var memory = "";
        if (leaving is { } then && Memory() is { } current)
            memory = $"; since leaving, {current.PageFaults - then.PageFaults:N0} page faults, working set " +
                     $"{then.WorkingSet >> 20:N0} MB on leaving and {current.WorkingSet >> 20:N0} MB now";

        Log.Information(
            "Slow to answer: {What} waited {Waited:N0} ms for a frame, {Away} after the last input{Memory}, {Verdict}",
            input.What, waited,
            away.TotalMinutes >= 1 ? $"{away.TotalMinutes:N0} min" : $"{away.TotalSeconds:N0} s",
            memory, verdict);
    }

    private static (uint PageFaults, ulong WorkingSet)? Memory()
    {
        var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
        return GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size)
            ? (counters.PageFaultCount, counters.WorkingSetSize)
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);
}
