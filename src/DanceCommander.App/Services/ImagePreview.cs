using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DanceCommander.Core.Preview;

namespace DanceCommander.App.Services;

/// <summary>
/// Decodes an image for the preview, the right way up (§6.11).
/// </summary>
/// <remarks>
/// <see cref="BitmapImage"/>, which this replaced, ignores the EXIF Orientation tag, so every
/// portrait photo off a phone showed on its side. Decoding through <see cref="BitmapDecoder"/>
/// keeps the frame's metadata in reach, read once into <see cref="ImageMetadata"/>; whatever else
/// the preview comes to want from a file (when it was taken, with what) is read there too.
/// </remarks>
public static class ImagePreview
{
    public sealed record Decoded(BitmapSource Image, ImageMetadata Metadata);

    /// <summary>
    /// Throws what <see cref="BitmapDecoder"/> throws for bytes it cannot decode
    /// (<see cref="NotSupportedException"/>, <see cref="FileFormatException"/>, <see cref="ArgumentException"/>).
    /// </summary>
    public static Decoded Decode(byte[] bytes)
    {
        var decoder = BitmapDecoder.Create(
            new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var metadata = ImageMetadata.Read(frame);

        var image = Orient(frame, metadata.Orientation);
        image.Freeze();
        return new Decoded(image, metadata);
    }

    /// <summary>The stored pixels turned as <paramref name="orientation"/> says.</summary>
    public static BitmapSource Orient(BitmapSource source, ImageOrientation orientation)
    {
        if (orientation == ImageOrientation.Upright)
            return source;

        var transform = new TransformGroup();
        if (orientation.Mirrored)
            transform.Children.Add(new ScaleTransform(-1, 1));
        if (orientation.QuarterTurns % 4 != 0)
            transform.Children.Add(new RotateTransform(90 * orientation.QuarterTurns));

        // A TransformedBitmap turns its pixels lazily, when first drawn, which would put a 12-megapixel
        // rotation on the render path. Caching it here does that work on the caller's thread, which
        // for the preview is the background one.
        return new CachedBitmap(
            new TransformedBitmap(source, transform), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }
}
