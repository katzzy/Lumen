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

public static class AutomationTextParser
{
    public static bool TryParseExpectRules(string? text, out List<ExpectRule> rules, out string error)
    {
        rules = new List<ExpectRule>();
        error = "";
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            int split = line.IndexOf("=>", StringComparison.Ordinal);
            if (split < 0)
            {
                error = $"第 {i + 1} 行缺少“=>”：{line}";
                return false;
            }

            var patternPart = line[..split].Trim();
            var response = line[(split + 2)..].Trim();
            if (patternPart.Length == 0)
            {
                error = $"第 {i + 1} 行匹配模式为空";
                return false;
            }

            bool regex = true;
            bool once = true;
            while (patternPart.Length > 0 && (patternPart[0] == '!' || patternPart[0] == '+'))
            {
                if (patternPart[0] == '!') regex = false;
                else once = false;
                patternPart = patternPart[1..].TrimStart();
            }

            if (patternPart.Length == 0)
            {
                error = $"第 {i + 1} 行匹配模式为空";
                return false;
            }

            if (regex)
            {
                try
                {
                    _ = new System.Text.RegularExpressions.Regex(
                        patternPart,
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(200));
                }
                catch (Exception ex)
                {
                    error = $"第 {i + 1} 行正则无效：{ex.Message}";
                    return false;
                }
            }

            rules.Add(new ExpectRule
            {
                Pattern = patternPart,
                IsRegex = regex,
                Once = once,
                Response = response,
                AppendEnter = true,
                Enabled = true
            });
        }

        return true;
    }

    public static string BuildExpectRules(IEnumerable<ExpectRule> rules)
    {
        var lines = new List<string>();
        lines.Add("# 每行一条：可匹配正则；前缀 ! 表示字面量，+ 表示可重复触发");
        lines.Add("# 示例：Password: => admin");
        lines.Add("# 示例：!+Switch> => enable");
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern)) continue;
            string prefix = (rule.IsRegex ? "" : "!") + (rule.Once ? "" : "+");
            lines.Add($"{prefix}{rule.Pattern} => {rule.Response}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static bool TryParseHighlightRules(string? text, out List<HighlightRule> rules, out string error)
    {
        rules = new List<HighlightRule>();
        error = "";
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            int split = line.IndexOf("=>", StringComparison.Ordinal);
            if (split < 0)
            {
                error = $"第 {i + 1} 行缺少“=>”：{line}";
                return false;
            }

            var patternPart = line[..split].Trim();
            var color = line[(split + 2)..].Trim();
            bool regex = true;
            if (patternPart.StartsWith('!'))
            {
                regex = false;
                patternPart = patternPart[1..].Trim();
            }

            if (patternPart.Length == 0)
            {
                error = $"第 {i + 1} 行关键字为空";
                return false;
            }

            if (regex)
            {
                try
                {
                    _ = new System.Text.RegularExpressions.Regex(
                        patternPart,
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(200));
                }
                catch (Exception ex)
                {
                    error = $"第 {i + 1} 行正则无效：{ex.Message}";
                    return false;
                }
            }

            if (!color.StartsWith('#')) color = "#" + color;
            if (!IsHexColor(color))
            {
                error = $"第 {i + 1} 行颜色需为 #RRGGBB";
                return false;
            }

            rules.Add(new HighlightRule
            {
                Pattern = patternPart,
                IsRegex = regex,
                Color = color,
                Enabled = true
            });
        }

        return true;
    }

    public static string BuildHighlightRules(IEnumerable<HighlightRule> rules)
    {
        var lines = new List<string>();
        lines.Add("# 每行一条：模式 => #RRGGBB；前缀 ! 表示字面量");
        lines.Add("# 示例：ERROR|fail => #F85149");
        lines.Add("# 示例：!Switch# => #22D3EE");
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern)) continue;
            string prefix = rule.IsRegex ? "" : "!";
            lines.Add($"{prefix}{rule.Pattern} => {rule.Color}");
        }
        return string.Join(Environment.NewLine, lines);
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
}
