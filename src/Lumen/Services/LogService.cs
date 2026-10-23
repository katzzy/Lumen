using System.IO;
using System.Text;

namespace Lumen.Services;

public sealed class LogService : IDisposable
{
    private StreamWriter? _writer;
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

    public string? FilePath { get; private set; }
    public bool IsActive => _writer is not null;
    public bool TimestampEnabled { get; private set; }

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

    public void Start(string path, bool timestamp, Encoding encoding)
    {
        Stop();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, encoding) { AutoFlush = false };
        FilePath = path;
        TimestampEnabled = timestamp;
        _atLineStart = true;
        _stripState = StripState.Text;
        _pendingChars = 0;
        if (timestamp)
        {
            _writer.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 会话开始{Environment.NewLine}");
        }
    }

    public void Write(string text)
    {
        if (_writer is null) return;
        foreach (char ch in text)
        {
            switch (_stripState)
            {
                case StripState.Text:
                    if (ch == '\x1b')
                    {
                        _stripState = StripState.Escape;
                        continue;
                    }
                    WriteVisible(ch);
                    break;

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

        _pendingChars += text.Length;
        if (_pendingChars >= 8192)
        {
            Flush();
        }
    }

    private void WriteVisible(char ch)
    {
        if (_writer is null) return;

        if (ch == '\r')
        {
            _skipLineFeed = true;
            return;
        }

        if (ch == '\n')
        {
            _writer.Write('\n');
            _atLineStart = true;
            _skipLineFeed = false;
            return;
        }

        if (_skipLineFeed) _skipLineFeed = false;

        if (char.IsControl(ch) && ch != '\t') return;

        if (_atLineStart)
        {
            if (TimestampEnabled) _writer.Write($"[{DateTime.Now:HH:mm:ss}] ");
            _atLineStart = false;
        }
        _writer.Write(ch);
    }

    public void WriteTx(string command)
    {
        if (_writer is null) return;
        command = command.TrimEnd('\r', '\n');
        if (command.Length == 0) return;

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

        _pendingChars += command.Length + 8;
        if (_pendingChars >= 8192) Flush();
    }

    public void Flush()
    {
        try
        {
            _writer?.Flush();
            _pendingChars = 0;
        }
        catch
        {
            // ignore
        }
    }

    public void Stop()
    {
        if (_writer is null) return;
        try
        {
            if (TimestampEnabled)
            {
                _writer.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 会话结束{Environment.NewLine}");
            }
            _writer.Flush();
            _writer.Dispose();
        }
        catch
        {
            // ignore
        }
        _writer = null;
        FilePath = null;
        _atLineStart = true;
    }

    public void Dispose() => Stop();
}
