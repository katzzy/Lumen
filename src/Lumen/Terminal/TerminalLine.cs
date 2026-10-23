namespace Lumen.Terminal;

public sealed class TerminalLine
{
    public Cell[] Cells;
    public bool Wrapped;

    public TerminalLine(int columns)
    {
        Cells = new Cell[columns];
        for (int i = 0; i < columns; i++)
        {
            Cells[i] = Cell.Empty;
        }
    }

    private TerminalLine(Cell[] cells, bool wrapped)
    {
        Cells = cells;
        Wrapped = wrapped;
    }

    public int Length => Cells.Length;

    public void Resize(int columns)
    {
        if (columns == Cells.Length) return;
        var next = new Cell[columns];
        int copy = Math.Min(columns, Cells.Length);
        Array.Copy(Cells, next, copy);
        for (int i = copy; i < columns; i++)
        {
            next[i] = Cell.Empty;
        }
        Cells = next;
    }

    public void Clear()
    {
        for (int i = 0; i < Cells.Length; i++)
        {
            Cells[i] = Cell.Empty;
        }
        Wrapped = false;
    }

    public TerminalLine Clone() => new((Cell[])Cells.Clone(), Wrapped);

    public string GetText(int start = 0, int length = -1)
    {
        if (length < 0) length = Cells.Length - start;
        int end = Math.Min(Cells.Length, start + length);
        var chars = new char[end - start];
        int n = 0;
        for (int i = start; i < end; i++)
        {
            var cell = Cells[i];
            if (cell.Flags.HasFlag(CellFlags.WideTail)) continue;
            int rune = cell.Rune == 0 ? ' ' : cell.Rune;
            if (rune <= 0xFFFF)
            {
                chars[n++] = (char)rune;
            }
            else
            {
                chars[n++] = ' ';
            }
        }
        return n == 0 ? "" : new string(chars, 0, n).TrimEnd();
    }
}
