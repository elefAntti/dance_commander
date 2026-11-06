using System.Windows.Media.Imaging;
using DanceCommander.Core.Preview;

namespace DanceCommander.App.Services;

/// <summary>What an image file says about itself beyond its pixels, as far as the app uses it.</summary>
/// <remarks>
/// Read with WIC's photo policy names (<c>System.Photo.*</c>) rather than format-specific paths
/// like <c>/app1/ifd/{ushort=274}</c>: one name finds the value in a JPEG's EXIF, a TIFF's IFD or
/// XMP, so a new field is one more query here, not one per format.
/// </remarks>
public sealed record ImageMetadata(ImageOrientation Orientation)
{
    public static ImageMetadata None { get; } = new(ImageOrientation.Upright);

    public static ImageMetadata Read(BitmapFrame frame)
    {
        var metadata = TryGetMetadata(frame);
        if (metadata is null)
            return None;

        return new ImageMetadata(
            Query(metadata, "System.Photo.Orientation") is ushort orientation
                ? ImageOrientation.FromExif(orientation)
                : ImageOrientation.Upright);
    }

    /// <summary>
    /// Some codecs (BMP, ICO) have no metadata at all and say so by throwing; metadata is never
    /// worth failing a preview over.
    /// </summary>
    private static BitmapMetadata? TryGetMetadata(BitmapFrame frame)
    {
        try
        {
            return frame.Metadata as BitmapMetadata;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static object? Query(BitmapMetadata metadata, string name)
    {
        try
        {
            return metadata.GetQuery(name);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
