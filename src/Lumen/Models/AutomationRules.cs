using System.Text;
using System.Text.RegularExpressions;

namespace Lumen.Models;

public sealed class ExpectRule
{
    public string Pattern { get; set; } = "";
    public bool IsRegex { get; set; } = true;
    public string Response { get; set; } = "";
    public bool AppendEnter { get; set; } = true;
    public bool Once { get; set; } = true;
    public int CooldownMs { get; set; } = 1000;
    public bool Enabled { get; set; } = true;

    public ExpectRule Clone() => new()
    {
        Pattern = Pattern,
        IsRegex = IsRegex,
        Response = Response,
        AppendEnter = AppendEnter,
        Once = Once,
        CooldownMs = CooldownMs,
        Enabled = Enabled
    };
}

public sealed class HighlightRule
{
    public string Pattern { get; set; } = "";
    public bool IsRegex { get; set; } = true;
    public string Color { get; set; } = "#F0B429";
    public bool Enabled { get; set; } = true;

    public HighlightRule Clone() => new()
    {
        Pattern = Pattern,
        IsRegex = IsRegex,
        Color = Color,
        Enabled = Enabled
    };
}

/// <summary>
/// Line-oriented editor format for automation rules.
///
/// A rule is "pattern => response". Leading !/+ select literal/repeat mode.
/// To keep that syntax editable without losing data, leading #, !, +, @ and
/// any "=>" inside a pattern are escaped with a backslash. Rules that use
/// non-default appendEnter/cooldown/enabled values are preceded by an
/// "@expect key=value ..." line; highlight rules use "@highlight".
/// </summary>
public static class AutomationTextParser
{
    private const int DefaultCooldownMs = 1000;
    private const string ExpectOptionPrefix = "@expect";
    private const string HighlightOptionPrefix = "@highlight";

    private sealed class ExpectOptions
    {
        public bool AppendEnter { get; set; } = true;
        public int CooldownMs { get; set; } = DefaultCooldownMs;
        public bool Enabled { get; set; } = true;
        public string? Pattern { get; set; }
        public string? Response { get; set; }
        public bool Regex { get; set; } = true;
        public bool Once { get; set; } = true;
        public bool RegexSpecified { get; set; }
        public bool OnceSpecified { get; set; }
        public bool IsExact => Pattern is not null || Response is not null;
    }

    private sealed class HighlightOptions
    {
        public bool Enabled { get; set; } = true;
        public string? Pattern { get; set; }
        public string? Color { get; set; }
        public bool Regex { get; set; } = true;
        public bool RegexSpecified { get; set; }
        public bool IsExact => Pattern is not null;
    }

    public static bool TryParseExpectRules(string? text, out List<ExpectRule> rules, out string error)
    {
        rules = new List<ExpectRule>();
        error = "";
        var lines = SplitLines(text);
        ExpectOptions? pending = null;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (HasOptionPrefix(line, ExpectOptionPrefix))
            {
                if (!TryParseExpectOptions(line, out var parsedOptions, out string optionError))
                {
                    error = LineError(i, optionError);
                    return false;
                }
                if (parsedOptions.IsExact)
                {
                    if (pending is not null)
                    {
                        error = LineError(i, "完整的 @expect 规则前存在未使用的选项行");
                        return false;
                    }
                    if (string.IsNullOrEmpty(parsedOptions.Pattern))
                    {
                        error = LineError(i, "完整 @expect 规则缺少 pattern");
                        return false;
                    }
                    if (parsedOptions.Regex &&
                        !ValidateRegex(parsedOptions.Pattern, out string exactRegexError))
                    {
                        error = LineError(i, exactRegexError);
                        return false;
                    }
                    rules.Add(new ExpectRule
                    {
                        Pattern = parsedOptions.Pattern,
                        IsRegex = parsedOptions.Regex,
                        Once = parsedOptions.Once,
                        Response = parsedOptions.Response ?? "",
                        AppendEnter = parsedOptions.AppendEnter,
                        CooldownMs = parsedOptions.CooldownMs,
                        Enabled = parsedOptions.Enabled
                    });
                    continue;
                }
                if (pending is not null)
                {
                    error = LineError(i, "连续的 @expect 选项行之间缺少规则");
                    return false;
                }
                if (parsedOptions.RegexSpecified || parsedOptions.OnceSpecified)
                {
                    error = LineError(i, "regex/once 只用于 @expect pattern=... 完整规则");
                    return false;
                }
                pending = parsedOptions;
                continue;
            }
            if (HasOptionPrefix(line, HighlightOptionPrefix))
            {
                error = LineError(i, "@highlight 选项不能用于 Expect 规则");
                return false;
            }

            int split = FindSeparator(line);
            if (split < 0)
            {
                error = LineError(i, $"缺少“=>”：{line}");
                return false;
            }

            var patternPart = line[..split].Trim();
            var response = line[(split + 2)..].Trim();
            if (patternPart.Length == 0)
            {
                error = LineError(i, "匹配模式为空");
                return false;
            }

            bool regex = true;
            bool once = true;
            while (patternPart.Length > 0)
            {
                if (HasEscapedLeadingChar(patternPart)) break;
                if (patternPart[0] == '!')
                {
                    regex = false;
                    patternPart = patternPart[1..].TrimStart();
                    continue;
                }
                if (patternPart[0] == '+')
                {
                    once = false;
                    patternPart = patternPart[1..].TrimStart();
                    continue;
                }
                break;
            }

            patternPart = UnescapePattern(patternPart);
            if (patternPart.Length == 0)
            {
                error = LineError(i, "匹配模式为空");
                return false;
            }
            if (regex && !ValidateRegex(patternPart, out string regexError))
            {
                error = LineError(i, regexError);
                return false;
            }

            var options = pending ?? new ExpectOptions();
            rules.Add(new ExpectRule
            {
                Pattern = patternPart,
                IsRegex = regex,
                Once = once,
                Response = response,
                AppendEnter = options.AppendEnter,
                CooldownMs = options.CooldownMs,
                Enabled = options.Enabled
            });
            pending = null;
        }

        if (pending is not null)
        {
            error = "文件末尾的 @expect 选项行后缺少规则";
            return false;
        }
        return true;
    }

    public static string BuildExpectRules(IEnumerable<ExpectRule> rules)
    {
        var lines = new List<string>
        {
            "# 每行：模式 => 发送内容；前缀 ! 字面量，+ 可重复触发",
            "# 行首特殊字符用 \\ 转义；可选 @expect 行设置 appendEnter/cooldown/enabled",
            "# 示例：Password: => admin"
        };
        foreach (var rule in rules)
        {
            if (string.IsNullOrEmpty(rule.Pattern)) continue;
            if (NeedsExactExpect(rule))
            {
                lines.Add(BuildExactExpectLine(rule));
                continue;
            }

            var options = new List<string>();
            if (!rule.AppendEnter) options.Add("appendEnter=false");
            if (rule.CooldownMs != DefaultCooldownMs) options.Add($"cooldown={rule.CooldownMs}");
            if (!rule.Enabled) options.Add("enabled=false");
            if (options.Count > 0) lines.Add($"{ExpectOptionPrefix} {string.Join(' ', options)}");

            string prefix = (rule.IsRegex ? "" : "!") + (rule.Once ? "" : "+");
            lines.Add($"{prefix}{EscapePattern(rule.Pattern)} => {rule.Response}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static bool TryParseHighlightRules(string? text, out List<HighlightRule> rules, out string error)
    {
        rules = new List<HighlightRule>();
        error = "";
        var lines = SplitLines(text);
        HighlightOptions? pending = null;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (HasOptionPrefix(line, HighlightOptionPrefix))
            {
                if (!TryParseHighlightOptions(line, out var parsedOptions, out string optionError))
                {
                    error = LineError(i, optionError);
                    return false;
                }
                if (parsedOptions.IsExact)
                {
                    if (pending is not null)
                    {
                        error = LineError(i, "完整的 @highlight 规则前存在未使用的选项行");
                        return false;
                    }
                    if (string.IsNullOrEmpty(parsedOptions.Pattern))
                    {
                        error = LineError(i, "完整 @highlight 规则缺少 pattern");
                        return false;
                    }
                    string exactColor = parsedOptions.Color ?? "#F0B429";
                    if (!exactColor.StartsWith('#')) exactColor = "#" + exactColor;
                    if (!IsHexColor(exactColor))
                    {
                        error = LineError(i, "颜色需为 #RRGGBB");
                        return false;
                    }
                    if (parsedOptions.Regex &&
                        !ValidateRegex(parsedOptions.Pattern, out string exactRegexError))
                    {
                        error = LineError(i, exactRegexError);
                        return false;
                    }
                    rules.Add(new HighlightRule
                    {
                        Pattern = parsedOptions.Pattern,
                        IsRegex = parsedOptions.Regex,
                        Color = exactColor,
                        Enabled = parsedOptions.Enabled
                    });
                    continue;
                }
                if (pending is not null)
                {
                    error = LineError(i, "连续的 @highlight 选项行之间缺少规则");
                    return false;
                }
                if (parsedOptions.RegexSpecified)
                {
                    error = LineError(i, "regex 只用于 @highlight pattern=... 完整规则");
                    return false;
                }
                pending = parsedOptions;
                continue;
            }
            if (HasOptionPrefix(line, ExpectOptionPrefix))
            {
                error = LineError(i, "@expect 选项不能用于高亮规则");
                return false;
            }

            int split = FindSeparator(line);
            if (split < 0)
            {
                error = LineError(i, $"缺少“=>”：{line}");
                return false;
            }

            var patternPart = line[..split].Trim();
            var color = line[(split + 2)..].Trim();
            bool regex = true;
            if (!HasEscapedLeadingChar(patternPart) && patternPart.StartsWith('!'))
            {
                regex = false;
                patternPart = patternPart[1..].TrimStart();
            }

            patternPart = UnescapePattern(patternPart);
            if (patternPart.Length == 0)
            {
                error = LineError(i, "关键字为空");
                return false;
            }
            if (regex && !ValidateRegex(patternPart, out string regexError))
            {
                error = LineError(i, regexError);
                return false;
            }

            if (!color.StartsWith('#')) color = "#" + color;
            if (!IsHexColor(color))
            {
                error = LineError(i, "颜色需为 #RRGGBB");
                return false;
            }

            rules.Add(new HighlightRule
            {
                Pattern = patternPart,
                IsRegex = regex,
                Color = color,
                Enabled = (pending ?? new HighlightOptions()).Enabled
            });
            pending = null;
        }

        if (pending is not null)
        {
            error = "文件末尾的 @highlight 选项行后缺少规则";
            return false;
        }
        return true;
    }

    public static string BuildHighlightRules(IEnumerable<HighlightRule> rules)
    {
        var lines = new List<string>
        {
            "# 每行：模式 => #RRGGBB；前缀 ! 字面量",
            "# 行首特殊字符用 \\ 转义；可选 @highlight 行设置 enabled",
            "# 示例：ERROR|fail => #F85149"
        };
        foreach (var rule in rules)
        {
            if (string.IsNullOrEmpty(rule.Pattern)) continue;
            if (NeedsExactHighlight(rule))
            {
                lines.Add(BuildExactHighlightLine(rule));
                continue;
            }
            if (!rule.Enabled) lines.Add($"{HighlightOptionPrefix} enabled=false");
            string prefix = rule.IsRegex ? "" : "!";
            lines.Add($"{prefix}{EscapePattern(rule.Pattern)} => {rule.Color}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static bool TryParseExpectOptions(
        string line, out ExpectOptions options, out string error)
    {
        options = new ExpectOptions();
        error = "";
        if (!TrySplitOptions(line[ExpectOptionPrefix.Length..], out var tokens, out error)) return false;
        if (tokens.Count == 0)
        {
            error = "@expect 缺少选项";
            return false;
        }

        foreach (string token in tokens)
        {
            if (!TrySplitOption(token, out string key, out string value, out error)) return false;
            switch (key)
            {
                case "pattern":
                    if (!TryParseQuoted(value, out string pattern, out error)) return false;
                    options.Pattern = pattern;
                    break;
                case "response":
                    if (!TryParseQuoted(value, out string response, out error)) return false;
                    options.Response = response;
                    break;
                case "regex":
                    if (!TryParseBool(value, out bool regex))
                    {
                        error = $"regex 需为 true/false：{value}";
                        return false;
                    }
                    options.Regex = regex;
                    options.RegexSpecified = true;
                    break;
                case "once":
                    if (!TryParseBool(value, out bool once))
                    {
                        error = $"once 需为 true/false：{value}";
                        return false;
                    }
                    options.Once = once;
                    options.OnceSpecified = true;
                    break;
                case "appendenter":
                    if (!TryParseBool(value, out bool appendEnter))
                    {
                        error = $"appendEnter 需为 true/false：{value}";
                        return false;
                    }
                    options.AppendEnter = appendEnter;
                    break;
                case "cooldown":
                    if (!int.TryParse(value, out int cooldown) || cooldown < 0 || cooldown > 60000)
                    {
                        error = $"cooldown 需为 0 - 60000 毫秒：{value}";
                        return false;
                    }
                    options.CooldownMs = cooldown;
                    break;
                case "enabled":
                    if (!TryParseBool(value, out bool enabled))
                    {
                        error = $"enabled 需为 true/false：{value}";
                        return false;
                    }
                    options.Enabled = enabled;
                    break;
                default:
                    error = $"未知的 @expect 选项：{key}";
                    return false;
            }
        }
        return true;
    }

    private static bool TryParseHighlightOptions(
        string line, out HighlightOptions options, out string error)
    {
        options = new HighlightOptions();
        error = "";
        if (!TrySplitOptions(line[HighlightOptionPrefix.Length..], out var tokens, out error)) return false;
        if (tokens.Count == 0)
        {
            error = "@highlight 缺少选项";
            return false;
        }

        foreach (string token in tokens)
        {
            if (!TrySplitOption(token, out string key, out string value, out error)) return false;
            switch (key)
            {
                case "pattern":
                    if (!TryParseQuoted(value, out string pattern, out error)) return false;
                    options.Pattern = pattern;
                    break;
                case "color":
                    options.Color = value;
                    break;
                case "regex":
                    if (!TryParseBool(value, out bool regex))
                    {
                        error = $"regex 需为 true/false：{value}";
                        return false;
                    }
                    options.Regex = regex;
                    options.RegexSpecified = true;
                    break;
                case "enabled":
                    if (!TryParseBool(value, out bool enabled))
                    {
                        error = $"enabled 需为 true/false：{value}";
                        return false;
                    }
                    options.Enabled = enabled;
                    break;
                default:
                    error = $"未知的 @highlight 选项：{key}";
                    return false;
            }
        }
        return true;
    }

    private static bool TrySplitOption(
        string token, out string key, out string value, out string error)
    {
        key = "";
        value = "";
        error = "";
        int equals = token.IndexOf('=');
        if (equals <= 0 || equals == token.Length - 1)
        {
            error = $"无效选项：{token}";
            return false;
        }
        key = token[..equals].Trim().ToLowerInvariant();
        value = token[(equals + 1)..].Trim();
        return true;
    }

    private static bool TrySplitOptions(
        string value, out List<string> tokens, out string error)
    {
        tokens = new List<string>();
        error = "";
        var current = new StringBuilder();
        bool quoted = false;
        bool escaped = false;
        foreach (char c in value)
        {
            if (escaped)
            {
                current.Append(c);
                escaped = false;
                continue;
            }
            if (c == '\\')
            {
                current.Append(c);
                escaped = true;
                continue;
            }
            if (c == '"')
            {
                quoted = !quoted;
                current.Append(c);
                continue;
            }
            if (!quoted && (c == ' ' || c == '\t' || c == ','))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        if (quoted)
        {
            error = "选项值缺少结束双引号";
            return false;
        }
        return true;
    }

    private static bool TryParseQuoted(
        string value, out string result, out string error)
    {
        result = "";
        error = "";
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            error = "pattern/response 需用双引号包围";
            return false;
        }

        var text = new StringBuilder(value.Length);
        for (int i = 1; i < value.Length - 1; i++)
        {
            char c = value[i];
            if (c != '\\' || i + 1 >= value.Length - 1)
            {
                text.Append(c);
                continue;
            }

            char escaped = value[++i];
            switch (escaped)
            {
                case '"': text.Append('"'); break;
                case '\\': text.Append('\\'); break;
                case 'n': text.Append('\n'); break;
                case 'r': text.Append('\r'); break;
                case 't': text.Append('\t'); break;
                default:
                    text.Append('\\');
                    text.Append(escaped);
                    break;
            }
        }
        result = text.ToString();
        return true;
    }

    private static string QuoteOption(string value)
    {
        var quoted = new StringBuilder(value.Length + 2);
        quoted.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': quoted.Append("\\\\"); break;
                case '"': quoted.Append("\\\""); break;
                case '\n': quoted.Append("\\n"); break;
                case '\r': quoted.Append("\\r"); break;
                case '\t': quoted.Append("\\t"); break;
                default: quoted.Append(c); break;
            }
        }
        quoted.Append('"');
        return quoted.ToString();
    }

    private static bool TryParseBool(string value, out bool result)
    {
        if (bool.TryParse(value, out result)) return true;
        if (value == "1") { result = true; return true; }
        if (value == "0") { result = false; return true; }
        result = false;
        return false;
    }

    private static bool HasOptionPrefix(string line, string prefix) =>
        line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        (line.Length == prefix.Length || char.IsWhiteSpace(line[prefix.Length]));

    private static bool HasEscapedLeadingChar(string pattern) =>
        pattern.Length >= 2 && pattern[0] == '\\' &&
        pattern[1] is '#' or '!' or '+' or '@';

    private static int FindSeparator(string line)
    {
        for (int i = 0; i + 1 < line.Length; i++)
        {
            if (line[i] == '=' && line[i + 1] == '>' &&
                (i == 0 || line[i - 1] != '\\'))
            {
                return i;
            }
        }
        return -1;
    }

    private static string EscapePattern(string pattern)
    {
        var escaped = new StringBuilder(pattern.Length + 4);
        for (int i = 0; i < pattern.Length; i++)
        {
            char value = pattern[i];
            if (i == 0 && value is '#' or '!' or '+' or '@') escaped.Append('\\');
            if (value == '=' && i + 1 < pattern.Length && pattern[i + 1] == '>') escaped.Append('\\');
            escaped.Append(value);
        }
        return escaped.ToString();
    }

    private static bool NeedsExactExpect(ExpectRule rule) =>
        NeedsExactText(rule.Pattern) || NeedsExactText(rule.Response) ||
        HasUnrepresentableBackslash(rule.Pattern);

    private static bool NeedsExactHighlight(HighlightRule rule) =>
        NeedsExactText(rule.Pattern) || HasUnrepresentableBackslash(rule.Pattern);

    private static bool NeedsExactText(string value) =>
        (value.Length > 0 &&
         (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))) ||
        value.Contains('\r') || value.Contains('\n');

    private static bool HasUnrepresentableBackslash(string value) =>
        value.Contains("\\=>", StringComparison.Ordinal) ||
        (value.Length >= 2 && value[0] == '\\' &&
         value[1] is '#' or '!' or '+' or '@' or '=');

    private static string BuildExactExpectLine(ExpectRule rule)
    {
        var options = new List<string>
        {
            $"pattern={QuoteOption(rule.Pattern)}",
            $"response={QuoteOption(rule.Response)}",
            $"regex={(rule.IsRegex ? "true" : "false")}",
            $"once={(rule.Once ? "true" : "false")}"
        };
        if (!rule.AppendEnter) options.Add("appendEnter=false");
        if (rule.CooldownMs != DefaultCooldownMs) options.Add($"cooldown={rule.CooldownMs}");
        if (!rule.Enabled) options.Add("enabled=false");
        return $"{ExpectOptionPrefix} {string.Join(' ', options)}";
    }

    private static string BuildExactHighlightLine(HighlightRule rule)
    {
        var options = new List<string>
        {
            $"pattern={QuoteOption(rule.Pattern)}",
            $"color={rule.Color}",
            $"regex={(rule.IsRegex ? "true" : "false")}"
        };
        if (!rule.Enabled) options.Add("enabled=false");
        return $"{HighlightOptionPrefix} {string.Join(' ', options)}";
    }

    private static string UnescapePattern(string pattern)
    {
        if (HasEscapedLeadingChar(pattern)) pattern = pattern[1..];
        if (!pattern.Contains("\\=>")) return pattern;

        var unescaped = new StringBuilder(pattern.Length);
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\\' && i + 2 < pattern.Length &&
                pattern[i + 1] == '=' && pattern[i + 2] == '>')
            {
                unescaped.Append("=>");
                i += 2;
            }
            else
            {
                unescaped.Append(pattern[i]);
            }
        }
        return unescaped.ToString();
    }

    private static bool ValidateRegex(string pattern, out string error)
    {
        try
        {
            _ = new Regex(
                pattern,
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(200));
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = $"正则无效：{ex.Message}";
            return false;
        }
    }

    private static bool IsHexColor(string color)
    {
        if (color.Length != 7 || color[0] != '#') return false;
        for (int i = 1; i < color.Length; i++)
        {
            if (!Uri.IsHexDigit(color[i])) return false;
        }
        return true;
    }

    private static string[] SplitLines(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string LineError(int lineIndex, string message) =>
        $"第 {lineIndex + 1} 行：{message}";
}
