using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Operations;

/// <summary>Something the application moved or renamed: what was at <see cref="From"/> is now at <see cref="To"/>.</summary>
public sealed record Relocation(FsUri From, FsUri To)
{
    /// <summary>
    /// Where <paramref name="uri"/> is after this relocation: moved with it when it is the thing
    /// moved or inside it, and null when the relocation does not touch it.
    /// </summary>
    public FsUri? Apply(FsUri uri)
    {
        var below = new List<string>();
        for (var at = uri; at is not null; at = at.Parent)
        {
            if (at == From)
            {
                var result = To;
                for (var i = below.Count - 1; i >= 0; i--)
                    result = result.Child(below[i]);
                return result;
            }
            below.Add(at.Name);
        }
        return null;
    }
}

/// <summary>
/// Every move and rename the application makes, told to whatever keeps references to paths —
/// collections, bookmarks — so they follow (§6.19). Moves made outside the application are not
/// here; those references show as "not found".
/// </summary>
public interface IRelocationFeed
{
    /// <summary>Raised after the moves happened, on whichever thread made them.</summary>
    event EventHandler<IReadOnlyList<Relocation>>? Relocated;

    void Report(IReadOnlyList<Relocation> relocations);
}

public sealed class RelocationFeed : IRelocationFeed
{
    public event EventHandler<IReadOnlyList<Relocation>>? Relocated;

    public void Report(IReadOnlyList<Relocation> relocations)
    {
        if (relocations.Count > 0)
            Relocated?.Invoke(this, relocations);
    }
}
