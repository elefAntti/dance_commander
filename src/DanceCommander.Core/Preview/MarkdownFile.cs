namespace DanceCommander.Core.Preview;

/// <summary>
/// What the preview needs to know about a Markdown file before it renders one: whether the name
/// says it is Markdown, and which of the images it names may be read to show it.
/// </summary>
public static class MarkdownFile
{
    private static readonly string[] Extensions = [".md", ".markdown", ".mdown", ".mkd"];

    public static bool IsMarkdown(string name) =>
        Extensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The local file an image reference points at, or null when it may not be read.
    /// </summary>
    /// <remarks>
    /// Only a relative path beside a file on a local drive. A preview never goes to the network:
    /// an http image would be fetched by a keystroke, and a <c>\\server\share</c> one would make
    /// Windows sign in to that server with the user's credentials just because a file named it.
    /// The same is why a file whose own folder is a UNC path shows no images at all. A rooted or
    /// drive path is left out too, so what a preview reads stays near the file being looked at.
    /// </remarks>
    /// <param name="folder">The Markdown file's folder as a local path, or null when it is not on a local drive.</param>
    /// <param name="url">The image's address as written in the file.</param>
    public static string? ResolveImage(string? folder, string? url)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(url) || IsUnc(folder))
            return null;

        // The address as a path: what follows ? or # is not part of the name.
        var path = url.Trim();
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
            path = path[..cut];

        try
        {
            path = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return null;
        }

        // A scheme (http:, file:, data:) or a drive letter, a rooted path, or a UNC one.
        if (path.Length == 0 || path.Contains(':') || path[0] is '/' or '\\')
            return null;

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(folder, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return IsUnc(full) ? null : full;
    }

    private static bool IsUnc(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
}
