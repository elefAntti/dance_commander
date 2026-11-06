using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Operations;

/// <summary>
/// What the application itself just put somewhere — copied, moved, pasted, created, compressed —
/// told to the views showing that folder, so they can show where it landed.
/// </summary>
/// <remarks>
/// The sibling of <see cref="IRelocationFeed"/>, and for the same reason not the watcher: the
/// watcher also sees every file some other program writes, and a view that drew attention to all of
/// those would never be calm. Only an arrival somebody caused here is worth pointing at. Reported
/// at the top level only — the folder that was copied, not the thousand files inside it.
/// </remarks>
public interface IArrivalFeed
{
    /// <summary>Raised after the items exist, on whichever thread put them there.</summary>
    event EventHandler<IReadOnlyList<FsUri>>? Arrived;

    void Report(IReadOnlyList<FsUri> placed);
}

public sealed class ArrivalFeed : IArrivalFeed
{
    public event EventHandler<IReadOnlyList<FsUri>>? Arrived;

    public void Report(IReadOnlyList<FsUri> placed)
    {
        if (placed.Count > 0)
            Arrived?.Invoke(this, placed);
    }
}

/// <summary>
/// The shape of an arrival's glow over time: quick to come, a moment held, slow to go. Calm means
/// the eye is drawn and then released — nothing that pulses, bounces or asks to be dismissed.
/// </summary>
public static class ArrivalGlow
{
    public static readonly TimeSpan Rise = TimeSpan.FromMilliseconds(160);
    public static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(420);
    public static readonly TimeSpan Total = TimeSpan.FromMilliseconds(1700);

    /// <summary>From 0 to 1: how strongly the row glows this long after it landed.</summary>
    public static double At(TimeSpan since)
    {
        if (since <= TimeSpan.Zero || since >= Total)
            return 0;

        if (since < Rise)
        {
            var t = since / Rise;
            return 1 - (1 - t) * (1 - t); // easing out: arrives fast, settles
        }

        if (since < Hold)
            return 1;

        var u = (since - Hold) / (Total - Hold);
        var eased = u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(-2 * u + 2, 3) / 2; // in and out
        return 1 - eased;
    }
}
