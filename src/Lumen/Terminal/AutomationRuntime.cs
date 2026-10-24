using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Lumen.Models;

namespace Lumen.Terminal;

public sealed record HighlightPattern(Regex Regex, uint Color, string Pattern);

/// <summary>
/// Compiles expect rules and keyword highlight rules, and evaluates incoming
/// terminal text without blocking the UI thread.
/// </summary>
public sealed class AutomationRuntime
{
    private sealed class ExpectEntry
    {
        public required ExpectRule Rule;
        public Regex? Regex;
        public long LastFiredAt;
        public bool Fired;
    }

    private readonly object _gate = new();
    private readonly StringBuilder _buffer = new();
    private ExpectEntry[] _expectRules = Array.Empty<ExpectEntry>();
    private volatile HighlightPattern[] _highlights = Array.Empty<HighlightPattern>();

    public Action<string>? SendText { get; set; }
    public string? LastError { get; private set; }

    public IReadOnlyList<HighlightPattern> Highlights => _highlights;

    public void Update(IEnumerable<ExpectRule>? expectRules, IEnumerable<HighlightRule>? highlights)
    {
        var compiledExpect = new List<ExpectEntry>();
        var compiledHighlights = new List<HighlightPattern>();
        LastError = null;

        foreach (var rule in expectRules ?? Enumerable.Empty<ExpectRule>())
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Pattern)) continue;
            Regex? regex = null;
            if (rule.IsRegex)
            {
                try
                {
                    regex = new Regex(
                        rule.Pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(200));
                }
                catch (Exception ex)
                {
                    LastError = $"Expect 正则无效：{rule.Pattern}（{ex.Message}）";
                    continue;
                }
            }
            compiledExpect.Add(new ExpectEntry { Rule = rule.Clone(), Regex = regex });
        }

        foreach (var rule in highlights ?? Enumerable.Empty<HighlightRule>())
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Pattern)) continue;
            try
            {
                var pattern = rule.IsRegex ? rule.Pattern : Regex.Escape(rule.Pattern);
                var regex = new Regex(
                    pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(200));
                compiledHighlights.Add(new HighlightPattern(regex, ParseColor(rule.Color), rule.Pattern));
            }
            catch (Exception ex)
            {
                LastError = $"高亮规则无效：{rule.Pattern}（{ex.Message}）";
            }
        }

        lock (_gate)
        {
            _expectRules = compiledExpect.ToArray();
            _highlights = compiledHighlights.ToArray();
            _buffer.Clear();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _buffer.Clear();
            foreach (var entry in _expectRules)
            {
                entry.Fired = false;
                entry.LastFiredAt = 0;
            }
        }
    }

    public void Feed(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var responses = new List<string>();
        lock (_gate)
        {
            _buffer.Append(text);
            if (_buffer.Length > 65536) _buffer.Remove(0, 32768);

            var snapshot = _buffer.ToString();
            long now = Environment.TickCount64;
            foreach (var entry in _expectRules)
            {
                if (!entry.Rule.Enabled || (entry.Rule.Once && entry.Fired)) continue;
                if (now - entry.LastFiredAt < Math.Clamp(entry.Rule.CooldownMs, 0, 60000)) continue;

                bool matched;
                try
                {
                    matched = entry.Regex is not null
                        ? entry.Regex.IsMatch(snapshot)
                        : snapshot.Contains(entry.Rule.Pattern, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    continue;
                }

                if (!matched) continue;

                entry.LastFiredAt = now;
                if (entry.Rule.Once) entry.Fired = true;
                if (!string.IsNullOrEmpty(entry.Rule.Response))
                {
                    responses.Add(entry.Rule.Response + (entry.Rule.AppendEnter ? "\r" : ""));
                }
                break; // one automatic response per incoming chunk
            }
        }

        foreach (var response in responses)
        {
            try
            {
                SendText?.Invoke(response);
            }
            catch
            {
                // a broken transport must not take down the read loop
            }
        }
    }

    private static uint ParseColor(string? value)
    {
        var text = (value ?? "").Trim().TrimStart('#');
        if (text.Length == 6 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            return 0xFF000000u | rgb;
        }
        return 0xFFF0B429u;
    }
}
