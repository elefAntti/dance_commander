using System.Text;
using DanceCommander.Core.Preview;
using Shouldly;

namespace DanceCommander.Tests;

public sealed class TextInspectorTests
{
    private static TextInspection Inspect(string text, Encoding? encoding = null, bool truncated = false) =>
        TextInspector.Inspect((encoding ?? new UTF8Encoding(false)).GetBytes(text), truncated);

    [Fact]
    public void PlainAsciiIsUtf8AsFarAsAnyoneCanTell()
    {
        var result = Inspect("hello");

        result.IsText.ShouldBeTrue();
        result.EncodingName.ShouldBe("UTF-8");
        result.Text.ShouldBe("hello");
    }

    [Fact]
    public void ReadsTextThatIsActuallyUtf8()
    {
        var result = Inspect("käytävä — naïve");

        result.IsText.ShouldBeTrue();
        result.Text.ShouldBe("käytävä — naïve");
    }

    [Theory]
    [InlineData("a\r\nb\r\nc", LineEndingStyle.Crlf, "CRLF")]
    [InlineData("a\nb\nc", LineEndingStyle.Lf, "LF")]
    [InlineData("a\rb\rc", LineEndingStyle.Cr, "CR")]
    [InlineData("one line", LineEndingStyle.None, "no line breaks")]
    public void ReportsHowTheLinesEnd(string text, LineEndingStyle expected, string label)
    {
        var result = Inspect(text);

        result.LineEndings.ShouldBe(expected);
        result.LineEndingLabel.ShouldBe(label);
    }

    [Fact]
    public void MixedLineEndingsAreCalledOutWithTheCounts()
    {
        // The case that costs an afternoon: it looks fine until something downstream disagrees.
        var result = Inspect("windows\r\nunix\nunix\n");

        result.LineEndings.ShouldBe(LineEndingStyle.Mixed);
        result.LineEndingLabel.ShouldBe("mixed line endings");
        result.LineEndingDetail.ShouldBe("1 CRLF, 2 LF");
    }

    [Fact]
    public void ACrlfIsOneEndingNotACrAndAnLf()
    {
        var result = Inspect("a\r\nb");

        result.CrlfCount.ShouldBe(1);
        result.LfCount.ShouldBe(0);
        result.CrCount.ShouldBe(0);
        result.LineEndings.ShouldBe(LineEndingStyle.Crlf);
    }

    [Fact]
    public void NoticesTabs()
    {
        Inspect("def f():\n\treturn 1\n").HasTabs.ShouldBeTrue();
        Inspect("def f():\n    return 1\n").HasTabs.ShouldBeFalse();
    }

    [Fact]
    public void AByteOrderMarkIsBelievedAndNotShownAsContent()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat("hello"u8.ToArray()).ToArray();

        var result = TextInspector.Inspect(bytes, truncated: false);

        result.EncodingName.ShouldBe("UTF-8 with BOM");
        result.Text.ShouldBe("hello"); // the mark is not a character in the file
    }

    [Fact]
    public void ReadsMarkedUtf16WhichWouldOtherwiseLookBinary()
    {
        // Half its bytes are NUL, so the mark has to be checked before the binary test -- which is
        // also why unmarked UTF-16 is reported as binary, below: without the mark there is nothing
        // to tell it apart from a file that really is.
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("hello")).ToArray();

        var result = TextInspector.Inspect(bytes, truncated: false);

        result.IsText.ShouldBeTrue();
        result.EncodingName.ShouldBe("UTF-16 LE");
        result.Text.ShouldBe("hello");
    }

    [Fact]
    public void UnmarkedUtf16IsIndistinguishableFromBinaryAndIsTreatedAsSuch() =>
        TextInspector.Inspect(Encoding.Unicode.GetBytes("hello"), truncated: false).IsText.ShouldBeFalse();

    [Fact]
    public void ANulByteMeansBinary()
    {
        var result = TextInspector.Inspect([0x7F, (byte)'E', (byte)'L', (byte)'F', 0x00, 0x01], truncated: false);

        result.IsText.ShouldBeFalse();
        result.Text.ShouldBeEmpty();
    }

    [Fact]
    public void EightBitTextIsShownRatherThanRefused()
    {
        // Latin-1 bytes that are not valid UTF-8. Showing them beats showing nothing, but the
        // label has to admit it is a guess.
        var result = TextInspector.Inspect([(byte)'c', (byte)'a', (byte)'f', 0xE9], truncated: false);

        result.IsText.ShouldBeTrue();
        result.EncodingName.ShouldBe("8-bit text (assumed Latin-1)");
        result.Text.ShouldBe("café");
    }

    [Fact]
    public void AnEmptyFileIsTextAndSaysSo()
    {
        var result = TextInspector.Inspect([], truncated: false);

        result.IsText.ShouldBeTrue();
        result.EncodingName.ShouldBe("empty");
    }

    [Fact]
    public void TruncationIsCarriedThroughSoThePreviewCanSaySo() =>
        Inspect("partial", truncated: true).Truncated.ShouldBeTrue();
}

public sealed class ImageSignatureTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "PNG")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "JPEG")]
    [InlineData(new byte[] { 0x42, 0x4D, 0x36, 0x00 }, "BMP")]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00 }, "TIFF")]
    [InlineData(new byte[] { 0x00, 0x00, 0x01, 0x00 }, "ICO")]
    public void RecognisesFormatsByTheirLeadingBytes(byte[] bytes, string expected) =>
        ImageSignature.Detect(bytes).ShouldBe(expected);

    [Fact]
    public void RecognisesGifOfEitherVintage()
    {
        ImageSignature.Detect("GIF87a"u8).ShouldBe("GIF");
        ImageSignature.Detect("GIF89a"u8).ShouldBe("GIF");
    }

    [Fact]
    public void WebPNeedsBothHalvesOfItsHeader()
    {
        ImageSignature.Detect("RIFF????WEBPVP8 "u8).ShouldBe("WebP");
        // A RIFF that is not a WebP is some other container, most likely audio.
        ImageSignature.Detect("RIFF????WAVEfmt "u8).ShouldBeNull();
    }

    [Fact]
    public void OrdinaryTextIsNotAnImage() => ImageSignature.Detect("# notes"u8).ShouldBeNull();

    [Fact]
    public void AFileTooShortToHaveASignatureIsNotAnImage() => ImageSignature.Detect([0x89]).ShouldBeNull();
}

public sealed class ImageOrientationTests
{
    // Corners in clockwise order, so a clockwise quarter turn moves each one step along.
    private const string TopLeft = "top left", TopRight = "top right", BottomRight = "bottom right", BottomLeft = "bottom left";
    private static readonly string[] Clockwise = [TopLeft, TopRight, BottomRight, BottomLeft];

    /// <summary>Where a corner of the stored pixels ends up once shown.</summary>
    private static string Shown(ImageOrientation orientation, string corner)
    {
        if (orientation.Mirrored)
            corner = corner switch { TopLeft => TopRight, TopRight => TopLeft, BottomRight => BottomLeft, _ => BottomRight };
        return Clockwise[(Array.IndexOf(Clockwise, corner) + orientation.QuarterTurns) % 4];
    }

    // The standard defines each value by where the stored image's first row and first column
    // belong; following the stored top two corners pins down all eight.
    [Theory]
    [InlineData(1, TopLeft, TopRight)]
    [InlineData(2, TopRight, TopLeft)]
    [InlineData(3, BottomRight, BottomLeft)]
    [InlineData(4, BottomLeft, BottomRight)]
    [InlineData(5, TopLeft, BottomLeft)]
    [InlineData(6, TopRight, BottomRight)]
    [InlineData(7, BottomRight, TopRight)]
    [InlineData(8, BottomLeft, TopLeft)]
    public void EachExifValueTurnsTheImageAsTheStandardSays(int exif, string storedTopLeft, string storedTopRight)
    {
        var orientation = ImageOrientation.FromExif(exif);

        Shown(orientation, TopLeft).ShouldBe(storedTopLeft);
        Shown(orientation, TopRight).ShouldBe(storedTopRight);
    }

    [Fact]
    public void APhoneHeldUprightSwapsWidthAndHeight()
    {
        // 6 is what a phone writes for an ordinary portrait photo.
        ImageOrientation.FromExif(6).SwapsSides.ShouldBeTrue();
        ImageOrientation.FromExif(3).SwapsSides.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-1)]
    public void AValueOutsideTheStandardIsUpright(int exif) =>
        ImageOrientation.FromExif(exif).ShouldBe(ImageOrientation.Upright);
}
