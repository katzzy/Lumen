using System.Text;

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
        var sb = new StringBuilder(end - start);
        for (int i = start; i < end; i++)
        {
            var cell = Cells[i];
            if ((cell.Flags & CellFlags.WideTail) != 0) continue;
            int rune = cell.Rune == 0 ? ' ' : cell.Rune;
            if (rune > 0 && rune <= 0x10FFFF && !char.IsSurrogate((char)Math.Min(rune, 0xFFFF)))
            {
                sb.Append(char.ConvertFromUtf32(rune));
            }
            else
            {
                sb.Append(' ');
            }
            if (cell.Combining is not null) sb.Append(cell.Combining);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Returns the line text plus a char-index-to-cell-index map for search highlighting.
    /// Wide glyphs occupy two UTF-16 chars in the text but still map to one cell.
    /// </summary>
    public (string Text, int[] CellAtChar) GetTextWithCells(int start = 0, int length = -1)
    {
        if (length < 0) length = Cells.Length - start;
        int end = Math.Min(Cells.Length, start + length);
        var sb = new StringBuilder(end - start);
        var map = new List<int>(end - start);

        for (int i = start; i < end; i++)
        {
            var cell = Cells[i];
            if ((cell.Flags & CellFlags.WideTail) != 0) continue;
            int rune = cell.Rune == 0 ? ' ' : cell.Rune;
            string text;
            if (rune > 0 && rune <= 0x10FFFF && !char.IsSurrogate((char)Math.Min(rune, 0xFFFF)))
            {
                text = char.ConvertFromUtf32(rune);
            }
            else
            {
                text = " ";
            }

            sb.Append(text);
            for (int n = 0; n < text.Length; n++) map.Add(i);

            if (cell.Combining is not null)
            {
                sb.Append(cell.Combining);
                for (int n = 0; n < cell.Combining.Length; n++) map.Add(i);
            }
        }

        int textLength = sb.Length;
        while (textLength > 0 && sb[textLength - 1] == ' ') textLength--;
        string value = sb.ToString(0, textLength);
        if (map.Count > textLength) map.RemoveRange(textLength, map.Count - textLength);
        return (value, map.ToArray());
    }
}
