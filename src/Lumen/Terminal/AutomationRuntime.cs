using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Lumen.Models;

namespace Lumen.Terminal;

public sealed record HighlightPattern(Regex Regex, uint Color, string Pattern);

/// <summary>
/// Compiles expect rules and keyword highlight rules, and evaluates incoming
/// terminal text on a dedicated worker so the serial read loop never runs user
/// regular expressions.
/// </summary>
public sealed class AutomationRuntime : IDisposable
{
    private sealed class ExpectEntry
    {
        public required ExpectRule Rule;
        public Regex? Regex;
        public long LastFiredAt;
        public bool Fired;
    }

    private readonly record struct QueueItem(int Generation, string Text);

    private readonly object _gate = new();
    private readonly StringBuilder _buffer = new();
    private readonly Channel<QueueItem> _queue = Channel.CreateBounded<QueueItem>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
    private volatile ExpectEntry[] _expectRules = Array.Empty<ExpectEntry>();
    private volatile HighlightPattern[] _highlights = Array.Empty<HighlightPattern>();
    private Task? _worker;
    private int _workerStarted;
    private int _version;
    private int _generation;
    private volatile bool _disposed;

    public Action<string>? SendText { get; set; }
    /// <summary>Optional dispatcher used to keep response writes off the worker thread.</summary>
    public Action<Action>? Dispatch { get; set; }
    /// <summary>Optional predicate that suspends processing while the session is locked.</summary>
    public Func<bool>? CanProcess { get; set; }
    public string? LastError { get; private set; }

    public IReadOnlyList<HighlightPattern> Highlights => _highlights;
    public bool HasExpectRules => _expectRules.Length > 0;

    /// <summary>Bumped whenever the compiled rule set changes, so views can drop row caches.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>
    /// Queues received text for the worker. Bounded; if the queue is saturated,
    /// process this chunk inline rather than silently dropping expect input.
    /// </summary>
    public void Post(string text)
    {
        if (_disposed || string.IsNullOrEmpty(text) || !HasExpectRules) return;
        if (CanProcess is not null && !CanProcess()) return;
        EnsureWorker();
        var item = new QueueItem(Volatile.Read(ref _generation), text);
        if (!_queue.Writer.TryWrite(item) &&
            item.Generation == Volatile.Read(ref _generation))
        {
            Feed(text);
        }
    }

    private void EnsureWorker()
    {
        if (Interlocked.Exchange(ref _workerStarted, 1) != 0) return;
        _worker = Task.Run(WorkerLoopAsync);
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (_disposed ||
                    item.Generation != Volatile.Read(ref _generation) ||
                    !HasExpectRules ||
                    (CanProcess is not null && !CanProcess()))
                {
                    continue;
                }
                Feed(item.Text);
            }
        }
        catch (Exception ex)
        {
            global::Lumen.App.LogError("automation worker", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Writer.TryComplete();
        try
        {
            _worker?.Wait(500);
        }
        catch
        {
            // best effort
        }
        _worker = null;
    }

    public void Update(IEnumerable<ExpectRule>? expectRules, IEnumerable<HighlightRule>? highlights)
    {
        var compiledExpect = new List<ExpectEntry>();
        var compiledHighlights = new List<HighlightPattern>();
        LastError = null;

        foreach (var rule in expectRules ?? Enumerable.Empty<ExpectRule>())
        {
            if (!rule.Enabled || string.IsNullOrEmpty(rule.Pattern)) continue;
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
            if (!rule.Enabled || string.IsNullOrEmpty(rule.Pattern)) continue;
            try
            {
                var pattern = rule.IsRegex ? rule.Pattern : Regex.Escape(rule.Pattern);
                var regex = new Regex(
                    pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    // Highlights run on the render thread; keep the per-row
                    // worst case small so a bad rule cannot freeze scrolling.
                    TimeSpan.FromMilliseconds(25));
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
            Interlocked.Increment(ref _version);
            Interlocked.Increment(ref _generation);
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
            Interlocked.Increment(ref _generation);
        }
    }

    /// <summary>Drops pending input without re-arming once-only rules.</summary>
    public void ClearBuffer()
    {
        lock (_gate)
        {
            _buffer.Clear();
            Interlocked.Increment(ref _generation);
        }
    }

    public void Feed(string text)
    {
        if (_disposed || string.IsNullOrEmpty(text)) return;
        if (CanProcess is not null && !CanProcess()) return;
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
                int matchStart = 0;
                int matchLength = 0;
                try
                {
                    if (entry.Regex is not null)
                    {
                        var match = entry.Regex.Match(snapshot);
                        matched = match.Success;
                        if (matched)
                        {
                            matchStart = match.Index;
                            matchLength = match.Length;
                        }
                    }
                    else
                    {
                        int index = snapshot.IndexOf(entry.Rule.Pattern, StringComparison.OrdinalIgnoreCase);
                        matched = index >= 0;
                        if (matched)
                        {
                            matchStart = index;
                            matchLength = entry.Rule.Pattern.Length;
                        }
                    }
                }
                catch
                {
                    continue;
                }

                if (!matched) continue;

                // Drop the matched slice. Otherwise a repeatable rule keeps
                // matching the same prompt on every later chunk.
                if (matchLength > 0)
                {
                    _buffer.Remove(matchStart, Math.Min(matchLength, _buffer.Length - matchStart));
                }

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
            void Deliver()
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

            try
            {
                var dispatch = Dispatch;
                if (dispatch is not null) dispatch(Deliver);
                else Deliver();
            }
            catch
            {
                // A dispatcher shutdown must not break terminal output parsing.
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
