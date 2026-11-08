using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// Windows' per-folder case sensitivity, as WSL sets on the folders it makes under /mnt/c: there
/// Readme.md and README.md are two files. Set by Windows' own fsutil, found by its full path.
/// </summary>
internal static class CaseSensitiveFolder
{
    /// <summary>Whether this Windows can make one: it needs WSL's support, not WSL itself.</summary>
    public static readonly Lazy<bool> Possible = new(() =>
    {
        var probe = Directory.CreateTempSubdirectory("dc-case-").FullName;
        try
        {
            return TryMake(probe);
        }
        finally
        {
            Directory.Delete(probe);
        }
    });

    public static void Make(string folder) =>
        TryMake(folder).ShouldBeTrue($"fsutil could not make {folder} case-sensitive");

    private static bool TryMake(string folder)
    {
        var fsutil = Path.Combine(Environment.SystemDirectory, "fsutil.exe");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fsutil)
        {
            ArgumentList = { "file", "setCaseSensitiveInfo", folder, "enable" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }
}

/// <summary>Runs only where a folder can be made case-sensitive, and says why it didn't elsewhere.</summary>
public sealed class CaseSensitiveFactAttribute : FactAttribute
{
    public CaseSensitiveFactAttribute()
    {
        if (!CaseSensitiveFolder.Possible.Value)
            Skip = "This Windows can't make a folder case-sensitive (fsutil file setCaseSensitiveInfo needs WSL's support).";
    }
}
