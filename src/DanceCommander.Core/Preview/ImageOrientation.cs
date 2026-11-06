namespace DanceCommander.Core.Preview;

/// <summary>
/// How to turn an image's stored pixels the right way up: mirror left to right if
/// <see cref="Mirrored"/>, then turn clockwise by <see cref="QuarterTurns"/>.
/// </summary>
/// <remarks>
/// A phone camera writes its pixels the way the sensor sits and records which way was up in the
/// EXIF Orientation tag, so a portrait photo is stored as a landscape one plus "turn 90°". Kept as
/// turns rather than as the tag's number so that a turn chosen by hand, for a file whose tag is
/// missing or wrong, adds to <see cref="QuarterTurns"/> instead of needing an eight-way table of
/// its own.
/// </remarks>
public readonly record struct ImageOrientation(int QuarterTurns, bool Mirrored)
{
    public static ImageOrientation Upright => default;

    /// <summary>True when the shown image is the stored one on its side, width and height swapped.</summary>
    public bool SwapsSides => QuarterTurns % 2 == 1;

    /// <summary>
    /// The EXIF Orientation tag (274), in the order the standard defines it: mirror first, then turn.
    /// Anything outside 1–8 is treated as upright, as every viewer does.
    /// </summary>
    public static ImageOrientation FromExif(int value) => value switch
    {
        2 => new(0, true),
        3 => new(2, false),
        4 => new(2, true),
        5 => new(3, true),
        6 => new(1, false),
        7 => new(1, true),
        8 => new(3, false),
        _ => Upright,
    };
}
