using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace Paperleaf
{
    internal static class EditorFeatureTests
    {
        private static Run[] Runs(FlowDocument document)
        {
            return DocumentCodec.Paragraphs(document.Blocks).SelectMany(p => Runs(p.Inlines)).ToArray();
        }
        private static System.Collections.Generic.IEnumerable<Run> Runs(InlineCollection inlines)
        {
            foreach (Inline inline in inlines)
            {
                var run = inline as Run; var span = inline as Span;
                if (run != null) yield return run;
                if (span != null) foreach (var child in Runs(span.Inlines)) yield return child;
            }
        }
        internal static void Run(Action<bool, string> assert, string directory)
        {
            assert(TextColors.ReadStyle("background:red; COLOR: #aBc ;font-size:99px") == "#AABBCC", "color reader accepts safe short hex colors only");
            assert(TextColors.ReadStyle("color:expression(bad)") == null && TextColors.ReadStyle("background-color:#ff0000") == null, "color reader ignores unsupported or unrelated CSS");
            string source = "<html><article><h1 style='color:#245cc5'>标题</h1><p style='color:#2f7d4a'>绿色<span style='color:#c73e3a'><strong><em>红色粗斜体</em></strong></span><span style='color:#303c34'>默认色</span></p></article></html>";
            var document = DocumentCodec.ReadHtml(source);
            var runs = Runs(document);
            assert(TextColors.Hex(runs[0].Foreground) == "#245CC5" && TextColors.Hex(runs[1].Foreground) == "#2F7D4A", "HTML restores heading and paragraph colors");
            assert(TextColors.Hex(runs[2].Foreground) == "#C73E3A" && runs[2].FontWeight == FontWeights.Bold && runs[2].FontStyle == FontStyles.Italic, "HTML retains color combined with bold and italic");
            assert(TextColors.Hex(runs[3].Foreground) == TextColors.Default, "default color can override colored paragraph text");
            string html = DocumentCodec.Html(document, "颜色");
            for (int i = 0; i < 5; i++) document = DocumentCodec.ReadHtml(DocumentCodec.Html(document, "颜色"));
            assert(DocumentCodec.Html(document, "颜色") == html, "repeated color saves do not grow formatting wrappers");
            assert(Runs(document).Select(r => TextColors.Hex(r.Foreground)).SequenceEqual(runs.Select(r => TextColors.Hex(r.Foreground))), "all text colors survive repeated HTML saves");
            var reset = DocumentCodec.ReadHtml("<html><article><p><strong>粗体<span style='font-weight:normal;color:#8546b2'>取消粗体</span></strong><em>斜体<span style='font-style:normal'>取消斜体</span></em></p></article></html>");
            string resetHtml = DocumentCodec.Html(reset, "格式");
            reset = DocumentCodec.ReadHtml(resetHtml);
            assert(Runs(reset)[1].FontWeight == FontWeights.Normal && TextColors.Hex(Runs(reset)[1].Foreground) == "#8546B2" && Runs(reset)[3].FontStyle == FontStyles.Normal, "partial bold and italic removal survives saving");
            assert(DocumentCodec.Html(reset, "格式") == resetHtml, "normal formatting overrides serialize stably");
            var whole = DocumentCodec.Empty(); var paragraph = (Paragraph)whole.Blocks.FirstBlock;
            paragraph.Inlines.Add(new Run("整段")); paragraph.FontWeight = FontWeights.Bold; paragraph.FontStyle = FontStyles.Italic;
            var wholeRestored = DocumentCodec.ReadHtml(DocumentCodec.Html(whole, "整段"));
            assert(Runs(wholeRestored).All(r => r.FontWeight == FontWeights.Bold && r.FontStyle == FontStyles.Italic), "paragraph-level bold and italic survive saving");

            foreach (string code in new[] { "\n\n首尾空行\n\n", "", "\n", "```\n包含围栏\n" })
            {
                var codeDocument = DocumentCodec.Empty(); codeDocument.Blocks.Clear();
                var pre = DocumentCodec.Paragraph(code); DocumentCodec.SetParagraphStyle(pre, "pre"); codeDocument.Blocks.Add(pre);
                string markdown = MarkdownCodec.Write(codeDocument, delegate(Image image) { return "unused"; });
                var restored = MarkdownCodec.Read(markdown, directory);
                assert(DocumentCodec.InlineText(((Paragraph)restored.Blocks.FirstBlock).Inlines) == code, "Markdown code block preserves whitespace: " + code.Length + " characters");
            }

            var search = DocumentCodec.Empty(); search.Blocks.Clear();
            var first = new Paragraph(); first.Inlines.Add(new Run("Hello ")); first.Inlines.Add(new Bold(new Run("WORLD")));
            first.Inlines.Add(new LineBreak()); first.Inlines.Add(new Run("next")); search.Blocks.Add(first);
            var second = DocumentCodec.Paragraph("paragraph"); search.Blocks.Add(second);
            var match = DocumentSearch.FindNext(search, search.ContentStart, "lo wor");
            assert(match != null && new TextRange(match.Start, match.End).Text == "lo WOR" && !match.Wrapped, "search maps case-insensitive matches across formatting");
            var wrapped = DocumentSearch.FindNext(search, search.ContentEnd, "hello");
            assert(wrapped != null && wrapped.Wrapped && new TextRange(wrapped.Start, wrapped.End).Text == "Hello", "search wraps from document end to the first match");
            assert(DocumentSearch.FindNext(search, search.ContentStart, "WORLDnext") == null && DocumentSearch.FindNext(search, search.ContentStart, "nextparagraph") == null, "search respects line and paragraph boundaries");
            assert(DocumentSearch.FindNext(search, search.ContentStart, "") == null && DocumentSearch.FindNext(search, search.ContentStart, "missing") == null, "empty and missing search queries return no match");
            var after = DocumentSearch.FindNext(search, match.End, "paragraph");
            assert(after != null && !after.Wrapped && new TextRange(after.Start, after.End).Text == "paragraph", "search advances correctly into subsequent paragraphs");
            var embedded = new Paragraph(); embedded.Inlines.Add(new Run("left")); embedded.Inlines.Add(new InlineUIContainer(new Image())); embedded.Inlines.Add(new Run("right")); search.Blocks.Add(embedded);
            assert(DocumentSearch.FindNext(search, search.ContentStart, "leftright") == null, "search cannot falsely join text across an image");
            var right = DocumentSearch.FindNext(search, search.ContentStart, "right");
            assert(right != null && new TextRange(right.Start, right.End).Text == "right", "search positions remain correct after an embedded image");

            var editor = new RichTextBox { Document = search };
            editor.Selection.Select(first.ContentStart, first.ContentStart);
            var context = new PasteContext(editor, 7);
            assert(context.IsCurrent(editor, 7) && !context.IsCurrent(editor, 8), "clipboard retries require the original document version");
            editor.Selection.Select(second.ContentStart, second.ContentStart);
            assert(!context.IsCurrent(editor, 7), "clipboard retries cancel when the insertion point moves");
            editor.Selection.Select(first.ContentStart, first.ContentStart); editor.IsReadOnly = true;
            assert(!context.IsCurrent(editor, 7), "clipboard retries cannot mutate reading mode");
            editor.IsReadOnly = false; editor.Document = DocumentCodec.Empty();
            assert(!context.IsCurrent(editor, 7), "clipboard retries cannot paste into a replacement document");
        }

        internal static void Palette(Window window, RichTextBox editor, Action<bool, string> assert)
        {
            var run = Runs(editor.Document).First(r => r.Text.Length >= 2);
            var start = run.ContentStart; var end = start.GetPositionAtOffset(2);
            editor.Selection.Select(start, end);
            string original = new TextRange(start, end).Text;
            string before = TextColors.Hex(editor.Selection.GetPropertyValue(TextElement.ForegroundProperty) as Brush);
            Choose(window, 3);
            assert(editor.Selection.Text == original && TextColors.Hex(editor.Selection.GetPropertyValue(TextElement.ForegroundProperty) as Brush) == "#C73E3A", "color palette changes only the selected text and retains selection");
            assert(window.Title.StartsWith("●"), "color changes mark the document as unsaved");
            editor.Undo(); editor.Selection.Select(start, end);
            assert(TextColors.Hex(editor.Selection.GetPropertyValue(TextElement.ForegroundProperty) as Brush) == before, "text color changes support native undo");
            editor.Redo(); editor.Selection.Select(start, end);
            assert(TextColors.Hex(editor.Selection.GetPropertyValue(TextElement.ForegroundProperty) as Brush) == "#C73E3A", "text color changes support native redo");
            Choose(window, 0);
            assert(TextColors.Hex(editor.Selection.GetPropertyValue(TextElement.ForegroundProperty) as Brush) == TextColors.Default, "default color resets selected text");
            Choose(window, 3);
            var saved = DocumentCodec.ReadHtml(DocumentCodec.Html(editor.Document, "颜色"));
            assert(Runs(saved).Any(r => TextColors.Hex(r.Foreground) == "#C73E3A" && r.Text.Contains(original)), "palette color survives saving and reopening the document");
            var caret = Runs(editor.Document).First().ContentEnd;
            editor.Selection.Select(caret, caret);
            Choose(window, 8);
            assert(editor.Selection.IsEmpty && TextColors.Hex(editor.Selection.GetPropertyValue(TextElement.ForegroundProperty) as Brush) == "#245CC5", "color palette sets typing color with an empty selection");
            editor.Selection.Select(editor.Document.ContentStart, editor.Document.ContentStart);
        }
        private static void Choose(Window window, int index)
        {
            var button = (Button)window.FindName("ColorButton");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var menu = button.ContextMenu;
            ((MenuItem)menu.Items[index]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
        }
    }
}
