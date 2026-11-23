using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace Paperleaf
{
    internal static class TextColors
    {
        public const string Default = "#303C34";
        public static string Hex(Brush brush)
        {
            var solid = brush as SolidColorBrush;
            return solid == null ? Default : "#" + solid.Color.R.ToString("X2") + solid.Color.G.ToString("X2") + solid.Color.B.ToString("X2");
        }

        public static SolidColorBrush Brush(string hex)
        {
            if (!Regex.IsMatch(hex ?? "", @"^#[0-9a-fA-F]{6}$")) throw new ArgumentException("请选择有效的文字颜色。");
            var brush = new SolidColorBrush(Color.FromRgb(byte.Parse(hex.Substring(1, 2), NumberStyles.HexNumber),
                byte.Parse(hex.Substring(3, 2), NumberStyles.HexNumber), byte.Parse(hex.Substring(5, 2), NumberStyles.HexNumber)));
            brush.Freeze(); return brush;
        }

        public static string ReadStyle(string style)
        {
            var match = Regex.Match(style ?? "", @"(?:^|;)\s*color\s*:\s*(#[0-9a-fA-F]{6}|#[0-9a-fA-F]{3})\s*(?:;|$)", RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            string hex = match.Groups[1].Value.ToUpperInvariant();
            return hex.Length == 4 ? "#" + hex[1] + hex[1] + hex[2] + hex[2] + hex[3] + hex[3] : hex;
        }
    }
}
