namespace Lumen.Terminal;

public sealed partial class ScreenBuffer
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
    private readonly ScrollbackBuffer _scrollback = new(10000);
    public object SyncRoot { get; } = new();
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
    public bool NeedsFullReflow { get; private set; }
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
    public int ScrollbackLimit
    {
        get => _scrollback.Capacity;
        set => _scrollback.TrimTo(Math.Max(0, value));
    }
    public IReadOnlyList<TerminalLine> Scrollback => _scrollback;
    public int ScrollbackCount
    {
        get { lock (SyncRoot) return _scrollback.Count; }
    }

    public long TotalPrinted { get; internal set; }
    public int TotalDisplayLines
    {
        get { lock (SyncRoot) return _altActive ? Rows : _scrollback.Count + Rows; }
    }

    public int CursorAbsoluteLine
    {
        get { lock (SyncRoot) return _altActive ? CursorRow : _scrollback.Count + CursorRow; }
    }

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
        lock (SyncRoot)
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
    }
}
