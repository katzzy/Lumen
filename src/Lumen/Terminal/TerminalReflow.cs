namespace Lumen.Terminal;

public sealed class ReflowResult
{
    public List<TerminalLine> Lines { get; } = new();
    public int CursorLine { get; set; }
    public int CursorColumn { get; set; }
}

/// <summary>
/// Rewraps terminal lines after a column change. A group of lines joined by
/// <see cref="TerminalLine.Wrapped"/> is treated as one logical line, so resizing
/// the window no longer truncates or drops content.
/// </summary>
public static class TerminalReflow
{
    public static ReflowResult Reflow(
        IReadOnlyList<TerminalLine> source,
        int columns,
        int cursorLine,
        int cursorColumn)
    {
        columns = Math.Max(2, columns);
        var result = new ReflowResult();
        if (source.Count == 0)
        {
            result.Lines.Add(new TerminalLine(columns));
            return result;
        }

        int index = 0;
        while (index < source.Count)
        {
            int start = index;
            while (index + 1 < source.Count && source[index].Wrapped) index++;
            int end = index;
            ReflowGroup(source, start, end, columns, cursorLine, cursorColumn, result);
            index++;
        }

        if (result.Lines.Count == 0) result.Lines.Add(new TerminalLine(columns));
        result.CursorLine = Math.Clamp(result.CursorLine, 0, result.Lines.Count - 1);
        result.CursorColumn = Math.Clamp(result.CursorColumn, 0, columns - 1);
        return result;
    }

    private static void ReflowGroup(
        IReadOnlyList<TerminalLine> source,
        int start,
        int end,
        int columns,
        int cursorLine,
        int cursorColumn,
        ReflowResult result)
    {
        int groupStart = result.Lines.Count;
        bool cursorInGroup = cursorLine >= start && cursorLine <= end;

        int cursorOffset = 0;
        if (cursorInGroup)
        {
            for (int line = start; line < cursorLine; line++)
            {
                cursorOffset += source[line].Cells.Length;
            }
            cursorOffset += Math.Min(cursorColumn, source[cursorLine].Cells.Length);
        }

        var cells = new List<Cell>();
        for (int line = start; line <= end; line++)
        {
            cells.AddRange(source[line].Cells);
        }

        int endCell = cells.Count;
        while (endCell > 0 && IsBlank(cells[endCell - 1])) endCell--;

        if (endCell == 0)
        {
            result.Lines.Add(new TerminalLine(columns));
            if (cursorInGroup)
            {
                result.CursorLine = groupStart;
                result.CursorColumn = 0;
            }
            return;
        }

        int position = 0;
        while (position < endCell)
        {
            var line = new TerminalLine(columns);
            int column = 0;
            while (position < endCell && column < columns)
            {
                var cell = cells[position];
                bool wide = (cell.Flags & CellFlags.Wide) != 0;

                if (wide)
                {
                    if (column + 2 > columns)
                    {
                        // A double-width glyph cannot straddle the right edge; pad
                        // this cell and retry the glyph at the start of the next line.
                        line.Cells[column] = Cell.Empty;
                        column++;
                        break;
                    }

                    line.Cells[column] = cell;
                    if (position + 1 < endCell)
                    {
                        line.Cells[column + 1] = cells[position + 1];
                    }
                    position += 2;
                    column += 2;
                }
                else
                {
                    line.Cells[column] = cell;
                    position++;
                    column++;
                }
            }
            result.Lines.Add(line);
        }

        for (int line = groupStart; line < result.Lines.Count - 1; line++)
        {
            result.Lines[line].Wrapped = true;
        }

        if (cursorInGroup)
        {
            int offset = Math.Clamp(cursorOffset, 0, endCell);
            if (offset == endCell && offset > 0) offset = endCell - 1;
            result.CursorLine = groupStart + offset / columns;
            result.CursorColumn = offset % columns;
        }
    }

    private static bool IsBlank(Cell cell) =>
        (cell.Rune == ' ' || cell.Rune == 0) &&
        cell.Fg == 0 &&
        cell.Bg == 0 &&
        cell.Flags == CellFlags.None &&
        cell.Combining is null;
}
