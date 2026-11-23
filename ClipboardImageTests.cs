using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Paperleaf
{
    internal static class ClipboardImageTests
    {
        internal static void Run(Action<bool, string> assert)
        {
            byte[] pixels = { 20, 60, 220, 255, 30, 140, 80, 255, 180, 80, 30, 255, 50, 170, 220, 255 };
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            byte[] png = DocumentCodec.ImageBytes(new Image { Source = bitmap });
            Image image; string error;
            foreach (string format in new[] { "PNG", "image/png", "image/x-png" })
            {
                var data = new DataObject(); var stream = new MemoryStream(png); stream.Position = stream.Length;
                data.SetData(format, stream, false);
                bool success = ClipboardImages.TryRead(data, out image, out error);
                assert(success && PixelBytes(image).SequenceEqual(pixels), "PNG-only clipboard imports " + format + " even with stream positioned at its end");
                assert(stream.CanRead && stream.Position == stream.Length, "clipboard reader preserves ownership and position of " + format + " streams");
            }
            var arrayData = new DataObject(); arrayData.SetData("PNG", png, false);
            assert(ClipboardImages.TryRead(arrayData, out image, out error) && PixelBytes(image).SequenceEqual(pixels), "PNG byte-array clipboard data imports correctly");

            byte[] transparent = { 70, 130, 210, 0, 20, 80, 160, 0, 30, 40, 190, 0, 10, 90, 120, 0 };
            var legacy = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, transparent, 8);
            var legacyData = new DataObject(); legacyData.SetImage(legacy);
            // This is a reproduction of the old path, which produced an invisible PNG.
            var oldResult = DocumentCodec.MakeImage(DocumentCodec.ImageBytes(new Image { Source = legacy }), "旧路径");
            assert(PixelBytes(oldResult).Where((b, i) => i % 4 == 3).All(b => b == 0), "reproduces blank image from legacy bitmap with zero alpha");
            assert(ClipboardImages.TryRead(legacyData, out image, out error) && PixelBytes(image).Where((b, i) => i % 4 == 3).All(b => b == 255), "legacy bitmap alpha repair makes messaging-app pixels visible");
            assert(PixelBytes(image).Where((b, i) => i % 4 != 3).SequenceEqual(transparent.Where((b, i) => i % 4 != 3)), "legacy alpha repair preserves original RGB values");
            var transparentPng = DocumentCodec.ImageBytes(new Image { Source = legacy });
            var preferred = new DataObject(); preferred.SetImage(bitmap); preferred.SetData("PNG", new MemoryStream(transparentPng), false);
            assert(ClipboardImages.TryRead(preferred, out image, out error) && PixelBytes(image).Where((b, i) => i % 4 == 3).All(b => b == 0), "encoded PNG takes priority and preserves intentional transparency");

            byte[] semi = (byte[])pixels.Clone(); semi[3] = 80;
            var semiData = new DataObject(); semiData.SetImage(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, semi, 8));
            assert(ClipboardImages.TryRead(semiData, out image, out error) && PixelBytes(image)[3] == 80, "valid partial alpha in bitmap data is preserved");
            var fallback = new DataObject(); fallback.SetData("PNG", new MemoryStream(new byte[] { 1, 2, 3 }), false); fallback.SetImage(bitmap);
            assert(ClipboardImages.TryRead(fallback, out image, out error) && PixelBytes(image).SequenceEqual(pixels), "broken PNG representation falls back to valid bitmap");

            foreach (bool topDown in new[] { false, true })
            {
                var dib = new DataObject(); dib.SetData(DataFormats.Dib, new MemoryStream(Dib(transparent, topDown, false)), false);
                bool success = ClipboardImages.TryRead(dib, out image, out error);
                byte[] expected = (byte[])transparent.Clone(); for (int i = 3; i < expected.Length; i += 4) expected[i] = 255;
                assert(success && PixelBytes(image).SequenceEqual(expected), "CF_DIB imports opaque pixels with correct " + (topDown ? "top-down" : "bottom-up") + " row order");
            }
            var dib24 = new DataObject(); dib24.SetData(DataFormats.Dib, new MemoryStream(Dib24()), false);
            assert(ClipboardImages.TryRead(dib24, out image, out error) && PixelBytes(image).SequenceEqual(new byte[] { 10, 20, 200, 255, 30, 140, 40, 255 }), "24-bit CF_DIB imports row padding correctly");
            byte[] v5Pixels = (byte[])pixels.Clone(); v5Pixels[3] = 100;
            var v5 = new DataObject(); v5.SetData(DataFormats.GetDataFormat(17).Name, new MemoryStream(Dib(v5Pixels, true, true)), false);
            assert(ClipboardImages.TryRead(v5, out image, out error) && PixelBytes(image).SequenceEqual(v5Pixels), "CF_DIBV5 imports explicit alpha masks and partial transparency");
            byte[] customMasks = Dib(pixels, true, true);
            Array.Copy(BitConverter.GetBytes(0x000000ffu), 0, customMasks, 40, 4);
            Array.Copy(BitConverter.GetBytes(0x00ff0000u), 0, customMasks, 48, 4);
            Array.Clear(customMasks, 52, 4);
            var masked = new DataObject(); masked.SetData(DataFormats.GetDataFormat(17).Name, new MemoryStream(customMasks), false);
            byte[] swapped = (byte[])pixels.Clone();
            for (int i = 0; i < swapped.Length; i += 4) { byte b = swapped[i]; swapped[i] = swapped[i + 2]; swapped[i + 2] = b; }
            assert(ClipboardImages.TryRead(masked, out image, out error) && PixelBytes(image).SequenceEqual(swapped), "opaque DIB bitfields honor nonstandard RGB channel masks");
            var brokenDib = new DataObject(); brokenDib.SetData(DataFormats.Dib, new MemoryStream(new byte[20]), false);
            assert(!ClipboardImages.TryRead(brokenDib, out image, out error) && error != null, "truncated image data reports failure instead of inserting a blank image");

            using (var drawing = new System.Drawing.Bitmap(2, 2))
            {
                drawing.SetPixel(0, 0, System.Drawing.Color.Red);
                var gdi = new DataObject(); gdi.SetData(DataFormats.Bitmap, drawing, false);
                assert(ClipboardImages.TryRead(gdi, out image, out error) && ((BitmapSource)image.Source).PixelWidth == 2, "GDI bitmap objects from other applications are supported");
            }
            var text = new DataObject(DataFormats.UnicodeText, "普通文字");
            assert(!ClipboardImages.TryRead(text, out image, out error) && error == null, "plain-text clipboard data remains available to the text paste path");
            var copied = ClipboardImages.CopyData(DocumentCodec.MakeImage(png, "复制图片"));
            assert(copied.GetDataPresent("PNG", false) && copied.GetDataPresent(DataFormats.Bitmap), "copying an image publishes both PNG and bitmap representations");
            assert(ClipboardImages.TryRead(copied, out image, out error) && PixelBytes(image).SequenceEqual(pixels), "copy and paste within the editor retains image pixels");
        }

        private static byte[] PixelBytes(Image image)
        {
            var source = (BitmapSource)image.Source;
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = converted.PixelWidth * 4;
            var bytes = new byte[stride * converted.PixelHeight]; converted.CopyPixels(bytes, stride, 0); return bytes;
        }

        private static byte[] Dib(byte[] topDownPixels, bool topDown, bool v5)
        {
            int header = v5 ? 124 : 40;
            var dib = new byte[header + 16];
            using (var stream = new MemoryStream(dib))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(header); writer.Write(2); writer.Write(topDown ? -2 : 2); writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(v5 ? 3 : 0); writer.Write(16);
                if (v5)
                {
                    stream.Position = 40; writer.Write(0x00ff0000u); writer.Write(0x0000ff00u); writer.Write(0x000000ffu); writer.Write(0xff000000u); writer.Write(0x73524742u);
                }
                stream.Position = header;
                if (topDown) writer.Write(topDownPixels);
                else { writer.Write(topDownPixels, 8, 8); writer.Write(topDownPixels, 0, 8); }
            }
            return dib;
        }

        private static byte[] Dib24()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(40); writer.Write(2); writer.Write(1); writer.Write((ushort)1); writer.Write((ushort)24); writer.Write(0); writer.Write(8);
                writer.Write(new byte[16]); writer.Write(new byte[] { 10, 20, 200, 30, 140, 40, 0, 0 }); return stream.ToArray();
            }
        }
    }
}
