using Markdig;
using Markdig.Syntax;

namespace DanceCommander.App.Services;

/// <summary>
/// A Markdown file's text, parsed for the rendered preview. Made where the file was read, off the
/// UI thread; the view turns it into a document on it (MarkdownFlow).
/// </summary>
/// <remarks>
/// Its own class so that nothing else names a Markdig type: the library is loaded the first time
/// a Markdown file is previewed, never at launch or for any other preview.
/// </remarks>
public sealed class MarkdownText
{
    /// <summary>
    /// What GitHub-flavoured files use, and nothing that would need HTML or script to show:
    /// tables, task lists, strikethrough and bare links.
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    private MarkdownText(string source, MarkdownDocument document, string? folder)
    {
        Source = source;
        Document = document;
        Folder = folder;
    }

    public string Source { get; }

    public MarkdownDocument Document { get; }

    /// <summary>The file's folder as a local path, where its images are looked for; null when it is not on a local drive.</summary>
    public string? Folder { get; }

    public static MarkdownText Parse(string source, string? folder) =>
        new(source, Markdown.Parse(source, Pipeline), folder);
}
