namespace Lumen.Terminal;

[Flags]
public enum CellFlags : ushort
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Reverse = 1 << 5,
    Hidden = 1 << 6,
    Strike = 1 << 7,
    Wide = 1 << 8,
    WideTail = 1 << 9
}

public struct Cell
{
    public int Rune;
    public uint Fg;
    public uint Bg;
    public CellFlags Flags;
    public string? Combining;

    public static Cell Empty => new() { Rune = ' ', Fg = 0, Bg = 0, Flags = CellFlags.None };

    public readonly bool IsBlank =>
        (Rune == ' ' || Rune == 0) && Fg == 0 && Bg == 0 && Flags == CellFlags.None && Combining is null;

    public readonly Cell WithRune(int rune) => new()
    {
        Rune = rune,
        Fg = Fg,
        Bg = Bg,
        Flags = Flags,
        Combining = Combining
    };
}
