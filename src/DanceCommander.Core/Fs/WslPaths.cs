using Microsoft.Win32;

namespace DanceCommander.Core.Fs;

/// <summary>
/// WSL's distributions as Windows sees them: each one's files are under
/// <c>\\wsl.localhost\&lt;name&gt;\</c>, or the older <c>\\wsl$\&lt;name&gt;\</c>, and what is
/// installed is registered under Lxss, one subkey per distribution. Reading either never starts a
/// distribution; only opening a file under the share does.
/// </summary>
public static class WslPaths
{
    private const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    private static readonly string[] Hosts = ["wsl.localhost", "wsl$"];

    /// <summary>
    /// The distribution and the Linux path of a Windows path inside one:
    /// <c>\\wsl.localhost\Debian\home\x</c> is <c>Debian</c> and <c>/home/x</c>. False for any
    /// other path, a share of some other server included.
    /// </summary>
    public static bool TrySplit(string windowsPath, out string distribution, out string linuxPath)
    {
        distribution = linuxPath = string.Empty;

        // As ToLocalPath writes a long one, and as a path from FsUri's own form.
        var path = windowsPath.Replace('/', '\\');
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[8..];
        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;

        var parts = path[2..].Split('\\', 3);
        if (parts.Length < 2 || parts[1].Length == 0 ||
            !Hosts.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
            return false;

        distribution = parts[1];
        linuxPath = "/" + (parts.Length > 2 ? parts[2].Replace('\\', '/').TrimEnd('/') : string.Empty);
        return true;
    }

    /// <summary>
    /// The distribution <c>wsl.exe</c> opens when not told which, or null with none recorded.
    /// Not the first one registered instead: that can be Docker Desktop's own, where a shell is
    /// root in Docker's machinery rather than the user's Linux.
    /// </summary>
    public static string? DefaultDistribution()
    {
        try
        {
            using var lxss = Registry.CurrentUser.OpenSubKey(LxssKey);
            if (lxss is null)
                return null;

            if (lxss.GetValue("DefaultDistribution") is not string { Length: > 0 } id)
                return null;

            using var distribution = lxss.OpenSubKey(id);
            return distribution?.GetValue("DistributionName") as string is { Length: > 0 } name ? name : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
