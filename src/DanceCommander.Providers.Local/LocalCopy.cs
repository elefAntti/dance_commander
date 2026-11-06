using System.ComponentModel;
using System.Runtime.InteropServices;
using DanceCommander.Core.Fs;

namespace DanceCommander.Providers.Local;

/// <summary>
/// A copy from one local file to another by Windows' own <c>CopyFileEx</c>, into a <c>.part</c>
/// and then committed as any local write is.
/// </summary>
/// <remarks>
/// A copy through streams carries the bytes and nothing else: the file came out dated today, a
/// hidden file came out visible, and a downloaded program lost the mark that makes Windows warn
/// before running it. <c>CopyFileEx</c> is what Explorer uses, and it keeps the last-modified date,
/// attributes and alternate streams; permissions are the new folder's, and the creation date is
/// now, both as in Explorer. It also reports progress, which is where a cancel is answered: told
/// to stop, it removes the partial copy itself.
///
/// Never onto anything: it copies only to a <c>.part</c> name that is free, told to fail if that
/// name is taken, and the finished <c>.part</c> takes the real name by the same commit as
/// <see cref="LocalPartStream"/>.
/// </remarks>
internal static class LocalCopy
{
    private const uint CopyFileFailIfExists = 0x1;
    private const uint ProgressContinue = 0;
    private const uint ProgressCancel = 1;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorRequestAborted = 1235;

    public static Task<long> CopyAsync(
        string source, string target, bool replace, bool durable, Action<long> moved, CancellationToken ct, Func<bool>? mayReplace = null) =>
        Task.Run(() => Copy(source, target, replace, durable, moved, ct, mayReplace), CancellationToken.None);

    private static long Copy(
        string source, string target, bool replace, bool durable, Action<long> moved, CancellationToken ct, Func<bool>? mayReplace)
    {
        ct.ThrowIfCancellationRequested();

        var part = CopyToFreePart(source, target, moved, ct);
        try
        {
            var length = new FileInfo(part).Length;

            // On the disk before it takes its name when something would be lost without it: a file
            // it replaces, or, for a move, the source deleted next. Otherwise not, since a flush per
            // file is slow and a plain copy loses nothing in a power cut.
            var exists = File.Exists(target);
            if (exists && !replace)
                throw WriteStreams.Appeared(Path.GetFileName(target));
            if (exists || durable)
                FlushToDisk(part);

            // Asked after the flush, which takes seconds on a stick, right before the name is
            // taken (review, 2026-10-23); and nothing is written over that was not there to ask
            // about, as a file can land during the flush too.
            if (exists && mayReplace is not null && !mayReplace())
                throw new IOException(
                    $"{Path.GetFileName(target)} was put there by another transfer while this one was copying over it, so it was kept and this transfer stopped.");

            LocalPartStream.SwapIn(part, target, replace && exists);
            return length;
        }
        catch
        {
            RemoveOurPart(part);
            throw;
        }
    }

    /// <returns>The .part it copied to, whole.</returns>
    private static string CopyToFreePart(string source, string target, Action<long> moved, CancellationToken ct)
    {
        long reported = 0;

        // Only the file's own content is counted: the alternate streams come after it and would
        // take the bar past the end.
        uint Progress(long total, long done, long streamSize, long streamDone, uint stream, uint reason,
            IntPtr sourceHandle, IntPtr targetHandle, IntPtr data)
        {
            if (ct.IsCancellationRequested)
                return ProgressCancel;

            if (stream == 1 && streamDone > reported)
            {
                // Inside Windows' own call: an exception thrown back through it would take the
                // process down, so a listener that fails is not allowed to.
                try
                {
                    moved(streamDone - reported);
                }
                catch (Exception)
                {
                }

                reported = streamDone;
            }

            return ProgressContinue;
        }

        CopyProgressRoutine routine = Progress;
        var cancel = 0;
        foreach (var part in LocalPartStream.PartNames(target))
        {
            // A name already taken is someone else's (a browser's download in progress, say), and
            // is not tried at all: what is at a name tried below is then ours to clean up.
            if (File.Exists(part) || Directory.Exists(part))
                continue;

            reported = 0;
            if (CopyFileEx(source, part, routine, IntPtr.Zero, ref cancel, CopyFileFailIfExists))
            {
                GC.KeepAlive(routine);
                return part;
            }

            var error = Marshal.GetLastWin32Error();
            if (error is ErrorFileExists or ErrorAlreadyExists)
                continue; // taken in the moment since the check: someone else's, never touched

            // Failed or cancelled: CopyFileEx removes what it had written itself. Whatever is at the
            // name now may be another program's, made in the moment since it was found free, so it
            // is left alone; at worst, debris of ours with an obvious name.
            if (error == ErrorRequestAborted)
                throw new OperationCanceledException(ct);

            throw new IOException(
                $"Copying {Path.GetFileName(source)} failed: {new Win32Exception(error).Message}",
                unchecked((int)0x80070000) | error);
        }

        throw new IOException($"Could not find a free name for copying {Path.GetFileName(target)}.");
    }

    /// <summary>Flushes a finished copy, whose attributes may say read-only, as its source's did.</summary>
    private static void FlushToDisk(string part)
    {
        var attributes = File.GetAttributes(part);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(part, attributes & ~FileAttributes.ReadOnly);
        try
        {
            using var stream = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(part, attributes);
        }
    }

    private static void RemoveOurPart(string part)
    {
        try
        {
            if (!File.Exists(part))
                return;

            // A copy of a read-only file is read-only, and can't be deleted until it isn't.
            File.SetAttributes(part, File.GetAttributes(part) & ~FileAttributes.ReadOnly);
#pragma warning disable RS0030 // §1.6: our own .part, at a name CopyFileEx found free (it fails on one that isn't); the target is untouched.
            File.Delete(part);
#pragma warning restore RS0030
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Debris with an obvious name; the caller is already reporting what went wrong.
        }
    }

    private delegate uint CopyProgressRoutine(
        long totalFileSize, long totalBytesTransferred, long streamSize, long streamBytesTransferred,
        uint streamNumber, uint callbackReason, IntPtr sourceFile, IntPtr destinationFile, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(
        string existingFileName, string newFileName, CopyProgressRoutine progressRoutine, IntPtr data,
        ref int cancel, uint copyFlags);
}
