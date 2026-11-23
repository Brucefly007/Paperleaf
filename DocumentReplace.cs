using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Paperleaf
{
    internal static class DocumentReplace
    {
        private static readonly DependencyProperty[] Appearance = { TextElement.ForegroundProperty, TextElement.FontWeightProperty,
            TextElement.FontStyleProperty, TextElement.FontFamilyProperty, TextElement.FontSizeProperty, Inline.TextDecorationsProperty };
        public static int All(RichTextBox editor, string query, string replacement)
        {
            if (editor.IsReadOnly) return 0;
            // Match the original content once. Text inserted during replacement
            // cannot become a new match in this operation.
            var matches = DocumentSearch.FindAll(editor.Document, query);
            if (matches.Count == 0) return 0;
            editor.BeginChange();
            try
            {
                for (int i = matches.Count - 1; i >= 0; i--)
                    One(new TextRange(matches[i].Start, matches[i].End), replacement);
            }
            finally { editor.EndChange(); }
            return matches.Count;
        }

        public static void One(TextRange range, string replacement)
        {
            // WPF may inherit the final run's formatting when a match crosses
            // several runs. Explicitly retain the first character's appearance.
            var first = new TextRange(range.Start, range.Start.GetNextInsertionPosition(LogicalDirection.Forward) ?? range.End);
            var values = new object[Appearance.Length];
            for (int i = 0; i < Appearance.Length; i++) values[i] = first.GetPropertyValue(Appearance[i]);
            range.Text = replacement ?? "";
            if (range.IsEmpty) return;
            for (int i = 0; i < Appearance.Length; i++)
                if (values[i] != DependencyProperty.UnsetValue) range.ApplyPropertyValue(Appearance[i], values[i]);
        }
    }
}
