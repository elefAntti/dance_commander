using System.IO;
using System.Runtime.InteropServices;

namespace DanceCommander.App.Services;

/// <summary>
/// Writes to the terminal this windowed program was launched from, when there was one.
/// </summary>
/// <remarks>
/// A WPF application has no console of its own, which is why the usual answer to <c>--help</c> in
/// one is a message box. This application does not have message boxes (§1.2/7), and a dialog would
/// be the wrong answer anyway: somebody who typed <c>--help</c> at a prompt wants text at that
/// prompt, where they can scroll it, pipe it and copy from it.
///
/// So we borrow the parent's console if it has one. When it has not -- launched from Explorer, or
/// from a shortcut -- this reports failure and the caller shows the text on screen instead.
/// </remarks>
internal static class ConsoleEcho
{
    private const int ParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    public static bool TryWrite(string text)
    {
        try
        {
            // Redirected first: "DanceCommander --help > usage.txt" hands this process a real
            // handle at startup and needs no console at all. Only an interactive prompt leaves
            // standard output bound to nothing, and that is the case AttachConsole is for.
            if (Console.OpenStandardOutput() is { } redirected && redirected != Stream.Null)
                return Write(redirected, text);

            if (!AttachConsole(ParentProcess))
                return false;

            // The handle was bound to nothing when the process started without a console, so it
            // has to be reopened now that there is one to bind it to.
            return Write(Console.OpenStandardOutput(), text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool Write(Stream stream, string text)
    {
        if (stream == Stream.Null)
            return false;

        using var writer = new StreamWriter(stream) { AutoFlush = true };
        writer.WriteLine();
        writer.WriteLine(text);
        return true;
    }
}
