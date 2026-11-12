namespace DanceCommander.Core.Preview;

/// <summary>The few colours a highlighted preview uses; everything else is plain text.</summary>
public enum SyntaxKind
{
    Plain,
    Comment,
    Keyword,
    String,
    Number,

    /// <summary>What names a thing: a markup tag or attribute, a called function, a heading.</summary>
    Name,
}

/// <summary>
/// Folds the highlighting definitions' own colour names onto <see cref="SyntaxKind"/>.
/// </summary>
/// <remarks>
/// The definitions that come with the editor name around thirty colours per language, picked for
/// a white background. The palette has five (DESIGN.md §1.3: near-monochrome, the user owns
/// colour), so the names are read for what they mean and anything unrecognised stays plain:
/// a missed name costs a colour, never readability.
/// </remarks>
public static class SyntaxColours
{
    public static SyntaxKind KindOf(string? colourName)
    {
        if (string.IsNullOrEmpty(colourName))
            return SyntaxKind.Plain;

        var name = colourName.ToLowerInvariant();

        // Order matters: "DocComment" is a comment before it is anything else, and an XML
        // "AttributeValue" is a string although it also says "attribute".
        if (name.Contains("comment") || name == "xmldoc")
            return SyntaxKind.Comment;
        if (name.Contains("string") || name.Contains("char") || name.Contains("attributevalue")
            || name.Contains("regex") || name.Contains("cdata") || name == "code")
            return SyntaxKind.String;
        if (name.Contains("number") || name.Contains("digit") || name.Contains("numeric"))
            return SyntaxKind.Number;
        if (name.Contains("keyword") || name.Contains("modifier") || name.Contains("visibility")
            || name.Contains("truefalse") || name.Contains("preprocessor") || name.Contains("declaration")
            || name.Contains("doctype") || name == "this" || name.Contains("thisorbase"))
            return SyntaxKind.Keyword;
        if (name.Contains("tag") || name.Contains("attributename") || name.Contains("methodcall")
            || name.Contains("function") || name.Contains("heading") || name.Contains("selector")
            || name.Contains("property") || name.Contains("variable") || name.Contains("command"))
            return SyntaxKind.Name;

        return SyntaxKind.Plain;
    }
}
