using System.IO;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;

namespace DanceCommander.App.Services;

/// <summary>
/// A previewed file's text, made into an editor document off the UI thread.
/// </summary>
/// <remarks>
/// Building the document of a megabyte is splitting it into lines, and loading a language's
/// highlighting is parsing its definition the first time it is used: both are done where the
/// file was read, so the UI thread only lays out what is on screen. A document belongs to the
/// thread that made it; this one belongs to none until <see cref="TakeDocument"/> claims it.
///
/// The only type that names the editor's own outside the view, and kept apart on purpose:
/// nothing at launch touches it, so the editor's assembly is loaded by the first text preview
/// and not by the window.
/// </remarks>
public sealed class CodeText
{
    /// <summary>
    /// Past this, a line is data rather than code (minified JSON, a one-line bundle). The
    /// highlighting rules are regular expressions run across the whole line, which there cost
    /// more than the colour is worth.
    /// </summary>
    private const int LongestHighlightedLine = 5000;

    /// <summary>Extensions the editor has no definition for, read as the nearest one it has.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ts"] = ".js", [".tsx"] = ".js", [".jsx"] = ".js", [".mjs"] = ".js", [".cjs"] = ".js",
        [".json"] = ".js", [".jsonc"] = ".js", [".code-workspace"] = ".js",
        [".csx"] = ".cs",
        [".props"] = ".xml", [".resx"] = ".xml", [".svg"] = ".xml", [".slnx"] = ".xml",
        [".runsettings"] = ".xml", [".ruleset"] = ".xml", [".axaml"] = ".xml", [".xlf"] = ".xml",
        [".plist"] = ".xml", [".vcxproj"] = ".xml", [".fsproj"] = ".xml", [".pubxml"] = ".xml",
        [".hxx"] = ".cpp", [".cxx"] = ".cpp", [".hh"] = ".cpp", [".ino"] = ".cpp", [".cu"] = ".cpp",
        [".pyi"] = ".py",
        [".markdown"] = ".md",
    };

    private readonly TextDocument _document;
    private Thread? _owner;

    private CodeText(TextDocument document, IHighlightingDefinition? highlighting)
    {
        _document = document;
        Highlighting = highlighting;
    }

    /// <summary>The colouring for the file's language, or null to show it plain.</summary>
    public IHighlightingDefinition? Highlighting { get; }

    /// <param name="longestLine">From the caller, which has measured it already.</param>
    public static CodeText Prepare(string text, string fileName, int longestLine)
    {
        var document = new TextDocument(text);
        document.SetOwnerThread(null);

        var highlighting = longestLine <= LongestHighlightedLine ? HighlightingFor(fileName) : null;
        return new CodeText(document, highlighting);
    }

    /// <summary>Hands the document to the calling thread, which from then on is the only one that may touch it.</summary>
    public TextDocument TakeDocument()
    {
        // Asked again when the same preview is shown again; by then the document is the UI
        // thread's already, and only its owner may pass it on.
        if (_owner != Thread.CurrentThread)
        {
            _document.SetOwnerThread(Thread.CurrentThread);
            _owner = Thread.CurrentThread;
        }

        return _document;
    }

    private static IHighlightingDefinition? HighlightingFor(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension))
            return null;

        var manager = HighlightingManager.Instance;
        return manager.GetDefinitionByExtension(extension)
            ?? (Aliases.TryGetValue(extension, out var alias) ? manager.GetDefinitionByExtension(alias) : null);
    }
}
