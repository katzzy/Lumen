using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace Lumen.Services;

public sealed class LogService : IDisposable
{
    private static readonly ConcurrentDictionary<int, Task> Pending = new();
    private static int _pendingId;
    private readonly object _gate = new();
    private Channel<LogEntry>? _queue;
    private Task? _worker;
    private StreamWriter? _writer;
    private string? _filePath;
    private bool _active;
    private bool _raw;

    private bool _atLineStart = true;
    private bool _skipLineFeed;
    private StripState _stripState = StripState.Text;
    private int _pendingChars;

    private enum StripState
    {
        Text,
        Escape,
        Csi,
        Osc,
        OscEscape,
        Dcs,
        DcsEscape
    }

    private enum EntryKind
    {
        Receive,
        Transmit,
        Flush,
        Stop
    }

    private readonly record struct LogEntry(EntryKind Kind, string Text = "", bool Raw = false);

    public string? FilePath
    {
        get { lock (_gate) return _filePath; }
    }

    public bool IsActive
    {
        get { lock (_gate) return _active; }
    }

    public bool TimestampEnabled { get; private set; }

    /// <summary>When true, escape sequences and control characters are logged verbatim.</summary>
    public bool RawEnabled
    {
        get { lock (_gate) return _raw; }
    }

    public static string BuildFilePath(string root, string sessionName)
    {
        Directory.CreateDirectory(root);
        var safe = Sanitize(sessionName);
        var name = $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.log";
        var path = Path.Combine(root, name);
        int n = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(root, $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}_{n++}.log");
        }
        return path;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(invalid.Contains(ch) || ch == ' ' ? '_' : ch);
        }
        var value = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(value) ? "session" : value;
    }

    public void Start(string path, bool timestamp, Encoding encoding, bool raw = false)
    {
        Stop();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var writer = new StreamWriter(stream, encoding) { AutoFlush = false };
        var queue = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(8192)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        lock (_gate)
        {
            _writer = writer;
            _filePath = path;
            _active = true;
            _raw = raw;
            _queue = queue;
        }

        TimestampEnabled = timestamp;
        _atLineStart = true;
        _skipLineFeed = false;
        _stripState = StripState.Text;
        _pendingChars = 0;
        if (timestamp)
        {
            writer.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 会话开始{Environment.NewLine}");
        }

        _worker = Task.Run(() => ProcessAsync(queue.Reader));
    }

    public void Write(string text)
    {
        if (text.Length == 0) return;
        Channel<LogEntry>? queue;
        bool raw;
        lock (_gate)
        {
            queue = _queue;
            raw = _raw;
        }
        queue?.Writer.TryWrite(new LogEntry(EntryKind.Receive, text, raw));
    }

    public void WriteTx(string command)
    {
        command = command.TrimEnd('\r', '\n');
        if (command.Length == 0) return;
        Channel<LogEntry>? queue;
        lock (_gate) queue = _queue;
        queue?.Writer.TryWrite(new LogEntry(EntryKind.Transmit, command));
    }

    public void Flush()
    {
        Channel<LogEntry>? queue;
        lock (_gate) queue = _queue;
        queue?.Writer.TryWrite(new LogEntry(EntryKind.Flush));
    }

    public void Stop()
    {
        try
        {
            StopAsync().Wait(1500);
        }
        catch
        {
            // best effort
        }
    }

    public Task StopAsync()
    {
        Channel<LogEntry>? queue;
        Task? worker;
        lock (_gate)
        {
            queue = _queue;
            worker = _worker;
            _queue = null;
            _worker = null;
            _active = false;
            _raw = false;
        }

        if (queue is null || worker is null) return Task.CompletedTask;

        queue.Writer.TryWrite(new LogEntry(EntryKind.Stop));
        queue.Writer.TryComplete();
        int id = Interlocked.Increment(ref _pendingId);
        Pending[id] = worker;
        return worker.ContinueWith(_ =>
        {
            if (Pending.TryRemove(id, out var removedTask)) GC.KeepAlive(removedTask);
            lock (_gate) _filePath = null;
        }, TaskScheduler.Default);
    }

    public static void WaitForPending(int milliseconds)
    {
        try
        {
            Task.WaitAll(Pending.Values.ToArray(), milliseconds);
        }
        catch
        {
            // best effort
        }
    }

    private async Task ProcessAsync(ChannelReader<LogEntry> reader)
    {
        try
        {
            await foreach (var entry in reader.ReadAllAsync().ConfigureAwait(false))
            {
                switch (entry.Kind)
                {
                    case EntryKind.Receive:
                        WriteRaw(entry.Text, entry.Raw);
                        break;
                    case EntryKind.Transmit:
                        WriteTxCore(entry.Text);
                        break;
                    case EntryKind.Flush:
                        FlushCore();
                        break;
                    case EntryKind.Stop:
                        FlushCore();
                        WriteEnd();
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            App.LogError("log writer", ex);
        }
        finally
        {
            try
            {
                _writer?.Dispose();
            }
            catch
            {
                // ignore
            }
            _writer = null;
            lock (_gate) _active = false;
        }
    }

    private void WriteRaw(string text, bool raw)
    {
        if (_writer is null) return;

        if (raw)
        {
            WriteVerbatim(text);
            _pendingChars += text.Length;
            if (_pendingChars >= 8192) FlushCore();
            return;
        }

        int index = 0;
        while (index < text.Length)
        {
            if (_stripState != StripState.Text)
            {
                ConsumeEscape(text[index]);
                index++;
                continue;
            }

            int escape = text.IndexOf('\x1b', index);
            int end = escape < 0 ? text.Length : escape;
            WriteVisible(text.AsSpan(index, end - index));
            if (escape < 0) break;

            _stripState = StripState.Escape;
            index = end + 1;
        }

        _pendingChars += text.Length;
        if (_pendingChars >= 8192) FlushCore();
    }

    private void ConsumeEscape(char ch)
    {
        switch (_stripState)
        {
            case StripState.Escape:
                if (ch == '[') _stripState = StripState.Csi;
                else if (ch == ']') _stripState = StripState.Osc;
                else if (ch == 'P') _stripState = StripState.Dcs;
                else _stripState = StripState.Text;
                break;

            case StripState.Csi:
                if (ch >= 0x40 && ch <= 0x7e) _stripState = StripState.Text;
                else if (ch == '\x1b') _stripState = StripState.Escape;
                break;

            case StripState.Osc:
                if (ch == '\a') _stripState = StripState.Text;
                else if (ch == '\x1b') _stripState = StripState.OscEscape;
                break;

            case StripState.OscEscape:
                _stripState = ch == '\\' ? StripState.Text : StripState.Osc;
                break;

            case StripState.Dcs:
                if (ch == '\x1b') _stripState = StripState.DcsEscape;
                else if (ch == '\a') _stripState = StripState.Text;
                break;

            case StripState.DcsEscape:
                _stripState = ch == '\\' ? StripState.Text : StripState.Dcs;
                break;
        }
    }

    private void WriteVisible(ReadOnlySpan<char> span)
    {
        if (_writer is null || span.IsEmpty) return;

        bool simple = true;
        foreach (char ch in span)
        {
            if (ch == '\r' || ch == '\n' || (char.IsControl(ch) && ch != '\t'))
            {
                simple = false;
                break;
            }
        }

        if (simple)
        {
            if (_skipLineFeed) _skipLineFeed = false;
            if (_atLineStart)
            {
                if (TimestampEnabled) _writer.Write($"[{DateTime.Now:HH:mm:ss}] ");
                _atLineStart = false;
            }
            _writer.Write(span);
            return;
        }

        foreach (char ch in span)
        {
            if (ch == '\r')
            {
                _skipLineFeed = true;
                continue;
            }

            if (ch == '\n')
            {
                _writer.Write('\n');
                _atLineStart = true;
                _skipLineFeed = false;
                continue;
            }

            if (_skipLineFeed) _skipLineFeed = false;
            if (char.IsControl(ch) && ch != '\t') continue;

            if (_atLineStart)
            {
                if (TimestampEnabled) _writer.Write($"[{DateTime.Now:HH:mm:ss}] ");
                _atLineStart = false;
            }
            _writer.Write(ch);
        }
    }

    private void WriteVerbatim(string text)
    {
        if (_writer is null) return;

        foreach (char ch in text)
        {
            if (_atLineStart && ch != '\r' && ch != '\n')
            {
                if (TimestampEnabled) _writer.Write($"[{DateTime.Now:HH:mm:ss}] ");
                _atLineStart = false;
            }

            _writer.Write(ch);

            if (ch == '\n')
            {
                _atLineStart = true;
                _skipLineFeed = false;
            }
        }
    }

    private void WriteTxCore(string command)
    {
        if (_writer is null) return;

        if (!_atLineStart)
        {
            _writer.Write(Environment.NewLine);
            _atLineStart = true;
            _skipLineFeed = false;
        }
        if (TimestampEnabled) _writer.Write($"[{DateTime.Now:HH:mm:ss}] ");
        _writer.Write("[TX] ");
        _writer.Write(command);
        _writer.Write(Environment.NewLine);
        _atLineStart = true;
        _skipLineFeed = false;

        _pendingChars += command.Length + 8;
        if (_pendingChars >= 8192) FlushCore();
    }

    private void FlushCore()
    {
        try
        {
            _writer?.Flush();
            _pendingChars = 0;
        }
        catch (Exception ex)
        {
            App.LogError("log flush", ex);
        }
    }

    private void WriteEnd()
    {
        if (_writer is null) return;
        try
        {
            if (TimestampEnabled)
            {
                _writer.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 会话结束{Environment.NewLine}");
            }
            _writer.Flush();
        }
        catch (Exception ex)
        {
            App.LogError("log stop", ex);
        }
    }

    public void Dispose() => Stop();
}
