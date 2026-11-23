using System;
using System.ComponentModel;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Paperleaf
{
    // RichTextBox serializes UI elements into XAML when undoing deletion. The
    // WPF stream decoder is internal and cannot be serialized; expose the PNG
    // bytes instead so undo/redo never depends on an external image file.
    public sealed class EmbeddedImage : Image
    {
        private string pngData;
        public string PngData
        {
            get { return pngData; }
            set
            {
                if (value == pngData) return;
                byte[] bytes = Convert.FromBase64String(value);
                using (var stream = new MemoryStream(bytes))
                {
                    var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    bitmap.Freeze(); base.Source = bitmap;
                }
                pngData = value;
            }
        }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public new ImageSource Source { get { return base.Source; } set { base.Source = value; } }
        public bool ShouldSerializeSource() { return false; }
        public bool ShouldSerializeContextMenu() { return false; }
    }
}
