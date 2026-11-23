using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Paperleaf
{
    internal sealed class EditorWindow
    {
        public Window Window { get; private set; }
        private readonly RichTextBox editor;
        private readonly string storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Paperleaf");
        private readonly string recoveryPath;
        private FileStream sessionLock;
        private string currentPath;
        private bool dirty, loading, restoringStyle;
        private long version, backupVersion = -1;
        private DateTime savedWriteTime;
        private long savedLength;
        private readonly DispatcherTimer statsTimer;
        private readonly DispatcherTimer recoveryTimer;
        private readonly System.Collections.Generic.List<string> recent = new System.Collections.Generic.List<string>();
        private readonly ConditionalWeakTable<Image, object> boundImages = new ConditionalWeakTable<Image, object>();
        private DispatcherTimer pasteRetryTimer;
        private PasteContext activePaste;
        private bool closed;
        private DocumentSearch.Match currentMatch;
        private long matchVersion;

        private T Find<T>(string name) where T : class { return Window.FindName(name) as T; }
        private void Button(string name, Action action) { Find<Button>(name).Click += delegate { Guard(action); }; }

        public EditorWindow(string initialPath, string storageOverride = null)
        {
            if (storageOverride != null) storage = storageOverride;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Paperleaf.MainWindow.xaml")) Window = (Window)XamlReader.Load(stream);
            using (var icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("Paperleaf.Icon")) Window.Icon = BitmapFrame.Create(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            editor = Find<RichTextBox>("Editor");
            editor.Document = DocumentCodec.Empty();
            recoveryPath = Path.Combine(storage, "draft-" + Guid.NewGuid().ToString("N") + ".html");
            try { Directory.CreateDirectory(storage); sessionLock = new FileStream(recoveryPath + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { SetStatus("草稿备份暂不可用，请及时保存文档。"); }
            catch (UnauthorizedAccessException) { SetStatus("草稿目录无法写入，请及时保存文档。"); }

            Button("NewButton", NewDocument); Button("OpenButton", OpenDialog); Button("SaveButton", delegate { Save(false); });
            Button("ExportButton", ExportMarkdown); Button("MoreButton", MoreMenu); Button("ImageButton", ImageDialog);
            Button("BoldButton", delegate { Format(EditingCommands.ToggleBold); });
            Button("ItalicButton", delegate { Format(EditingCommands.ToggleItalic); });
            Button("ColorButton", ColorMenu);
            Button("ListButton", delegate { Format(EditingCommands.ToggleBullets); });
            Button("UndoButton", delegate { if (!editor.IsReadOnly) editor.Undo(); editor.Focus(); });
            Button("RedoButton", delegate { if (!editor.IsReadOnly) editor.Redo(); editor.Focus(); });
            Button("FindButton", ShowFind); Button("FindNextButton", FindNext); Button("FindPreviousButton", FindPrevious);
            Button("ReplaceOneButton", ReplaceOne); Button("ReplaceAllButton", ReplaceAll);
            Find<ToggleButton>("ReplaceToggle").Click += delegate { SetReplaceVisible(Find<ToggleButton>("ReplaceToggle").IsChecked == true); };
            Button("CloseFindButton", delegate { Find<Border>("FindBar").Visibility = Visibility.Collapsed; editor.Focus(); });
            Button("HelpButton", Help);
            Find<ToggleButton>("ReadButton").Click += delegate { SetReadMode(); };
            Find<ToggleButton>("FocusButton").Click += delegate { SetFocusMode(); };
            Find<ComboBox>("ParagraphStyle").SelectionChanged += delegate { if (!restoringStyle) Guard(ApplyParagraphStyle); };
            Find<TextBox>("FindInput").TextChanged += delegate { currentMatch = null; Find<TextBlock>("FindResult").Text = "Enter 下一处 · Shift+Enter 上一处 · 不区分大小写"; };
            Find<TextBox>("FindInput").KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { Guard(delegate { NavigateMatch((Keyboard.Modifiers & ModifierKeys.Shift) != 0); }); e.Handled = true; }
            };
            Find<TextBox>("ReplaceInput").KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { Guard(ReplaceOne); e.Handled = true; } };

            statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            statsTimer.Tick += delegate { statsTimer.Stop(); UpdateStats(); };
            recoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            recoveryTimer.Tick += delegate { BackupDraft(); };
            recoveryTimer.Start();
            editor.TextChanged += delegate { if (!loading) { dirty = true; version++; UpdateCaption(); statsTimer.Stop(); statsTimer.Start(); } };
            editor.SelectionChanged += delegate { SyncParagraphStyle(); };
            editor.SizeChanged += delegate { UpdateImageWidths(); };
            Window.SizeChanged += delegate { Find<Border>("SidebarTip").Visibility = Window.ActualHeight < 680 ? Visibility.Collapsed : Visibility.Visible; };
            DataObject.AddPastingHandler(editor, OnPaste);
            // WPF's built-in Paste command disables PNG-only clipboard data before
            // the Pasting event can fire. Handle the command itself as well.
            editor.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste,
                delegate(object sender, ExecutedRoutedEventArgs e) { e.Handled = true; CancelPasteRetry(); activePaste = new PasteContext(editor, version); PasteClipboard(0, activePaste); },
                delegate(object sender, CanExecuteRoutedEventArgs e) { e.CanExecute = !editor.IsReadOnly; e.Handled = true; }));
            Window.PreviewDragOver += OnDragOver;
            Window.PreviewDrop += OnDrop;
            Window.Closing += OnClosing;
            Window.Closed += delegate { closed = true; CancelPasteRetry(); statsTimer.Stop(); recoveryTimer.Stop(); if (sessionLock != null) sessionLock.Dispose(); TryDelete(recoveryPath + ".lock"); };
            Window.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape)
                {
                    if (Find<Border>("FindBar").Visibility == Visibility.Visible) Find<Border>("FindBar").Visibility = Visibility.Collapsed;
                    else if (Find<ToggleButton>("FocusButton").IsChecked == true) { Find<ToggleButton>("FocusButton").IsChecked = false; SetFocusMode(); }
                    editor.Focus(); e.Handled = true;
                }
            };
            Bind(Key.N, ModifierKeys.Control, NewDocument);
            Bind(Key.O, ModifierKeys.Control, OpenDialog);
            Bind(Key.S, ModifierKeys.Control, delegate { Save(false); });
            Bind(Key.S, ModifierKeys.Control | ModifierKeys.Shift, delegate { Save(true); });
            Bind(Key.E, ModifierKeys.Control | ModifierKeys.Shift, ExportMarkdown);
            Bind(Key.I, ModifierKeys.Control | ModifierKeys.Shift, ImageDialog);
            Bind(Key.F, ModifierKeys.Control, ShowFind);
            Bind(Key.H, ModifierKeys.Control, ShowReplace);
            Bind(Key.F3, ModifierKeys.None, FindNext);
            Bind(Key.F3, ModifierKeys.Shift, FindPrevious);
            Bind(Key.F11, ModifierKeys.None, delegate { var toggle = Find<ToggleButton>("FocusButton"); toggle.IsChecked = toggle.IsChecked != true; SetFocusMode(); });
            Bind(Key.R, ModifierKeys.Control | ModifierKeys.Shift, delegate { var toggle = Find<ToggleButton>("ReadButton"); toggle.IsChecked = toggle.IsChecked != true; SetReadMode(); });

            LoadRecent(); UpdateStats(); UpdateCaption();
            Window.Loaded += delegate
            {
                Guard(delegate
                {
                    if (!string.IsNullOrWhiteSpace(initialPath)) OpenPath(Path.GetFullPath(initialPath));
                    else RecoverDraft();
                    editor.Focus();
                });
            };
        }

        private void Bind(Key key, ModifierKeys modifiers, Action action)
        {
            var command = new RoutedCommand();
            Window.CommandBindings.Add(new CommandBinding(command, delegate { Guard(action); }));
            Window.InputBindings.Add(new KeyBinding(command, key, modifiers));
        }

        private void Guard(Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                MessageBox.Show(Window, "操作未完成，当前文档仍保留在窗口中。\n\n" + e.Message, "纸间", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetStatus("操作未完成，请重试。");
            }
        }

        private void SetStatus(string text) { Find<TextBlock>("StatusText").Text = text; }
        private void UpdateCaption()
        {
            string name = currentPath == null ? "未命名文档" : Path.GetFileNameWithoutExtension(currentPath);
            Find<TextBlock>("DocumentName").Text = name;
            Window.Title = (dirty ? "● " : "") + name + " — 纸间 · Paperleaf";
            Find<TextBlock>("SaveState").Text = dirty ? "有未保存的修改" : currentPath == null ? "尚未保存" : "已保存 · " + currentPath;
            Find<TextBlock>("SaveState").ToolTip = currentPath ?? "按 Ctrl+S 保存为一个包含图片的 HTML 文档";
        }

        private void UpdateStats()
        {
            string text = DocumentCodec.Text(editor.Document);
            int characters = text.Count(c => !char.IsWhiteSpace(c));
            var embeddedImages = DocumentCodec.Paragraphs(editor.Document.Blocks).SelectMany(p => DocumentCodec.Images(p.Inlines)).ToArray();
            foreach (var image in embeddedImages) AttachImageMenu(image);
            int images = embeddedImages.Length;
            Find<TextBlock>("StatsText").Text = characters.ToString("N0") + " 字 · " + images + " 张图片";
            Find<TextBlock>("EmptyHint").Visibility = characters == 0 && images == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool ConfirmDiscard()
        {
            if (!dirty) return true;
            var answer = MessageBox.Show(Window, "当前文档有未保存的修改。是否先保存？\n\n选择“否”将放弃当前修改。", "保存文档", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes) return Save(false);
            return true;
        }

        private void ReplaceDocument(FlowDocument document, string path, bool changed)
        {
            CancelPasteRetry();
            currentMatch = null;
            loading = true;
            try { editor.Document = document; editor.IsUndoEnabled = false; editor.IsUndoEnabled = true; }
            finally { loading = false; }
            currentPath = path; dirty = changed; version++; backupVersion = -1;
            foreach (var image in DocumentCodec.Paragraphs(document.Blocks).SelectMany(p => DocumentCodec.Images(p.Inlines))) AttachImageMenu(image);
            UpdateImageWidths();
            RememberDiskState();
            Find<ToggleButton>("ReadButton").IsChecked = false; SetReadMode();
            UpdateCaption(); UpdateStats(); SyncParagraphStyle();
            editor.CaretPosition = document.ContentStart; editor.ScrollToHome(); editor.Focus();
        }

        private void NewDocument()
        {
            if (!ConfirmDiscard()) return;
            ReplaceDocument(DocumentCodec.Empty(), null, false); TryDelete(recoveryPath); SetStatus("新文档已准备好");
        }

        private void OpenDialog()
        {
            var dialog = new OpenFileDialog { Title = "打开图文文档", Filter = "支持的文档|*.html;*.htm;*.md;*.markdown;*.txt|纸间 HTML 文档|*.html;*.htm|Markdown|*.md;*.markdown|纯文本|*.txt", CheckFileExists = true };
            if (dialog.ShowDialog(Window) == true) OpenPath(dialog.FileName);
        }

        private void OpenPath(string path)
        {
            if (!ConfirmDiscard()) return;
            // Confirm first: saving the current file may update the very file being opened.
            // Parse before replacing the current document; a failed import leaves it intact.
            FlowDocument document = DocumentCodec.Read(path);
            string extension = Path.GetExtension(path).ToLowerInvariant();
            bool import = extension != ".html" && extension != ".htm";
            ReplaceDocument(document, import ? null : Path.GetFullPath(path), import);
            TryDelete(recoveryPath); AddRecent(path);
            SetStatus(import ? "已导入 · 保存时会生成含图片的 HTML 文档" : "已打开 · " + Path.GetFileName(path));
        }

        private void RememberDiskState()
        {
            if (currentPath != null && File.Exists(currentPath)) { var file = new FileInfo(currentPath); savedWriteTime = file.LastWriteTimeUtc; savedLength = file.Length; }
        }

        private bool Save(bool saveAs)
        {
            string destination = currentPath;
            if (saveAs || destination == null)
            {
                var dialog = new SaveFileDialog { Title = "保存图文文档", Filter = "图文文档（图片内嵌）|*.html", DefaultExt = ".html", AddExtension = true,
                    FileName = currentPath == null ? SuggestedName() + ".html" : Path.GetFileName(currentPath), OverwritePrompt = true };
                if (currentPath != null) dialog.InitialDirectory = Path.GetDirectoryName(currentPath);
                if (dialog.ShowDialog(Window) != true) return false;
                destination = dialog.FileName;
                if (Path.GetExtension(destination).ToLowerInvariant() != ".html" && Path.GetExtension(destination).ToLowerInvariant() != ".htm")
                { MessageBox.Show(Window, "请使用 .html 或 .htm 扩展名保存图文文档。", "保存文档"); return false; }
            }
            else if (File.Exists(destination))
            {
                var info = new FileInfo(destination);
                if ((info.LastWriteTimeUtc != savedWriteTime || info.Length != savedLength) && MessageBox.Show(Window,
                    "这个文件已在其他地方被修改。仍要用当前内容覆盖吗？\n\n可取消后使用“另存为”保留两个版本。", "文件已变化", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            }
            try
            {
                string html = DocumentCodec.Html(editor.Document, Path.GetFileNameWithoutExtension(destination));
                if (DocumentCodec.Utf8.GetByteCount(html) > DocumentCodec.MaxFileBytes) throw new InvalidDataException("文档超过 120 MB，请缩小图片或拆分文档后保存。");
                DocumentCodec.AtomicWrite(destination, html);
                currentPath = Path.GetFullPath(destination); dirty = false; RememberDiskState();
                TryDelete(recoveryPath); AddRecent(currentPath); UpdateCaption(); SetStatus("已保存 · " + DateTime.Now.ToString("HH:mm"));
                return true;
            }
            catch (Exception e) { MessageBox.Show(Window, "保存失败，文档仍在窗口中，请尝试另存到其他位置。\n\n" + e.Message, "保存文档", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        }

        private string SuggestedName()
        {
            string title = DocumentCodec.Text(editor.Document).Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "未命名文档";
            title = new string(title.Trim().Where(c => !Path.GetInvalidFileNameChars().Contains(c) && !char.IsControl(c)).Take(40).ToArray()).Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(title) ? "未命名文档" : title;
        }

        private void ExportMarkdown()
        {
            var dialog = new SaveFileDialog { Title = "导出给 AI · Markdown 与图片", Filter = "Markdown 文档|*.md", DefaultExt = ".md", AddExtension = true,
                FileName = (currentPath == null ? SuggestedName() : Path.GetFileNameWithoutExtension(currentPath)) + ".md" };
            if (dialog.ShowDialog(Window) != true) return;
            if (!Path.GetExtension(dialog.FileName).Equals(".md", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请使用 .md 扩展名导出。");
            MarkdownCodec.Export(editor.Document, dialog.FileName);
            SetStatus("已导出 Markdown 与图片 · " + Path.GetFileName(dialog.FileName));
            MessageBox.Show(Window, "已导出：" + dialog.FileName + "\n\n图片保存在旁边的同名 .assets 文件夹中。请把 Markdown 和图片文件夹一起交给 AI；能否识图取决于所用 AI 是否支持读取图片。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void MoreMenu()
        {
            var menu = new ContextMenu();
            AddMenu(menu, "另存为…   Ctrl+Shift+S", delegate { Save(true); });
            AddMenu(menu, "导出纯文本…", delegate
            {
                var dialog = new SaveFileDialog { Title = "导出纯文本（不含图片）", Filter = "纯文本|*.txt", DefaultExt = ".txt", FileName = SuggestedName() + ".txt" };
                if (dialog.ShowDialog(Window) == true) { DocumentCodec.AtomicWrite(dialog.FileName, DocumentCodec.Text(editor.Document)); SetStatus("已导出纯文本（不含图片）"); }
            });
            menu.Items.Add(new Separator());
            AddMenu(menu, "使用说明", Help);
            menu.PlacementTarget = Find<Button>("MoreButton"); menu.IsOpen = true;
        }

        private void AddMenu(ContextMenu menu, string label, Action action)
        {
            var item = new MenuItem { Header = label }; item.Click += delegate { Guard(action); }; menu.Items.Add(item);
        }

        private void Format(RoutedUICommand command)
        {
            if (editor.IsReadOnly) return;
            editor.Focus(); command.Execute(null, editor);
        }

        private void SyncParagraphStyle()
        {
            var p = editor.CaretPosition.Paragraph;
            string tag = p == null ? "p" : DocumentCodec.ParagraphTag(p);
            restoringStyle = true;
            Find<ComboBox>("ParagraphStyle").SelectedIndex = tag == "h1" ? 1 : tag == "h2" ? 2 : tag == "h3" ? 3 : 0;
            restoringStyle = false;
            var brush = editor.Selection.GetPropertyValue(TextElement.ForegroundProperty) as Brush;
            Find<System.Windows.Shapes.Rectangle>("ColorSwatch").Fill = brush ?? Brushes.Gray;
        }

        private sealed class DialogOwner : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }
            public DialogOwner(Window window) { Handle = new System.Windows.Interop.WindowInteropHelper(window).Handle; }
        }

        private void ColorMenu()
        {
            if (editor.IsReadOnly) return;
            TextPointer start = editor.Selection.Start, end = editor.Selection.End;
            var menu = new ContextMenu();
            string[] names = { "默认", "黑色", "灰色", "红色", "橙色", "金色", "绿色", "青色", "蓝色", "紫色" };
            string[] colors = { TextColors.Default, "#111827", "#6B7280", "#C73E3A", "#B95D16", "#977012", "#2F7D4A", "#167D8D", "#245CC5", "#8546B2" };
            var selectedBrush = editor.Selection.GetPropertyValue(TextElement.ForegroundProperty) as Brush;
            string selected = TextColors.Hex(selectedBrush);
            for (int i = 0; i < names.Length; i++)
            {
                string color = colors[i];
                var item = new MenuItem { Header = names[i], Tag = color, IsCheckable = true, IsChecked = selectedBrush != null && color == selected,
                    Icon = new System.Windows.Shapes.Rectangle { Width = 14, Height = 14, Fill = TextColors.Brush(color), RadiusX = 3, RadiusY = 3 } };
                item.Click += delegate { Guard(delegate { ApplyTextColor(color, start, end); }); }; menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            AddMenu(menu, "更多颜色…", delegate
            {
                using (var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true })
                {
                    var current = TextColors.Brush(selected).Color;
                    dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
                    if (dialog.ShowDialog(new DialogOwner(Window)) == System.Windows.Forms.DialogResult.OK)
                        ApplyTextColor("#" + dialog.Color.R.ToString("X2") + dialog.Color.G.ToString("X2") + dialog.Color.B.ToString("X2"), start, end);
                }
            });
            var button = Find<Button>("ColorButton");
            button.ContextMenu = menu; menu.PlacementTarget = button; menu.IsOpen = true;
        }

        private void ApplyTextColor(string color, TextPointer start, TextPointer end)
        {
            if (editor.IsReadOnly) return;
            editor.Focus(); editor.Selection.Select(start, end);
            editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, TextColors.Brush(color));
            SyncParagraphStyle();
            SetStatus(start.CompareTo(end) == 0 ? "已设置接下来输入文字的颜色" : "已修改选中文字的颜色");
        }

        private void ApplyParagraphStyle()
        {
            if (editor.IsReadOnly) return;
            string tag = Find<ComboBox>("ParagraphStyle").SelectedIndex == 0 ? "p" : "h" + Find<ComboBox>("ParagraphStyle").SelectedIndex;
            var start = editor.Selection.Start;
            var end = editor.Selection.End;
            editor.BeginChange();
            try
            {
                foreach (var p in DocumentCodec.Paragraphs(editor.Document.Blocks).ToArray())
                    if (p.ContentEnd.CompareTo(start) >= 0 && p.ContentStart.CompareTo(end) <= 0) DocumentCodec.SetParagraphStyle(p, tag);
            }
            finally { editor.EndChange(); }
            editor.Focus();
        }

        private void SetReadMode()
        {
            bool read = Find<ToggleButton>("ReadButton").IsChecked == true;
            if (read) CancelPasteRetry();
            editor.IsReadOnly = read; editor.IsReadOnlyCaretVisible = !read;
            Find<FrameworkElement>("FormatTools").IsEnabled = !read;
            Find<Grid>("ReplaceRow").IsEnabled = !read;
            Find<ToggleButton>("ReadButton").Content = read ? "返回编辑" : "阅读";
            Find<TextBlock>("ModeState").Text = read ? "阅读模式 · 可选择和复制内容" : "编辑模式";
            editor.Focus();
        }

        private void SetFocusMode()
        {
            bool focus = Find<ToggleButton>("FocusButton").IsChecked == true;
            Find<Border>("Sidebar").Visibility = focus ? Visibility.Collapsed : Visibility.Visible;
            Find<ColumnDefinition>("SidebarColumn").Width = new GridLength(focus ? 0 : 210);
            editor.Focus();
        }

        private void ImageDialog()
        {
            if (editor.IsReadOnly) return;
            var dialog = new OpenFileDialog { Title = "插入图片", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff", Multiselect = true };
            if (dialog.ShowDialog(Window) != true) return;
            InsertImageFiles(dialog.FileNames);
        }

        private void InsertImageFiles(IEnumerable<string> files)
        {
            if (editor.IsReadOnly) return;
            // Decode all files first so a failed insertion does not remove selected text.
            var images = files.Select(path =>
            {
                if (new FileInfo(path).Length > DocumentCodec.MaxImageBytes) throw new InvalidDataException("图片超过 25 MB：" + Path.GetFileName(path));
                return DocumentCodec.MakeImage(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
            }).ToArray();
            editor.BeginChange();
            try { foreach (var image in images) InsertImage(image); }
            finally { editor.EndChange(); }
            SetStatus("已插入 " + images.Length + " 张图片 · 右键可编辑图片说明");
        }

        private void InsertImage(Image image)
        {
            if (editor.IsReadOnly) return;
            AttachImageMenu(image);
            editor.Selection.Text = "";
            var position = editor.CaretPosition.GetInsertionPosition(LogicalDirection.Forward);
            if (position == null) position = editor.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            var container = new InlineUIContainer(image, position) { BaselineAlignment = BaselineAlignment.Center };
            UpdateImageWidths();
            editor.CaretPosition = container.ElementEnd.GetInsertionPosition(LogicalDirection.Forward) ?? container.ElementEnd;
            editor.Focus();
        }

        private void UpdateImageWidths()
        {
            double available = Math.Max(80, Math.Min(720, editor.ActualWidth - 132));
            foreach (var image in DocumentCodec.Paragraphs(editor.Document.Blocks).SelectMany(p => DocumentCodec.Images(p.Inlines)))
            {
                var bitmap = image.Source as BitmapSource;
                image.Width = bitmap == null ? available : Math.Min(available, bitmap.PixelWidth);
            }
        }

        private void AttachImageMenu(Image image)
        {
            object bound;
            if (boundImages.TryGetValue(image, out bound)) return;
            boundImages.Add(image, new object());
            var menu = new ContextMenu();
            AddMenu(menu, "编辑图片说明…", delegate
            {
                if (editor.IsReadOnly) return;
                string description = Prompt("图片说明", "写下图片内容，帮助你和 AI 理解这张图片。", image.Tag as string ?? "图片");
                if (description == null) return;
                image.Tag = description; image.ToolTip = description; AutomationPropertiesHelper.SetImageName(image, description);
                dirty = true; version++; UpdateCaption();
            });
            AddMenu(menu, "复制图片", delegate { Clipboard.SetDataObject(ClipboardImages.CopyData(image), true); SetStatus("图片已复制"); });
            image.ContextMenu = menu;
        }

        private string Prompt(string title, string hint, string value)
        {
            var dialog = new Window { Title = title, Owner = Window, Width = 460, Height = 215, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Window.Background, FontFamily = Window.FontFamily };
            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
            var input = new TextBox { Text = value, Padding = new Thickness(8), MaxLength = 1000 }; panel.Children.Add(input);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(16, 7, 16, 7) };
            var ok = new Button { Content = "确定", IsDefault = true, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(8, 0, 0, 0) };
            ok.Click += delegate { dialog.DialogResult = true; }; row.Children.Add(cancel); row.Children.Add(ok); panel.Children.Add(row); dialog.Content = panel;
            dialog.Loaded += delegate { input.Focus(); input.SelectAll(); };
            return dialog.ShowDialog() == true ? input.Text : null;
        }

        private void OnPaste(object sender, DataObjectPastingEventArgs e)
        {
            e.CancelCommand();
            if (editor.IsReadOnly) return;
            Guard(delegate { PasteData(e.DataObject); });
        }

        private void CancelPasteRetry()
        {
            if (pasteRetryTimer != null) { pasteRetryTimer.Stop(); pasteRetryTimer = null; }
            activePaste = null;
        }

        private void PasteClipboard(int attempt, PasteContext request)
        {
            if (closed || activePaste != request || !request.IsCurrent(editor, version)) return;
            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null) { SetStatus("剪贴板暂时没有内容，请重新复制后粘贴。"); return; }
                PasteData(data);
            }
            catch (ExternalException)
            {
                if (attempt >= 4) { SetStatus("剪贴板正被其他软件占用，请稍后再次按 Ctrl+V。"); return; }
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80 * (attempt + 1)) };
                pasteRetryTimer = timer;
                timer.Tick += delegate { timer.Stop(); PasteClipboard(attempt + 1, request); };
                timer.Start();
            }
            catch (Exception error) { SetStatus("粘贴未完成：" + error.Message); }
        }

        private void PasteData(IDataObject data)
        {
            if (editor.IsReadOnly) return;
            Image image; string error;
            if (ClipboardImages.TryRead(data, out image, out error))
            {
                editor.BeginChange(); try { InsertImage(image); } finally { editor.EndChange(); }
                SetStatus("已粘贴图片 · 右键可编辑图片说明"); return;
            }
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0 && files.All(IsImagePath)) { InsertImageFiles(files); return; }
            }
            if (error != null) { SetStatus("图片未能粘贴：" + error); return; }
            string text = data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string;
            if (text != null) { editor.Selection.Text = text; editor.CaretPosition = editor.Selection.End; SetStatus("已粘贴文字"); return; }
            SetStatus("剪贴板中没有可用的文字或图片，请重新复制图片后粘贴。");
        }

        private static bool IsImagePath(string path) { return new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" }.Contains(Path.GetExtension(path).ToLowerInvariant()); }
        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
        }
        private void OnDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Handled = true;
            Guard(delegate
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return;
                if (files.All(IsImagePath)) InsertImageFiles(files);
                else if (files.Length == 1) OpenPath(files[0]);
                else SetStatus("请一次打开一份文档，或拖入多张图片。");
            });
        }

        private void ShowFind()
        {
            Find<Border>("FindBar").Visibility = Visibility.Visible;
            var input = Find<TextBox>("FindInput"); input.Focus(); input.SelectAll();
        }

        private void ShowReplace()
        {
            SetReplaceVisible(true); ShowFind();
        }

        private void SetReplaceVisible(bool visible)
        {
            Find<ToggleButton>("ReplaceToggle").IsChecked = visible;
            Find<Grid>("ReplaceRow").Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            Find<Border>("FindBar").Visibility = Visibility.Visible;
        }

        private void FindNext() { NavigateMatch(false); }
        private void FindPrevious() { NavigateMatch(true); }

        private bool NavigateMatch(bool previous)
        {
            string query = Find<TextBox>("FindInput").Text;
            if (string.IsNullOrEmpty(query)) { currentMatch = null; ShowFind(); return false; }
            Find<Border>("FindBar").Visibility = Visibility.Visible;
            var match = previous ? DocumentSearch.FindPrevious(editor.Document, editor.Selection.Start, query) : DocumentSearch.FindNext(editor.Document, editor.Selection.End, query);
            currentMatch = null;
            if (match == null) { Find<TextBlock>("FindResult").Text = "没有找到"; return false; }
            editor.Selection.Select(match.Start, match.End);
            match.Start = editor.Selection.Start; match.End = editor.Selection.End;
            currentMatch = match; matchVersion = version;
            editor.Focus(); var rect = match.Start.GetCharacterRect(LogicalDirection.Forward);
            editor.ScrollToVerticalOffset(editor.VerticalOffset + rect.Top - editor.ActualHeight / 3);
            Find<TextBlock>("FindResult").Text = match.Wrapped ? (previous ? "已回到末尾" : "已回到开头") : "已找到";
            return true;
        }

        private void ReplaceOne()
        {
            if (editor.IsReadOnly) return;
            if (currentMatch == null || matchVersion != version || currentMatch.Start.CompareTo(editor.Selection.Start) != 0 || currentMatch.End.CompareTo(editor.Selection.End) != 0)
            {
                if (NavigateMatch(false)) Find<TextBlock>("FindResult").Text = "已定位一处，点击“替换此处”进行替换";
                return;
            }
            CancelPasteRetry();
            var range = new TextRange(currentMatch.Start, currentMatch.End);
            editor.BeginChange();
            try { DocumentReplace.One(range, Find<TextBox>("ReplaceInput").Text); }
            finally { editor.EndChange(); }
            editor.Selection.Select(range.End, range.End); currentMatch = null;
            bool found = NavigateMatch(false);
            Find<TextBlock>("FindResult").Text = found ? "已替换 1 处，已定位下一处" : "已替换 1 处，没有更多匹配";
        }

        private void ReplaceAll()
        {
            if (editor.IsReadOnly) return;
            string query = Find<TextBox>("FindInput").Text;
            if (string.IsNullOrEmpty(query)) { ShowReplace(); Find<TextBlock>("FindResult").Text = "请输入要查找的文字"; return; }
            CancelPasteRetry();
            int count = DocumentReplace.All(editor, query, Find<TextBox>("ReplaceInput").Text);
            currentMatch = null;
            Find<TextBlock>("FindResult").Text = count == 0 ? "没有找到" : "已替换 " + count.ToString("N0") + " 处 · Ctrl+Z 可一次撤销";
            editor.Focus();
        }

        private void LoadRecent()
        {
            try
            {
                string path = Path.Combine(storage, "recent.txt");
                if (File.Exists(path)) recent.AddRange(File.ReadAllLines(path).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(6));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            RenderRecent();
        }
        private void AddRecent(string path)
        {
            recent.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase)); recent.Insert(0, path);
            if (recent.Count > 6) recent.RemoveRange(6, recent.Count - 6);
            try { DocumentCodec.AtomicWrite(Path.Combine(storage, "recent.txt"), string.Join("\n", recent)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            RenderRecent();
        }
        private void RenderRecent()
        {
            var panel = Find<StackPanel>("RecentPanel"); panel.Children.Clear();
            if (recent.Count == 0) panel.Children.Add(new TextBlock { Text = "还没有打开过文档", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(5, 8, 0, 0) });
            foreach (string path in recent)
            {
                string captured = path;
                var button = new Button { HorizontalContentAlignment = HorizontalAlignment.Left, ToolTip = path, Padding = new Thickness(7, 9, 7, 9), Margin = new Thickness(0, 2, 0, 0) };
                button.Content = new TextBlock { Text = Path.GetFileNameWithoutExtension(path), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 145 };
                button.Click += delegate { Guard(delegate { OpenPath(captured); }); }; panel.Children.Add(button);
            }
        }

        private void BackupDraft()
        {
            if (!dirty || version == backupVersion || sessionLock == null) return;
            try { DocumentCodec.AtomicWrite(recoveryPath, DocumentCodec.Html(editor.Document, "未保存的草稿")); backupVersion = version; SetStatus("草稿已备份 · " + DateTime.Now.ToString("HH:mm")); }
            catch (Exception) { SetStatus("草稿备份失败，请按 Ctrl+S 手动保存。"); }
        }
        private void RecoverDraft()
        {
            if (!Directory.Exists(storage)) return;
            foreach (string path in Directory.GetFiles(storage, "draft-*.html").OrderByDescending(File.GetLastWriteTimeUtc))
            {
                if (path == recoveryPath) continue;
                try { using (var probe = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { } }
                catch (IOException) { continue; }
                var answer = MessageBox.Show(Window, "发现一份未正常关闭的草稿（" + File.GetLastWriteTime(path).ToString("MM-dd HH:mm") + "）。\n\n是否恢复？选择“否”会保留备份，稍后仍可恢复。", "恢复草稿", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                {
                    ReplaceDocument(DocumentCodec.Read(path), null, true);
                    BackupDraft();
                    // Remove the old recovery only after this session has a complete backup.
                    if (File.Exists(recoveryPath)) { TryDelete(path); TryDelete(path + ".lock"); }
                    SetStatus("草稿已恢复，请保存到你选择的位置。");
                }
                break;
            }
        }
        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        private void OnClosing(object sender, CancelEventArgs e)
        {
            try { if (!ConfirmDiscard()) { e.Cancel = true; return; } TryDelete(recoveryPath); }
            catch (Exception error) { e.Cancel = true; MessageBox.Show(Window, error.Message, "关闭文档"); }
        }
        private void Help()
        {
            MessageBox.Show(Window,
                "纸间 · Paperleaf 1.2.0\n\n" +
                "写文字：直接输入，工具栏可设置标题、加粗、斜体、颜色和列表。\n" +
                "改颜色：选中文字后点“颜色”，可选预设或“更多颜色”；也可先选颜色再输入。\n" +
                "加图片：粘贴截图、拖入图片，或点击“＋ 图片”。右键图片可写说明。\n" +
                "保存：Ctrl+S，保存为一个 HTML 文件，图片内嵌，移动时无需附带其他文件。\n" +
                "打开：支持纸间 HTML、TXT，以及常见 Markdown 语法与本地相对路径图片。\n" +
                "交给 AI：导出 Markdown 后，将 .md 和旁边的 .assets 图片文件夹一起提供。\n" +
                "阅读：Ctrl+Shift+R 切换阅读；F11 隐藏侧栏；Ctrl+F 查找。\n" +
                "替换：Ctrl+H，上一处 / 下一处定位，点“替换此处”或“全部替换”。\n" +
                "替换为留空可删除；不区分大小写；全部替换可用 Ctrl+Z 一次撤销。\n" +
                "保护草稿：有修改时每 12 秒备份一次，下次启动可恢复。仍请及时手动保存。\n\n" +
                "所有内容均保存在本机。软件无需联网，也不上传任何内容。\n" +
                "HTML 保存文字颜色；Markdown / TXT 导出不保留颜色。\n" +
                "Markdown 支持常用语法；复杂表格、公式和网页排版暂不支持。", "使用说明", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
