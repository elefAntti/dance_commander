namespace DanceCommander.Core.Transfer;

public enum ExecutableKind
{
    None,

    /// <summary>An ELF executable or shared library.</summary>
    Binary,

    /// <summary>A file that starts with "#!".</summary>
    Script,

    /// <summary>A "#!" file whose first line ends in CR LF, which the kernel will not run.</summary>
    ScriptWithWindowsLineEndings,
}

/// <summary>
/// Whether a file is meant to be run, read from its first bytes rather than its name (§6.8).
/// </summary>
/// <remarks>
/// A file written from Windows carries no execute bit, because Windows has none to give. What it
/// does carry is its content, and for the two kinds of file a Unix host runs directly that content
/// says so unambiguously: an ELF header of an executable type, or a "#!" line. Names cannot say it;
/// most binaries have no extension, and ".sh" is a habit, not a rule.
/// </remarks>
public static class ExecutableContent
{
    /// <summary>Enough for the ELF type field and a generous "#!" line.</summary>
    public const int HeadLength = 256;

    public static ExecutableKind Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 18 && head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F')
        {
            // e_type, in the byte order EI_DATA names. Executables are 2 and position-independent
            // executables and shared libraries both 3; object files (1) and core dumps (4) are ELF
            // too, and are not something anybody means to run.
            var type = head[5] == 2 ? (head[16] << 8) | head[17] : head[16] | (head[17] << 8);
            return type is 2 or 3 ? ExecutableKind.Binary : ExecutableKind.None;
        }

        if (head.Length >= 2 && head[0] == (byte)'#' && head[1] == (byte)'!')
        {
            // The kernel reads the interpreter up to the newline, so a CR before it becomes part of
            // the interpreter's name: "/bin/sh\r: bad interpreter". A long line that runs past the
            // head is left alone rather than guessed at.
            var newline = head.IndexOf((byte)'\n');
            return newline > 0 && head[newline - 1] == (byte)'\r'
                ? ExecutableKind.ScriptWithWindowsLineEndings
                : ExecutableKind.Script;
        }

        return ExecutableKind.None;
    }

    /// <summary>
    /// Execute added wherever read is allowed: 0644 becomes 0755, 0640 becomes 0750. What
    /// chmod +x does, so the server's umask still decides who may see the file at all.
    /// </summary>
    public static int WithExecute(int mode) => mode | ((mode & 0x124) >> 2);

    /// <summary>
    /// A source's mode, limited to what the server allows a new file here, the way cp without -p
    /// gives a new file its source's bits less the umask. The new file's own default says what the
    /// umask is: 0644 means group and others may not write, so a source's 0777 lands as 0755.
    /// </summary>
    public static int Inherit(int source, int fresh) => source & (fresh | ((fresh & 0x124) >> 2));
}
