using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lumen.Terminal;

namespace Lumen.Controls;

public sealed partial class TerminalView
{
    private void SelectionRange(out int startLine, out int startCol, out int endLine, out int endCol)
    {
        var a = _selAnchor;
        var b = _selEnd;
        if (a.Line < b.Line || (a.Line == b.Line && a.Col <= b.Col))
        {
            startLine = a.Line; startCol = a.Col; endLine = b.Line; endCol = b.Col;
        }
        else
        {
            startLine = b.Line; startCol = b.Col; endLine = a.Line; endCol = a.Col;
        }
    }

    private bool SelectionIsEmpty()
    {
        var a = _selAnchor;
        var b = _selEnd;
        return a.Line == b.Line && a.Col == b.Col;
    }

    private (int Line, int Col) PointToCell(Point point)
    {
        if (_session is null) return (0, 0);
        var buffer = _session.Buffer;
        int rows = (int)VisibleRows;
        int total = buffer.TotalDisplayLines;
        int start = total - rows - _scrollOffset;
        int col = (int)Math.Floor((point.X - PadX) / _cellW);
        int row = (int)Math.Floor((point.Y - PadY) / _cellH);
        col = Math.Clamp(col, 0, buffer.Columns - 1);
        row = Math.Clamp(row, 0, rows - 1);
        return (Math.Max(0, start + row), col);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_session is null) return;
        Focus();
        System.Windows.Input.Keyboard.Focus(this);
        var cell = PointToCell(e.GetPosition(this));
        if (e.ClickCount == 2)
        {
            SelectWord(cell.Line, cell.Col);
            e.Handled = true;
            return;
        }
        if (e.ClickCount >= 3)
        {
            SelectLine(cell.Line);
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _hasSelection)
        {
            _selEnd = cell;
        }
        else
        {
            _selAnchor = cell;
            _selEnd = cell;
            _hasSelection = false;
        }
        _selecting = true;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_selecting || _session is null) return;
        var p = e.GetPosition(this);
        if (p.Y < PadY && _scrollOffset < MaxScrollOffset())
        {
            _scrollOffset = Math.Min(MaxScrollOffset(), _scrollOffset + 2);
        }
        else if (p.Y > ActualHeight - PadY && _scrollOffset > 0)
        {
            _scrollOffset = Math.Max(0, _scrollOffset - 2);
        }
        _selEnd = PointToCell(p);
        _hasSelection = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_selecting) return;
        _selecting = false;
        ReleaseMouseCapture();
        _hasSelection = !SelectionIsEmpty();
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_session is null) return;
        int max = MaxScrollOffset();
        if (max <= 0) return;
        _scrollOffset = Math.Clamp(_scrollOffset + (e.Delta > 0 ? 3 : -3), 0, max);
        InvalidateVisual();
        e.Handled = true;
    }

    public void ScrollToBottom()
    {
        _scrollOffset = 0;
        InvalidateVisual();
    }

    public void ScrollPage(bool up)
    {
        int step = Math.Max(1, (int)VisibleRows - 2);
        _scrollOffset = Math.Clamp(_scrollOffset + (up ? step : -step), 0, MaxScrollOffset());
        InvalidateVisual();
    }

    private void SelectWord(int lineIndex, int col)
    {
        if (_session is null) return;
        var line = _session.Buffer.GetDisplayLine(lineIndex);
        if (line is null || line.Cells.Length == 0) return;
        col = Math.Clamp(col, 0, line.Cells.Length - 1);
        int start = col;
        int end = col;
        while (start > 0 && IsWordCell(line.Cells[start - 1])) start--;
        while (end < line.Cells.Length - 1 && IsWordCell(line.Cells[end + 1])) end++;
        _selAnchor = (lineIndex, start);
        _selEnd = (lineIndex, end + 1);
        _hasSelection = true;
        InvalidateVisual();
    }

    private static bool IsWordCell(Cell cell)
    {
        if (cell.Rune <= 0) return false;
        char ch = cell.Rune <= 0xFFFF ? (char)cell.Rune : ' ';
        return char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or '/' or ':';
    }

    private void SelectLine(int lineIndex)
    {
        if (_session is null) return;
        var line = _session.Buffer.GetDisplayLine(lineIndex);
        if (line is null) return;
        _selAnchor = (lineIndex, 0);
        _selEnd = (lineIndex, line.Cells.Length);
        _hasSelection = true;
        InvalidateVisual();
    }

    public void SelectAll()
    {
        if (_session is null) return;
        var buffer = _session.Buffer;
        _selAnchor = (0, 0);
        _selEnd = (buffer.TotalDisplayLines - 1, buffer.Columns);
        _hasSelection = true;
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        _hasSelection = false;
        _selecting = false;
        _selAnchor = default;
        _selEnd = default;
        InvalidateVisual();
    }

    public string GetSelectedText()
    {
        if (_session is null || !_hasSelection) return "";
        var buffer = _session.Buffer;
        SelectionRange(out int startLine, out int startCol, out int endLine, out int endCol);
        var sb = new StringBuilder();
        for (int lineIndex = startLine; lineIndex <= endLine; lineIndex++)
        {
            var line = buffer.GetDisplayLine(lineIndex);
            if (line is null) continue;
            int from = lineIndex == startLine ? startCol : 0;
            int to = lineIndex == endLine ? endCol : line.Cells.Length;
            from = Math.Clamp(from, 0, line.Cells.Length);
            to = Math.Clamp(to, 0, line.Cells.Length);
            var lineText = new StringBuilder();
            for (int c = from; c < to; c++)
            {
                var cell = line.Cells[c];
                if ((cell.Flags & CellFlags.WideTail) != 0) continue;
                if (cell.Rune == 0) continue;
                lineText.Append(cell.Rune <= 0xFFFF ? (char)cell.Rune : char.ConvertFromUtf32(cell.Rune));
                if (cell.Combining is not null) lineText.Append(cell.Combining);
            }
            sb.Append(lineText.ToString().TrimEnd());
            if (lineIndex < endLine && !line.Wrapped) sb.Append("\r\n");
        }
        return sb.ToString();
    }

    public void CopySelection()
    {
        var text = GetSelectedText();
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text);
            CopyPerformed?.Invoke();
        }
        catch
        {
            // clipboard may be locked
        }
    }

    public void ClearScreen()
    {
        _session?.ParseLocal("\x1b[2J\x1b[H");
        ScrollToBottom();
        Cleared?.Invoke();
    }

    public int SetSearch(string query)
    {
        _searchVersion++;
        if (!BeginSearch(query)) return 0;

        var buffer = _session!.Buffer;
        int total = buffer.TotalDisplayLines;
        for (int i = 0; i < total; i++) AddMatchesForLine(buffer, i);
        return FinishSearch();
    }

    /// <summary>
    /// Chunked search used by the find bar. It yields to the dispatcher between
    /// batches so a 10k-line scrollback cannot freeze the UI for ~50ms per key.
    /// </summary>
    public async Task<int> SetSearchAsync(string query, CancellationToken token)
    {
        int version = ++_searchVersion;
        if (!BeginSearch(query)) return 0;

        var buffer = _session!.Buffer;
        int total = buffer.TotalDisplayLines;
        for (int i = 0; i < total; i++)
        {
            if (token.IsCancellationRequested || version != _searchVersion) return 0;
            AddMatchesForLine(buffer, i);
            if ((i & 255) == 0) await Dispatcher.Yield(DispatcherPriority.Background);
        }

        if (token.IsCancellationRequested || version != _searchVersion) return 0;
        return FinishSearch();
    }

    private bool BeginSearch(string query)
    {
        _searchText = query ?? "";
        _matches.Clear();
        _matchesByLine.Clear();
        _matchIndex = -1;
        if (_session is null || _searchText.Length == 0)
        {
            InvalidateVisual();
            return false;
        }
        return true;
    }

    private void AddMatchesForLine(ScreenBuffer buffer, int lineIndex)
    {
        var line = buffer.GetDisplayLine(lineIndex);
        if (line is null) return;

        var (text, cellAtChar) = line.GetTextWithCells();
        if (text.Length == 0) return;

        int index = 0;
        while (index < text.Length)
        {
            int found = text.IndexOf(_searchText, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0) break;
            int endChar = found + _searchText.Length - 1;
            if (endChar < cellAtChar.Length)
            {
                int startCell = cellAtChar[found];
                int endCell = cellAtChar[endChar];
                int width = CellWidth(line, endCell);
                int length = Math.Max(1, endCell - startCell + width);
                _matches.Add((lineIndex, startCell, length));
            }
            index = found + Math.Max(1, _searchText.Length);
        }
    }

    private int FinishSearch()
    {
        for (int i = 0; i < _matches.Count; i++)
        {
            var match = _matches[i];
            if (!_matchesByLine.TryGetValue(match.Line, out var list))
            {
                list = new List<int>();
                _matchesByLine[match.Line] = list;
            }
            list.Add(i);
        }

        InvalidateVisual();
        return _matches.Count;
    }

    private static int CellWidth(TerminalLine line, int cellIndex)
    {
        if (cellIndex < 0 || cellIndex >= line.Cells.Length) return 1;
        return (line.Cells[cellIndex].Flags & CellFlags.Wide) != 0 ? 2 : 1;
    }

    public bool FindNext(bool forward)
    {
        if (_matches.Count == 0) return false;
        if (_matchIndex < 0)
        {
            _matchIndex = forward ? 0 : _matches.Count - 1;
        }
        else
        {
            _matchIndex = (_matchIndex + (forward ? 1 : -1) + _matches.Count) % _matches.Count;
        }

        var match = _matches[_matchIndex];
        int rows = (int)VisibleRows;
        int total = _session?.Buffer.TotalDisplayLines ?? 0;
        int start = total - rows - _scrollOffset;
        if (match.Line < start || match.Line >= start + rows)
        {
            _scrollOffset = Math.Clamp(total - rows - match.Line, 0, MaxScrollOffset());
        }
        InvalidateVisual();
        return true;
    }

    public void ClearSearch()
    {
        _searchVersion++;
        _searchText = "";
        _matches.Clear();
        _matchesByLine.Clear();
        _matchIndex = -1;
        InvalidateVisual();
    }

}
