using System.Globalization;

namespace DanceCommander.Core.Jobs;

/// <summary>When something happened, as the strip and the activity log say it.</summary>
public static class Clock
{
    /// <summary>
    /// The time alone for today, which is nearly everything; the day as well before that, so a
    /// window left open overnight doesn't pass yesterday's copy off as this morning's.
    /// </summary>
    public static string Time(DateTimeOffset at, DateTimeOffset now, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var local = at.ToLocalTime();
        var time = local.ToString("t", culture);
        return local.Date == now.ToLocalTime().Date
            ? time
            : local.ToString(culture.DateTimeFormat.ShortDatePattern, culture) + " " + time;
    }
}
