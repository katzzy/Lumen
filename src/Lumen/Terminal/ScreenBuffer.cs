namespace Lumen.Terminal;

public sealed class ScreenBuffer
{
    private struct CursorState
    {
        public int Row;
        public int Col;
        public Cell Pen;
        public bool Origin;
        public bool AutoWrap;
    }

    private TerminalLine[] _main = Array.Empty<TerminalLine>();
    private TerminalLine[] _alt = Array.Empty<TerminalLine>();
    private bool _altActive;
    private readonly List<TerminalLine> _scrollback = new();
    private Cell _pen = Cell.Empty;
    private bool[] _tabStops = Array.Empty<bool>();
    private int _scrollTop;
    private int _scrollBottom;
    private bool _pendingWrap;
    private CursorState _savedMain;
    private CursorState _savedAlt;

    public ScreenBuffer(int columns = 80, int rows = 24)
    {
        Reset(columns, rows);
    }

    public TerminalPalette Palette { get; set; } = TerminalPalette.Dark;
    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int CursorRow { get; private set; }
    public int CursorCol { get; private set; }
    public bool CursorVisible { get; set; } = true;
    public int CursorStyle { get; set; }
    public bool AutoWrap { get; set; } = true;
    public bool OriginMode { get; set; }
    public bool InsertMode { get; set; }
    public bool AppCursorKeys { get; set; }
    public bool BracketedPaste { get; set; }
    public bool NewLineMode { get; set; }
    public bool IsAltScreen => _altActive;
    public int ScrollTop => _scrollTop;
    public int ScrollBottom => _scrollBottom;
    public int ScrollbackLimit { get; set; } = 10000;
    public IReadOnlyList<TerminalLine> Scrollback => _scrollback;
    public int ScrollbackCount => _scrollback.Count;
    public long TotalPrinted { get; internal set; }
    public int TotalDisplayLines => _altActive ? Rows : _scrollback.Count + Rows;
    public int CursorAbsoluteLine => _altActive ? CursorRow : _scrollback.Count + CursorRow;

    public Cell Pen => _pen;
    public uint PenFg
    {
        get => _pen.Fg;
        set => _pen.Fg = value;
    }
    public uint PenBg
    {
        get => _pen.Bg;
        set => _pen.Bg = value;
    }
    public CellFlags PenFlags
    {
        get => _pen.Flags;
        set => _pen.Flags = value;
    }

    public void Reset() => Reset(Math.Max(1, Columns), Math.Max(1, Rows));

    public void Reset(int columns, int rows)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        _scrollback.Clear();
        _altActive = false;
        _main = CreateLines(Rows, Columns);
        _alt = CreateLines(Rows, Columns);
        CursorRow = 0;
        CursorCol = 0;
        _pendingWrap = false;
        _pen = Cell.Empty;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        AutoWrap = true;
        OriginMode = false;
        InsertMode = false;
        CursorVisible = true;
        CursorStyle = 0;
        AppCursorKeys = false;
        BracketedPaste = false;
        NewLineMode = false;
        TotalPrinted = 0;
        _tabStops = new bool[Columns];
        for (int i = 0; i < Columns; i += 8) _tabStops[i] = true;
        _savedMain = default;
        _savedAlt = default;
    }

    private static TerminalLine[] CreateLines(int rows, int columns)
    {
        var lines = new TerminalLine[rows];
        for (int i = 0; i < rows; i++) lines[i] = new TerminalLine(columns);
        return lines;
    }

    private TerminalLine[] Screen => _altActive ? _alt : _main;

    public TerminalLine GetScreenLine(int row) => Screen[Math.Clamp(row, 0, Rows - 1)];

    public TerminalLine? GetDisplayLine(int index)
    {
        if (_altActive)
        {
            return index >= 0 && index < Rows ? _alt[index] : null;
        }
        if (index < 0) return null;
        if (index < _scrollback.Count) return _scrollback[index];
        int screenRow = index - _scrollback.Count;
        return screenRow >= 0 && screenRow < Rows ? _main[screenRow] : null;
    }

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(2, rows);
        if (columns == Columns && rows == Rows) return;

        var oldMain = _main;
        var oldAlt = _alt;
        int oldRows = Rows;
        int oldCols = Columns;

        Columns = columns;
        Rows = rows;
        _main = new TerminalLine[rows];
        _alt = new TerminalLine[rows];
        for (int r = 0; r < rows; r++)
        {
            if (r < oldRows)
            {
                oldMain[r].Resize(columns);
                oldAlt[r].Resize(columns);
                _main[r] = oldMain[r];
                _alt[r] = oldAlt[r];
            }
            else
            {
                _main[r] = new TerminalLine(columns);
                _alt[r] = new TerminalLine(columns);
            }
        }

        CursorRow = Math.Clamp(CursorRow, 0, rows - 1);
        CursorCol = Math.Clamp(CursorCol, 0, columns - 1);
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        _pendingWrap = false;

        var tabs = new bool[columns];
        for (int i = 0; i < oldCols && i < columns; i++) tabs[i] = _tabStops[i];
        for (int i = oldCols; i < columns; i += 8) tabs[i] = true;
        _tabStops = tabs;

        while (_scrollback.Count > ScrollbackLimit) _scrollback.RemoveAt(0);
    }

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
        while (_scrollback.Count > ScrollbackLimit) _scrollback.RemoveAt(0);
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

    public string GetDisplayText() => "ScreenBuffer";
}
