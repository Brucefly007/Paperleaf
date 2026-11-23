using System.Windows.Controls;
using System.Windows.Documents;

namespace Paperleaf
{
    internal sealed class PasteContext
    {
        private readonly FlowDocument document;
        private readonly long version;
        private readonly TextPointer start, end;
        public PasteContext(RichTextBox editor, long version)
        {
            document = editor.Document; this.version = version;
            start = editor.Selection.Start; end = editor.Selection.End;
        }
        public bool IsCurrent(RichTextBox editor, long currentVersion)
        {
            return !editor.IsReadOnly && document == editor.Document && version == currentVersion &&
                start.CompareTo(editor.Selection.Start) == 0 && end.CompareTo(editor.Selection.End) == 0;
        }
    }
}
