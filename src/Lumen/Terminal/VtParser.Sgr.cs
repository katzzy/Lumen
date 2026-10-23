using System.Text;

namespace Lumen.Terminal;

public sealed partial class VtParser
{
    private void SetModes(bool enabled)
    {
        if (_prefix == '?')
        {
            switch (Raw(0))
            {
                case 1:
                    _b.AppCursorKeys = enabled;
                    break;
                case 6:
                    _b.OriginMode = enabled;
                    _b.SetCursor(0, 0);
                    break;
                case 7:
                    _b.AutoWrap = enabled;
                    break;
                case 12:
                    break;
                case 25:
                    _b.CursorVisible = enabled;
                    break;
                case 47:
                case 1047:
                    if (enabled) _b.EnterAltScreen(false);
                    else _b.ExitAltScreen();
                    break;
                case 1048:
                    if (enabled) _b.SaveCursor();
                    else _b.RestoreCursor();
                    break;
                case 1049:
                    if (enabled) _b.EnterAltScreen(true);
                    else _b.ExitAltScreen();
                    break;
                case 2004:
                    _b.BracketedPaste = enabled;
                    break;
                case 1000:
                case 1002:
                case 1003:
                case 1005:
                case 1006:
                    break;
            }
        }
        else
        {
            switch (Raw(0))
            {
                case 4:
                    _b.InsertMode = enabled;
                    break;
                case 20:
                    _b.NewLineMode = enabled;
                    break;
            }
        }
    }

    private void SoftReset()
    {
        _b.ResetPen();
        _b.AutoWrap = true;
        _b.InsertMode = false;
        _b.OriginMode = false;
        _b.AppCursorKeys = false;
        _b.CursorVisible = true;
        _b.SetScrollRegion(0, _b.Rows - 1);
        _b.SetCursor(0, 0);
    }

    private void ApplySgr()
    {
        for (int i = 0; i < _paramCount; i++)
        {
            int p = Raw(i);
            if (p < 0) p = 0;
            switch (p)
            {
                case 0:
                    _b.ResetPen();
                    break;
                case 1:
                    _b.PenFlags |= CellFlags.Bold;
                    break;
                case 2:
                    _b.PenFlags |= CellFlags.Dim;
                    break;
                case 3:
                    _b.PenFlags |= CellFlags.Italic;
                    break;
                case 4:
                    _b.PenFlags |= CellFlags.Underline;
                    break;
                case 5:
                case 6:
                    _b.PenFlags |= CellFlags.Blink;
                    break;
                case 7:
                    _b.PenFlags |= CellFlags.Reverse;
                    break;
                case 8:
                    _b.PenFlags |= CellFlags.Hidden;
                    break;
                case 9:
                    _b.PenFlags |= CellFlags.Strike;
                    break;
                case 21:
                case 22:
                    _b.PenFlags &= ~(CellFlags.Bold | CellFlags.Dim);
                    break;
                case 23:
                    _b.PenFlags &= ~CellFlags.Italic;
                    break;
                case 24:
                    _b.PenFlags &= ~CellFlags.Underline;
                    break;
                case 25:
                    _b.PenFlags &= ~CellFlags.Blink;
                    break;
                case 27:
                    _b.PenFlags &= ~CellFlags.Reverse;
                    break;
                case 28:
                    _b.PenFlags &= ~CellFlags.Hidden;
                    break;
                case 29:
                    _b.PenFlags &= ~CellFlags.Strike;
                    break;
                case 38:
                    i = ApplyExtendedColor(i, true);
                    break;
                case 39:
                    _b.PenFg = 0;
                    break;
                case 48:
                    i = ApplyExtendedColor(i, false);
                    break;
                case 49:
                    _b.PenBg = 0;
                    break;
                default:
                    if (p >= 30 && p <= 37) _b.PenFg = _b.Palette.Colors[p - 30];
                    else if (p >= 40 && p <= 47) _b.PenBg = _b.Palette.Colors[p - 40];
                    else if (p >= 90 && p <= 97) _b.PenFg = _b.Palette.Colors[p - 90 + 8];
                    else if (p >= 100 && p <= 107) _b.PenBg = _b.Palette.Colors[p - 100 + 8];
                    break;
            }
        }
    }

    private int ApplyExtendedColor(int index, bool foreground)
    {
        int mode = Raw(index + 1);
        if (mode == 5)
        {
            int n = Math.Clamp(Raw(index + 2), 0, 255);
            uint color = _b.Palette.Colors[n];
            if (foreground) _b.PenFg = color;
            else _b.PenBg = color;
            return index + 2;
        }
        if (mode == 2)
        {
            int r = Math.Clamp(Raw(index + 2), 0, 255);
            int g = Math.Clamp(Raw(index + 3), 0, 255);
            int b = Math.Clamp(Raw(index + 4), 0, 255);
            uint color = TerminalPalette.Pack(r, g, b);
            if (foreground) _b.PenFg = color;
            else _b.PenBg = color;
            return index + 4;
        }
        return index;
    }

}
