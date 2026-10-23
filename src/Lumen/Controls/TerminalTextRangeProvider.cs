using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;

namespace Lumen.Controls;

/// <summary>
/// Minimal TextPattern range over the terminal snapshot. It supports reading,
/// searching and line/character movement; visual selection is handled by the
/// terminal itself, so Select/AddToSelection are intentionally no-ops.
/// </summary>
public sealed class TerminalTextRangeProvider : ITextRangeProvider
{
    private readonly TerminalAutomationPeer _peer;
    private int _start;
    private int _end;

    public TerminalTextRangeProvider(TerminalAutomationPeer peer, int start, int end)
    {
        _peer = peer;
        _start = Math.Max(0, start);
        _end = Math.Max(_start, end);
        Clamp();
    }

    private string Text => _peer.GetTextSnapshot();

    private void Clamp()
    {
        int length = Text.Length;
        _start = Math.Clamp(_start, 0, length);
        _end = Math.Clamp(_end, _start, length);
    }

    public ITextRangeProvider Clone() => new TerminalTextRangeProvider(_peer, _start, _end);

    public bool Compare(ITextRangeProvider range) =>
        range is TerminalTextRangeProvider other && _start == other._start && _end == other._end;

    public int CompareEndpoints(
        TextPatternRangeEndpoint endpoint,
        ITextRangeProvider targetRange,
        TextPatternRangeEndpoint targetEndpoint)
    {
        if (targetRange is not TerminalTextRangeProvider other) return 1;
        int left = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int right = targetEndpoint == TextPatternRangeEndpoint.Start ? other._start : other._end;
        return left.CompareTo(right);
    }

    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        string text = Text;
        if (text.Length == 0) return;

        switch (unit)
        {
            case TextUnit.Character:
                _end = Math.Min(text.Length, _start + 1);
                break;
            case TextUnit.Line:
                _start = LineStart(text, _start);
                _end = LineEnd(text, _end);
                break;
            default:
                _start = 0;
                _end = text.Length;
                break;
        }
    }

    public ITextRangeProvider? FindAttribute(int attribute, object value, bool backward) => null;

    public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(text)) return null;
        string source = Text;
        if (source.Length == 0) return null;
        StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int index = backward
            ? source.LastIndexOf(text, Math.Clamp(_end, 0, source.Length - 1), comparison)
            : source.IndexOf(text, Math.Clamp(_start, 0, source.Length), comparison);
        return index < 0 ? null : new TerminalTextRangeProvider(_peer, index, index + text.Length);
    }

    public object? GetAttributeValue(int attribute) => null;

    public double[] GetBoundingRectangles() => Array.Empty<double>();

    public IRawElementProviderSimple GetEnclosingElement() => _peer.GetElementProvider();

    public string GetText(int maxLength)
    {
        string text = Text;
        int start = Math.Clamp(_start, 0, text.Length);
        int length = Math.Clamp(_end - start, 0, text.Length - start);
        if (maxLength >= 0) length = Math.Min(length, maxLength);
        return text.Substring(start, length);
    }

    public int Move(TextUnit unit, int count)
    {
        if (count == 0) return 0;
        string text = Text;
        if (unit == TextUnit.Document)
        {
            _start = _end = count > 0 ? text.Length : 0;
            return 1;
        }

        int delta = unit switch
        {
            TextUnit.Character => count,
            TextUnit.Line => count * Math.Max(1, AverageLineLength(text)),
            _ => count
        };
        int oldStart = _start;
        _start += delta;
        _end += delta;
        Clamp();
        return _start == oldStart ? 0 : Math.Sign(_start - oldStart);
    }

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        if (count == 0) return 0;
        int before = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int target = before;

        if (unit == TextUnit.Document)
        {
            target = count > 0 ? Text.Length : 0;
        }
        else if (unit == TextUnit.Line)
        {
            target = MoveByLines(Text, before, count);
        }
        else
        {
            target += count;
        }

        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            _start = Math.Clamp(target, 0, _end);
        }
        else
        {
            _end = Math.Clamp(target, _start, Text.Length);
        }

        int after = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        return after == before ? 0 : Math.Sign(after - before);
    }

    public void MoveEndpointByRange(
        TextPatternRangeEndpoint endpoint,
        ITextRangeProvider targetRange,
        TextPatternRangeEndpoint targetEndpoint)
    {
        if (targetRange is not TerminalTextRangeProvider other) return;
        int value = targetEndpoint == TextPatternRangeEndpoint.Start ? other._start : other._end;
        if (endpoint == TextPatternRangeEndpoint.Start) _start = Math.Min(value, _end);
        else _end = Math.Max(value, _start);
    }

    public void Select()
    {
        _peer.SelectRange(_start, _end);
    }

    public void AddToSelection()
    {
        _peer.SelectRange(_start, _end);
    }

    public void RemoveFromSelection()
    {
        _peer.ClearSelection();
    }

    public void ScrollIntoView(bool alignToTop)
    {
        _peer.SelectRange(_start, _end);
    }

    public IRawElementProviderSimple[] GetChildren() => Array.Empty<IRawElementProviderSimple>();

    private static int LineStart(string text, int offset)
    {
        int index = Math.Clamp(offset, 0, text.Length);
        while (index > 0 && text[index - 1] != '\n') index--;
        return index;
    }

    private static int LineEnd(string text, int offset)
    {
        int index = Math.Clamp(offset, 0, text.Length);
        while (index < text.Length && text[index] != '\n') index++;
        if (index < text.Length) index++;
        return index;
    }

    private static int MoveByLines(string text, int offset, int count)
    {
        int index = Math.Clamp(offset, 0, text.Length);
        if (count > 0)
        {
            for (int i = 0; i < count && index < text.Length; i++) index = LineEnd(text, index);
        }
        else
        {
            for (int i = 0; i < -count && index > 0; i++) index = LineStart(text, index - 1);
        }
        return index;
    }

    private static int AverageLineLength(string text)
    {
        int lines = 1;
        foreach (char c in text) if (c == '\n') lines++;
        return Math.Max(1, text.Length / lines);
    }
}
