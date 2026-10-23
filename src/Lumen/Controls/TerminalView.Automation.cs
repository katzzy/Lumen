using Lumen.Terminal;

namespace Lumen.Controls;

public sealed partial class TerminalView
{
    private const int AutomationMaxLines = 2000;

    public (int Start, int End)? GetAutomationSelection()
    {
        if (_session is null || !_hasSelection) return null;
        var buffer = _session.Buffer;
        lock (buffer.SyncRoot)
        {
            int total = buffer.TotalDisplayLines;
            int first = Math.Max(0, total - AutomationMaxLines);
            SelectionRange(out int startLine, out int startCol, out int endLine, out int endCol);
            startLine = Math.Clamp(startLine, first, Math.Max(first, total - 1));
            endLine = Math.Clamp(endLine, first, Math.Max(first, total - 1));
            if (startLine > endLine) return null;

            int offset = 0;
            int? start = null;
            int? end = null;
            for (int i = first; i <= endLine; i++)
            {
                var line = buffer.GetDisplayLine(i);
                if (line is null) continue;
                var (text, map) = line.GetTextWithCells();
                if (i == startLine) start = offset + CharAtCell(map, text.Length, startCol);
                if (i == endLine) end = offset + CharAtCell(map, text.Length, endCol);
                offset += text.Length + 1;
            }

            if (start is null || end is null || end < start) return null;
            return (start.Value, end.Value);
        }
    }

    public void SelectAutomationRange(int start, int end)
    {
        if (_session is null) return;
        if (end < start) (start, end) = (end, start);

        var buffer = _session.Buffer;
        lock (buffer.SyncRoot)
        {
            int total = buffer.TotalDisplayLines;
            int first = Math.Max(0, total - AutomationMaxLines);
            int offset = 0;
            (int Line, int Col)? anchor = null;
            (int Line, int Col)? caret = null;

            for (int i = first; i < total; i++)
            {
                var line = buffer.GetDisplayLine(i);
                if (line is null) continue;
                var (text, map) = line.GetTextWithCells();
                int lineStart = offset;
                int lineEnd = offset + text.Length;

                if (anchor is null && start >= lineStart && start <= lineEnd)
                {
                    int charIndex = start - lineStart;
                    int cell = charIndex >= map.Length ? line.Cells.Length : map[charIndex];
                    anchor = (i, cell);
                }
                if (caret is null && end >= lineStart && end <= lineEnd)
                {
                    int charIndex = end - lineStart;
                    int cell;
                    if (charIndex <= 0) cell = 0;
                    else if (charIndex > map.Length) cell = line.Cells.Length;
                    else
                    {
                        int last = map[Math.Min(charIndex - 1, map.Length - 1)];
                        cell = last + CellWidth(line, last);
                    }
                    caret = (i, cell);
                }

                offset = lineEnd + 1;
                if (anchor is not null && caret is not null) break;
            }

            if (anchor is null || caret is null)
            {
                anchor ??= (first, 0);
                if (caret is null)
                {
                    var last = buffer.GetDisplayLine(Math.Max(0, total - 1));
                    caret = (Math.Max(0, total - 1), last?.Cells.Length ?? 0);
                }
            }
            _selAnchor = anchor.Value;
            _selEnd = caret.Value;
            _hasSelection = !SelectionIsEmpty();
            ScrollToAutomationLine(anchor.Value.Line, total);
            InvalidateVisual();
        }
    }

    private void ScrollToAutomationLine(int line, int total)
    {
        int rows = Math.Max(1, (int)VisibleRows);
        int desiredStart = Math.Clamp(line - rows / 2, 0, Math.Max(0, total - rows));
        _scrollOffset = Math.Clamp(total - rows - desiredStart, 0, MaxScrollOffset());
    }

    private static int CharAtCell(int[] map, int textLength, int cell)
    {
        for (int i = 0; i < map.Length; i++)
        {
            if (map[i] >= cell) return i;
        }
        return textLength;
    }
}
