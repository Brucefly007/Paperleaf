using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Documents;

namespace Paperleaf
{
    internal static class DocumentSearch
    {
        internal sealed class Match
        {
            public TextPointer Start, End;
            public bool Wrapped;
        }
        private sealed class Segment
        {
            public int Offset, Length;
            public TextPointer Start, End;
            public bool IsText;
        }

        public static Match FindNext(FlowDocument document, TextPointer from, string query)
        {
            return Find(document, from, query, false);
        }

        public static Match FindPrevious(FlowDocument document, TextPointer from, string query)
        {
            return Find(document, from, query, true);
        }

        private sealed class Index
        {
            public string Text;
            public List<Segment> Segments;
        }

        private static Index BuildIndex(FlowDocument document)
        {
            var text = new StringBuilder();
            var segments = new List<Segment>();
            for (var pointer = document.ContentStart; pointer != null && pointer.CompareTo(document.ContentEnd) < 0; )
            {
                var context = pointer.GetPointerContext(LogicalDirection.Forward);
                if (context == TextPointerContext.Text)
                {
                    string run = pointer.GetTextInRun(LogicalDirection.Forward);
                    var end = pointer.GetPositionAtOffset(run.Length);
                    segments.Add(new Segment { Offset = text.Length, Length = run.Length, Start = pointer, End = end, IsText = true });
                    text.Append(run); pointer = end; continue;
                }
                var next = pointer.GetNextContextPosition(LogicalDirection.Forward);
                object element = pointer.GetAdjacentElement(LogicalDirection.Forward);
                if (context == TextPointerContext.EmbeddedElement || (context == TextPointerContext.ElementStart && element is LineBreak) || (context == TextPointerContext.ElementEnd && element is Paragraph))
                {
                    segments.Add(new Segment { Offset = text.Length, Length = 1, Start = pointer, End = next ?? pointer });
                    text.Append(context == TextPointerContext.EmbeddedElement ? '\uFFFC' : '\n');
                }
                pointer = next;
            }
            return new Index { Text = text.ToString(), Segments = segments };
        }

        private static bool ValidQuery(string query)
        {
            // An embedded object is a boundary, never replaceable text.
            return !string.IsNullOrEmpty(query) && query.IndexOf('\uFFFC') < 0;
        }

        private static Match Find(FlowDocument document, TextPointer from, string query, bool previous)
        {
            if (!ValidQuery(query)) return null;
            var index = BuildIndex(document);
            int offset = index.Text.Length;
            foreach (var segment in index.Segments)
            {
                if (from.CompareTo(segment.Start) <= 0) { offset = segment.Offset; break; }
                if (from.CompareTo(segment.End) < 0)
                {
                    offset = segment.Offset + (segment.IsText ? Math.Min(segment.Length, segment.Start.GetOffsetToPosition(from)) : 0); break;
                }
            }
            string haystack = index.Text;
            int match = previous ? (offset > 0 ? haystack.LastIndexOf(query, offset - 1, offset, StringComparison.OrdinalIgnoreCase) : -1) : haystack.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
            bool wrapped = match < 0 && (previous ? offset < haystack.Length : offset > 0);
            if (wrapped) match = previous ? haystack.LastIndexOf(query, StringComparison.OrdinalIgnoreCase) : haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (match < 0) return null;
            var result = Map(index.Segments, match, query.Length); result.Wrapped = wrapped; return result;
        }

        public static List<Match> FindAll(FlowDocument document, string query)
        {
            var matches = new List<Match>();
            if (!ValidQuery(query)) return matches;
            var index = BuildIndex(document);
            for (int offset = 0; offset <= index.Text.Length - query.Length; )
            {
                int found = index.Text.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                matches.Add(Map(index.Segments, found, query.Length));
                offset = found + query.Length;
            }
            return matches;
        }

        private static Match Map(List<Segment> segments, int offset, int length)
        {
            // At adjacent matches, earlier endpoints must stay before text
            // inserted by a later replacement. This also makes deletions safe.
            return new Match { Start = Position(segments, offset, false).GetPositionAtOffset(0, LogicalDirection.Forward),
                End = Position(segments, offset + length - 1, true).GetPositionAtOffset(0, LogicalDirection.Backward) };
        }

        private static TextPointer Position(List<Segment> segments, int index, bool after)
        {
            // Only map the two matched endpoints, rather than allocating two
            // TextPointers for every character in a potentially large document.
            int low = 0, high = segments.Count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                var segment = segments[middle];
                if (index < segment.Offset) high = middle - 1;
                else if (index >= segment.Offset + segment.Length) low = middle + 1;
                else return segment.IsText ? segment.Start.GetPositionAtOffset(index - segment.Offset + (after ? 1 : 0)) : after ? segment.End : segment.Start;
            }
            throw new InvalidOperationException("查找位置已变化，请重试。");
        }
    }
}
