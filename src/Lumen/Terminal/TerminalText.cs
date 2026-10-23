using System.Globalization;

namespace Lumen.Terminal;

public static class TerminalText
{
    public static bool IsCombining(int codepoint)
    {
        if (codepoint < 0x300) return false;
        var category = CharUnicodeInfo.GetUnicodeCategory(codepoint);
        return category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
    }

    public static int Width(int codepoint)
    {
        if (codepoint <= 0 || codepoint == 0x7F) return 0;
        if (codepoint < 0x20) return 0;
        if (IsCombining(codepoint)) return 0;
        if (IsWide(codepoint)) return 2;
        return 1;
    }

    private static bool IsWide(int cp)
    {
        if (cp < 0x1100) return false;
        return cp <= 0x115F
            || cp == 0x2329 || cp == 0x232A
            || (cp >= 0x2E80 && cp <= 0xA4CF && cp != 0x303F)
            || (cp >= 0xAC00 && cp <= 0xD7A3)
            || (cp >= 0xF900 && cp <= 0xFAFF)
            || (cp >= 0xFE10 && cp <= 0xFE19)
            || (cp >= 0xFE30 && cp <= 0xFE6F)
            || (cp >= 0xFF00 && cp <= 0xFF60)
            || (cp >= 0xFFE0 && cp <= 0xFFE6)
            || (cp >= 0x1F300 && cp <= 0x1F64F)
            || (cp >= 0x1F680 && cp <= 0x1F6FF)
            || (cp >= 0x1F900 && cp <= 0x1F9FF)
            || (cp >= 0x1FA70 && cp <= 0x1FAFF)
            || (cp >= 0x20000 && cp <= 0x3FFFD);
    }
}
