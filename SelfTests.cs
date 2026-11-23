using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Paperleaf
{
    internal static class SelfTests
    {
        private static int count;
        private static readonly StringBuilder log = new StringBuilder();
        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            count++; log.AppendLine("PASS: " + name);
        }
        private static void Reject(Action action, string name)
        {
            bool rejected = false; try { action(); } catch { rejected = true; } Assert(rejected, name);
        }

        public static int Run()
        {
            string directory = Path.Combine(Path.GetTempPath(), "Paperleaf-tests-" + Guid.NewGuid().ToString("N"));
            string resultPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test-result.txt");
            Directory.CreateDirectory(directory);
            int exit = 0;
            try
            {
                ClipboardImageTests.Run(Assert);
                EditorFeatureTests.Run(Assert, directory);
                ReplaceTests.Run(Assert, directory);
                var document = DocumentCodec.Empty(); document.Blocks.Clear();
                var heading = DocumentCodec.Paragraph("文字与图片 <AI> & 阅读"); DocumentCodec.SetParagraphStyle(heading, "h1"); document.Blocks.Add(heading);
                var p = DocumentCodec.Paragraph("开头：你好，世界。 emoji 🌿 ");
                p.Inlines.Add(new Bold(new Run("粗体"))); p.Inlines.Add(new Italic(new Run("斜体"))); p.Inlines.Add(new LineBreak()); p.Inlines.Add(new Run("第二行 [文字] *星号*"));
                document.Blocks.Add(p);
                byte[] pixels = { 70, 130, 90, 255, 180, 210, 190, 255, 220, 230, 200, 255, 40, 70, 50, 255 };
                var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
                var sample = new Image { Source = bitmap };
                byte[] imageBytes = DocumentCodec.ImageBytes(sample);
                p.Inlines.Add(new InlineUIContainer(DocumentCodec.MakeImage(imageBytes, "截图 [测试] & 中文")));
                p.Inlines.Add(new InlineUIContainer(DocumentCodec.MakeImage(imageBytes, "重复图片")));
                var list = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Decimal, StartIndex = 3 };
                list.ListItems.Add(new ListItem(DocumentCodec.Paragraph("第一项"))); list.ListItems.Add(new ListItem(DocumentCodec.Paragraph("第二项"))); document.Blocks.Add(list);
                var code = DocumentCodec.Paragraph("const text = `<hello>`;\n```\nend"); DocumentCodec.SetParagraphStyle(code, "pre"); document.Blocks.Add(code);
                var quote = DocumentCodec.Paragraph("引用句子"); DocumentCodec.SetParagraphStyle(quote, "blockquote"); document.Blocks.Add(quote);
                var inlineCode = DocumentCodec.Paragraph(""); inlineCode.Inlines.Clear();
                inlineCode.Inlines.Add(new Span(new Run("x + `y`")) { Tag = "code", FontFamily = new FontFamily("Consolas") }); document.Blocks.Add(inlineCode);

                string html = DocumentCodec.Html(document, "测试 & document");
                Assert(html.Contains("data:image/png;base64,"), "HTML embeds image bytes");
                Assert(html.Contains("&lt;AI&gt; &amp;"), "HTML escapes text and title");
                Assert(!html.Contains("<script"), "HTML contains no executable scripts");
                var restored = DocumentCodec.ReadHtml(html);
                Assert(DocumentCodec.Text(restored) == DocumentCodec.Text(document), "HTML round trip preserves Unicode, line breaks, code and list text");
                Assert(DocumentCodec.ParagraphTag(DocumentCodec.Paragraphs(restored.Blocks).First()) == "h1", "HTML preserves heading level");
                Assert(DocumentCodec.Paragraphs(restored.Blocks).SelectMany(x => DocumentCodec.Images(x.Inlines)).Count() == 2, "HTML restores both embedded images");
                var restoredImage = DocumentCodec.Paragraphs(restored.Blocks).SelectMany(x => DocumentCodec.Images(x.Inlines)).First();
                Assert((string)restoredImage.Tag == "截图 [测试] & 中文", "HTML preserves image descriptions");
                Assert(DocumentCodec.ImageBytes(restoredImage).SequenceEqual(imageBytes), "HTML preserves image pixels");
                Assert(((System.Windows.Documents.List)restored.Blocks.ElementAt(2)).StartIndex == 3, "HTML preserves ordered list start");
                Assert(DocumentCodec.Html(restored, "测试 & document") == html, "HTML format is stable across repeated saves");

                string htmlPath = Path.Combine(directory, "中文文档.html");
                DocumentCodec.AtomicWrite(htmlPath, html); DocumentCodec.AtomicWrite(htmlPath, html);
                Assert(DocumentCodec.Text(DocumentCodec.Read(htmlPath)) == DocumentCodec.Text(document), "atomic overwrite produces a complete readable document");
                Assert(Directory.GetFiles(directory, ".paperleaf-*.tmp").Length == 0, "atomic save cleans temporary files");
                string markdownPath = Path.Combine(directory, "给 AI #1.md");
                MarkdownCodec.Export(document, markdownPath);
                string markdown = File.ReadAllText(markdownPath);
                Assert(markdown.StartsWith("# 文字"), "Markdown contains semantic heading");
                Assert(markdown.Contains("**粗体**") && markdown.Contains("*斜体*"), "Markdown preserves emphasis");
                Assert(markdown.Contains("3. 第一项") && markdown.Contains("4. 第二项"), "Markdown preserves ordered lists");
                Assert(markdown.Contains("````\nconst"), "Markdown chooses a safe code fence");
                Assert(markdown.Contains("%23"), "Markdown escapes spaces and hash in asset links");
                Assert(Directory.GetFiles(Path.Combine(directory, "给 AI #1.assets"), "*.png").Length == 1, "Markdown deduplicates images by content");
                var imported = DocumentCodec.Read(markdownPath);
                Assert(DocumentCodec.Paragraphs(imported.Blocks).SelectMany(x => DocumentCodec.Images(x.Inlines)).Count() == 2, "Markdown import restores relative image links");
                Assert(DocumentCodec.Text(imported).Contains("x + `y`"), "Markdown imports inline code with backticks");
                Assert(DocumentCodec.Text(imported).Contains("const text = `<hello>`;\n```\nend"), "Markdown preserves fenced code verbatim");
                string reexport = MarkdownCodec.Write(imported, delegate(Image image) { return "image.png"; });
                Assert(reexport.Contains("**粗体**") && reexport.Contains("*斜体*"), "Markdown import preserves emphasis");
                Assert(reexport.Contains("截图 \\[测试\\]"), "Markdown preserves escaped image descriptions");
                var both = MarkdownCodec.Read("***粗斜体*** 与 **粗体**", directory);
                Assert(MarkdownCodec.Write(both, delegate(Image image) { return "unused"; }).Contains("***粗斜体***"), "Markdown imports combined bold and italic");
                var spaced = DocumentCodec.Empty(); ((Paragraph)spaced.Blocks.FirstBlock).Inlines.Add(new Bold(new Run(" 尾随空白 ")));
                Assert(MarkdownCodec.Write(spaced, delegate(Image image) { return "unused"; }).Contains(" **尾随空白**"), "Markdown places whitespace outside emphasis delimiters");

                var nested = MarkdownCodec.Read("- 父项\n  - 子项\n- 下一项\n\n尾部", directory);
                var nestedHtml = DocumentCodec.Html(nested, "嵌套");
                Assert(nestedHtml.Contains("<li><p>父项</p><ul>"), "nested Markdown lists retain hierarchy");
                var external = MarkdownCodec.Read("![外部图片](https://example.com/image.png)\n\n![越界](../outside.png)", directory);
                Assert(DocumentCodec.Paragraphs(external.Blocks).SelectMany(x => DocumentCodec.Images(x.Inlines)).Count() == 0, "Markdown does not fetch remote or out-of-folder images");
                Assert(DocumentCodec.Text(external).Contains("https://example.com"), "unsupported image references remain visible");
                Reject(delegate { DocumentCodec.ReadHtml("<!DOCTYPE html [<!ENTITY secret SYSTEM 'file:///C:/Windows/win.ini'>]><html><article><p>&secret;</p></article></html>"); }, "HTML rejects external entities");
                Reject(delegate { DocumentCodec.ReadHtml("<html><article><p><img src='https://example.com/a.png'/></p></article></html>"); }, "HTML rejects non-embedded images");
                Reject(delegate { DocumentCodec.MakeImage(new byte[] { 1, 2, 3 }, "bad"); }, "corrupt images fail without changing the document");
                var unsafeHtml = DocumentCodec.ReadHtml("<html><article><p>安全<script>bad()</script><a href='javascript:alert(1)'>链接</a></p></article></html>");
                string sanitized = DocumentCodec.Html(unsafeHtml, "安全");
                Assert(!sanitized.Contains("javascript:") && !sanitized.Contains("bad()"), "HTML strips scripts and unsafe links");

                var edit = new RichTextBox { Document = DocumentCodec.Empty() };
                edit.IsUndoEnabled = false; edit.IsUndoEnabled = true;
                var testWindow = new Window { Content = edit, Width = 200, Height = 100, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Opacity = 0 };
                testWindow.Show(); testWindow.UpdateLayout();
                var ep = (Paragraph)edit.Document.Blocks.FirstBlock;
                edit.BeginChange(); DocumentCodec.SetParagraphStyle(ep, "h1"); edit.EndChange();
                edit.Undo();
                Assert(DocumentCodec.ParagraphTag((Paragraph)edit.Document.Blocks.FirstBlock) == "p", "heading undo also restores exported semantics");
                testWindow.Close();

                // Verify the embedded view loads and every required named control exists.
                using (var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Paperleaf.MainWindow.xaml"))
                {
                    var view = (Window)System.Windows.Markup.XamlReader.Load(stream);
                    Assert(view.FindName("Editor") is RichTextBox && view.FindName("SidebarColumn") is ColumnDefinition, "embedded UI loads and names resolve");
                    view.Close();
                }
                string example = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "examples", "欢迎使用.html"));
                string appDocument = Path.Combine(directory, "欢迎使用.html");
                if (File.Exists(example)) File.Copy(example, appDocument); else File.Copy(htmlPath, appDocument);
                var controller = new EditorWindow(appDocument, Path.Combine(directory, "session"));
                controller.Window.ShowInTaskbar = false; controller.Window.ShowActivated = false; controller.Window.Opacity = 0;
                controller.Window.Show(); controller.Window.UpdateLayout();
                var liveEditor = (RichTextBox)controller.Window.FindName("Editor");
                Assert(System.Windows.Input.ApplicationCommands.Paste.CanExecute(null, liveEditor), "custom paste command supports formats rejected by WPF's native format filter");
                Assert(DocumentCodec.Paragraphs(liveEditor.Document.Blocks).SelectMany(x => DocumentCodec.Images(x.Inlines)).Any(), "full application starts and loads an embedded image document");
                Assert(liveEditor.ActualWidth > 500 && liveEditor.ActualHeight > 300, "full application lays out the editor surface");
                string originalText = DocumentCodec.Text(liveEditor.Document);
                int originalImages = DocumentCodec.Paragraphs(liveEditor.Document.Blocks).SelectMany(x => DocumentCodec.Images(x.Inlines)).Count();
                var imageData = new DataObject(); imageData.SetImage(bitmap);
                var imagePaste = new DataObjectPastingEventArgs(imageData, false, DataFormats.Bitmap);
                liveEditor.RaiseEvent(imagePaste);
                Assert(DocumentCodec.Paragraphs(liveEditor.Document.Blocks).SelectMany(x => DocumentCodec.Images(x.Inlines)).Count() == originalImages + 1, "paste handler inserts bitmap data as an embedded image");
                liveEditor.Undo();
                Assert(DocumentCodec.Paragraphs(liveEditor.Document.Blocks).SelectMany(x => DocumentCodec.Images(x.Inlines)).Count() == originalImages, "undo removes a pasted image in one operation");
                liveEditor.Redo();
                var redoneImages = DocumentCodec.Paragraphs(liveEditor.Document.Blocks).SelectMany(x => DocumentCodec.Images(x.Inlines)).ToArray();
                Assert(redoneImages.Length == originalImages + 1, "redo restores a pasted embedded image");
                Assert(DocumentCodec.ImageBytes(redoneImages.First()).SequenceEqual(imageBytes), "redo preserves pasted image pixels without external files");
                Assert(DocumentCodec.Html(liveEditor.Document, "撤销重做").Contains("data:image/png;base64,"), "document remains saveable after image undo and redo");
                liveEditor.Undo();
                var pngOnly = new DataObject(); pngOnly.SetData("PNG", new MemoryStream(imageBytes), false);
                liveEditor.RaiseEvent(new DataObjectPastingEventArgs(pngOnly, false, "PNG"));
                controller.Window.UpdateLayout();
                var pngInserted = DocumentCodec.Paragraphs(liveEditor.Document.Blocks).SelectMany(x => DocumentCodec.Images(x.Inlines)).First();
                Assert(DocumentCodec.ImageBytes(pngInserted).SequenceEqual(imageBytes) && pngInserted.ActualHeight > 0, "PNG-only image paste inserts a visible image in the real editor layout");
                liveEditor.Undo();
                var textData = new DataObject(DataFormats.UnicodeText, "粘贴验证\n第二行");
                liveEditor.RaiseEvent(new DataObjectPastingEventArgs(textData, false, DataFormats.UnicodeText));
                Assert(DocumentCodec.Text(liveEditor.Document).Contains("粘贴验证\n第二行"), "paste handler inserts multiline Unicode text");
                liveEditor.Undo();
                Assert(DocumentCodec.Text(liveEditor.Document) == originalText, "undo restores text after a multiline paste");
                liveEditor.SelectAll();
                var brokenImageData = new DataObject(); brokenImageData.SetData("PNG", new MemoryStream(new byte[] { 1, 2, 3 }), false);
                liveEditor.RaiseEvent(new DataObjectPastingEventArgs(brokenImageData, false, "PNG"));
                Assert(DocumentCodec.Text(liveEditor.Document) == originalText && DocumentCodec.Paragraphs(liveEditor.Document.Blocks).SelectMany(x => DocumentCodec.Images(x.Inlines)).Count() == originalImages,
                    "failed image paste preserves selected text and existing images");
                liveEditor.Selection.Select(liveEditor.Document.ContentStart, liveEditor.Document.ContentStart);
                var readButton = (System.Windows.Controls.Primitives.ToggleButton)controller.Window.FindName("ReadButton");
                readButton.IsChecked = true; readButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert(liveEditor.IsReadOnly, "reading mode makes the editor read-only");
                Assert(!((Button)controller.Window.FindName("ColorButton")).IsEnabled, "reading mode disables text color controls");
                Assert(!System.Windows.Input.ApplicationCommands.Paste.CanExecute(null, liveEditor), "custom clipboard command is disabled in reading mode");
                liveEditor.RaiseEvent(new DataObjectPastingEventArgs(textData, false, DataFormats.UnicodeText));
                Assert(DocumentCodec.Text(liveEditor.Document) == originalText, "reading mode blocks paste mutations");
                readButton.IsChecked = false; readButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert(!liveEditor.IsReadOnly, "reading mode returns to editing");

                EditorFeatureTests.Palette(controller.Window, liveEditor, Assert);
                ((Button)controller.Window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var savedByApp = DocumentCodec.Read(appDocument);
                Assert(DocumentCodec.Text(savedByApp) == originalText, "application save handler writes the edited document correctly");
                Assert(DocumentCodec.Paragraphs(savedByApp.Blocks).SelectMany(x => DocumentCodec.Images(x.Inlines)).Count() == originalImages, "application save handler preserves embedded images");
                Assert(!controller.Window.Title.StartsWith("●"), "successful application save clears the unsaved indicator");
                controller.Window.UpdateLayout();
                var visual = (FrameworkElement)controller.Window.Content;
                var preview = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                preview.Render(visual);
                var previewEncoder = new PngBitmapEncoder(); previewEncoder.Frames.Add(BitmapFrame.Create(preview));
                using (var output = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "interface-preview.png"))) previewEncoder.Save(output);
                controller.Window.Width = 840; controller.Window.Height = 580; controller.Window.UpdateLayout();
                Assert(liveEditor.ActualWidth > 400, "editor stays usable at the minimum window width");
                var help = (Button)controller.Window.FindName("HelpButton");
                Assert(help.TranslatePoint(new Point(0, help.ActualHeight), controller.Window).Y < controller.Window.ActualHeight - 32, "sidebar help remains visible at minimum window height");
                var recents = (StackPanel)controller.Window.FindName("RecentPanel");
                Assert(recents.ActualHeight > 30 && ((Border)controller.Window.FindName("SidebarTip")).Visibility == Visibility.Collapsed, "small windows reserve sidebar space for recent documents");
                var color = (Button)controller.Window.FindName("ColorButton");
                var toolbar = (FrameworkElement)controller.Window.FindName("FormatTools");
                Assert(color.ActualWidth > 50 && color.TranslatePoint(new Point(color.ActualWidth, color.ActualHeight), toolbar).Y <= toolbar.ActualHeight + 1, "color control fits the wrapping toolbar at minimum window size");
                var minimumPreview = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                minimumPreview.Render(visual);
                var minimumEncoder = new PngBitmapEncoder(); minimumEncoder.Frames.Add(BitmapFrame.Create(minimumPreview));
                using (var output = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "interface-minimum-preview.png"))) minimumEncoder.Save(output);
                controller.Window.Close();
                Assert(Directory.GetFiles(Path.Combine(directory, "session"), "*.lock").Length == 0, "clean application exit releases its session lock");
                log.AppendLine("SUCCESS: " + count + " checks passed.");
            }
            catch (Exception e) { log.AppendLine(e.ToString()); exit = 1; }
            finally
            {
                // Delete only the unique test directory created by this invocation.
                try { Directory.Delete(directory, true); } catch { }
                File.WriteAllText(resultPath, log.ToString(), DocumentCodec.Utf8);
            }
            return exit;
        }
    }
}
