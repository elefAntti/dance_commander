using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace DanceCommander.App.Services;

/// <summary>A file a program handed over as bytes rather than as a path: its name and its content.</summary>
/// <param name="DeclaredSize">The size the program said it was, where it said one.</param>
public sealed record VirtualFile(string Name, byte[] Content, long? DeclaredSize)
{
    /// <summary>
    /// What came differs in length from what was promised: maybe cut short, maybe a size that was
    /// only an estimate. Not refused, as nobody has checked how exact Outlook's sizes are, but said,
    /// so a short file is looked at before the email it came from is deleted.
    /// </summary>
    public bool SizeDiffers => DeclaredSize is { } size && size != Content.LongLength;
}

/// <summary>
/// Reads "virtual files" off a drag: what Outlook offers for an attachment, and what zip viewers
/// and browsers offer for things that are not on disk. There is no path, only a list of names
/// (<c>FileGroupDescriptorW</c>) and each one's bytes (<c>FileContents</c>, asked for by index).
/// Explorer reads these; a drop that only understood paths turned an Outlook attachment away.
/// </summary>
/// <remarks>
/// WPF's own <see cref="IDataObject.GetData(string)"/> can't ask for an index, so the contents
/// are read through the COM interface WPF's data object also implements. Everything is read
/// during the drop, while the source is still bound to answer.
/// </remarks>
public static class VirtualFiles
{
    private const string DescriptorFormat = "FileGroupDescriptorW";
    private const string ContentsFormat = "FileContents";

    /// <summary>The size of one FILEDESCRIPTORW, and where in it the fields used here sit.</summary>
    private const int DescriptorSize = 592;
    private const int FlagsOffset = 0;
    private const int AttributesOffset = 36;
    private const int SizeHighOffset = 64;
    private const int SizeLowOffset = 68;
    private const int NameOffset = 72;
    private const int NameChars = 260;

    private const int FdAttributes = 0x4;
    private const int FdFileSize = 0x40;
    private const int FileAttributeDirectory = 0x10;

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: a cloud placeholder that downloads when read.</summary>
    private const int RecallOnDataAccess = 0x400000;

    /// <summary>
    /// The most a drop reads into memory from paths a program gave. Attachments are megabytes; this
    /// is only there so a mistaken drop can't take the machine's memory.
    /// </summary>
    private const long MaxReadAtDrop = 1L << 30;

    public static bool IsPresent(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DescriptorFormat);
        }
        catch (Exception)
        {
            // A source that offers a format and then fails to say so is not offering it.
            return false;
        }
    }

    /// <summary>
    /// Every file on the drag, or null with <paramref name="problem"/> saying why not. All or
    /// nothing: a drop that quietly brought three of four attachments would look like it worked.
    /// </summary>
    public static IReadOnlyList<VirtualFile>? Read(IDataObject data, out string? problem)
    {
        problem = null;
        try
        {
            if (data.GetData(DescriptorFormat) is not MemoryStream descriptors
                || data is not ComTypes.IDataObject com)
            {
                problem = "The program offered files but did not say what they are.";
                return null;
            }

            var bytes = descriptors.ToArray();
            var count = bytes.Length >= 4 ? BitConverter.ToInt32(bytes, 0) : 0;
            if (count <= 0 || bytes.Length < 4 + (long)count * DescriptorSize)
            {
                problem = "The program offered files but did not say what they are.";
                return null;
            }

            var files = new List<VirtualFile>(count);
            for (var i = 0; i < count; i++)
            {
                var at = 4 + i * DescriptorSize;
                var flags = BitConverter.ToInt32(bytes, at + FlagsOffset);
                var name = Encoding.Unicode.GetString(bytes, at + NameOffset, NameChars * 2);
                var end = name.IndexOf('\0', StringComparison.Ordinal);
                name = end < 0 ? name : name[..end];

                // A folder in a virtual drag comes with its contents as further entries, named by
                // path. Flattening them would scatter a tree into one folder; not done yet.
                if ((flags & FdAttributes) != 0
                    && (BitConverter.ToInt32(bytes, at + AttributesOffset) & FileAttributeDirectory) != 0)
                {
                    problem = "Folders dragged out of another program cannot be dropped here yet.";
                    return null;
                }

                long? size = (flags & FdFileSize) != 0
                    ? ((long)(uint)BitConverter.ToInt32(bytes, at + SizeHighOffset) << 32)
                      | (uint)BitConverter.ToInt32(bytes, at + SizeLowOffset)
                    : null;

                // Nothing at all where something was promised is a read that failed, not a file.
                if (ReadContents(com, i, size) is not { } content || (content.Length == 0 && size > 0))
                {
                    problem = $"The program did not hand over the contents of {name}. "
                              + "An Outlook message itself cannot be dropped yet, only its attachments.";
                    return null;
                }

                files.Add(new VirtualFile(name, content, size));
            }

            // How many the program offered, by name: a drag of ten that wrote one has to be told
            // apart from a program that offered one (2026-10-13).
            Serilog.Log.Information(
                "Drop of files with no paths: {Count} offered: {Names}", count, string.Join(" | ", files.Select(f => f.Name)));
            return files;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Files a program rendered to paths of its own only at the drop (new Outlook downloads an
    /// attachment to a temporary folder). Read whole, now: the program may clean that folder up as
    /// soon as the drag is over, and the copy written from these bytes must not depend on it.
    /// All or nothing, as <see cref="Read"/>.
    /// </summary>
    public static IReadOnlyList<VirtualFile>? ReadPaths(IReadOnlyList<string> paths, out string? problem)
    {
        problem = null;

        // Looked at before anything is read: this runs on the UI thread during the drop, so a
        // device, a placeholder that downloads on open, or gigabytes would hang both programs.
        long total = 0;
        foreach (var path in paths)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists)
                {
                    problem = Directory.Exists(path)
                        ? "Folders dragged out of another program cannot be dropped here yet."
                        : $"{Path.GetFileName(path)} is not there.";
                    return null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                problem = $"{Path.GetFileName(path)}: {ex.Message}";
                return null;
            }

            if ((info.Attributes & (FileAttributes.Device | FileAttributes.Offline | FileAttributes.ReparsePoint)) != 0
                || (info.Attributes & (FileAttributes)RecallOnDataAccess) != 0)
            {
                problem = $"{info.Name} is not an ordinary file on this disk.";
                return null;
            }

            total += info.Length;
        }

        if (total > MaxReadAtDrop)
        {
            problem = $"That is {total / (1024 * 1024):N0} MB, too much to take in one drop from another program. Save it from there instead.";
            return null;
        }

        var files = new List<VirtualFile>(paths.Count);
        foreach (var path in paths)
        {
            try
            {
                var content = File.ReadAllBytes(path);
                files.Add(new VirtualFile(Path.GetFileName(path), content, new FileInfo(path).Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
            {
                problem = $"{Path.GetFileName(path)}: {ex.Message}";
                return null;
            }
        }

        return files.Count > 0 ? files : null;
    }

    /// <summary>
    /// Begins taking a drop the way Explorer does with a program that renders its files late:
    /// told the target will ask in its own time, Chromium (new Outlook) downloads the attachment
    /// and then answers the file list, which it refuses while its drag is still going. Null when the
    /// source doesn't offer this, or its data object can't be reached under WPF's wrapper.
    /// Must be called inside the Drop handler; <see cref="AsyncDrop.End"/> must follow.
    /// </summary>
    public static AsyncDrop? StartAsync(IDataObject data)
    {
        IDataObjectAsyncCapability operation;
        try
        {
            if (ComObjectUnder(data, 0) is not IDataObjectAsyncCapability found)
                return null;

            found.GetAsyncMode(out var isAsync);
            if (!isAsync)
                return null;
            operation = found;
        }
        catch (Exception)
        {
            // Nothing promised yet, so anything here (reflection included) only means asking the
            // other way, after the drop.
            return null;
        }

        try
        {
            operation.StartOperation(IntPtr.Zero);
            return new AsyncDrop(operation);
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// The other program's own data object, under WPF's wrapper. Found by looking rather than by
    /// field name: the wrapper's insides differ between .NET versions (a Composition in 10, an
    /// OleConverter before), and a name that stopped matching would fail without a word.
    /// </summary>
    private static object? ComObjectUnder(object data, int depth)
    {
        if (Marshal.IsComObject(data))
            return data;
        if (depth >= 4)
            return null;

        const System.Reflection.BindingFlags Fields = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.DeclaredOnly;
        for (var type = data.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(Fields))
            {
                if (field.FieldType.IsValueType || field.FieldType == typeof(string))
                    continue;
                if (field.GetValue(data) is { } value && ComObjectUnder(value, depth + 1) is { } found)
                    return found;
            }
        }

        return null;
    }

    /// <summary>An asynchronous drop under way: the source waits to hear how it ended.</summary>
    public sealed class AsyncDrop
    {
        private const int DropEffectCopy = 1;
        private const int Failed = unchecked((int)0x80004005); // E_FAIL

        private readonly IDataObjectAsyncCapability _operation;
        private bool _ended;

        internal AsyncDrop(IDataObjectAsyncCapability operation) => _operation = operation;

        /// <summary>
        /// Tells the source the drop is over: a copy taken, or nothing. Never Move, which would let
        /// it delete its own; a source told nothing may wait for ever, so this runs on every path.
        /// </summary>
        public void End(bool taken)
        {
            if (_ended)
                return;
            _ended = true;
            try
            {
                _operation.EndOperation(taken ? 0 : Failed, IntPtr.Zero, taken ? DropEffectCopy : 0);
            }
            catch (Exception)
            {
                // The source has gone (a COM error, or its object already released): there is
                // no one left to tell, and this runs inside finally blocks that mustn't throw.
            }
        }
    }

    [ComImport]
    [Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDataObjectAsyncCapability
    {
        void SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool doOpAsync);

        void GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool isOpAsync);

        void StartOperation(IntPtr bindContext);

        void InOperation([MarshalAs(UnmanagedType.Bool)] out bool inAsyncOp);

        void EndOperation(int result, IntPtr bindContext, int effects);
    }

    /// <summary>
    /// Marks a file written from a drop as come from the internet, as Outlook and Explorer do when
    /// they save an attachment. Without it a macro document opens outside Protected View and a
    /// program runs without SmartScreen's question: an email attachment is the classic carrier.
    /// </summary>
    /// <returns>False when the disk can't hold the mark (FAT, some shares).</returns>
    public static bool MarkFromInternet(string path)
    {
        try
        {
            // A stream of the file's own, new: CreateNew, so a mark already there stays.
            using var mark = new FileStream(path + ":Zone.Identifier", FileMode.CreateNew, FileAccess.Write);
            mark.Write(Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\n"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static byte[]? ReadContents(ComTypes.IDataObject com, int index, long? size)
    {
        var format = new ComTypes.FORMATETC
        {
            cfFormat = unchecked((short)DataFormats.GetDataFormat(ContentsFormat).Id),
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            ptd = IntPtr.Zero,
            // Outlook gives an attachment as a stream, and a message as storage, which isn't asked for.
            tymed = ComTypes.TYMED.TYMED_ISTREAM | ComTypes.TYMED.TYMED_HGLOBAL,
        };

        ComTypes.STGMEDIUM medium;
        try
        {
            com.GetData(ref format, out medium);
        }
        catch (COMException)
        {
            // An Outlook message refuses both: it only comes as storage.
            return null;
        }

        try
        {
            return medium.tymed switch
            {
                ComTypes.TYMED.TYMED_HGLOBAL => ReadGlobal(medium.unionmember, size),
                ComTypes.TYMED.TYMED_ISTREAM => ReadStream(medium.unionmember),
                _ => null,
            };
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static byte[]? ReadGlobal(IntPtr global, long? size)
    {
        // The block can be larger than the file, rounded up by the allocator; the descriptor's
        // size is the file's, where it gives one.
        var length = (long)GlobalSize(global);
        if (size is { } known && known <= length)
            length = known;

        var pointer = GlobalLock(global);
        if (pointer == IntPtr.Zero)
            return null;

        try
        {
            var content = new byte[length];
            Marshal.Copy(pointer, content, 0, content.Length);
            return content;
        }
        finally
        {
            GlobalUnlock(global);
        }
    }

    private static byte[] ReadStream(IntPtr unknown)
    {
        var stream = (ComTypes.IStream)Marshal.GetObjectForIUnknown(unknown);
        var read = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            // Some sources hand over a stream left at its end from writing it.
            try
            {
                stream.Seek(0, 0 /* STREAM_SEEK_SET */, IntPtr.Zero);
            }
            catch (COMException)
            {
                // Not seekable: it is read from wherever it is, which for these is the start.
            }

            using var content = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                stream.Read(buffer, buffer.Length, read);
                var got = Marshal.ReadInt32(read);
                if (got <= 0)
                    break;
                content.Write(buffer, 0, got);
            }

            return content.ToArray();
        }
        finally
        {
            Marshal.FreeCoTaskMem(read);
            Marshal.ReleaseComObject(stream);
        }
    }

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr memory);
}
