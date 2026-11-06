using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DanceCommander.Core.Security;

/// <summary>
/// Windows Credential Manager, under targets named <c>DanceCommander:&lt;key&gt;</c> (§7).
/// </summary>
/// <remarks>
/// Generic credentials rather than domain ones, persisted to the local machine profile so they
/// survive a reboot. Using the OS vault rather than a file of our own is the point: it is
/// encrypted per-user by the platform, it is visible in <c>rundll32 keymgr.dll</c> and
/// <c>cmdkey</c>, and a user who wants a saved password gone can remove it without us.
/// </remarks>
public sealed class WindowsCredentialStore(ILogger<WindowsCredentialStore> log) : ICredentialStore
{
    private const string Prefix = "DanceCommander:";

    public string? Read(string key)
    {
        if (!CredRead(Prefix + key, CRED_TYPE_GENERIC, 0, out var handle))
            return null;

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return null;

            // The blob is raw bytes, not a string: we wrote UTF-16, so read it back as UTF-16.
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not read the stored credential for {Key}", key);
            return null;
        }
        finally
        {
            CredFree(handle);
        }
    }

    public void Write(string key, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var blobHandle = Marshal.AllocHGlobal(blob.Length);

        try
        {
            Marshal.Copy(blob, 0, blobHandle, blob.Length);

            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Prefix + key,
                CredentialBlob = blobHandle,
                CredentialBlobSize = (uint)blob.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = key,
            };

            if (!CredWrite(ref credential, 0))
            {
                // Worth a line in the log, but not worth interrupting the user: the operation they
                // asked for succeeded, and only "remember this" failed.
                log.LogWarning(
                    "Could not save the credential for {Key} (error {Error})",
                    key, Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            // The secret must not linger in unmanaged memory once the OS has copied it. Cleared
            // by copying the cleared array over it, exactly its own length: ZeroFreeGlobalAllocUnicode
            // measures a NUL-terminated string, and this blob has no terminator, so it zeroed on
            // past the end of the allocation into whatever the heap kept there.
            Array.Clear(blob);
            Marshal.Copy(blob, 0, blobHandle, blob.Length);
            Marshal.FreeHGlobal(blobHandle);
        }
    }

    public void Remove(string key)
    {
        if (!CredDelete(Prefix + key, CRED_TYPE_GENERIC, 0))
            log.LogDebug("No stored credential to remove for {Key}", key);
    }

    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
