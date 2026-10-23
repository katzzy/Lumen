using System.Text;

namespace Lumen.Terminal;

public sealed class VtParser
{
    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        Osc,
        OscEsc,
        DcsIgnore,
        DcsEsc,
        Charset
    }

    private readonly ScreenBuffer _b;
    private readonly int[] _params = new int[32];
    private int _paramCount;
    private char _prefix;
    private char _intermediate;
    private char _charsetTarget;
    private State _state = State.Ground;
    private readonly StringBuilder _osc = new();
    private bool _g0Special;
    private bool _g1Special;
    private bool _shiftOut;
    private int _pendingHigh;
    private int _lastRune = ' ';

    public VtParser(ScreenBuffer buffer)
    {
        _b = buffer;
    }

    public Action<string>? TitleChanged { get; set; }
    public Action<string>? Response { get; set; }
    public Action? Bell { get; set; }

    public ScreenBuffer Buffer => _b;

    public void Parse(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            switch (_state)
            {
                case State.Ground:
                    GroundChar(ch);
                    break;
                case State.Escape:
                    EscapeChar(ch);
                    break;
                case State.EscapeIntermediate:
                    EscapeIntermediateChar(ch);
                    break;
                case State.CsiEntry:
                case State.CsiParam:
                case State.CsiIntermediate:
                    CsiChar(ch);
                    break;
                case State.CsiIgnore:
                    if (ch == '\x1b') EnterEscape();
                    else if (ch >= 0x40 && ch <= 0x7E) _state = State.Ground;
                    break;
                case State.Osc:
                    OscChar(ch);
                    break;
                case State.OscEsc:
                    if (ch == '\\') DispatchOsc();
                    else
                    {
                        if (_osc.Length < 8192) _osc.Append('\x1b').Append(ch);
                        _state = State.Osc;
                    }
                    break;
                case State.DcsIgnore:
                    if (ch == '\x1b') _state = State.DcsEsc;
                    else if (ch == '\a') _state = State.Ground;
                    break;
                case State.DcsEsc:
                    _state = ch == '\\' ? State.Ground : State.DcsIgnore;
                    break;
                case State.Charset:
                    if (_charsetTarget == '(') _g0Special = ch == '0';
                    else if (_charsetTarget == ')') _g1Special = ch == '0';
                    _state = State.Ground;
                    break;
            }
        }
    }

    private void GroundChar(char ch)
    {
        if (ch == '\x1b')
        {
            EnterEscape();
            return;
        }
        if (ch < 0x20)
        {
            ExecuteControl(ch);
            return;
        }
        if (ch == '\x7f') return;

        if (char.IsHighSurrogate(ch))
        {
            _pendingHigh = ch;
            return;
        }
        if (char.IsLowSurrogate(ch) && _pendingHigh != 0)
        {
            int cp = char.ConvertToUtf32((char)_pendingHigh, ch);
            _pendingHigh = 0;
            Print(cp);
            return;
        }
        if (_pendingHigh != 0)
        {
            Print(_pendingHigh);
            _pendingHigh = 0;
        }
        Print(ch);
    }

    private void ExecuteControl(char ch)
    {
        switch (ch)
        {
            case '\a':
                Bell?.Invoke();
                break;
            case '\b':
                _b.Backspace();
                break;
            case '\t':
                _b.Tab();
                break;
            case '\n':
            case '\v':
            case '\f':
                _b.LineFeed();
                break;
            case '\r':
                _b.CarriageReturn();
                break;
            case '\x0e':
                _shiftOut = true;
                break;
            case '\x0f':
                _shiftOut = false;
                break;
            case '\x18':
            case '\x1a':
                _state = State.Ground;
                break;
        }
    }

    private void Print(int codepoint)
    {
        if (codepoint >= 0x5f && codepoint <= 0x7e && ActiveSpecialCharset)
        {
            codepoint = MapSpecial((char)codepoint);
        }
        _lastRune = codepoint;
        _b.Print(codepoint);
        _b.TotalPrinted++;
    }

    private bool ActiveSpecialCharset => _shiftOut ? _g1Special : _g0Special;

    private static char MapSpecial(char c) => c switch
    {
        '_' => ' ',
        '`' => '◆',
        'a' => '▒',
        'b' => '␉',
        'c' => '␌',
        'd' => '␍',
        'e' => '␊',
        'f' => '°',
        'g' => '±',
        'h' => '␤',
        'i' => '␋',
        'j' => '┘',
        'k' => '┐',
        'l' => '┌',
        'm' => '└',
        'n' => '┼',
        'o' => '⎺',
        'p' => '⎻',
        'q' => '─',
        'r' => '⎼',
        's' => '⎽',
        't' => '├',
        'u' => '┤',
        'v' => '┴',
        'w' => '┬',
        'x' => '│',
        'y' => '≤',
        'z' => '≥',
        '{' => 'π',
        '|' => '≠',
        '}' => '£',
        '~' => '·',
        _ => c
    };

    private void EnterEscape()
    {
        _state = State.Escape;
        _prefix = '\0';
        _intermediate = '\0';
        _osc.Clear();
    }

    private void EscapeChar(char ch)
    {
        switch (ch)
        {
            case '[':
                BeginCsi();
                return;
            case ']':
                _state = State.Osc;
                _osc.Clear();
                return;
            case 'P':
                _state = State.DcsIgnore;
                return;
            case '(':
            case ')':
                _charsetTarget = ch;
                _state = State.Charset;
                return;
            case '#':
                _intermediate = ch;
                _state = State.EscapeIntermediate;
                return;
            case '7':
                _b.SaveCursor();
                break;
            case '8':
                _b.RestoreCursor();
                break;
            case 'D':
                _b.LineFeed();
                break;
            case 'E':
                _b.NextLine();
                break;
            case 'M':
                _b.ReverseIndex();
                break;
            case 'H':
                _b.SetTabStop();
                break;
            case 'c':
                _b.Reset();
                _g0Special = false;
                _g1Special = false;
                _shiftOut = false;
                break;
            case '=':
            case '>':
                break;
            case '\\':
                break;
            case '\x1b':
                _state = State.Escape;
                return;
            default:
                if (ch >= 0x20 && ch <= 0x2f)
                {
                    _intermediate = ch;
                    _state = State.EscapeIntermediate;
                    return;
                }
                break;
        }
        _state = State.Ground;
    }

    private void EscapeIntermediateChar(char ch)
    {
        if (_intermediate == '#' && ch == '8')
        {
            FillAlignmentPattern();
            _state = State.Ground;
            return;
        }
        _state = ch == '\x1b' ? State.Escape : State.Ground;
    }

    private void FillAlignmentPattern()
    {
        for (int r = 0; r < _b.Rows; r++)
        {
            for (int c = 0; c < _b.Columns; c++)
            {
                _b.SetCursor(r, c);
                _b.Print('E');
            }
        }
        _b.SetCursor(0, 0);
    }

    private void BeginCsi()
    {
        _state = State.CsiEntry;
        _paramCount = 1;
        _params[0] = -1;
        _prefix = '\0';
        _intermediate = '\0';
    }

    private void CsiChar(char ch)
    {
        if (ch == '\x1b')
        {
            EnterEscape();
            return;
        }
        if (ch == '\x18' || ch == '\x1a')
        {
            _state = State.Ground;
            return;
        }

        if (ch >= 0x40 && ch <= 0x7e)
        {
            DispatchCsi(ch);
            _state = State.Ground;
            return;
        }

        if (ch == '?' || ch == '>' || ch == '<' || ch == '=')
        {
            if (_state == State.CsiEntry)
            {
                _prefix = ch;
                return;
            }
            _state = State.CsiIgnore;
            return;
        }

        if (ch >= '0' && ch <= '9')
        {
            if (_state == State.CsiEntry) _state = State.CsiParam;
            if (_state == State.CsiParam)
            {
                int idx = _paramCount - 1;
                if (_params[idx] < 0) _params[idx] = 0;
                if (_params[idx] < 100000)
                {
                    _params[idx] = _params[idx] * 10 + (ch - '0');
                }
            }
            return;
        }

        if (ch == ';' || ch == ':')
        {
            if (_state == State.CsiEntry || _state == State.CsiParam)
            {
                _state = State.CsiParam;
                if (_paramCount < _params.Length)
                {
                    _params[_paramCount++] = -1;
                }
            }
            return;
        }

        if (ch >= 0x20 && ch <= 0x2f)
        {
            _intermediate = ch;
            _state = State.CsiIntermediate;
            return;
        }

        if (_state != State.CsiIntermediate)
        {
            _state = State.CsiIgnore;
        }
    }

    private int Raw(int index) => index >= 0 && index < _paramCount ? _params[index] : -1;

    private int P(int index, int fallback)
    {
        int v = Raw(index);
        return v <= 0 ? fallback : v;
    }

    private void DispatchCsi(char final)
    {
        switch (final)
        {
            case 'A':
                _b.MoveCursor(-P(0, 1), 0);
                break;
            case 'B':
                _b.MoveCursor(P(0, 1), 0);
                break;
            case 'C':
                _b.MoveCursor(0, P(0, 1));
                break;
            case 'D':
                _b.MoveCursor(0, -P(0, 1));
                break;
            case 'E':
                _b.MoveCursor(P(0, 1), 0);
                _b.CarriageReturn();
                break;
            case 'F':
                _b.MoveCursor(-P(0, 1), 0);
                _b.CarriageReturn();
                break;
            case 'G':
            case '`':
                _b.MoveCursor(0, P(0, 1) - 1 - _b.CursorCol);
                break;
            case 'H':
            case 'f':
                {
                    int row = P(0, 1) - 1;
                    int col = P(1, 1) - 1;
                    if (_b.OriginMode) row += _b.ScrollTop;
                    _b.SetCursor(row, col);
                    break;
                }
            case 'I':
                _b.Tab(P(0, 1));
                break;
            case 'J':
                _b.EraseInDisplay(Raw(0) <= 0 ? 0 : Raw(0));
                break;
            case 'K':
                _b.EraseInLine(Raw(0) <= 0 ? 0 : Raw(0));
                break;
            case 'L':
                _b.InsertLines(P(0, 1));
                break;
            case 'M':
                _b.DeleteLines(P(0, 1));
                break;
            case 'P':
                _b.DeleteChars(P(0, 1));
                break;
            case 'S':
                _b.ScrollUp(P(0, 1));
                break;
            case 'T':
                _b.ScrollDown(P(0, 1));
                break;
            case 'X':
                _b.EraseChars(P(0, 1));
                break;
            case 'Z':
                _b.BackTab(P(0, 1));
                break;
            case '@':
                _b.InsertChars(P(0, 1));
                break;
            case 'a':
                _b.MoveCursor(0, P(0, 1));
                break;
            case 'b':
                if (_lastRune > 0)
                {
                    int n = Math.Min(P(0, 1), 4096);
                    for (int i = 0; i < n; i++) _b.Print(_lastRune);
                }
                break;
            case 'c':
                if (_prefix == '>') Response?.Invoke("\x1b[>0;276;0c");
                else Response?.Invoke("\x1b[?1;2c");
                break;
            case 'd':
                {
                    int row = P(0, 1) - 1;
                    if (_b.OriginMode) row += _b.ScrollTop;
                    _b.MoveCursor(row - _b.CursorRow, 0);
                    break;
                }
            case 'e':
                _b.MoveCursor(P(0, 1), 0);
                break;
            case 'g':
                _b.ClearTabStops(Raw(0) <= 0 ? 0 : Raw(0));
                break;
            case 'h':
                SetModes(true);
                break;
            case 'l':
                SetModes(false);
                break;
            case 'm':
                ApplySgr();
                break;
            case 'n':
                if (Raw(0) == 5) Response?.Invoke("\x1b[0n");
                else if (Raw(0) == 6) Response?.Invoke($"\x1b[{_b.CursorRow + 1};{_b.CursorCol + 1}R");
                break;
            case 'q':
                if (_intermediate == ' ')
                {
                    _b.CursorStyle = Math.Clamp(Raw(0), 0, 6);
                }
                break;
            case 'r':
                {
                    int top = P(0, 1) - 1;
                    int bottom = P(1, _b.Rows) - 1;
                    _b.SetScrollRegion(top, bottom);
                    break;
                }
            case 's':
                _b.SaveCursor();
                break;
            case 'u':
                _b.RestoreCursor();
                break;
            case 't':
                break;
            case 'p':
                if (_intermediate == '!') SoftReset();
                break;
        }
    }

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

    private void OscChar(char ch)
    {
        if (ch == '\a')
        {
            DispatchOsc();
            return;
        }
        if (ch == '\x1b')
        {
            _state = State.OscEsc;
            return;
        }
        if (_osc.Length < 8192) _osc.Append(ch);
    }

    private void DispatchOsc()
    {
        string value = _osc.ToString();
        _osc.Clear();
        _state = State.Ground;

        int semi = value.IndexOf(';');
        string code = semi >= 0 ? value[..semi] : value;
        string payload = semi >= 0 ? value[(semi + 1)..] : "";

        if ((code == "0" || code == "2") && payload.Length > 0)
        {
            payload = payload.Replace("\0", "").Trim();
            if (payload.Length > 0) TitleChanged?.Invoke(payload);
        }
    }
}
