using DanceCommander.Core.Columns;
using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Listing;

/// <summary>
/// How a listing is ordered. Lives in Core rather than in the view model because it is listing
/// policy, not presentation — and because it is worth testing without a UI.
/// </summary>
/// <param name="DirectoriesFirst">
/// False is the default: folders and files sort together by name, the way Finder does it.
/// True restores the classic commander behaviour of pinning folders to the top.
/// </param>
public sealed record ListingOrder(
    string ColumnId = ColumnIds.Name,
    bool Descending = false,
    bool DirectoriesFirst = false) : IComparer<FsEntry>
{
    public int Compare(FsEntry? x, FsEntry? y)
    {
        if (x is null || y is null)
            return 0;

        if (DirectoriesFirst && x.IsDirectory != y.IsDirectory)
            return x.IsDirectory ? -1 : 1; // unaffected by sort direction

        // By type, a folder is a type of its own and comes first — not an extensionless file, which
        // is what comparing extensions alone made of it, scattering folders among README and
        // Makefile. First in either direction, as above: reversing the types does not make a
        // folder a file.
        if (ColumnId == ColumnIds.Extension && x.IsDirectory != y.IsDirectory)
            return x.IsDirectory ? -1 : 1;

        var result = CompareBy(ColumnId, x, y);
        if (result != 0)
            return Descending ? -result : result;

        // Ties read A-Z whichever way the column runs: newest first, files of the same minute
        // still come in name order rather than backwards — as do the places in the history from
        // before visits were timed, which all tie at "no time".
        return ColumnId == ColumnIds.Name ? 0 : NaturalComparer.Instance.Compare(x.Name, y.Name);
    }

    private static int CompareBy(string columnId, FsEntry x, FsEntry y) => columnId switch
    {
        ColumnIds.Name => NaturalComparer.Instance.Compare(x.Name, y.Name),
        // Among folders a dot is just part of the name ("v1.2", "site.old"), not a type.
        ColumnIds.Extension => x.IsDirectory && y.IsDirectory
            ? 0
            : string.Compare(x.Extension, y.Extension, StringComparison.OrdinalIgnoreCase),
        ColumnIds.Size => x.Size.CompareTo(y.Size),
        ColumnIds.Modified => Nullable.Compare(x.Modified, y.Modified),
        ColumnIds.Attributes => ((int)x.Attributes).CompareTo((int)y.Attributes),
        _ => string.Compare(SortKey(x, columnId), SortKey(y, columnId), StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>What a listing's own column sorts by: the key it was given, if any, else its text.</summary>
    private static string? SortKey(FsEntry entry, string columnId) =>
        entry.Extra?.GetValueOrDefault(columnId + ColumnIds.SortKeySuffix) ?? entry.Extra?.GetValueOrDefault(columnId);

    /// <summary>Clicking the current column flips direction; clicking another switches to it.</summary>
    public ListingOrder ToggledBy(string columnId) =>
        ColumnId == columnId
            ? this with { Descending = !Descending }
            : this with { ColumnId = columnId, Descending = StartsDescending(columnId) };

    /// <summary>
    /// Which end of a column is the interesting one on first click.
    /// </summary>
    /// <remarks>
    /// Nobody clicks "Modified" to find their oldest file, or "Size" to find their smallest. Names
    /// are different: A-Z is what a name column is for. This is the same rule every file manager
    /// and every mail client uses, and getting it wrong costs a second click every single time.
    /// </remarks>
    private static bool StartsDescending(string columnId) =>
        columnId is Columns.ColumnIds.Modified or Columns.ColumnIds.Size;
}

/// <summary>
/// Compares names the way people read them, so file9 comes before file10. Ordinary character
/// runs compare case-insensitively; digit runs compare as numbers.
/// </summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static NaturalComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        // "file9" and "file09" are numerically equal. Rather than leave their order undefined,
        // remember the first zero-padding difference and fall back to it only if nothing else
        // separates the two names.
        var padding = 0;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;

                var runX = x.AsSpan(startX, i - startX);
                var runY = y.AsSpan(startY, j - startY);
                var digitsX = runX.TrimStart('0');
                var digitsY = runY.TrimStart('0');

                if (digitsX.Length != digitsY.Length)
                    return digitsX.Length - digitsY.Length;

                var digits = digitsX.CompareTo(digitsY, StringComparison.Ordinal);
                if (digits != 0)
                    return digits;

                if (padding == 0)
                    padding = runX.Length - runY.Length; // fewer leading zeros sorts first

                continue;
            }

            var a = char.ToUpperInvariant(x[i]);
            var b = char.ToUpperInvariant(y[j]);
            if (a != b)
                return a - b;

            i++;
            j++;
        }

        var remaining = (x.Length - i) - (y.Length - j);
        return remaining != 0 ? remaining : padding;
    }
}
