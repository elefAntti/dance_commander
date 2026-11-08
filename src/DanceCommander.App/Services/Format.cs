using DanceCommander.Core.Fs;

namespace DanceCommander.App.Services;

/// <summary>
/// Cell formatting. Timestamps are shown as timestamps, not as "2 minutes ago" (§1.3): the app
/// does not round off facts about your files.
/// </summary>
public static class Format
{
    /// <summary>"KB" with a capital K, as Explorer writes it: every unit two letters, bar the bytes.</summary>
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Size(long bytes)
    {
        var (number, unit) = SizeParts(bytes);
        return $"{number} {unit}";
    }

    /// <summary>
    /// The size as a number and a unit, apart, so a column can align each on its own: numbers to
    /// the right, units in a slot of their own. Bytes say "B", so every row in the column has a
    /// unit and the column reads as regular.
    /// </summary>
    public static (string Number, string Unit) SizeParts(long bytes)
    {
        if (bytes < 1000)
            return (bytes.ToString("N0"), Units[0]);

        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return (value.ToString("N1"), Units[unit]);
    }

    public static string ExactSize(long bytes) => $"{bytes:N0} B";

    public static string Date(DateTimeOffset? value) =>
        value is null ? string.Empty : value.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    /// <summary>The date and the time apart, so a column can line up the times of every row.</summary>
    public static (string Day, string Time) DateParts(DateTimeOffset? value) =>
        value is null
            ? (string.Empty, string.Empty)
            : (value.Value.LocalDateTime.ToString("yyyy-MM-dd"), value.Value.LocalDateTime.ToString("HH:mm"));

    /// <summary>
    /// "Today" or "Yesterday" for a time on either of those days, and null for any other. Days
    /// are the local calendar's, the same as the dates beside them.
    /// </summary>
    public static string? FriendlyDay(DateTimeOffset? value, DateTime today)
    {
        if (value is null)
            return null;

        var day = value.Value.LocalDateTime.Date;
        return day == today.Date ? "Today"
            : day == today.Date.AddDays(-1) ? "Yesterday"
            : null;
    }

    /// <summary>
    /// Blank for an ordinary entry, and the full positional <c>rhsl</c> form for anything else.
    /// A fixed <c>----</c> on every row is a column of punctuation the height of the window
    /// saying "normal" several hundred times, but dropping the dashes entirely costs more than it
    /// saves: a lone <c>r</c> and a lone <c>h</c> occupy the same spot, so you have to know the
    /// letter order to read either one. Keeping the slots means position identifies the flag, and
    /// keeping them only on rows that have something to say means the column is empty where there
    /// is nothing to know.
    /// </summary>
    /// <param name="isDirectory">
    /// Directories never show the read-only flag.
    /// </param>
    /// <remarks>
    /// On Windows the read-only attribute on a *directory* does not mean the directory is
    /// read-only — nothing enforces it, and you can create files inside one all day. The bit is
    /// reused by the shell to mark a folder as customised, which is why Explorer draws that
    /// checkbox filled rather than ticked. Reporting it as "r" was the file manager stating
    /// something about the filesystem that is not true.
    /// </remarks>
    public static string Attributes(FsEntryAttributes attributes, bool isDirectory = false)
    {
        // Only the flags that have a slot: a cloud-only file with nothing else set is "----"
        // otherwise, four dashes saying nothing on two rows in three of a OneDrive folder.
        attributes &= FsEntryAttributes.ReadOnly | FsEntryAttributes.Hidden | FsEntryAttributes.System | FsEntryAttributes.Symlink;
        var readOnly = !isDirectory && attributes.HasFlag(FsEntryAttributes.ReadOnly);

        if (attributes == FsEntryAttributes.None || (isDirectory && Only(attributes, FsEntryAttributes.ReadOnly)))
            return string.Empty;

        Span<char> flags =
        [
            readOnly ? 'r' : '-',
            attributes.HasFlag(FsEntryAttributes.Hidden) ? 'h' : '-',
            attributes.HasFlag(FsEntryAttributes.System) ? 's' : '-',
            attributes.HasFlag(FsEntryAttributes.Symlink) ? 'l' : '-',
        ];
        return new string(flags);
    }

    /// <summary>
    /// The same four flags in words, for a tooltip, and where a cloud file lives in Explorer's own
    /// words. Null when there are none to explain.
    /// </summary>
    /// <remarks>
    /// "r-h-" is compact and completely opaque the first time you meet it. The positional form is
    /// right for a column that has to stay narrow, and the words are right for the moment somebody
    /// wonders what the third slot means; a tooltip is where the second one costs nothing.
    /// </remarks>
    public static string? AttributeNames(FsEntryAttributes attributes, bool isDirectory = false)
    {
        if (attributes == FsEntryAttributes.None)
            return null;

        var set = new List<string>(4);
        if (!isDirectory && attributes.HasFlag(FsEntryAttributes.ReadOnly)) set.Add("read-only");
        if (attributes.HasFlag(FsEntryAttributes.Hidden)) set.Add("hidden");
        if (attributes.HasFlag(FsEntryAttributes.System)) set.Add("system");
        if (attributes.HasFlag(FsEntryAttributes.Symlink)) set.Add("symbolic link");
        if (attributes.HasFlag(FsEntryAttributes.CloudOnly)) set.Add("available when online");
        if (attributes.HasFlag(FsEntryAttributes.AlwaysKept)) set.Add("always available on this device");

        return set.Count == 0 ? null : string.Join(", ", set);
    }

    /// <summary>True when the only flag set is this one.</summary>
    private static bool Only(FsEntryAttributes attributes, FsEntryAttributes flag) =>
        attributes == flag;
}
