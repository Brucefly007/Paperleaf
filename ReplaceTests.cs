using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Paperleaf
{
    internal static class ReplaceTests
    {
        private static void Click(Window window, string name)
        {
            ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }

        private static void Shortcut(Window window, Key key, ModifierKeys modifiers)
        {
            var binding = window.InputBindings.OfType<KeyBinding>().Single(b => b.Key == key && b.Modifiers == modifiers);
            ((RoutedCommand)binding.Command).Execute(null, window);
        }

        private static void Reset(RichTextBox editor, string text)
        {
            var document = DocumentCodec.Empty(); document.Blocks.Clear(); document.Blocks.Add(DocumentCodec.Paragraph(text));
            editor.Document = document; editor.IsUndoEnabled = false; editor.IsUndoEnabled = true;
        }

        internal static void Run(Action<bool, string> assert, string directory)
        {
            var editor = new RichTextBox();
            var host = new Window { Content = editor, Width = 400, Height = 200, ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
            host.Show(); host.UpdateLayout();
            Reset(editor, "aaa aaa");
            assert(DocumentReplace.All(editor, "a", "aa") == 6 && DocumentCodec.InlineText(((Paragraph)editor.Document.Blocks.FirstBlock).Inlines) == "aaaaaa aaaaaa", "replace all handles adjacent matches and replacement containing the query");
            editor.Undo();
            assert(DocumentCodec.InlineText(((Paragraph)editor.Document.Blocks.FirstBlock).Inlines) == "aaa aaa", "one undo restores every replacement in the batch");
            editor.Redo();
            assert(DocumentCodec.InlineText(((Paragraph)editor.Document.Blocks.FirstBlock).Inlines) == "aaaaaa aaaaaa", "one redo reapplies the complete replacement batch");
            Reset(editor, "aaaaa");
            assert(DocumentReplace.All(editor, "aa", "b") == 2 && DocumentCodec.InlineText(((Paragraph)editor.Document.Blocks.FirstBlock).Inlines) == "bba", "replace all uses nonoverlapping original matches");
            Reset(editor, "删除删除保留删除");
            assert(DocumentReplace.All(editor, "删除", "") == 3 && DocumentCodec.InlineText(((Paragraph)editor.Document.Blocks.FirstBlock).Inlines) == "保留", "empty replacement deletes all matches including adjacent ones");
            Reset(editor, "abc");
            assert(DocumentReplace.All(editor, "", "x") == 0 && DocumentReplace.All(editor, "missing", "x") == 0 && DocumentCodec.InlineText(((Paragraph)editor.Document.Blocks.FirstBlock).Inlines) == "abc", "empty or absent queries leave the document unchanged");
            Reset(editor, "HELLO hello Hello");
            assert(DocumentReplace.All(editor, "hello", "你好🌿") == 3 && DocumentCodec.InlineText(((Paragraph)editor.Document.Blocks.FirstBlock).Inlines) == "你好🌿 你好🌿 你好🌿", "literal replacements support Unicode and case-insensitive search");
            Reset(editor, ".* [x] .*");
            assert(DocumentReplace.All(editor, ".*", "$1\\new") == 2 && DocumentCodec.InlineText(((Paragraph)editor.Document.Blocks.FirstBlock).Inlines) == "$1\\new [x] $1\\new", "queries and replacements treat regex symbols literally");
            Reset(editor, "word word"); editor.IsReadOnly = true;
            assert(DocumentReplace.All(editor, "word", "change") == 0 && DocumentCodec.Text(editor.Document).Contains("word word"), "replace engine blocks changes in reading mode"); editor.IsReadOnly = false;

            var rich = DocumentCodec.Empty(); rich.Blocks.Clear();
            var paragraph = new Paragraph();
            paragraph.Inlines.Add(new Run("HEL") { Foreground = TextColors.Brush("#C73E3A"), FontWeight = FontWeights.Bold, FontStyle = FontStyles.Italic });
            paragraph.Inlines.Add(new Run("LO"));
            var pixels = new byte[] { 70, 130, 90, 255 };
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
            var bytes = DocumentCodec.ImageBytes(new Image { Source = bitmap });
            paragraph.Inlines.Add(new Run("left")); paragraph.Inlines.Add(new InlineUIContainer(DocumentCodec.MakeImage(bytes, "保留图片"))); paragraph.Inlines.Add(new Run("right"));
            rich.Blocks.Add(paragraph); rich.Blocks.Add(DocumentCodec.Paragraph("hello"));
            editor.Document = rich; editor.IsUndoEnabled = false; editor.IsUndoEnabled = true;
            assert(DocumentReplace.All(editor, "hello", "替换") == 2 && DocumentCodec.InlineText(paragraph.Inlines).StartsWith("替换left"), "replacement spans differently formatted runs and subsequent paragraphs");
            var range = DocumentSearch.FindNext(rich, rich.ContentStart, "替换");
            var firstCharacter = new TextRange(range.Start, range.Start.GetPositionAtOffset(1));
            assert(TextColors.Hex(firstCharacter.GetPropertyValue(TextElement.ForegroundProperty) as Brush) == "#C73E3A" && (FontWeight)firstCharacter.GetPropertyValue(TextElement.FontWeightProperty) == FontWeights.Bold && (FontStyle)firstCharacter.GetPropertyValue(TextElement.FontStyleProperty) == FontStyles.Italic, "replacement inherits the matched text's initial color and emphasis");
            var unchanged = DocumentSearch.FindNext(rich, rich.ContentStart, "left");
            assert(TextColors.Hex(new TextRange(unchanged.Start, unchanged.End).GetPropertyValue(TextElement.ForegroundProperty) as Brush) == TextColors.Default, "replacement formatting does not spill into following unmatched text");
            assert(DocumentReplace.All(editor, "leftright", "bad") == 0 && DocumentReplace.All(editor, "\uFFFC", "bad") == 0 && DocumentCodec.Images(paragraph.Inlines).Single().Tag as string == "保留图片", "replacement never joins text across or removes embedded pictures");
            assert(DocumentCodec.ImageBytes(DocumentCodec.Images(paragraph.Inlines).Single()).SequenceEqual(bytes), "replacement preserves embedded image pixels");
            var previous = DocumentSearch.FindPrevious(rich, rich.ContentStart, "替换");
            assert(previous != null && previous.Wrapped && previous.Start.Paragraph == rich.Blocks.LastBlock, "previous occurrence wraps from the start to the last match");
            var earlier = DocumentSearch.FindPrevious(rich, previous.Start, "替换");
            assert(earlier != null && !earlier.Wrapped && earlier.Start.Paragraph == paragraph, "previous occurrence moves across paragraphs");
            assert(DocumentSearch.FindPrevious(rich, rich.ContentEnd, "missing") == null && DocumentSearch.FindPrevious(rich, rich.ContentEnd, "") == null, "previous occurrence handles empty and missing queries");
            var adjacent = DocumentCodec.Empty(); adjacent.Blocks.Clear(); var colored = new Paragraph();
            colored.Inlines.Add(new Run("a") { Foreground = TextColors.Brush("#C73E3A") });
            colored.Inlines.Add(new Run("a") { Foreground = TextColors.Brush("#245CC5") });
            colored.Inlines.Add(new Run("a") { Foreground = TextColors.Brush("#8546B2") }); adjacent.Blocks.Add(colored); editor.Document = adjacent;
            assert(DocumentReplace.All(editor, "a", "XX") == 3 && DocumentCodec.InlineText(colored.Inlines) == "XXXXXX", "adjacent matches with different formatting all replace correctly");
            var adjacentMatches = DocumentSearch.FindAll(adjacent, "XX");
            assert(adjacentMatches.Select(m => TextColors.Hex(new TextRange(m.Start, m.End).GetPropertyValue(TextElement.ForegroundProperty) as Brush)).SequenceEqual(new[] { "#C73E3A", "#245CC5", "#8546B2" }), "each adjacent replacement retains its own text color");
            host.Close();

            RunInterface(assert, directory, bytes);
        }

        private static void RunInterface(Action<bool, string> assert, string directory, byte[] bytes)
        {
            string path = Path.Combine(directory, "替换测试.html");
            var fixture = DocumentCodec.Empty(); fixture.Blocks.Clear();
            fixture.Blocks.Add(DocumentCodec.Paragraph("猫 一 猫 二 猫"));
            var imageParagraph = DocumentCodec.Paragraph("图片与颜色应当保留：");
            imageParagraph.Inlines.Add(new Run("彩色文字") { Foreground = TextColors.Brush("#8546B2"), FontWeight = FontWeights.Bold });
            imageParagraph.Inlines.Add(new InlineUIContainer(DocumentCodec.MakeImage(bytes, "测试图片"))); fixture.Blocks.Add(imageParagraph);
            DocumentCodec.AtomicWrite(path, DocumentCodec.Html(fixture, "替换测试"));
            var controller = new EditorWindow(path, Path.Combine(directory, "replace-session")); var window = controller.Window;
            window.ShowInTaskbar = false; window.ShowActivated = false; window.Opacity = 0; window.Show(); window.UpdateLayout();
            var editor = (RichTextBox)window.FindName("Editor");
            var query = (TextBox)window.FindName("FindInput"); var replacement = (TextBox)window.FindName("ReplaceInput");
            var result = (TextBlock)window.FindName("FindResult");
            Shortcut(window, Key.H, ModifierKeys.Control);
            assert(((Border)window.FindName("FindBar")).Visibility == Visibility.Visible && ((Grid)window.FindName("ReplaceRow")).Visibility == Visibility.Visible && ((ToggleButton)window.FindName("ReplaceToggle")).IsChecked == true, "Ctrl+H opens the find and replace panel");
            query.Text = "猫"; replacement.Text = "虎";
            editor.Selection.Select(editor.Document.ContentStart, editor.Document.ContentStart);
            Click(window, "FindNextButton"); var first = editor.Selection.Start;
            Click(window, "FindNextButton"); var second = editor.Selection.Start;
            assert(editor.Selection.Text == "猫" && second.CompareTo(first) > 0, "next button selects successive occurrences");
            Click(window, "FindPreviousButton");
            assert(editor.Selection.Start.CompareTo(first) == 0, "previous button returns to the earlier occurrence");
            Shortcut(window, Key.F3, ModifierKeys.Shift);
            assert(editor.Selection.Start.CompareTo(second) > 0 && result.Text == "已回到末尾", "Shift+F3 navigates backward and reports wrapping");
            Shortcut(window, Key.F3, ModifierKeys.None);
            assert(editor.Selection.Start.CompareTo(first) == 0 && result.Text == "已回到开头", "F3 navigates forward and reports wrapping");
            Click(window, "ReplaceOneButton");
            string afterFirst = DocumentCodec.Text(editor.Document);
            assert(afterFirst.StartsWith("虎 一 猫 二 猫") && editor.Selection.Text == "猫", "replace current changes one occurrence and selects the next");
            Click(window, "ReplaceOneButton");
            assert(DocumentCodec.Text(editor.Document).StartsWith("虎 一 虎 二 猫"), "repeated single replacement advances through the document");
            editor.Undo(); assert(DocumentCodec.Text(editor.Document) == afterFirst, "single replacement is one native undo operation");
            editor.Redo(); assert(DocumentCodec.Text(editor.Document).StartsWith("虎 一 虎 二 猫"), "single replacement supports native redo");
            query.Text = "虎"; replacement.Text = "";
            string beforeLocate = DocumentCodec.Text(editor.Document);
            Click(window, "ReplaceOneButton");
            assert(DocumentCodec.Text(editor.Document) == beforeLocate && editor.Selection.Text == "虎", "changed search query locates a match before replacing text");
            Click(window, "ReplaceOneButton");
            assert(DocumentCodec.Text(editor.Document).Count(c => c == '虎') == 1, "single replacement allows an empty replacement");
            editor.Selection.Select(editor.Document.ContentEnd, editor.Document.ContentEnd);
            string beforeMoved = DocumentCodec.Text(editor.Document); Click(window, "ReplaceOneButton");
            assert(DocumentCodec.Text(editor.Document) == beforeMoved && editor.Selection.Text == "虎", "moving selection requires locating a match before replacement");
            replacement.Text = "虎虎";
            string beforeAll = DocumentCodec.Html(editor.Document, "完整性"); Click(window, "ReplaceAllButton");
            assert(result.Text.Contains("已替换 1 处") && DocumentCodec.Text(editor.Document).Count(c => c == '虎') == 2, "replace all button reports the original match count");
            editor.Undo(); assert(DocumentCodec.Html(editor.Document, "完整性") == beforeAll, "batch undo restores the complete rich document");
            query.Text = "不存在"; Click(window, "ReplaceAllButton"); assert(result.Text == "没有找到", "replace all displays a no-match result");
            query.Text = ""; Click(window, "ReplaceAllButton"); assert(result.Text.Contains("请输入"), "replace all requests a nonempty query");
            query.Text = "猫"; replacement.Text = "狮";
            var read = (ToggleButton)window.FindName("ReadButton"); read.IsChecked = true; read.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            string readText = DocumentCodec.Text(editor.Document);
            Click(window, "ReplaceAllButton"); Click(window, "ReplaceOneButton"); Click(window, "FindNextButton");
            assert(!((Button)window.FindName("ReplaceAllButton")).IsEnabled && DocumentCodec.Text(editor.Document) == readText && editor.Selection.Text == "猫", "reading mode permits navigation while blocking both replace actions");
            read.IsChecked = false; read.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            assert(((Button)window.FindName("ReplaceAllButton")).IsEnabled, "returning to editing reenables replacement");
            Click(window, "SaveButton");
            var saved = DocumentCodec.Read(path);
            assert(DocumentCodec.Text(saved) == DocumentCodec.Text(editor.Document) && DocumentCodec.ImageBytes(DocumentCodec.Paragraphs(saved.Blocks).SelectMany(p => DocumentCodec.Images(p.Inlines)).Single()).SequenceEqual(bytes), "replaced documents save and reopen with their pictures intact");
            window.Width = 840; window.Height = 580; window.UpdateLayout();
            var bar = (Border)window.FindName("FindBar");
            foreach (string name in new[] { "FindInput", "ReplaceInput", "FindPreviousButton", "FindNextButton", "ReplaceOneButton", "ReplaceAllButton", "CloseFindButton" })
            {
                var control = (FrameworkElement)window.FindName(name); var edge = control.TranslatePoint(new Point(control.ActualWidth, control.ActualHeight), bar);
                assert(control.ActualWidth > 20 && edge.X <= bar.ActualWidth && edge.Y <= bar.ActualHeight, "replace panel fits the minimum window: " + name);
            }
            assert(editor.ActualHeight > 100, "editor remains usable with replacement open at minimum window size");
            var visual = (FrameworkElement)window.Content;
            var preview = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32); preview.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(preview));
            using (var output = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "replace-preview.png"))) encoder.Save(output);
            var toggle = (ToggleButton)window.FindName("ReplaceToggle"); toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            assert(((Grid)window.FindName("ReplaceRow")).Visibility == Visibility.Collapsed && bar.Visibility == Visibility.Visible, "replacement row can collapse while keeping search open");
            Click(window, "CloseFindButton"); assert(bar.Visibility == Visibility.Collapsed, "close hides the complete find and replace panel");
            window.Close();
            assert(Directory.GetFiles(Path.Combine(directory, "replace-session"), "*.lock").Length == 0, "replace tests close without leaving a recovery session lock");
        }
    }
}
