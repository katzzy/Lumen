using System.Text;
using System.Windows.Input;

namespace Lumen.Terminal;

public static class TerminalKeyEncoder
{
    public static byte[]? Encode(Key key, ModifierKeys modifiers, ScreenBuffer buffer)
    {
        bool ctrl = (modifiers & ModifierKeys.Control) != 0;
        bool alt = (modifiers & ModifierKeys.Alt) != 0;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;

        if (ctrl && shift && key == Key.D6) return new byte[] { 0x1E };
        if (ctrl && key == Key.Space) return new byte[] { 0x00 };

        string? sequence = key switch
        {
            Key.Enter => buffer.NewLineMode ? "\r\n" : "\r",
            Key.Tab => shift ? "\x1b[Z" : "\t",
            Key.Back => "\x7f",
            Key.Escape => "\x1b",
            Key.Up => buffer.AppCursorKeys ? "\x1bOA" : "\x1b[A",
            Key.Down => buffer.AppCursorKeys ? "\x1bOB" : "\x1b[B",
            Key.Right => buffer.AppCursorKeys ? "\x1bOC" : "\x1b[C",
            Key.Left => buffer.AppCursorKeys ? "\x1bOD" : "\x1b[D",
            Key.Home => "\x1b[H",
            Key.End => "\x1b[F",
            Key.Insert => "\x1b[2~",
            Key.Delete => "\x1b[3~",
            Key.PageUp => "\x1b[5~",
            Key.PageDown => "\x1b[6~",
            Key.F1 => "\x1bOP",
            Key.F2 => "\x1bOQ",
            Key.F3 => "\x1bOR",
            Key.F4 => "\x1bOS",
            Key.F5 => "\x1b[15~",
            Key.F6 => "\x1b[17~",
            Key.F7 => "\x1b[18~",
            Key.F8 => "\x1b[19~",
            Key.F9 => "\x1b[20~",
            Key.F10 => "\x1b[21~",
            Key.F11 => "\x1b[23~",
            Key.F12 => "\x1b[24~",
            _ => null
        };

        if (sequence is not null) return Encoding.UTF8.GetBytes(sequence);

        if (ctrl)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                if (shift && key is not (Key.C or Key.V or Key.A)) { }
                return new[] { (byte)(key - Key.A + 1) };
            }
            if (key == Key.OemOpenBrackets) return new byte[] { 0x1B };
            if (key == Key.OemPipe) return new byte[] { 0x1C };
            if (key == Key.OemCloseBrackets) return new byte[] { 0x1D };
            if (key == Key.D6) return new byte[] { 0x1E };
            if (key == Key.OemMinus) return new byte[] { 0x1F };
            if (key == Key.OemQuestion) return new byte[] { 0x7F };
            return null;
        }

        if (alt && !ctrl)
        {
            char ch = KeyToChar(key, shift);
            if (ch != '\0') return new[] { (byte)0x1B, (byte)ch };
        }

        return null;
    }

    private static char KeyToChar(Key key, bool shift)
    {
        if (key >= Key.A && key <= Key.Z)
        {
            char c = (char)('a' + (key - Key.A));
            return shift ? char.ToUpperInvariant(c) : c;
        }
        if (key >= Key.D0 && key <= Key.D9)
        {
            return (char)('0' + (key - Key.D0));
        }
        return key switch
        {
            Key.OemMinus => '-',
            Key.OemPlus => '=',
            Key.OemOpenBrackets => '[',
            Key.OemCloseBrackets => ']',
            Key.OemPipe => '\\',
            Key.OemSemicolon => ';',
            Key.OemQuotes => '\'',
            Key.OemComma => ',',
            Key.OemPeriod => '.',
            Key.OemQuestion => '/',
            _ => '\0'
        };
    }
}
