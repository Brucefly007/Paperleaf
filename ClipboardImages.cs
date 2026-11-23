using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Paperleaf
{
    internal static class ClipboardImages
    {
        // Prefer encoded images: CF_BITMAP often loses or invents an alpha channel
        // when messaging apps publish several representations of the same image.
        private static readonly string[] EncodedFormats = { "PNG", "image/png", "image/x-png", "JFIF", "image/jpeg", "GIF", "image/gif", "TIFF", "image/tiff", "image/bmp" };
        private static readonly string[] DibFormats = { DataFormats.GetDataFormat(17).Name, "DeviceIndependentBitmapV5", "DIBV5", DataFormats.Dib };

        public static bool TryRead(IDataObject data, out Image image, out string error)
        {
            image = null; error = null;
            ExternalException busy = null;
            foreach (string format in EncodedFormats)
            {
                try
                {
                    if (!data.GetDataPresent(format, false)) continue;
                    image = DocumentCodec.MakeImage(ReadBytes(data.GetData(format, false)), "粘贴的图片");
                    return true;
                }
                catch (Exception e) { RememberFailure(e, ref error, ref busy); }
            }
            foreach (string format in DibFormats)
            {
                try
                {
                    if (!data.GetDataPresent(format, false)) continue;
                    image = FromBitmap(ReadDib(ReadBytes(data.GetData(format, false))), false);
                    return true;
                }
                catch (Exception e) { RememberFailure(e, ref error, ref busy); }
            }
            try
            {
                if (data.GetDataPresent(DataFormats.Bitmap, true))
                {
                    object value = data.GetData(DataFormats.Bitmap, true);
                    var bitmap = value as BitmapSource;
                    if (bitmap != null) { image = FromBitmap(bitmap, true); return true; }
                    var drawing = value as System.Drawing.Image;
                    if (drawing != null)
                    {
                        using (var stream = new MemoryStream())
                        {
                            drawing.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                            stream.Position = 0;
                            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                            image = FromBitmap(frame, true); return true;
                        }
                    }
                    if (value != null) { image = DocumentCodec.MakeImage(ReadBytes(value), "粘贴的图片"); return true; }
                }
            }
            catch (Exception e) { RememberFailure(e, ref error, ref busy); }
            if (busy != null) throw busy;
            return false;
        }

        private static void RememberFailure(Exception e, ref string error, ref ExternalException busy)
        {
            if (e is OutOfMemoryException || e is StackOverflowException || e is System.Threading.ThreadAbortException) throw e;
            error = e.Message;
            if (e is ExternalException) busy = (ExternalException)e;
        }

        internal static byte[] ReadBytes(object value)
        {
            var bytes = value as byte[];
            if (bytes != null)
            {
                if (bytes.Length > DocumentCodec.MaxImageBytes) throw new InvalidDataException("剪贴板中的单张图片超过 25 MB。");
                return bytes;
            }
            var stream = value as Stream;
            if (stream == null) throw new InvalidDataException("剪贴板提供的图片格式无法读取。");
            long position = stream.CanSeek ? stream.Position : 0;
            try
            {
                if (stream.CanSeek) stream.Position = 0;
                using (var copy = new MemoryStream())
                {
                    var buffer = new byte[81920]; int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (copy.Length + read > DocumentCodec.MaxImageBytes) throw new InvalidDataException("剪贴板中的单张图片超过 25 MB。");
                        copy.Write(buffer, 0, read);
                    }
                    return copy.ToArray();
                }
            }
            finally { if (stream.CanSeek) stream.Position = position; }
        }

        internal static Image FromBitmap(BitmapSource source, bool repairLegacyAlpha)
        {
            if ((long)source.PixelWidth * source.PixelHeight > 64000000) throw new InvalidDataException("图片超过 6400 万像素，请先缩小。");
            // Some CF_BITMAP providers fill RGB correctly but leave every alpha byte
            // at zero. Repair only this legacy representation, never encoded PNGs.
            if (repairLegacyAlpha && (source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32))
            {
                int stride = checked(source.PixelWidth * 4);
                var pixels = new byte[checked(stride * source.PixelHeight)];
                source.CopyPixels(pixels, stride, 0);
                bool alphaAllZero = true;
                for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { alphaAllZero = false; break; }
                if (alphaAllZero)
                {
                    for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                    source = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                }
            }
            return DocumentCodec.MakeImage(DocumentCodec.ImageBytes(new Image { Source = source }), "粘贴的图片");
        }

        internal static BitmapSource ReadDib(byte[] dib)
        {
            if (dib.Length < 40) throw new InvalidDataException("剪贴板中的 DIB 图片不完整。");
            int header = BitConverter.ToInt32(dib, 0), width = BitConverter.ToInt32(dib, 4), height = BitConverter.ToInt32(dib, 8);
            int bits = BitConverter.ToUInt16(dib, 14), compression = BitConverter.ToInt32(dib, 16);
            if (header < 40 || header > dib.Length || width <= 0 || height == 0 || height == int.MinValue || (long)width * Math.Abs((long)height) > 64000000 || BitConverter.ToUInt16(dib, 12) != 1)
                throw new InvalidDataException("剪贴板中的 DIB 图片尺寸或头信息无效。");
            if (!(bits == 1 || bits == 4 || bits == 8 || bits == 16 || bits == 24 || bits == 32) || !(compression == 0 || compression == 3 || compression == 6))
                throw new InvalidDataException("剪贴板中的 DIB 压缩格式暂不支持。");
            uint colors = BitConverter.ToUInt32(dib, 32);
            if (colors == 0 && bits <= 8) colors = (uint)(1 << bits);
            long pixelOffset = header + (header == 40 ? compression == 3 ? 12 : compression == 6 ? 16 : 0 : 0) + colors * 4L;
            long pixelBytes = (((long)width * bits + 31) / 32) * 4 * Math.Abs((long)height);
            if (pixelOffset > dib.Length || pixelBytes > dib.Length - pixelOffset) throw new InvalidDataException("剪贴板中的 DIB 图片像素不完整。");
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    writer.Write((ushort)0x4d42); writer.Write(dib.Length + 14); writer.Write(0); writer.Write((int)pixelOffset + 14); writer.Write(dib);
                }
                stream.Position = 0;
                var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                uint alphaMask = header >= 56 ? BitConverter.ToUInt32(dib, 52) : header == 40 && compression == 6 ? BitConverter.ToUInt32(dib, 52) : 0;
                // BI_RGB's fourth byte is padding, not an alpha channel. Explicit
                // bitfields in DIBV5 can carry real transparency and are preserved.
                if (bits == 32 && (compression == 0 || alphaMask == 0))
                {
                    if (compression != 0)
                    {
                        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr32, null, 0);
                        converted.Freeze(); return converted;
                    }
                    var pixels = new byte[checked(width * Math.Abs(height) * 4)];
                    int stride = width * 4;
                    for (int row = 0; row < Math.Abs(height); row++)
                    {
                        int sourceRow = height > 0 ? height - row - 1 : row;
                        Buffer.BlockCopy(dib, (int)pixelOffset + sourceRow * stride, pixels, row * stride, stride);
                    }
                    for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                    var opaque = BitmapSource.Create(width, Math.Abs(height), 96, 96, PixelFormats.Bgra32, null, pixels, stride); opaque.Freeze(); return opaque;
                }
                bitmap.Freeze(); return bitmap;
            }
        }

        public static DataObject CopyData(Image image)
        {
            var data = new DataObject();
            data.SetData("PNG", new MemoryStream(DocumentCodec.ImageBytes(image), false), false);
            data.SetImage((BitmapSource)image.Source);
            return data;
        }
    }
}
