using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DanceCommander.App.Services;
using DanceCommander.Core.Preview;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfList = System.Windows.Documents.List;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace DanceCommander.App.Views;

/// <summary>
/// Turns parsed Markdown into a native document drawn with the app's own tokens: the same type,
/// colours and spacing as everything around it, in both schemes, with no second renderer
/// (DESIGN.md §1.4). Headings keep to the three type sizes and stand out by weight.
/// </summary>
/// <remarks>
/// Raw HTML is shown as the text it is, not run; links say where they go and are not followed;
/// images are read only from beside a file on a local drive (MarkdownFile.ResolveImage).
/// </remarks>
internal static class MarkdownFlow
{
    /// <summary>An image has to be read whole to decode; past this it stays its alt text.</summary>
    private const long ImageCap = 32L << 20;

    public static FlowDocument Build(MarkdownText markdown)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(4, 0, 16, 12),
            TextAlignment = TextAlignment.Left,
            Background = Brushes.Transparent,
        };
        document.SetResourceReference(FlowDocument.FontFamilyProperty, "UiFontFamily");
        document.SetResourceReference(FlowDocument.FontSizeProperty, "FontSizeBody");
        document.SetResourceReference(FlowDocument.ForegroundProperty, "TextPrimaryBrush");

        var builder = new Builder(markdown);
        foreach (var block in markdown.Document)
        {
            if (builder.Block(block) is { } made)
                document.Blocks.Add(made);
        }

        return document;
    }

    private sealed class Builder(MarkdownText markdown)
    {
        public WpfBlock? Block(Markdig.Syntax.Block block) => block switch
        {
            HeadingBlock heading => Heading(heading),
            ParagraphBlock paragraph => Paragraph(paragraph),
            QuoteBlock quote => Quote(quote),
            ListBlock list => List(list),
            MdTable table => Table(table),
            HtmlBlock html => Raw(html.Lines.ToString()),
            CodeBlock code => Code(code.Lines.ToString()),
            ThematicBreakBlock => Rule(),
            LinkReferenceDefinitionGroup => null,
            ContainerBlock container => Section(container),
            _ => Raw(SourceOf(block)),
        };

        private Paragraph Heading(HeadingBlock heading)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, heading.Level <= 2 ? 14 : 10, 0, 6) };
            paragraph.SetResourceReference(TextElement.FontSizeProperty, heading.Level <= 2 ? "FontSizeTitle" : "FontSizeBody");
            paragraph.FontWeight = heading.Level is 1 or 3 ? FontWeights.Bold : FontWeights.SemiBold;
            AddInlines(paragraph.Inlines, heading.Inline);
            return paragraph;
        }

        private WpfBlock Paragraph(ParagraphBlock block)
        {
            // A paragraph that is only a picture gets the width of the page, scaled down to fit:
            // screenshots in a README are the common case.
            if (block.Inline is { } inlines && OnlyImages(inlines) is { Count: > 0 } images)
            {
                var section = new Section { Margin = new Thickness(0, 0, 0, 10) };
                foreach (var image in images)
                    section.Blocks.Add(new BlockUIContainer(Picture(image, inline: false)));
                return section;
            }

            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
            AddInlines(paragraph.Inlines, block.Inline);
            return paragraph;
        }

        private Section Quote(QuoteBlock quote)
        {
            var section = Section(quote);
            section.BorderThickness = new Thickness(3, 0, 0, 0);
            section.Padding = new Thickness(10, 0, 0, 0);
            section.Margin = new Thickness(0, 0, 0, 10);
            section.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush");
            section.SetResourceReference(WpfBlock.BorderBrushProperty, "DividerBrush");
            return section;
        }

        private Section Section(ContainerBlock container)
        {
            var section = new Section();
            foreach (var child in container)
            {
                if (Block(child) is { } made)
                    section.Blocks.Add(made);
            }

            return section;
        }

        private WpfList List(ListBlock block)
        {
            var list = new WpfList
            {
                MarkerStyle = block.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(22, 0, 0, 0),
            };
            if (block.IsOrdered && int.TryParse(block.OrderedStart, out var start) && start > 0)
                list.StartIndex = start;

            foreach (var child in block)
            {
                var item = new ListItem();
                if (child is ContainerBlock container)
                {
                    foreach (var inner in container)
                    {
                        if (Block(inner) is not { } made)
                            continue;

                        // Tight lists: the paragraph inside an item doesn't push the next one away.
                        if (made is Paragraph paragraph)
                            paragraph.Margin = new Thickness(0, 0, 0, 2);
                        item.Blocks.Add(made);
                    }
                }

                list.ListItems.Add(item);
            }

            return list;
        }

        private WpfTable Table(MdTable block)
        {
            var table = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 10) };
            var columns = block.ColumnDefinitions.Count;
            for (var i = 0; i < columns; i++)
                table.Columns.Add(new TableColumn { Width = GridLength.Auto });

            var group = new TableRowGroup();
            foreach (var child in block)
            {
                if (child is not MdTableRow mdRow)
                    continue;

                var row = new WpfTableRow();
                if (mdRow.IsHeader)
                    row.FontWeight = FontWeights.SemiBold;

                var column = 0;
                foreach (var cellBlock in mdRow)
                {
                    if (cellBlock is not MdTableCell mdCell)
                        continue;

                    var cell = new WpfTableCell
                    {
                        Padding = new Thickness(0, 3, 16, 3),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                    };
                    cell.SetResourceReference(WpfTableCell.BorderBrushProperty, "DividerBrush");

                    var alignment = column < block.ColumnDefinitions.Count
                        ? block.ColumnDefinitions[column].Alignment
                        : null;
                    foreach (var inner in mdCell)
                    {
                        if (Block(inner) is not { } made)
                            continue;
                        made.Margin = new Thickness(0);
                        made.TextAlignment = alignment switch
                        {
                            TableColumnAlign.Right => TextAlignment.Right,
                            TableColumnAlign.Center => TextAlignment.Center,
                            _ => TextAlignment.Left,
                        };
                        cell.Blocks.Add(made);
                    }

                    row.Cells.Add(cell);
                    column++;
                }

                group.Rows.Add(row);
            }

            table.RowGroups.Add(group);
            return table;
        }

        private static Paragraph Code(string text)
        {
            var paragraph = new Paragraph(new Run(text.TrimEnd('\r', '\n')))
            {
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 10),
            };
            paragraph.SetResourceReference(TextElement.FontFamilyProperty, "MonoFontFamily");
            paragraph.SetResourceReference(TextElement.FontSizeProperty, "FontSizeSmall");
            paragraph.SetResourceReference(TextElement.BackgroundProperty, "SunkenBrush");
            return paragraph;
        }

        /// <summary>HTML, and anything else this doesn't draw, as the text it is.</summary>
        private static Paragraph Raw(string text)
        {
            var paragraph = Code(text);
            paragraph.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush");
            return paragraph;
        }

        private static Paragraph Rule()
        {
            var paragraph = new Paragraph
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                Margin = new Thickness(0, 4, 0, 14),
                LineHeight = 1,
            };
            paragraph.SetResourceReference(WpfBlock.BorderBrushProperty, "DividerBrush");
            return paragraph;
        }

        private string SourceOf(Markdig.Syntax.Block block) =>
            block.Span.Start >= 0 && block.Span.End < markdown.Source.Length && block.Span.Length > 0
                ? markdown.Source.Substring(block.Span.Start, block.Span.Length)
                : string.Empty;

        private void AddInlines(InlineCollection target, ContainerInline? container)
        {
            if (container is null)
                return;

            foreach (var inline in container)
            {
                if (Inline(inline) is { } made)
                    target.Add(made);
            }
        }

        private WpfInline? Inline(Markdig.Syntax.Inlines.Inline inline)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    return new Run(literal.Content.ToString());

                case CodeInline code:
                {
                    var run = new Run(code.Content);
                    run.SetResourceReference(TextElement.FontFamilyProperty, "MonoFontFamily");
                    run.SetResourceReference(TextElement.BackgroundProperty, "SunkenBrush");
                    return run;
                }

                case LineBreakInline lineBreak:
                    return lineBreak.IsHard ? new LineBreak() : new Run(" ");

                case TaskList task:
                    return new Run(task.Checked ? "☑ " : "☐ ");

                case HtmlEntityInline entity:
                    return new Run(entity.Transcoded.ToString());

                case HtmlInline html:
                {
                    var run = new Run(html.Tag);
                    run.SetResourceReference(TextElement.ForegroundProperty, "TextMutedBrush");
                    return run;
                }

                case AutolinkInline auto:
                    return Link(new Span(new Run(auto.Url)), auto.Url);

                case LinkInline { IsImage: true } image:
                    return new InlineUIContainer(Picture(image, inline: true)) { BaselineAlignment = BaselineAlignment.Bottom };

                case LinkInline link:
                {
                    var span = new Span();
                    AddInlines(span.Inlines, link);
                    return Link(span, link.Url);
                }

                case EmphasisInline emphasis:
                {
                    Span span = emphasis.DelimiterChar switch
                    {
                        '*' or '_' when emphasis.DelimiterCount >= 2 => new Bold(),
                        '*' or '_' => new Italic(),
                        _ => new Span(),
                    };
                    if (emphasis.DelimiterChar == '~')
                        span.TextDecorations = TextDecorations.Strikethrough;
                    AddInlines(span.Inlines, emphasis);
                    return span;
                }

                case ContainerInline container:
                {
                    var span = new Span();
                    AddInlines(span.Inlines, container);
                    return span;
                }

                default:
                    return null;
            }
        }

        /// <summary>
        /// A link looks like one and says where it goes, but isn't followed: a click in a
        /// preview would open whatever the file says.
        /// </summary>
        private static Span Link(Span span, string? url)
        {
            span.TextDecorations = TextDecorations.Underline;
            span.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            if (!string.IsNullOrEmpty(url))
                span.ToolTip = url;
            return span;
        }

        /// <summary>
        /// The image, read off the UI thread when it may be read, standing as its alt text until
        /// then and for good when it can't be shown.
        /// </summary>
        private FrameworkElement Picture(LinkInline image, bool inline)
        {
            var alt = PlainText(image);
            var placeholder = new TextBlock
            {
                Text = alt.Length > 0 ? alt : "image",
                TextWrapping = TextWrapping.Wrap,
                ToolTip = image.Url,
            };
            placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");

            var host = new ContentControl { Content = placeholder, Focusable = false, IsTabStop = false };
            if (MarkdownFile.ResolveImage(markdown.Folder, image.Url) is not { } path)
                return host;

            _ = LoadAsync(host, path, alt, inline);
            return host;
        }

        private static async Task LoadAsync(ContentControl host, string path, string alt, bool inline)
        {
            var bitmap = await Task.Run(() => Decode(path)).ConfigureAwait(true);
            if (bitmap is null)
                return;

            host.Content = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxHeight = inline ? 160 : double.PositiveInfinity,
                ToolTip = alt.Length > 0 ? alt : null,
            };
            RenderOptions.SetBitmapScalingMode(host, BitmapScalingMode.HighQuality);
        }

        private static BitmapSource? Decode(string path)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > ImageCap)
                    return null;

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // Not there, not ours to read, or not a picture WPF decodes (SVG): the alt text stays.
                return null;
            }
        }

        private static List<LinkInline>? OnlyImages(ContainerInline inlines)
        {
            var images = new List<LinkInline>();
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case LinkInline { IsImage: true } image:
                        images.Add(image);
                        break;
                    case LineBreakInline:
                        break;
                    case LiteralInline literal when literal.Content.IsEmptyOrWhitespace():
                        break;
                    default:
                        return null;
                }
            }

            return images;
        }

        private static string PlainText(ContainerInline container)
        {
            var text = new StringBuilder();
            foreach (var inline in container.Descendants<LiteralInline>())
                text.Append(inline.Content.ToString());
            return text.ToString();
        }
    }
}
