namespace Lumen.Terminal;

public sealed class TerminalPalette
{
    public uint[] Colors { get; } = new uint[256];
    public uint DefaultForeground { get; init; }
    public uint DefaultBackground { get; init; }
    public uint Cursor { get; init; }
    public uint SelectionBackground { get; init; }
    public uint SelectionForeground { get; init; }

    public static uint Pack(byte r, byte g, byte b) =>
        0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;

    public static uint Pack(int r, int g, int b) =>
        0xFF000000u | ((uint)(r & 0xFF) << 16) | ((uint)(g & 0xFF) << 8) | (uint)(b & 0xFF);

    public static TerminalPalette Dark { get; } = CreateDark();

    private static TerminalPalette CreateDark()
    {
        var p = new TerminalPalette
        {
            DefaultForeground = 0xFFD7DEEA,
            DefaultBackground = 0xFF0B0E13,
            Cursor = 0xFF6EA8FF,
            SelectionBackground = 0xFF2B4A7A,
            SelectionForeground = 0xFFFFFFFF
        };

        uint[] baseColors =
        {
            0xFF1C2027, 0xFFF07178, 0xFF9ECE6A, 0xFFE0AF68,
            0xFF7AA2F7, 0xFFBB9AF7, 0xFF7DCFFF, 0xFFC0CAF5,
            0xFF505A70, 0xFFFF8A93, 0xFFB9E68C, 0xFFF0C987,
            0xFF9DB8FF, 0xFFD3B4FF, 0xFF9FE8FF, 0xFFFFFFFF
        };
        Array.Copy(baseColors, p.Colors, 16);

        // xterm 256-color cube (16..231)
        int[] steps = { 0, 95, 135, 175, 215, 255 };
        int idx = 16;
        for (int r = 0; r < 6; r++)
            for (int g = 0; g < 6; g++)
                for (int b = 0; b < 6; b++)
                {
                    p.Colors[idx++] = Pack(steps[r], steps[g], steps[b]);
                }

        // grayscale ramp (232..255)
        for (int i = 0; i < 24; i++)
        {
            int v = 8 + i * 10;
            p.Colors[232 + i] = Pack(v, v, v);
        }

        return p;
    }
}
