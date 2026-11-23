using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace Paperleaf
{
    internal static class DocumentCodec
    {
        public const long MaxFileBytes = 120L * 1024 * 1024;
        public const int MaxImageBytes = 25 * 1024 * 1024;
        public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static FlowDocument Empty()
        {
            var document = new FlowDocument { FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 17,
                Foreground = new SolidColorBrush(Color.FromRgb(48, 60, 52)), PagePadding = new Thickness(0),
                ColumnWidth = double.PositiveInfinity, LineHeight = 30 };
            document.Blocks.Add(Paragraph(""));
            return document;
        }

        public static Paragraph Paragraph(string text)
        {
            return new Paragraph(new Run(text)) { Margin = new Thickness(0, 0, 0, 12) };
        }

        public static void SetParagraphStyle(Paragraph p, string tag)
        {
            p.FontFamily = new FontFamily(tag == "pre" ? "Consolas" : "Microsoft YaHei UI");
            p.FontSize = tag == "h1" ? 30 : tag == "h2" ? 24 : tag == "h3" ? 20 : tag == "pre" ? 14 : 17;
            p.FontWeight = tag.StartsWith("h") ? FontWeights.SemiBold : FontWeights.Normal;
            p.Background = tag == "pre" ? Brushes.WhiteSmoke : null;
            p.Margin = tag.StartsWith("h") ? new Thickness(0, 20, 0, 14) : new Thickness(0, 0, 0, 12);
            p.Padding = tag == "pre" ? new Thickness(12) : tag == "blockquote" ? new Thickness(14, 0, 0, 0) : new Thickness(0);
            p.BorderThickness = tag == "blockquote" ? new Thickness(3, 0, 0, 0) : new Thickness(0);
            p.BorderBrush = tag == "blockquote" ? new SolidColorBrush(Color.FromRgb(174, 191, 166)) : null;
        }

        // Derive semantics from WPF's undoable formatting rather than the non-undoable Tag.
        public static string ParagraphTag(Paragraph p)
        {
            if (p.FontSize >= 29) return "h1";
            if (p.FontSize >= 23) return "h2";
            if (p.FontSize >= 19) return "h3";
            if (p.FontFamily.Source == "Consolas") return "pre";
            if (p.BorderThickness.Left > 0) return "blockquote";
            return "p";
        }

        public static Image MakeImage(byte[] bytes, string alt)
        {
            if (bytes.Length > MaxImageBytes) throw new InvalidDataException("单张图片请控制在 25 MB 以内。");
            BitmapFrame bitmap;
            using (var stream = new MemoryStream(bytes))
            {
                bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 64000000) throw new InvalidDataException("图片尺寸过大，请缩小到 6400 万像素以内。");
                bitmap.Freeze();
            }
            var temporary = new Image { Source = bitmap };
            var image = new EmbeddedImage { PngData = Convert.ToBase64String(ImageBytes(temporary)), MaxWidth = 720, Width = Math.Min(720, bitmap.PixelWidth),
                Stretch = Stretch.Uniform, Margin = new Thickness(0, 8, 0, 8), Tag = alt ?? "图片", ToolTip = alt ?? "图片" };
            AutomationPropertiesHelper.SetImageName(image, alt);
            return image;
        }

        public static byte[] ImageBytes(Image image)
        {
            var embedded = image as EmbeddedImage;
            if (embedded != null && !string.IsNullOrEmpty(embedded.PngData)) return Convert.FromBase64String(embedded.PngData);
            var source = image.Source as BitmapSource;
            if (source == null) throw new InvalidDataException("图片无法读取。");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                if (stream.Length > MaxImageBytes) throw new InvalidDataException("这张图片转为内嵌 PNG 后超过 25 MB，请缩小图片后插入。");
                return stream.ToArray();
            }
        }

        public static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
        {
            foreach (Block block in blocks)
            {
                var p = block as Paragraph;
                if (p != null) yield return p;
                var section = block as Section;
                if (section != null) foreach (var child in Paragraphs(section.Blocks)) yield return child;
                var list = block as System.Windows.Documents.List;
                if (list != null) foreach (ListItem item in list.ListItems) foreach (var child in Paragraphs(item.Blocks)) yield return child;
            }
        }

        public static IEnumerable<Image> Images(InlineCollection inlines)
        {
            foreach (Inline inline in inlines)
            {
                var container = inline as InlineUIContainer;
                if (container != null && container.Child is Image) yield return (Image)container.Child;
                var span = inline as Span;
                if (span != null) foreach (var image in Images(span.Inlines)) yield return image;
            }
        }

        public static string Text(FlowDocument document)
        {
            return new TextRange(document.ContentStart, document.ContentEnd).Text.Replace("\r\n", "\n");
        }

        public static string InlineText(InlineCollection inlines)
        {
            var text = new StringBuilder();
            foreach (var inline in inlines)
            {
                var run = inline as Run; var span = inline as Span;
                if (run != null) text.Append(run.Text);
                else if (span != null) text.Append(InlineText(span.Inlines));
                else if (inline is LineBreak) text.Append('\n');
            }
            return text.ToString();
        }

        public static string Html(FlowDocument document, string title)
        {
            var article = new XElement("article", HtmlBlocks(document.Blocks));
            var html = new XElement("html", new XAttribute("lang", "zh-CN"),
                new XElement("head", new XElement("meta", new XAttribute("charset", "utf-8")),
                    new XElement("meta", new XAttribute("name", "viewport"), new XAttribute("content", "width=device-width, initial-scale=1")),
                    new XElement("meta", new XAttribute("name", "generator"), new XAttribute("content", "Paperleaf 1.2.0")),
                    new XElement("title", title), new XElement("style",
                    "body{margin:0;background:#f4f3ef;color:#303c34;font:17px/1.9 'Microsoft YaHei',system-ui,sans-serif}article{box-sizing:border-box;max-width:900px;min-height:100vh;margin:auto;padding:60px 64px;background:#fff}p{margin:0 0 1em;white-space:pre-wrap;overflow-wrap:anywhere}h1,h2,h3{line-height:1.5;margin:1.2em 0 .6em}img{max-width:100%;height:auto;vertical-align:middle;border-radius:3px}blockquote{border-left:3px solid #aebfa6;margin:1em 0;padding-left:1em}pre{white-space:pre-wrap;overflow-wrap:anywhere;padding:16px;background:#f5f6f2}code{font-family:Consolas,monospace}a{color:#315c47}@media(max-width:640px){article{padding:28px 22px}}@media print{body{background:white}article{max-width:none;padding:0}img{break-inside:avoid}}")),
                new XElement("body", article));
            return html.ToString(SaveOptions.DisableFormatting);
        }

        private static IEnumerable<XElement> HtmlBlocks(BlockCollection blocks)
        {
            foreach (Block block in blocks)
            {
                var p = block as Paragraph;
                if (p != null)
                {
                    string tag = ParagraphTag(p);
                    if (!new[] { "p", "h1", "h2", "h3", "blockquote", "pre" }.Contains(tag)) tag = "p";
                    var element = new XElement(tag, HtmlInlines(p.Inlines, p));
                    string color = TextColors.Hex(p.Foreground);
                    string style = color != TextColors.Default ? "color:" + color + ";" : "";
                    bool heading = tag.StartsWith("h");
                    if ((p.FontWeight.ToOpenTypeWeight() >= 600) != heading) style += heading ? "font-weight:normal;" : "font-weight:bold;";
                    if (p.FontStyle == FontStyles.Italic) style += "font-style:italic;";
                    if (style.Length > 0) element.SetAttributeValue("style", style.TrimEnd(';'));
                    yield return element;
                }
                var section = block as Section;
                if (section != null) foreach (var element in HtmlBlocks(section.Blocks)) yield return element;
                var list = block as System.Windows.Documents.List;
                if (list != null)
                {
                    bool ordered = list.MarkerStyle == TextMarkerStyle.Decimal;
                    yield return new XElement(ordered ? "ol" : "ul", ordered ? new XAttribute("start", list.StartIndex) : null,
                        list.ListItems.Cast<ListItem>().Select(item => new XElement("li", HtmlBlocks(item.Blocks))));
                }
            }
        }

        private static IEnumerable<XNode> HtmlInlines(InlineCollection inlines, TextElement parent)
        {
            foreach (Inline inline in inlines)
            {
                var run = inline as Run;
                var span = inline as Span;
                var container = inline as InlineUIContainer;
                IEnumerable<XNode> nodes = null;
                if (run != null) nodes = new XNode[] { new XText(run.Text) };
                else if (inline is LineBreak) nodes = new XNode[] { new XElement("br") };
                else if (container != null && container.Child is Image)
                {
                    var image = (Image)container.Child;
                    nodes = new XNode[] { new XElement("img", new XAttribute("src", "data:image/png;base64," + Convert.ToBase64String(ImageBytes(image))),
                        new XAttribute("alt", image.Tag as string ?? "图片")) };
                }
                else if (span != null) nodes = HtmlInlines(span.Inlines, inline).ToArray();
                if (nodes == null) continue;
                if (inline.FontWeight.ToOpenTypeWeight() >= 600 && parent.FontWeight.ToOpenTypeWeight() < 600) nodes = new XNode[] { new XElement("strong", nodes) };
                if (inline.FontStyle == FontStyles.Italic && parent.FontStyle != FontStyles.Italic) nodes = new XNode[] { new XElement("em", nodes) };
                bool underline = inline.TextDecorations != null && inline.TextDecorations.Any(d => d.Location == TextDecorationLocation.Underline);
                var parentInline = parent as Inline;
                bool parentUnderline = parentInline != null && parentInline.TextDecorations != null && parentInline.TextDecorations.Any(d => d.Location == TextDecorationLocation.Underline);
                if (underline && !parentUnderline) nodes = new XNode[] { new XElement("u", nodes) };
                var hyperlink = inline as Hyperlink;
                if (hyperlink != null && hyperlink.NavigateUri != null && SafeLink(hyperlink.NavigateUri.ToString())) nodes = new XNode[] { new XElement("a", new XAttribute("href", hyperlink.NavigateUri.ToString()), nodes) };
                if (inline.Tag as string == "code") nodes = new XNode[] { new XElement("code", nodes) };
                string color = TextColors.Hex(inline.Foreground);
                string style = color != TextColors.Hex(parent.Foreground) ? "color:" + color + ";" : "";
                if (inline.FontWeight.ToOpenTypeWeight() < 600 && parent.FontWeight.ToOpenTypeWeight() >= 600) style += "font-weight:normal;";
                if (inline.FontStyle == FontStyles.Normal && parent.FontStyle == FontStyles.Italic) style += "font-style:normal;";
                if (style.Length > 0) nodes = new XNode[] { new XElement("span", new XAttribute("style", style.TrimEnd(';')), nodes) };
                foreach (var node in nodes) yield return node;
            }
        }

        public static FlowDocument Read(string path)
        {
            if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("文档超过 120 MB，请先缩小图片或拆分文档。");
            string extension = Path.GetExtension(path).ToLowerInvariant();
            string content = File.ReadAllText(path, Encoding.UTF8);
            if (extension == ".md" || extension == ".markdown") return MarkdownCodec.Read(content, Path.GetDirectoryName(Path.GetFullPath(path)));
            if (extension == ".txt")
            {
                var document = Empty(); document.Blocks.Clear();
                foreach (string line in content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')) document.Blocks.Add(Paragraph(line));
                return document;
            }
            if (extension != ".html" && extension != ".htm") throw new InvalidDataException("请打开 HTML、Markdown 或 TXT 文档。");
            return ReadHtml(content);
        }

        public static FlowDocument ReadHtml(string html)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 180L * 1024 * 1024 };
            XDocument xml;
            try { using (var reader = XmlReader.Create(new StringReader(html), settings)) xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace); }
            catch (XmlException) { throw new InvalidDataException("此 HTML 不是纸间可编辑的文档。请打开纸间保存的 HTML，或先将内容复制粘贴到新文档。"); }
            var article = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "article");
            if (article == null) throw new InvalidDataException("未找到文档正文。请打开纸间保存的 HTML，或复制粘贴正文。");
            var document = Empty(); document.Blocks.Clear();
            foreach (var element in article.Elements()) ReadBlock(element, document.Blocks, 0);
            if (document.Blocks.Count == 0) document.Blocks.Add(Paragraph(""));
            return document;
        }

        private static void ReadBlock(XElement element, BlockCollection blocks, int depth)
        {
            if (depth > 32) throw new InvalidDataException("文档结构过深。");
            string tag = element.Name.LocalName.ToLowerInvariant();
            if (tag == "script" || tag == "style" || tag == "iframe" || tag == "object") return;
            if (tag == "ul" || tag == "ol")
            {
                var list = new System.Windows.Documents.List { MarkerStyle = tag == "ol" ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(0, 4, 0, 12), Padding = new Thickness(26, 0, 0, 0) };
                int start;
                if (tag == "ol" && int.TryParse((string)element.Attribute("start"), out start) && start > 0) list.StartIndex = start;
                foreach (var li in element.Elements().Where(e => e.Name.LocalName == "li"))
                {
                    var item = new ListItem();
                    if (li.Elements().Any(e => new[] { "p", "ul", "ol", "h1", "h2", "h3", "blockquote", "pre" }.Contains(e.Name.LocalName)))
                        foreach (var child in li.Elements()) ReadBlock(child, item.Blocks, depth + 1);
                    else { var p = Paragraph(""); p.Inlines.Clear(); ReadInline(li, p.Inlines, depth + 1); item.Blocks.Add(p); }
                    if (item.Blocks.Count == 0) item.Blocks.Add(Paragraph(""));
                    list.ListItems.Add(item);
                }
                if (list.ListItems.Count > 0) blocks.Add(list);
                return;
            }
            if (tag == "div" || tag == "section") { foreach (var child in element.Elements()) ReadBlock(child, blocks, depth + 1); return; }
            var paragraph = Paragraph(""); paragraph.Inlines.Clear();
            string color = TextColors.ReadStyle((string)element.Attribute("style"));
            if (color != null) paragraph.Foreground = TextColors.Brush(color);
            ReadInline(element, paragraph.Inlines, depth + 1);
            if (new[] { "h1", "h2", "h3", "blockquote", "pre" }.Contains(tag)) SetParagraphStyle(paragraph, tag);
            ReadFontStyle((string)element.Attribute("style"), paragraph);
            blocks.Add(paragraph);
        }

        private static bool ReadFontStyle(string style, TextElement target)
        {
            bool applied = false;
            foreach (string declaration in (style ?? "").Split(';'))
            {
                string[] pair = declaration.Split(':');
                if (pair.Length != 2) continue;
                string name = pair[0].Trim().ToLowerInvariant(), value = pair[1].Trim().ToLowerInvariant();
                if (name == "font-weight" && (value == "normal" || value == "bold")) { target.FontWeight = value == "bold" ? FontWeights.Bold : FontWeights.Normal; applied = true; }
                if (name == "font-style" && (value == "normal" || value == "italic")) { target.FontStyle = value == "italic" ? FontStyles.Italic : FontStyles.Normal; applied = true; }
            }
            return applied;
        }

        private static void ReadInline(XElement parent, InlineCollection inlines, int depth)
        {
            if (depth > 64) throw new InvalidDataException("文档结构过深。");
            foreach (XNode node in parent.Nodes())
            {
                var text = node as XText;
                if (text != null) { inlines.Add(new Run(text.Value)); continue; }
                var element = node as XElement;
                if (element == null) continue;
                string tag = element.Name.LocalName.ToLowerInvariant();
                if (tag == "script" || tag == "style" || tag == "iframe" || tag == "object") continue;
                string color = TextColors.ReadStyle((string)element.Attribute("style"));
                if (tag == "br") { inlines.Add(new LineBreak()); continue; }
                if (tag == "img")
                {
                    string source = (string)element.Attribute("src") ?? "";
                    if (!Regex.IsMatch(source, @"^data:image/(png|jpeg|jpg|gif|bmp|tiff);base64,", RegexOptions.IgnoreCase)) throw new InvalidDataException("文档含有未嵌入的图片，请用 Markdown 导入本地图片。");
                    int comma = source.IndexOf(',');
                    if (source.Length - comma > MaxImageBytes * 4L / 3 + 8) throw new InvalidDataException("文档中的图片超过 25 MB。");
                    inlines.Add(new InlineUIContainer(MakeImage(Convert.FromBase64String(source.Substring(comma + 1)), (string)element.Attribute("alt") ?? "图片")));
                    continue;
                }
                Span span;
                string href = (string)element.Attribute("href");
                if (tag == "a" && SafeLink(href)) span = new Hyperlink { NavigateUri = new Uri(href, UriKind.RelativeOrAbsolute) };
                else span = new Span();
                if (color != null) span.Foreground = TextColors.Brush(color);
                if (tag == "strong" || tag == "b") span.FontWeight = FontWeights.Bold;
                if (tag == "em" || tag == "i") span.FontStyle = FontStyles.Italic;
                if (tag == "u") span.TextDecorations = TextDecorations.Underline;
                if (tag == "code") { span.FontFamily = new FontFamily("Consolas"); span.Tag = "code"; }
                bool hasFontStyle = ReadFontStyle((string)element.Attribute("style"), span);
                if (tag == "span" && color == null && !hasFontStyle) { ReadInline(element, inlines, depth + 1); continue; }
                ReadInline(element, span.Inlines, depth + 1); inlines.Add(span);
            }
        }

        public static bool SafeLink(string link)
        {
            return !string.IsNullOrEmpty(link) && (link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || link.StartsWith("#"));
        }

        public static void AtomicWrite(string path, string content)
        {
            path = Path.GetFullPath(path);
            string temp = Path.Combine(Path.GetDirectoryName(path), ".paperleaf-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, Utf8)) { writer.Write(content); writer.Flush(); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }
    }

    internal static class AutomationPropertiesHelper
    {
        public static void SetImageName(Image image, string name)
        {
            System.Windows.Automation.AutomationProperties.SetName(image, name ?? "图片");
        }
    }
}
