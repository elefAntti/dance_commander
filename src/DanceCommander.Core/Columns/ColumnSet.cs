namespace DanceCommander.Core.Columns;

public enum ColumnKind
{
    /// <summary>Icon plus name. Exactly one column per set is of this kind.</summary>
    Name,
    Text,

    /// <summary>A '/'-separated path, trimmed from the front so the distinguishing end survives.</summary>
    Path,
    Size,
    Date,
    Attributes,
}

public enum ColumnAlignment
{
    Left,
    Right,
}

/// <param name="Flexible">
/// Absorbs whatever width the fixed columns leave over, so the listing fills the pane instead of
/// ending in a dead zone. At most one column per set should claim it; the grid falls back to
/// <see cref="ColumnDefinition.DefaultWidth"/> if none does.
/// </param>
public sealed record ColumnDefinition(
    string Id,
    string Header,
    ColumnKind Kind,
    double DefaultWidth,
    ColumnAlignment Alignment = ColumnAlignment.Left,
    bool Flexible = false);

/// <summary>
/// Well-known column ids. Providers and listing sources may contribute their own; the values
/// then come from <see cref="Fs.FsEntry.Extra"/>.
/// </summary>
public static class ColumnIds
{
    public const string Name = "name";
    public const string Extension = "ext";
    public const string Size = "size";
    public const string Modified = "modified";
    public const string Attributes = "attributes";
    public const string RelativePath = "relpath";

    /// <summary>Which of the places an everywhere search walks a row was found in.</summary>
    public const string Where = "where";

    /// <summary>
    /// How likely a search's row is to be the one wanted. An order, not a column: nothing about it
    /// reads as text, and the name column's header says when the rows are in it.
    /// </summary>
    public const string Relevance = "relevance";

    /// <summary>
    /// Appended to a column id, the key that column sorts by when it is not its text: "Where" reads
    /// "Documents" but sorts by how likely the row is to be the one wanted.
    /// </summary>
    public const string SortKeySuffix = ".sort";

    /// <summary>Anything a listing needs to say about one row that is not a property of the file.</summary>
    public const string Note = "note";
    public const string Owner = "owner";
    public const string Permissions = "permissions";
}

/// <summary>
/// The columns a listing offers. Per §1.2/4 this belongs to the listing, not to a global
/// setting: a flattened view adds a relative path, SFTP adds owner and permissions, search adds
/// where the match was found.
/// </summary>
/// <param name="RelevanceColumnId">
/// The order that puts the likeliest row first, for a search: what Ctrl+F7 goes back to. Null for
/// a listing that is not ranked.
/// </param>
public sealed record ColumnSet(
    IReadOnlyList<ColumnDefinition> Columns,
    string DefaultSortColumnId = ColumnIds.Name,
    bool DefaultDescending = false,
    string? RelevanceColumnId = null)
{
    public static ColumnSet Standard { get; } = new(
    [
        // The fixed columns are sized to their longest realistic content and nothing more — a
        // date, a size — and the name takes everything that is left. A
        // two-pane app has half the room a single-pane one does, so the slack belongs to the one
        // column whose content has no upper bound.
        //
        // There is no Ext column. It existed to make sorting by type possible and cost 66 px of a
        // half-width pane to do it, while the extension it showed was already on screen at the end
        // of every name. Sorting by type is a keystroke now (Ctrl+F4), which is both cheaper and
        // reachable without the mouse, which the column headers never were.
        //
        // No Attr column either, for the same kind of reason: 60 px blank on nearly every row, and
        // letters to decode on the rest. Hidden and system entries are already drawn dimmed; the
        // two flags worth seeing, read-only and link, are glyphs after the name, spelled out in its
        // tooltip. A listing that wants the column can still add it (ColumnIds.Attributes).
        new ColumnDefinition(ColumnIds.Name, "Name", ColumnKind.Name, 220, Flexible: true),
        new ColumnDefinition(ColumnIds.Size, "Size", ColumnKind.Size, 78, ColumnAlignment.Right),
        // Wide enough for "2026-09-16 22.05" and its cell margins. Trimming a fixed-width value
        // to an ellipsis is the one thing a fixed column must never do.
        new ColumnDefinition(ColumnIds.Modified, "Modified", ColumnKind.Date, 148),
    ]);

    /// <summary>
    /// Columns for a listing whose rows come from many folders (§1.2/3, §1.2/4).
    /// </summary>
    /// <remarks>
    /// The folder takes the slack rather than the name, which is the opposite of a directory
    /// listing and is the right way round here: in a flat tree or a search result the names are
    /// short and repetitive while the folder is the part that differs and the part you are
    /// reading.
    ///
    /// Attributes are dropped: a pane is half a window wide, these rows need one more column than
    /// a directory listing does, and the attributes are rarely what you flattened a tree to find.
    /// </remarks>
    public static ColumnSet OfScatteredFiles { get; } = new(
    [
        new ColumnDefinition(ColumnIds.Name, "Name", ColumnKind.Name, 190),
        new ColumnDefinition(ColumnIds.RelativePath, "Folder", ColumnKind.Path, 240, Flexible: true),
        new ColumnDefinition(ColumnIds.Size, "Size", ColumnKind.Size, 78, ColumnAlignment.Right),

        new ColumnDefinition(ColumnIds.Modified, "Modified", ColumnKind.Date, 148),
    ]);

    /// <summary>
    /// A search's: a flattened tree's columns, in order of relevance to begin with. A walk finds
    /// rows in folder order and a server in an order of its own, and neither puts what was meant
    /// near the top.
    /// </summary>
    public static ColumnSet OfSearch { get; } = OfScatteredFiles with
    {
        DefaultSortColumnId = ColumnIds.Relevance,
        RelevanceColumnId = ColumnIds.Relevance,
    };

    /// <summary>
    /// An everywhere search's: the folder in full, since rows come from all over the machine, and
    /// where each was found, which is also the order the rows are most likely wanted in.
    /// </summary>
    public static ColumnSet OfEverywhere { get; } = new(
    [
        new ColumnDefinition(ColumnIds.Name, "Name", ColumnKind.Name, 190),
        new ColumnDefinition(ColumnIds.Where, "Where", ColumnKind.Text, 90),
        new ColumnDefinition(ColumnIds.RelativePath, "Folder", ColumnKind.Path, 240, Flexible: true),
        new ColumnDefinition(ColumnIds.Size, "Size", ColumnKind.Size, 78, ColumnAlignment.Right),
        new ColumnDefinition(ColumnIds.Modified, "Modified", ColumnKind.Date, 148),
    ],
    DefaultSortColumnId: ColumnIds.Where,
    RelevanceColumnId: ColumnIds.Where);

    /// <summary>The same set without a column, for a provider that answers the question better.</summary>
    public ColumnSet Without(string columnId) => this with
    {
        Columns = [.. Columns.Where(c => !string.Equals(c.Id, columnId, StringComparison.Ordinal))],
    };

    public ColumnSet With(params ColumnDefinition[] extra) =>
        this with { Columns = [.. Columns, .. extra] };

    public ColumnDefinition? Find(string id) =>
        Columns.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));
}
