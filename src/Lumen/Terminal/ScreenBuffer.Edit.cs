namespace Lumen.Terminal;

public sealed partial class ScreenBuffer
{
    public void SetScrollRegion(int top, int bottom)
    {
        top = Math.Clamp(top, 0, Rows - 1);
        bottom = Math.Clamp(bottom, 0, Rows - 1);
        if (top >= bottom) return;
        _scrollTop = top;
        _scrollBottom = bottom;
        CursorRow = OriginMode ? top : 0;
        CursorCol = 0;
        _pendingWrap = false;
    }

    public void SetCursor(int row, int col)
    {
        int minRow = OriginMode ? _scrollTop : 0;
        int maxRow = OriginMode ? _scrollBottom : Rows - 1;
        CursorRow = Math.Clamp(row, minRow, maxRow);
        CursorCol = Math.Clamp(col, 0, Columns - 1);
        _pendingWrap = false;
    }

    public void MoveCursor(int deltaRow, int deltaCol)
    {
        int minRow = OriginMode ? _scrollTop : 0;
        int maxRow = OriginMode ? _scrollBottom : Rows - 1;
        CursorRow = Math.Clamp(CursorRow + deltaRow, minRow, maxRow);
        CursorCol = Math.Clamp(CursorCol + deltaCol, 0, Columns - 1);
        _pendingWrap = false;
    }

    public void CarriageReturn()
    {
        CursorCol = 0;
        _pendingWrap = false;
    }

    public void Backspace()
    {
        if (CursorCol > 0) CursorCol--;
        _pendingWrap = false;
    }

    public void Tab(int count = 1)
    {
        for (int n = 0; n < count; n++)
        {
            int next = Columns - 1;
            for (int i = CursorCol + 1; i < Columns; i++)
            {
                if (_tabStops[i])
                {
                    next = i;
                    break;
                }
            }
            CursorCol = next;
        }
        _pendingWrap = false;
    }

    public void BackTab(int count = 1)
    {
        for (int n = 0; n < count; n++)
        {
            int prev = 0;
            for (int i = CursorCol - 1; i >= 0; i--)
            {
                if (_tabStops[i])
                {
                    prev = i;
                    break;
                }
            }
            CursorCol = prev;
        }
        _pendingWrap = false;
    }

    public void SetTabStop()
    {
        if (CursorCol >= 0 && CursorCol < Columns) _tabStops[CursorCol] = true;
    }

    public void ClearTabStops(int mode)
    {
        if (mode == 0)
        {
            if (CursorCol >= 0 && CursorCol < Columns) _tabStops[CursorCol] = false;
        }
        else if (mode == 3)
        {
            Array.Clear(_tabStops);
        }
    }

    public void LineFeed()
    {
        if (CursorRow == _scrollBottom)
        {
            ScrollUp(1);
        }
        else if (CursorRow < Rows - 1)
        {
            CursorRow++;
        }
        _pendingWrap = false;
    }

    public void ReverseIndex()
    {
        if (CursorRow == _scrollTop)
        {
            ScrollDown(1);
        }
        else if (CursorRow > 0)
        {
            CursorRow--;
        }
        _pendingWrap = false;
    }

    public void NextLine()
    {
        CursorCol = 0;
        LineFeed();
    }

    public void ScrollUp(int count)
    {
        if (count <= 0) return;
        var screen = Screen;
        for (int n = 0; n < count; n++)
        {
            var removed = screen[_scrollTop];
            if (!_altActive && _scrollTop == 0 && _scrollBottom == Rows - 1)
            {
                AddScrollback(removed);
            }
            for (int r = _scrollTop; r < _scrollBottom; r++)
            {
                screen[r] = screen[r + 1];
            }
            screen[_scrollBottom] = new TerminalLine(Columns);
        }
    }

    public void ScrollDown(int count)
    {
        if (count <= 0) return;
        var screen = Screen;
        for (int n = 0; n < count; n++)
        {
            for (int r = _scrollBottom; r > _scrollTop; r--)
            {
                screen[r] = screen[r - 1];
            }
            screen[_scrollTop] = new TerminalLine(Columns);
        }
    }

    private void AddScrollback(TerminalLine line)
    {
        _scrollback.Add(line);
    }

    public void ClearScrollback() => _scrollback.Clear();

    public void Print(int codepoint)
    {
        int width = TerminalText.Width(codepoint);
        if (width == 0)
        {
            AttachCombining(codepoint);
            return;
        }

        if (_pendingWrap)
        {
            MarkWrapped();
            CursorCol = 0;
            LineFeed();
        }

        if (width == 2 && CursorCol >= Columns - 1)
        {
            if (AutoWrap)
            {
                MarkWrapped();
                CursorCol = 0;
                LineFeed();
            }
            else
            {
                CursorCol = Math.Max(0, Columns - 2);
            }
        }

        if (InsertMode) InsertChars(width);

        var line = Screen[CursorRow];
        var cell = new Cell
        {
            Rune = codepoint,
            Fg = _pen.Fg,
            Bg = _pen.Bg,
            Flags = _pen.Flags | (width == 2 ? CellFlags.Wide : CellFlags.None)
        };
        line.Cells[CursorCol] = cell;

        if (width == 2 && CursorCol + 1 < Columns)
        {
            line.Cells[CursorCol + 1] = new Cell
            {
                Rune = 0,
                Fg = _pen.Fg,
                Bg = _pen.Bg,
                Flags = _pen.Flags | CellFlags.WideTail
            };
        }

        int next = CursorCol + width;
        if (next >= Columns)
        {
            CursorCol = Columns - 1;
            _pendingWrap = AutoWrap;
        }
        else
        {
            CursorCol = next;
        }
    }

    private void MarkWrapped()
    {
        if (CursorRow >= 0 && CursorRow < Rows)
        {
            Screen[CursorRow].Wrapped = true;
        }
    }

    private void AttachCombining(int codepoint)
    {
        int col = Math.Clamp(CursorCol > 0 ? CursorCol - 1 : 0, 0, Columns - 1);
        var line = Screen[CursorRow];
        var cell = line.Cells[col];
        string text = codepoint <= 0xFFFF ? ((char)codepoint).ToString() : char.ConvertFromUtf32(codepoint);
        cell.Combining = (cell.Combining ?? "") + text;
        line.Cells[col] = cell;
    }

    public void PrintString(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                Print(char.ConvertToUtf32(ch, text[i + 1]));
                i++;
            }
            else
            {
                Print(ch);
            }
        }
    }

    public void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (int r = CursorRow + 1; r < Rows; r++) ClearLine(r, 0, Columns);
                break;
            case 1:
                for (int r = 0; r < CursorRow; r++) ClearLine(r, 0, Columns);
                EraseInLine(1);
                break;
            case 2:
                for (int r = 0; r < Rows; r++) ClearLine(r, 0, Columns);
                break;
            case 3:
                _scrollback.Clear();
                break;
        }
    }

    public void EraseInLine(int mode)
    {
        switch (mode)
        {
            case 0:
                ClearLine(CursorRow, CursorCol, Columns);
                break;
            case 1:
                ClearLine(CursorRow, 0, CursorCol + 1);
                break;
            case 2:
                ClearLine(CursorRow, 0, Columns);
                break;
        }
    }

    public void EraseChars(int count)
    {
        ClearLine(CursorRow, CursorCol, Math.Min(Columns, CursorCol + Math.Max(1, count)));
    }

    private void ClearLine(int row, int start, int end)
    {
        var line = Screen[Math.Clamp(row, 0, Rows - 1)];
        start = Math.Clamp(start, 0, Columns);
        end = Math.Clamp(end, 0, Columns);
        var blank = new Cell { Rune = ' ', Bg = _pen.Bg };
        for (int c = start; c < end; c++)
        {
            line.Cells[c] = blank;
        }
    }

    public void InsertChars(int count)
    {
        if (count <= 0) return;
        var line = Screen[CursorRow];
        count = Math.Min(count, Columns - CursorCol);
        for (int c = Columns - 1; c >= CursorCol + count; c--)
        {
            line.Cells[c] = line.Cells[c - count];
        }
        var blank = new Cell { Rune = ' ', Bg = _pen.Bg };
        for (int c = CursorCol; c < CursorCol + count; c++) line.Cells[c] = blank;
        _pendingWrap = false;
    }

    public void DeleteChars(int count)
    {
        if (count <= 0) return;
        var line = Screen[CursorRow];
        count = Math.Min(count, Columns - CursorCol);
        for (int c = CursorCol; c < Columns - count; c++)
        {
            line.Cells[c] = line.Cells[c + count];
        }
        var blank = new Cell { Rune = ' ', Bg = _pen.Bg };
        for (int c = Columns - count; c < Columns; c++) line.Cells[c] = blank;
        _pendingWrap = false;
    }

    public void InsertLines(int count)
    {
        if (count <= 0 || CursorRow < _scrollTop || CursorRow > _scrollBottom) return;
        var screen = Screen;
        count = Math.Min(count, _scrollBottom - CursorRow + 1);
        for (int r = _scrollBottom; r >= CursorRow + count; r--)
        {
            screen[r] = screen[r - count];
        }
        for (int r = CursorRow; r < CursorRow + count; r++)
        {
            screen[r] = new TerminalLine(Columns);
        }
        CursorCol = 0;
        _pendingWrap = false;
    }

    public void DeleteLines(int count)
    {
        if (count <= 0 || CursorRow < _scrollTop || CursorRow > _scrollBottom) return;
        var screen = Screen;
        count = Math.Min(count, _scrollBottom - CursorRow + 1);
        for (int r = CursorRow; r <= _scrollBottom - count; r++)
        {
            screen[r] = screen[r + count];
        }
        for (int r = _scrollBottom - count + 1; r <= _scrollBottom; r++)
        {
            screen[r] = new TerminalLine(Columns);
        }
        CursorCol = 0;
        _pendingWrap = false;
    }

    public void SaveCursor()
    {
        var state = new CursorState
        {
            Row = CursorRow,
            Col = CursorCol,
            Pen = _pen,
            Origin = OriginMode,
            AutoWrap = AutoWrap
        };
        if (_altActive) _savedAlt = state;
        else _savedMain = state;
    }

    public void RestoreCursor()
    {
        var state = _altActive ? _savedAlt : _savedMain;
        if (state.Row == 0 && state.Col == 0 && state.Pen.Rune == 0) return;
        CursorRow = Math.Clamp(state.Row, 0, Rows - 1);
        CursorCol = Math.Clamp(state.Col, 0, Columns - 1);
        _pen = state.Pen;
        OriginMode = state.Origin;
        AutoWrap = state.AutoWrap;
        _pendingWrap = false;
    }

    public void EnterAltScreen(bool clear)
    {
        if (_altActive) return;
        _savedMain = new CursorState
        {
            Row = CursorRow,
            Col = CursorCol,
            Pen = _pen,
            Origin = OriginMode,
            AutoWrap = AutoWrap
        };
        _altActive = true;
        if (clear)
        {
            for (int r = 0; r < Rows; r++) _alt[r] = new TerminalLine(Columns);
        }
        CursorRow = 0;
        CursorCol = 0;
        _pendingWrap = false;
    }

    public void ExitAltScreen()
    {
        if (!_altActive) return;
        _altActive = false;
        var state = _savedMain;
        CursorRow = Math.Clamp(state.Row, 0, Rows - 1);
        CursorCol = Math.Clamp(state.Col, 0, Columns - 1);
        _pen = state.Pen;
        OriginMode = state.Origin;
        AutoWrap = state.AutoWrap;
        _pendingWrap = false;
    }

    public void ResetPen()
    {
        _pen = Cell.Empty;
    }

}
