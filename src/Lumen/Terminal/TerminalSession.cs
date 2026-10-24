using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Lumen.Models;
using Lumen.Services;

namespace Lumen.Terminal;

public sealed class TerminalSession : IDisposable
{
    private readonly SessionProfile _profile;
    private readonly AppSettings _settings;
    private readonly ITerminalTransport _transport;
    private readonly ScreenBuffer _buffer;
    private readonly VtParser _parser;
    private Decoder _decoder;
    private Encoding _encoding;
    private readonly SynchronizationContext _ui;
    private int _updateScheduled;
    private readonly LogService _log = new();
    private readonly StringBuilder _detectBuffer = new();
    private VendorProfile? _vendor;
    private bool _vendorDetectionDone;
    private bool _sawNonAscii;
    private bool _pagingSent;
    private bool _disposed;
    private CancellationTokenSource? _pasteCts;
    private readonly object _echoLock = new();
    private readonly StringBuilder _echo = new();

    public TerminalSession(SessionProfile profile, ITerminalTransport transport, AppSettings settings)
    {
        _profile = profile.Clone();
        _settings = settings;
        _transport = transport;
        _encoding = EncodingUtil.Resolve(profile.Encoding);
        _decoder = _encoding.GetDecoder();
        var resolvedVendor = VendorCatalog.Find(profile.VendorId);
        _vendor = resolvedVendor.Id == "auto" ? null : resolvedVendor;
        _vendorDetectionDone = _vendor is not null;
        _buffer = new ScreenBuffer(100, 30) { ScrollbackLimit = Math.Max(1000, settings.ScrollbackLines) };
        _parser = new VtParser(_buffer);
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        _parser.TitleChanged = title =>
        {
            Title = title;
            _ui.Post(_ =>
            {
                if (!_disposed) TitleChanged?.Invoke();
            }, null);
        };
        _parser.Response = text => SendText(text);

        _transport.DataReceived += OnDataReceived;
        _transport.Disconnected += OnDisconnected;
        _transport.Reconnected += OnReconnected;
    }

    public SessionProfile Profile => _profile;
    public ITerminalTransport Transport => _transport;
    public ScreenBuffer Buffer => _buffer;
    public HexBuffer Hex { get; } = new(1024 * 1024);
    public int BreakDurationMs => _profile.Serial.EffectiveBreakDurationMs;
    public bool IsReconnecting => _transport.IsReconnecting;
    public VendorProfile? Vendor => _vendor;
    public bool IsConnected { get; private set; }
    public bool IsPasting { get; private set; }
    public string Title { get; private set; } = "";
    public string? LogPath => _log.FilePath;
    public bool IsLogging => _log.IsActive;
    public DateTime LastActivity { get; private set; } = DateTime.Now;
    public long BytesReceived => _transport.BytesReceived;
    public long BytesSent => _transport.BytesSent;

    public event Action? Updated;
    public event Action? StateChanged;
    public event Action? TitleChanged;
    public event Action<int, int>? PasteProgress;
    public event Action<VendorProfile>? VendorDetected;

    private static readonly char[] InitSeparators = { '\r', '\n' };

    public async Task<bool> ConnectAsync()
    {
        try
        {
            await _transport.OpenAsync().ConfigureAwait(true);
            if (_disposed)
            {
                IsConnected = false;
                return false;
            }

            IsConnected = true;
            lock (_echoLock) _echo.Clear();
            StateChanged?.Invoke();

            if (_profile.AutoLog && _settings.AutoLog) StartLog();

            var commands = _profile.InitCommands
                .Split(InitSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            if (commands.Count > 0)
            {
                await Task.Delay(650);
                foreach (var command in commands)
                {
                    if (_disposed || !IsConnected) break;
                    SendText(command + "\r");
                    await Task.Delay(160);
                }
            }

            return !_disposed;
        }
        catch (Exception ex)
        {
            if (_disposed) return false;

            // The reconnect worker may have restored the port before this
            // continuation ran; in that case the session is actually online.
            if (_transport.IsOpen)
            {
                IsConnected = true;
                if (_profile.AutoLog && _settings.AutoLog) StartLog();
                InjectSystem($"已连接 {_profile.Serial.PortName}");
                StateChanged?.Invoke();
                return true;
            }

            IsConnected = false;
            InjectSystem($"无法打开 {_profile.Serial.PortName}：{ex.Message}", true);
            StateChanged?.Invoke();
            return false;
        }
    }

    private void OnDataReceived(ReadOnlyMemory<byte> data)
    {
        if (_disposed) return;
        Hex.Append(data.Span);
        Feed(data);
    }

    private void Feed(ReadOnlyMemory<byte> data)
    {
        if (_disposed) return;
        try
        {
            int max = _encoding.GetMaxCharCount(data.Length);
            var chars = new char[Math.Max(1, max)];
            int count = _decoder.GetChars(data.Span, chars, flush: false);
            if (count <= 0) return;
            var text = new string(chars, 0, count);
            if (text.Any(c => c > 127)) _sawNonAscii = true;
            var detected = TryDetectVendor(text);
            _log.Write(text);
            lock (_echoLock)
            {
                _echo.Append(text);
                if (_echo.Length > 65536) _echo.Remove(0, 32768);
            }

            lock (_buffer.SyncRoot)
            {
                _parser.Parse(text);
                LastActivity = DateTime.Now;
                MaybeAutoPage();
            }

            if (detected is not null)
            {
                _ui.Post(_ =>
                {
                    if (!_disposed) VendorDetected?.Invoke(detected);
                }, null);
            }
            PostUpdated();
        }
        catch (Exception ex)
        {
            App.LogError("terminal feed", ex);
        }
    }

    private void PostUpdated()
    {
        if (Interlocked.Exchange(ref _updateScheduled, 1) != 0) return;
        _ui.Post(_ =>
        {
            _updateScheduled = 0;
            if (!_disposed) Updated?.Invoke();
        }, null);
    }

    private VendorProfile? TryDetectVendor(string text)
    {
        if (_vendor is not null || _vendorDetectionDone) return null;
        if (_detectBuffer.Length < 16384) _detectBuffer.Append(text);

        var vendor = VendorCatalog.Detect(_detectBuffer.ToString());
        if (vendor is null)
        {
            if (_detectBuffer.Length >= 16384) _vendorDetectionDone = true;
            return null;
        }

        _vendor = vendor;
        _vendorDetectionDone = true;
        _profile.VendorId = vendor.Id;

        if (!_sawNonAscii)
        {
            _encoding = EncodingUtil.Resolve(vendor.Encoding);
            _decoder = _encoding.GetDecoder();
            _profile.Encoding = vendor.Encoding;
        }

        return vendor;
    }

    private void MaybeAutoPage()
    {
        if (!_profile.AutoPaging || _vendor is null || _pagingSent || !IsConnected) return;

        var command = _vendor.PagingCommand;
        if (string.IsNullOrWhiteSpace(command)) return;

        if (!string.IsNullOrWhiteSpace(_profile.InitCommands) &&
            _profile.InitCommands.Contains(command, StringComparison.OrdinalIgnoreCase))
        {
            _pagingSent = true;
            return;
        }

        var pattern = _vendor.PromptPattern;
        if (string.IsNullOrWhiteSpace(pattern)) return;

        var line = _buffer.GetScreenLine(_buffer.CursorRow).GetText().Trim();
        if (line.Length == 0) return;

        try
        {
            if (!Regex.IsMatch(line, pattern)) return;
        }
        catch
        {
            return;
        }

        _pagingSent = true;
        _ = SendPagingAsync(command);
    }

    private async Task SendPagingAsync(string command)
    {
        try
        {
            await Task.Delay(280).ConfigureAwait(true);
        }
        catch
        {
            return;
        }

        if (_disposed || !IsConnected) return;
        SendText(command + "\r");
    }

    private void OnDisconnected(string reason)
    {
        if (_disposed) return;
        _ui.Post(_ => HandleDisconnected(reason), null);
    }

    private void HandleDisconnected(string reason)
    {
        if (_disposed) return;
        if (_transport.IsOpen)
        {
            // A reconnect completed before this queued notification ran.
            if (!IsConnected)
            {
                IsConnected = true;
                if (_profile.AutoLog && _settings.AutoLog) StartLog();
                StateChanged?.Invoke();
            }
            return;
        }
        if (!IsConnected) return;
        IsConnected = false;
        _log.Stop();
        var hint = _profile.AutoReconnect ? "，正在自动重连…" : "";
        InjectSystem($"连接已断开：{reason}{hint}", true);
        StateChanged?.Invoke();
    }

    private void OnReconnected()
    {
        if (_disposed) return;
        _ui.Post(_ => HandleReconnected(), null);
    }

    private void HandleReconnected()
    {
        if (_disposed || IsConnected) return;
        IsConnected = true;
        if (_profile.AutoLog && _settings.AutoLog) StartLog();
        InjectSystem("串口已重新连接");
        StateChanged?.Invoke();
    }

    public void SendText(string text) => SendTextCore(text, logTransmit: true);

    private bool SendTextCore(string text, bool logTransmit)
    {
        if (_disposed || !IsConnected || text.Length == 0) return false;
        try
        {
            var bytes = _encoding.GetBytes(text);
            _transport.Write(bytes);
            if (logTransmit && (text.EndsWith('\r') || text.EndsWith('\n'))) _log.WriteTx(text);
            return true;
        }
        catch (Exception ex)
        {
            HandleDisconnected(ex.Message);
            return false;
        }
    }

    public void SendBytes(byte[] bytes)
    {
        if (_disposed || !IsConnected || bytes.Length == 0) return;
        try
        {
            _transport.Write(bytes);
        }
        catch (Exception ex)
        {
            HandleDisconnected(ex.Message);
        }
    }

    public void SendKey(Key key, ModifierKeys modifiers)
    {
        var bytes = TerminalKeyEncoder.Encode(key, modifiers, _buffer);
        if (bytes is not null) SendBytes(bytes);
    }

    public async Task SendBreakAsync()
    {
        try
        {
            await _transport.SendBreakAsync(BreakDurationMs).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HandleDisconnected(ex.Message);
        }
    }

    public void SetDtr(bool enabled) => _transport.SetDtr(enabled);
    public void SetRts(bool enabled) => _transport.SetRts(enabled);

    public Task<bool> PasteAsync(string text, int lineDelayMs) =>
        PasteAsync(text, lineDelayMs, 0, waitForEcho: false, echoTimeoutMs: 3000);

    public async Task<bool> PasteAsync(string text, int lineDelayMs, int charDelayMs, bool waitForEcho, int echoTimeoutMs)
    {
        CancelPaste();
        var cts = new CancellationTokenSource();
        _pasteCts = cts;
        var token = cts.Token;

        lineDelayMs = Math.Clamp(lineDelayMs, 0, 5000);
        charDelayMs = Math.Clamp(charDelayMs, 0, 1000);
        echoTimeoutMs = Math.Clamp(echoTimeoutMs, 200, 60000);

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) return true;

        bool completed = true;
        IsPasting = true;
        try
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (token.IsCancellationRequested || _disposed || !IsConnected)
                {
                    completed = false;
                    break;
                }

                if (waitForEcho) ClearEchoBuffer();
                if (!await SendPasteLineAsync(lines[i], charDelayMs, token).ConfigureAwait(true))
                {
                    completed = false;
                    break;
                }

                PasteProgress?.Invoke(i + 1, lines.Count);

                if (waitForEcho)
                {
                    await WaitForEchoAsync(lines[i], echoTimeoutMs, token).ConfigureAwait(true);
                }

                if (i < lines.Count - 1 && lineDelayMs > 0)
                {
                    await Task.Delay(lineDelayMs, token).ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            completed = false;
        }
        finally
        {
            IsPasting = false;
            if (ReferenceEquals(_pasteCts, cts)) _pasteCts = null;
            cts.Dispose();
        }

        return completed;
    }

    private async Task<bool> SendPasteLineAsync(string line, int charDelayMs, CancellationToken token)
    {
        if (charDelayMs <= 0) return SendTextCore(line + "\r", logTransmit: true);

        var enumerator = StringInfo.GetTextElementEnumerator(line);
        int index = 0;
        while (enumerator.MoveNext())
        {
            token.ThrowIfCancellationRequested();
            if (_disposed || !IsConnected) return false;
            if (index > 0) await Task.Delay(charDelayMs, token).ConfigureAwait(true);
            if (!SendTextCore((string)enumerator.Current, logTransmit: false)) return false;
            index++;
        }

        if (index > 0) await Task.Delay(charDelayMs, token).ConfigureAwait(true);
        if (_disposed || !IsConnected) return false;
        if (!SendTextCore("\r", logTransmit: false)) return false;
        _log.WriteTx(line);
        return true;
    }

    private void ClearEchoBuffer()
    {
        lock (_echoLock) _echo.Clear();
    }

    private async Task<bool> WaitForEchoAsync(string line, int timeoutMs, CancellationToken token)
    {
        var match = StripLineBreaks(line);
        if (match.Length == 0) return true;

        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            token.ThrowIfCancellationRequested();
            string buffer;
            lock (_echoLock) buffer = _echo.ToString();
            // Device echo wraps long lines, so ignore CR/LF inserted by the terminal.
            if (StripLineBreaks(buffer).Contains(match, StringComparison.Ordinal)) return true;
            await Task.Delay(20, token).ConfigureAwait(true);
        }
        return false;
    }

    private static string StripLineBreaks(string text)
    {
        if (!text.Contains('\r') && !text.Contains('\n')) return text;
        return text.Replace("\r", "").Replace("\n", "");
    }

    public void CancelPaste()
    {
        try
        {
            _pasteCts?.Cancel();
        }
        catch
        {
            // ignore
        }
    }

    public void StartLog(string? directory = null, bool? timestamps = null)
    {
        if (_disposed || _log.IsActive) return;
        try
        {
            var root = directory ?? _profile.LogDirectory;
            if (string.IsNullOrWhiteSpace(root)) root = _settings.EffectiveLogRoot;
            var path = LogService.BuildFilePath(root, _profile.Name);
            _log.Start(path, timestamps ?? _settings.LogTimestamps, new UTF8Encoding(false), _profile.RawLog);
        }
        catch (Exception ex)
        {
            App.LogError("start log", ex);
        }
    }

    public void StopLog() => _log.Stop();

    public bool ToggleLog()
    {
        if (_log.IsActive) StopLog();
        else StartLog();
        return _log.IsActive;
    }

    public void InjectSystem(string message, bool error = false)
    {
        if (_disposed) return;
        string color = error ? "248;81;73" : "139;147;167";
        lock (_buffer.SyncRoot)
        {
            _parser.Parse($"\r\n\x1b[38;2;{color}m[Lumen] {message}\x1b[0m\r\n");
        }
        PostUpdated();
    }

    public void ParseLocal(string escapeSequence)
    {
        if (_disposed) return;
        lock (_buffer.SyncRoot)
        {
            _parser.Parse(escapeSequence);
        }
        PostUpdated();
    }

    public void Disconnect()
    {
        if (_disposed || !IsConnected) return;
        IsConnected = false;
        _log.Stop();
        try
        {
            _transport.Dispose();
        }
        catch
        {
            // ignore
        }
        InjectSystem("已断开连接", false);
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsConnected = false;
        CancelPaste();
        _transport.DataReceived -= OnDataReceived;
        _transport.Disconnected -= OnDisconnected;
        _transport.Reconnected -= OnReconnected;
        _ = _log.StopAsync();
        try
        {
            _transport.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
