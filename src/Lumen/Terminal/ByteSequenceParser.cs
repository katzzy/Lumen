using System.Text;

namespace Lumen.Terminal;

public enum ByteInputMode
{
    Text,
    Escape,
    Hex
}

/// <summary>
/// Parses operator input into raw bytes for "send arbitrary bytes" workflows.
/// </summary>
public static class ByteSequenceParser
{
    public static bool TryParse(string? input, ByteInputMode mode, Encoding encoding, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = "";
        input ??= "";

        try
        {
            switch (mode)
            {
                case ByteInputMode.Hex:
                    return TryParseHex(input, out bytes, out error);
                case ByteInputMode.Escape:
                    return TryParseEscape(input, encoding, out bytes, out error);
                default:
                    bytes = encoding.GetBytes(input);
                    return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    private static bool TryParseHex(string input, out byte[] bytes, out string error)
    {
        var result = new List<byte>(input.Length / 2);
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (char.IsWhiteSpace(c) || c is ',' or ';' or ':' or '-' or '_')
            {
                i++;
                continue;
            }

            if (c == '\\' && i + 1 < input.Length && (input[i + 1] == 'x' || input[i + 1] == 'X'))
            {
                i += 2;
            }
            else if (c == '0' && i + 1 < input.Length && (input[i + 1] == 'x' || input[i + 1] == 'X'))
            {
                i += 2;
            }

            int start = i;
            while (i < input.Length && IsHex(input[i])) i++;
            int length = i - start;
            if (length == 0)
            {
                error = $"位置 {start + 1} 的字符“{c}”不是十六进制数字";
                bytes = Array.Empty<byte>();
                return false;
            }

            if (length == 1)
            {
                result.Add((byte)HexValue(input[start]));
                continue;
            }

            if (length % 2 != 0)
            {
                error = $"“{input[start..i]}”是奇数个十六进制数字，请用空格分隔字节";
                bytes = Array.Empty<byte>();
                return false;
            }

            for (int k = start; k < i; k += 2)
            {
                result.Add((byte)(HexValue(input[k]) * 16 + HexValue(input[k + 1])));
            }
        }

        bytes = result.ToArray();
        error = "";
        return true;
    }

    private static bool TryParseEscape(string input, Encoding encoding, out byte[] bytes, out string error)
    {
        var result = new List<byte>(input.Length);
        var literal = new StringBuilder(input.Length);

        void FlushLiteral()
        {
            if (literal.Length == 0) return;
            result.AddRange(encoding.GetBytes(literal.ToString()));
            literal.Clear();
        }

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];

            if (c == '^' && i + 1 < input.Length && IsControlTarget(input[i + 1]))
            {
                FlushLiteral();
                char target = input[++i];
                result.Add(target == '?' ? (byte)0x7F : (byte)(char.ToUpperInvariant(target) & 0x1F));
                continue;
            }

            if (c != '\\')
            {
                literal.Append(c);
                continue;
            }

            if (i + 1 >= input.Length)
            {
                error = "转义串以单个反斜杠结尾，缺少转义字符";
                bytes = Array.Empty<byte>();
                return false;
            }

            char esc = input[++i];
            FlushLiteral();

            switch (esc)
            {
                case '0':
                    result.Add(0x00);
                    break;
                case 'a':
                    result.Add(0x07);
                    break;
                case 'b':
                    result.Add(0x08);
                    break;
                case 'e':
                case 'E':
                    result.Add(0x1B);
                    break;
                case 'f':
                    result.Add(0x0C);
                    break;
                case 'n':
                    result.Add(0x0A);
                    break;
                case 'r':
                    result.Add(0x0D);
                    break;
                case 't':
                    result.Add(0x09);
                    break;
                case 'v':
                    result.Add(0x0B);
                    break;
                case '\\':
                    result.Add(0x5C);
                    break;
                case '\'':
                    result.Add(0x27);
                    break;
                case '"':
                    result.Add(0x22);
                    break;
                case 'x':
                case 'X':
                {
                    int start = i + 1;
                    int digits = 0;
                    while (i + 1 < input.Length && digits < 2 && IsHex(input[i + 1]))
                    {
                        i++;
                        digits++;
                    }
                    if (digits == 0)
                    {
                        error = "\\x 后需要 1-2 位十六进制数字";
                        bytes = Array.Empty<byte>();
                        return false;
                    }
                    result.Add((byte)ParseHex(input.AsSpan(start, digits)));
                    break;
                }
                case 'u':
                case 'U':
                {
                    int digits = esc == 'u' ? 4 : 8;
                    if (i + digits >= input.Length)
                    {
                        error = $"\\{esc} 后需要 {digits} 位十六进制数字";
                        bytes = Array.Empty<byte>();
                        return false;
                    }
                    var span = input.AsSpan(i + 1, digits);
                    for (int k = 0; k < span.Length; k++)
                    {
                        if (!IsHex(span[k]))
                        {
                            error = $"\\{esc} 后需要 {digits} 位十六进制数字";
                            bytes = Array.Empty<byte>();
                            return false;
                        }
                    }
                    i += digits;
                    int codePoint = ParseHex(span);
                    if (codePoint is < 0 or > 0x10FFFF)
                    {
                        error = "Unicode 码点超出范围";
                        bytes = Array.Empty<byte>();
                        return false;
                    }
                    result.AddRange(encoding.GetBytes(char.ConvertFromUtf32(codePoint)));
                    break;
                }
                default:
                    error = $"未知转义序列“\\{esc}”";
                    bytes = Array.Empty<byte>();
                    return false;
            }
        }

        FlushLiteral();
        bytes = result.ToArray();
        error = "";
        return true;
    }

    private static bool IsControlTarget(char c) =>
        c is '?' or '^' or '_' or '[' or ']' or '\\' ||
        (c >= 'A' && c <= 'Z') ||
        (c >= 'a' && c <= 'z');

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0
    };

    private static int ParseHex(ReadOnlySpan<char> digits)
    {
        int value = 0;
        foreach (char c in digits) value = value * 16 + HexValue(c);
        return value;
    }
}
