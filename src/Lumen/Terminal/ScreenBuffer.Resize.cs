namespace Lumen.Terminal;

public sealed partial class ScreenBuffer
{
    public void Resize(int columns, int rows)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(2, rows);
        if (columns == Columns && rows == Rows) return;

        int oldColumns = Columns;
        int oldCursorRow = CursorRow;
        int oldCursorCol = CursorCol;

        if (columns != oldColumns)
        {
            NeedsFullReflow = ResizeWithReflow(columns, rows, oldCursorRow, oldCursorCol, 512);
        }
        else
        {
            ResizeHeight(rows);
        }

        Columns = columns;
        Rows = rows;
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        _pendingWrap = false;
        CursorRow = Math.Clamp(CursorRow, 0, rows - 1);
        CursorCol = Math.Clamp(CursorCol, 0, columns - 1);
        _savedMain.Row = Math.Clamp(_savedMain.Row, 0, rows - 1);
        _savedMain.Col = Math.Clamp(_savedMain.Col, 0, columns - 1);
        _savedAlt.Row = Math.Clamp(_savedAlt.Row, 0, rows - 1);
        _savedAlt.Col = Math.Clamp(_savedAlt.Col, 0, columns - 1);

        RebuildTabStops(oldColumns, columns);
        _scrollback.TrimTo(ScrollbackLimit);
    }

    private bool ResizeWithReflow(int newColumns, int newRows, int cursorRow, int cursorCol, int scrollbackBudget)
    {
        int mainCursorRow = _altActive ? _savedMain.Row : cursorRow;
        int mainCursorCol = _altActive ? _savedMain.Col : cursorCol;

        // Reflow only recent history plus the visible screen. Reflowing a full
        // 10k-line scrollback on every drag frame would make the UI stutter;
        // TerminalView schedules ReflowAll once resizing settles.
        int prefixCount = Math.Max(0, _scrollback.Count - scrollbackBudget);
        int recentCount = _scrollback.Count - prefixCount;

        var mainSource = new List<TerminalLine>(recentCount + _main.Length);
        for (int i = prefixCount; i < _scrollback.Count; i++) mainSource.Add(_scrollback[i]);
        mainSource.AddRange(_main);
        int mainCursorAbs = recentCount + mainCursorRow;
        var main = TerminalReflow.Reflow(mainSource, newColumns, mainCursorAbs, mainCursorCol);

        int altCursorRow = _altActive ? cursorRow : 0;
        int altCursorCol = _altActive ? cursorCol : 0;
        var alt = TerminalReflow.Reflow(_alt, newColumns, altCursorRow, altCursorCol);

        int mainStart = Math.Max(0, main.Lines.Count - newRows);
        var scrollback = new List<TerminalLine>(prefixCount + mainStart);
        for (int i = 0; i < prefixCount; i++) scrollback.Add(_scrollback[i]);
        for (int i = 0; i < mainStart; i++) scrollback.Add(main.Lines[i]);
        _scrollback.Clear();
        for (int i = 0; i < scrollback.Count; i++) _scrollback.Add(scrollback[i]);

        _main = new TerminalLine[newRows];
        for (int r = 0; r < newRows; r++)
        {
            int index = mainStart + r;
            _main[r] = index < main.Lines.Count ? main.Lines[index] : new TerminalLine(newColumns);
        }

        int altStart = Math.Max(0, alt.Lines.Count - newRows);
        _alt = new TerminalLine[newRows];
        for (int r = 0; r < newRows; r++)
        {
            int index = altStart + r;
            _alt[r] = index < alt.Lines.Count ? alt.Lines[index] : new TerminalLine(newColumns);
        }

        if (_altActive)
        {
            CursorRow = Math.Clamp(alt.CursorLine - altStart, 0, newRows - 1);
            CursorCol = Math.Clamp(alt.CursorColumn, 0, newColumns - 1);
            _savedMain.Row = Math.Clamp(main.CursorLine - mainStart, 0, newRows - 1);
            _savedMain.Col = Math.Clamp(main.CursorColumn, 0, newColumns - 1);
        }
        else
        {
            CursorRow = Math.Clamp(main.CursorLine - mainStart, 0, newRows - 1);
            CursorCol = Math.Clamp(main.CursorColumn, 0, newColumns - 1);
            _savedAlt.Row = Math.Clamp(alt.CursorLine - altStart, 0, newRows - 1);
            _savedAlt.Col = Math.Clamp(alt.CursorColumn, 0, newColumns - 1);
        }

        return prefixCount > 0;
    }

    /// <summary>
    /// Reflows the entire scrollback after the user has stopped resizing. This is
    /// deliberately not done on every resize frame (a 10k-line reflow takes ~90ms).
    /// </summary>
    public void ReflowAll()
    {
        if (!NeedsFullReflow) return;
        var job = CreateReflowJob();
        if (job is null)
        {
            NeedsFullReflow = false;
            return;
        }

        var result = TerminalReflow.Reflow(job.Source, Columns, job.CursorLine, job.CursorColumn);
        ApplyReflow(job, result);
    }

    public ReflowJob? CreateReflowJob()
    {
        if (!NeedsFullReflow || _altActive) return null;

        var source = new List<TerminalLine>(_scrollback.Count + _main.Length);
        for (int i = 0; i < _scrollback.Count; i++) source.Add(_scrollback[i]);
        foreach (var line in _main)
        {
            var clone = new TerminalLine(line.Cells.Length) { Wrapped = line.Wrapped };
            Array.Copy(line.Cells, clone.Cells, line.Cells.Length);
            source.Add(clone);
        }

        return new ReflowJob(
            source,
            _scrollback.Count + CursorRow,
            CursorCol,
            Columns,
            Rows,
            ComputeSignature());
    }

    public bool IsReflowJobValid(ReflowJob job) =>
        !_altActive && job.Columns == Columns && job.Rows == Rows && job.Signature == ComputeSignature();

    public void ApplyReflow(ReflowJob job, ReflowResult result)
    {
        if (!IsReflowJobValid(job)) return;

        int start = Math.Max(0, result.Lines.Count - Rows);
        _scrollback.Clear();
        for (int i = 0; i < start; i++) _scrollback.Add(result.Lines[i]);

        _main = new TerminalLine[Rows];
        for (int r = 0; r < Rows; r++)
        {
            int index = start + r;
            _main[r] = index < result.Lines.Count ? result.Lines[index] : new TerminalLine(Columns);
        }

        CursorRow = Math.Clamp(result.CursorLine - start, 0, Rows - 1);
        CursorCol = Math.Clamp(result.CursorColumn, 0, Columns - 1);
        NeedsFullReflow = false;
        _scrollback.TrimTo(ScrollbackLimit);
    }

    private long ComputeSignature()
    {
        var hash = new HashCode();
        hash.Add(Columns);
        hash.Add(Rows);
        hash.Add(_scrollback.Count);
        hash.Add(CursorRow);
        hash.Add(CursorCol);
        hash.Add(TotalPrinted);
        foreach (var line in _main)
        {
            foreach (var cell in line.Cells)
            {
                hash.Add(cell.Rune);
                hash.Add(cell.Fg);
                hash.Add(cell.Bg);
                hash.Add((ushort)cell.Flags);
            }
        }
        return hash.ToHashCode();
    }

    public sealed record ReflowJob(
        List<TerminalLine> Source,
        int CursorLine,
        int CursorColumn,
        int Columns,
        int Rows,
        long Signature);

    private void ResizeHeight(int newRows)
    {
        int oldRows = _main.Length;
        if (newRows >= oldRows)
        {
            _main = ExpandRows(_main, newRows);
            _alt = ExpandRows(_alt, newRows);
            return;
        }

        int drop = oldRows - newRows;
        for (int i = 0; i < drop; i++)
        {
            AddScrollback(_main[i]);
        }

        _main = TakeBottomRows(_main, newRows);
        _alt = TakeBottomRows(_alt, newRows);

        if (_altActive)
        {
            CursorRow = Math.Max(0, CursorRow - drop);
            _savedMain.Row = Math.Max(0, _savedMain.Row - drop);
        }
        else
        {
            CursorRow = Math.Max(0, CursorRow - drop);
        }
    }

    private static TerminalLine[] ExpandRows(TerminalLine[] source, int rows)
    {
        var result = new TerminalLine[rows];
        for (int i = 0; i < rows; i++)
        {
            result[i] = i < source.Length ? source[i] : new TerminalLine(source.Length > 0 ? source[0].Cells.Length : 2);
        }
        return result;
    }

    private static TerminalLine[] TakeBottomRows(TerminalLine[] source, int rows)
    {
        var result = new TerminalLine[rows];
        int start = Math.Max(0, source.Length - rows);
        int columns = source.Length > 0 ? source[0].Cells.Length : 2;
        for (int i = 0; i < rows; i++)
        {
            int index = start + i;
            result[i] = index < source.Length ? source[index] : new TerminalLine(columns);
        }
        return result;
    }

    private void RebuildTabStops(int oldColumns, int columns)
    {
        var tabs = new bool[columns];
        for (int i = 0; i < oldColumns && i < columns; i++) tabs[i] = _tabStops[i];
        for (int i = oldColumns; i < columns; i += 8) tabs[i] = true;
        _tabStops = tabs;
    }
}
