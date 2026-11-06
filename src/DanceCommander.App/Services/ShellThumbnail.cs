using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DanceCommander.App.Services;

/// <summary>
/// Asks Windows for the picture it would show for a file in Explorer.
/// </summary>
/// <remarks>
/// <para>
/// The shell already knows how to render a great many formats the app has no business decoding
/// itself — HEIC and camera raw, PSD, video first-frames, PDF page one, Office documents wherever
/// Office is installed. Every one of those arrives here as a bitmap and nothing more, which is the
/// whole reason this is worth having and the preview *handler* mechanism
/// (<c>IPreviewHandler</c>) is not: a handler wants a child HWND to draw into, and that is a
/// second rendering stack inside the window with its own theming, DPI and keyboard behaviour. A
/// bitmap has none of those problems.
/// </para>
/// <para>
/// Only local paths qualify. The shell needs something it can parse into a shell item, and a file
/// inside an archive or on an SFTP server is not that; spilling those to a temp file to make them
/// eligible is a separate decision, deliberately not taken here.
/// </para>
/// <para>
/// The consumer-side interface is <c>IShellItemImageFactory</c>. <c>IThumbnailProvider</c>, which
/// is the name people reach for, is what an extractor *implements* — not something to call.
/// </para>
/// </remarks>
public static class ShellThumbnail
{
    /// <summary>
    /// Loads the thumbnail for <paramref name="path"/> at up to <paramref name="pixels"/> square,
    /// or null when Windows has no thumbnail for the file — which is the ordinary outcome for most
    /// of them and is not an error.
    /// </summary>
    /// <remarks>
    /// Extraction runs on its own single-use STA thread. Thumbnail extractors are third-party COM
    /// living in the shell, they expect an apartment, and some of them are slow or wedge outright;
    /// a thread per call keeps all of that off the UI thread and off the thread pool. Cancelling
    /// abandons the thread rather than stopping it — <c>GetImage</c> is synchronous and offers no
    /// way to interrupt it — so the work is arranged to clean up after itself either way. The token
    /// does reach the thread, but only to stop it asking again while a thumbnail is still pending.
    /// </remarks>
    public static Task<BitmapSource?> LoadAsync(string path, int pixels, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Extract(path, pixels, ct));
            }
            catch (Exception)
            {
                completion.SetResult(null);
            }
        })
        {
            IsBackground = true,
            Name = "shell-thumbnail",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task.WaitAsync(ct);
    }

    private static BitmapSource? Extract(string path, int pixels, CancellationToken ct)
    {
        object? factory = null;
        var bitmap = IntPtr.Zero;

        try
        {
            // Failure here is the normal way to learn that a path is not something the shell can
            // name -- a deleted file, a path it refuses to parse -- so it is not worth a throw.
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ImageFactoryIid, out factory) != 0 ||
                factory is not IShellItemImageFactory images)
            {
                return null;
            }

            // THUMBNAILONLY is the point of the whole exercise. Without it the shell falls back to
            // the file type's icon, and a preview pane filled with a 1024-pixel blur of the generic
            // document icon is worse than the sentence it replaced.
            //
            // "Pending" means someone else is making the picture: OneDrive renders Office files
            // that carry no thumbnail of their own, and takes a second or three the first time it
            // is asked. Taking that as "no thumbnail" is what put the zip-archive sentence on
            // documents Explorer showed fine. So ask again until it arrives or the caller gives up.
            int result;
            while ((result = images.GetImage(new SIZE { cx = pixels, cy = pixels }, SIIGBF_THUMBNAILONLY, out bitmap))
                   == WTS_E_EXTRACTIONPENDING &&
                   !ct.IsCancellationRequested)
            {
                Thread.Sleep(PendingPoll);
            }

            if (result != 0 || bitmap == IntPtr.Zero)
                return null;

            return Convert(bitmap);
        }
        catch (Exception)
        {
            // An extractor that throws across the COM boundary is not going to produce a picture,
            // and a preview is not worth taking the application down for.
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
                DeleteObject(bitmap);
            if (factory is not null)
                Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Copies the shell's bitmap into a frozen WPF one, so it can cross to the UI thread.</summary>
    /// <remarks>
    /// The obvious call, <see cref="Imaging.CreateBitmapSourceFromHBitmap"/>, discards the alpha
    /// channel, and thumbnails use it: page thumbnails come with a drop shadow, and premultiplied
    /// colour with the alpha thrown away renders those edges as black fringing. So a 32-bit result
    /// is copied out of the DIB directly as Pbgra32, which is exactly the layout the shell produced.
    /// The rows are taken top-down, which is how <c>GetImage</c> returns them; the fallback covers
    /// the depths where that assumption does not apply anyway.
    /// </remarks>
    private static BitmapSource Convert(IntPtr bitmap)
    {
        var info = default(BITMAP);
        var described = GetObject(bitmap, Marshal.SizeOf<BITMAP>(), ref info) != 0;

        if (!described || info.bmBitsPixel != 32 || info.bmBits == IntPtr.Zero)
        {
            var copy = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            copy.Freeze();
            return copy;
        }

        var source = BitmapSource.Create(
            info.bmWidth,
            info.bmHeight,
            96,
            96,
            PixelFormats.Pbgra32,
            null,
            info.bmBits,
            info.bmWidthBytes * info.bmHeight,
            info.bmWidthBytes);
        source.Freeze();
        return source;
    }

    /// <summary>Return the file's thumbnail or nothing; never substitute its icon.</summary>
    private const int SIIGBF_THUMBNAILONLY = 0x08;

    /// <summary>The thumbnail is being made elsewhere (a cloud provider) and is not ready yet.</summary>
    private const int WTS_E_EXTRACTIONPENDING = unchecked((int)0x8004B205);

    /// <summary>How long to wait between asks while a thumbnail is pending.</summary>
    private static readonly TimeSpan PendingPoll = TimeSpan.FromMilliseconds(100);

    private static readonly Guid ImageFactoryIid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, ref BITMAP info);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
