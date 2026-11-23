using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Paperleaf
{
    internal static class MarkdownCodec
    {
        private static readonly Regex InlinePattern = new Regex(@"\\[\\`*_{}\[\]<>!#|~>+\-.]|!\[(?<alt>(?:\\.|[^\]\\])*)\]\((?<src>[^\s)]+)(?:\s+""[^""]*"")?\)|\[(?<label>(?:\\.|[^\]\\])*)\]\((?<href>[^\s)]+)\)|(?<ticks>`+)(?<code>[\s\S]*?)\k<ticks>|\*\*\*(?<both>.+?)\*\*\*|\*\*(?<bold>.+?)\*\*|\*(?!\s)(?<italic>.+?)\*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        public static void Export(FlowDocument document, string markdownPath)
        {
            markdownPath = Path.GetFullPath(markdownPath);
            string assetName = Path.GetFileNameWithoutExtension(markdownPath) + ".assets";
            string assetDirectory = Path.Combine(Path.GetDirectoryName(markdownPath), assetName);
            Func<Image, string> saveImage = delegate(Image image)
            {
                byte[] bytes = DocumentCodec.ImageBytes(image);
                string name = DocumentCodec.Hash(bytes) + ".png";
                Directory.CreateDirectory(assetDirectory);
                string destination = Path.Combine(assetDirectory, name);
                // Content-addressed names make repeated exports and duplicate images safe.
                if (!File.Exists(destination)) File.WriteAllBytes(destination, bytes);
                else if (!File.ReadAllBytes(destination).SequenceEqual(bytes)) throw new IOException("图片导出目录中存在同名但内容不同的文件：" + destination);
                return Uri.EscapeDataString(assetName) + "/" + name;
            };
            DocumentCodec.AtomicWrite(markdownPath, Write(document, saveImage));
        }

        public static string Write(FlowDocument document, Func<Image, string> imageSource)
        {
            return WriteBlocks(document.Blocks, imageSource).TrimEnd() + "\n";
        }

        private static string WriteBlocks(BlockCollection blocks, Func<Image, string> imageSource)
        {
            var result = new StringBuilder();
            foreach (Block block in blocks)
            {
                var p = block as Paragraph;
                if (p != null)
                {
                    string tag = DocumentCodec.ParagraphTag(p);
                    if (tag == "pre")
                    {
                        string text = DocumentCodec.InlineText(p.Inlines).Replace("\r\n", "\n");
                        string fence = "```";
                        while (text.Contains(fence)) fence += "`";
                        result.Append(fence).Append('\n').Append(text).Append('\n').Append(fence).Append("\n\n");
                        continue;
                    }
                    string line = WriteInlines(p.Inlines, imageSource, p);
                    if (tag == "h1") line = "# " + line;
                    if (tag == "h2") line = "## " + line;
                    if (tag == "h3") line = "### " + line;
                    if (tag == "blockquote") line = "> " + line.Replace("\n", "\n> ");
                    result.Append(line).Append("\n\n");
                }
                var section = block as Section;
                if (section != null) result.Append(WriteBlocks(section.Blocks, imageSource));
                var list = block as System.Windows.Documents.List;
                if (list != null)
                {
                    int index = list.StartIndex;
                    foreach (ListItem item in list.ListItems)
                    {
                        string marker = list.MarkerStyle == TextMarkerStyle.Decimal ? (index++).ToString() + ". " : "- ";
                        string indent = new string(' ', marker.Length);
                        string content = WriteBlocks(item.Blocks, imageSource).TrimEnd();
                        result.Append(marker).Append(content.Replace("\n", "\n" + indent)).Append('\n');
                    }
                    result.Append('\n');
                }
            }
            return result.ToString();
        }

        private static string WriteInlines(InlineCollection inlines, Func<Image, string> imageSource, TextElement parent)
        {
            var result = new StringBuilder();
            foreach (Inline inline in inlines)
            {
                string text = "";
                var run = inline as Run;
                var span = inline as Span;
                var container = inline as InlineUIContainer;
                if (run != null) text = Escape(run.Text);
                else if (inline is LineBreak) text = "  \n";
                else if (container != null && container.Child is Image)
                {
                    var image = (Image)container.Child;
                    text = "![" + Escape((image.Tag as string ?? "图片").Replace("\r", " ").Replace("\n", " ")) + "](" + imageSource(image) + ")";
                }
                else if (span != null) text = WriteInlines(span.Inlines, imageSource, inline);
                if (inline.Tag as string == "code")
                {
                    string raw = new TextRange(inline.ContentStart, inline.ContentEnd).Text;
                    string delimiter = "`";
                    while (raw.Contains(delimiter)) delimiter += "`";
                    text = delimiter + " " + raw + " " + delimiter;
                }
                else
                {
                    bool headingParent = parent is Paragraph && DocumentCodec.ParagraphTag((Paragraph)parent).StartsWith("h");
                    if (inline.FontWeight.ToOpenTypeWeight() >= 600 && parent.FontWeight.ToOpenTypeWeight() < 600 && !headingParent && !string.IsNullOrWhiteSpace(text)) text = Emphasize(text, "**");
                    if (inline.FontStyle == FontStyles.Italic && parent.FontStyle != FontStyles.Italic && !string.IsNullOrWhiteSpace(text)) text = Emphasize(text, "*");
                }
                var link = inline as Hyperlink;
                if (link != null && link.NavigateUri != null && DocumentCodec.SafeLink(link.NavigateUri.ToString()))
                    text = "[" + text + "](" + link.NavigateUri.ToString().Replace("(", "%28").Replace(")", "%29").Replace(" ", "%20") + ")";
                result.Append(text);
            }
            return result.ToString();
        }

        private static string Escape(string text)
        {
            return Regex.Replace(text.Replace("\r\n", "\n"), @"([\\`*_{}\[\]<>!#|~>+\-.])", @"\$1");
        }

        private static string Emphasize(string text, string marker)
        {
            int first = 0, last = text.Length;
            while (first < last && char.IsWhiteSpace(text[first])) first++;
            while (last > first && char.IsWhiteSpace(text[last - 1])) last--;
            return text.Substring(0, first) + marker + text.Substring(first, last - first) + marker + text.Substring(last);
        }

        public static FlowDocument Read(string markdown, string baseDirectory)
        {
            var document = DocumentCodec.Empty(); document.Blocks.Clear();
            string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            Paragraph paragraph = null;
            var listStack = new System.Collections.Generic.List<System.Windows.Documents.List>();
            var indentStack = new System.Collections.Generic.List<int>();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                var fence = Regex.Match(line, @"^\s*(`{3,}|~{3,})");
                if (fence.Success)
                {
                    paragraph = null; listStack.Clear(); indentStack.Clear();
                    var code = new StringBuilder();
                    bool firstLine = true;
                    string marker = fence.Groups[1].Value;
                    while (++i < lines.Length)
                    {
                        if (Regex.IsMatch(lines[i], @"^\s*" + Regex.Escape(marker[0].ToString()) + "{" + marker.Length + @",}\s*$")) break;
                        if (!firstLine) code.Append('\n');
                        firstLine = false;
                        code.Append(lines[i]);
                    }
                    var pre = DocumentCodec.Paragraph(code.ToString()); DocumentCodec.SetParagraphStyle(pre, "pre"); document.Blocks.Add(pre); continue;
                }
                if (string.IsNullOrWhiteSpace(line)) { paragraph = null; continue; }
                var itemMatch = Regex.Match(line, @"^(\s*)([-+*]|\d+\.)\s+(.*)$");
                if (itemMatch.Success)
                {
                    int indent = itemMatch.Groups[1].Value.Replace("\t", "    ").Length;
                    bool ordered = itemMatch.Groups[2].Value.EndsWith(".");
                    while (indentStack.Count > 0 && indent < indentStack[indentStack.Count - 1]) { indentStack.RemoveAt(indentStack.Count - 1); listStack.RemoveAt(listStack.Count - 1); }
                    if (listStack.Count == 0 || indent > indentStack[indentStack.Count - 1] || (listStack[listStack.Count - 1].MarkerStyle == TextMarkerStyle.Decimal) != ordered)
                    {
                        var list = new System.Windows.Documents.List { MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Padding = new Thickness(26, 0, 0, 0), Margin = new Thickness(0, 4, 0, 12) };
                        int start;
                        if (ordered && int.TryParse(itemMatch.Groups[2].Value.TrimEnd('.'), out start) && start > 0) list.StartIndex = start;
                        if (listStack.Count > 0 && indent > indentStack[indentStack.Count - 1]) listStack[listStack.Count - 1].ListItems.LastListItem.Blocks.Add(list);
                        else
                        {
                            if (listStack.Count > 0) { listStack.RemoveAt(listStack.Count - 1); indentStack.RemoveAt(indentStack.Count - 1); }
                            if (listStack.Count == 0) document.Blocks.Add(list);
                            else listStack[listStack.Count - 1].ListItems.LastListItem.Blocks.Add(list);
                        }
                        listStack.Add(list); indentStack.Add(indent);
                    }
                    paragraph = DocumentCodec.Paragraph(""); paragraph.Inlines.Clear(); ParseInline(itemMatch.Groups[3].Value, paragraph.Inlines, baseDirectory, 0);
                    listStack[listStack.Count - 1].ListItems.Add(new ListItem(paragraph)); paragraph = null; continue;
                }
                // Indented continuation lines belong to the preceding list item.
                if (listStack.Count > 0 && line.TakeWhile(char.IsWhiteSpace).Count() > indentStack[indentStack.Count - 1])
                {
                    var continuation = DocumentCodec.Paragraph(""); continuation.Inlines.Clear(); ParseInline(line.TrimStart(), continuation.Inlines, baseDirectory, 0);
                    listStack[listStack.Count - 1].ListItems.LastListItem.Blocks.Add(continuation); continue;
                }
                listStack.Clear(); indentStack.Clear();
                var heading = Regex.Match(line, @"^(#{1,3})\s+(.*)$");
                bool quote = line.StartsWith("> ");
                if (heading.Success || quote || paragraph == null)
                {
                    paragraph = DocumentCodec.Paragraph(""); paragraph.Inlines.Clear();
                    document.Blocks.Add(paragraph);
                }
                else paragraph.Inlines.Add(new LineBreak());
                ParseInline(heading.Success ? heading.Groups[2].Value : quote ? line.Substring(2) : line, paragraph.Inlines, baseDirectory, 0);
                if (heading.Success || quote) DocumentCodec.SetParagraphStyle(paragraph, heading.Success ? "h" + heading.Groups[1].Length : "blockquote");
                if (heading.Success || quote) paragraph = null;
            }
            if (document.Blocks.Count == 0) document.Blocks.Add(DocumentCodec.Paragraph(""));
            return document;
        }

        private static void ParseInline(string text, InlineCollection output, string baseDirectory, int depth)
        {
            if (depth > 20) { output.Add(new Run(text)); return; }
            // A conservative Markdown subset; unknown syntax remains visible as text.
            int position = 0;
            foreach (Match match in InlinePattern.Matches(text))
            {
                if (match.Index > position) output.Add(new Run(text.Substring(position, match.Index - position)));
                if (match.Value.StartsWith("\\")) output.Add(new Run(match.Value.Substring(1)));
                else if (match.Groups["alt"].Success)
                {
                    string source = Uri.UnescapeDataString(match.Groups["src"].Value);
                    string imagePath = null;
                    try
                    {
                        if (!Path.IsPathRooted(source) && !source.Contains(":"))
                        {
                            string root = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                            string candidate = Path.GetFullPath(Path.Combine(root, source.Replace('/', Path.DirectorySeparatorChar)));
                            if (candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)) imagePath = candidate;
                        }
                    }
                    catch (ArgumentException) { }
                    catch (NotSupportedException) { }
                    if (imagePath == null) output.Add(new Run(match.Value));
                    else
                    {
                        if (new FileInfo(imagePath).Length > DocumentCodec.MaxImageBytes) throw new InvalidDataException("Markdown 引用的图片超过 25 MB：" + source);
                        output.Add(new InlineUIContainer(DocumentCodec.MakeImage(File.ReadAllBytes(imagePath), Unescape(match.Groups["alt"].Value))));
                    }
                }
                else if (match.Groups["label"].Success)
                {
                    string href = match.Groups["href"].Value;
                    Span link = DocumentCodec.SafeLink(href) ? new Hyperlink { NavigateUri = new Uri(href, UriKind.RelativeOrAbsolute) } : new Span();
                    ParseInline(match.Groups["label"].Value, link.Inlines, baseDirectory, depth + 1); output.Add(link);
                }
                else if (match.Groups["ticks"].Success)
                {
                    string code = match.Groups["code"].Value;
                    if (code.Length >= 2 && code.StartsWith(" ") && code.EndsWith(" ")) code = code.Substring(1, code.Length - 2);
                    var span = new Span(new Run(code)) { Tag = "code", FontFamily = new FontFamily("Consolas") }; output.Add(span);
                }
                else
                {
                    bool both = match.Groups["both"].Success, bold = match.Groups["bold"].Success;
                    var span = new Span();
                    if (bold || both) span.FontWeight = FontWeights.Bold;
                    if (!bold || both) span.FontStyle = FontStyles.Italic;
                    ParseInline(match.Groups[both ? "both" : bold ? "bold" : "italic"].Value, span.Inlines, baseDirectory, depth + 1); output.Add(span);
                }
                position = match.Index + match.Length;
            }
            if (position < text.Length) output.Add(new Run(text.Substring(position)));
        }

        private static string Unescape(string value) { return Regex.Replace(value, @"\\(.)", "$1"); }
    }
}
