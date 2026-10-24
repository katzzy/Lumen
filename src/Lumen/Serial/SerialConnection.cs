using Lumen.Models;
using Lumen.Terminal;

namespace Lumen.Serial;

public sealed class SerialConnection : ITerminalTransport
{
    private readonly SerialConfig _config;
    private volatile bool _autoReconnect;
    private readonly object _gate = new();
    private Win32SerialPort? _port;
    private volatile bool _disposed;
    private volatile bool _isOpen;
    private volatile bool _reconnecting;
    private volatile bool _immediateRetry;
    private int _retryVersion;
    private long _bytesReceived;
    private long _bytesSent;

    public SerialConnection(SerialConfig config, bool autoReconnect = false)
    {
        _config = config.Clone();
        _autoReconnect = autoReconnect;
    }

    public bool IsOpen => _isOpen;
    public bool IsReconnecting => _reconnecting;
    public string DisplayName => _config.Summary;
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public SerialConfig Config => _config;
    public bool Dtr => _port?.Dtr ?? _config.Dtr;
    public bool Rts => _port?.Rts ?? _config.Rts;

    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<string>? Disconnected;
    public event Action? Reconnected;

    public async Task OpenAsync()
    {
        ThrowIfDisposed();
        if (IsOpen) return;

        var port = CreatePort();
        try
        {
            await Task.Run(() => port.Open(_config)).ConfigureAwait(false);
        }
        catch
        {
            ReleasePort(port);
            if (_autoReconnect) StartReconnectLoop();
            throw;
        }

        bool accepted;
        lock (_gate)
        {
            accepted = !_disposed;
            if (accepted)
            {
                _port = port;
                _isOpen = true;
            }
        }

        if (!accepted) ReleasePort(port);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed || !_isOpen) return;
        var port = CurrentPort();
        if (port is null) return;

        try
        {
            port.Write(data);
            Interlocked.Add(ref _bytesSent, data.Length);
        }
        catch (Exception ex)
        {
            HandlePortFailure(port, ex.Message);
        }
    }

    public async Task SendBreakAsync(int durationMs)
    {
        var port = CurrentPort();
        if (port is null) return;

        try
        {
            await port.SendBreakAsync(durationMs).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HandlePortFailure(port, ex.Message);
        }
    }

    public void SetDtr(bool enabled)
    {
        _config.Dtr = enabled;
        CurrentPort()?.SetDtr(enabled);
    }

    public void SetRts(bool enabled)
    {
        _config.Rts = enabled;
        CurrentPort()?.SetRts(enabled);
    }

    /// <summary>Changes the baud rate on the live port without reconnecting.</summary>
    public bool SetBaudRate(int baudRate)
    {
        var port = CurrentPort();
        if (port is null || !port.SetBaudRate(baudRate)) return false;
        _config.BaudRate = baudRate;
        return true;
    }

    public bool TryGetPins(out SerialPins pins)
    {
        var port = CurrentPort();
        if (port is null)
        {
            pins = default;
            return false;
        }
        return port.TryGetPins(out pins);
    }

    /// <summary>Enables or disables automatic reconnect for a live connection.</summary>
    public void SetAutoReconnect(bool enabled)
    {
        _autoReconnect = enabled;
        if (enabled)
        {
            if (!_disposed && !_isOpen) StartReconnectLoop();
            return;
        }

        lock (_gate)
        {
            _retryVersion++;
            _reconnecting = false;
            _immediateRetry = false;
        }
    }

    /// <summary>
    /// Called when Windows reports a device change. Short-circuits the reconnect
    /// backoff so a re-plugged USB adapter is picked up immediately.
    /// </summary>
    public void NotifyDevicesChanged()
    {
        if (_disposed || !_autoReconnect) return;
        if (_reconnecting) _immediateRetry = true;
    }

    private Win32SerialPort? CurrentPort()
    {
        lock (_gate)
        {
            return _disposed || !_isOpen ? null : _port;
        }
    }

    private Win32SerialPort CreatePort()
    {
        var port = new Win32SerialPort();
        port.DataReceived += OnPortData;
        port.Faulted += OnPortFaulted;
        return port;
    }

    private void ReleasePort(Win32SerialPort? port)
    {
        if (port is null) return;
        port.DataReceived -= OnPortData;
        port.Faulted -= OnPortFaulted;
        try
        {
            port.Dispose();
        }
        catch
        {
            // best effort
        }
    }

    private void OnPortData(ReadOnlyMemory<byte> data)
    {
        Interlocked.Add(ref _bytesReceived, data.Length);
        DataReceived?.Invoke(data);
    }

    private void OnPortFaulted(string message)
    {
        if (_disposed) return;

        lock (_gate)
        {
            if (_disposed) return;
            _isOpen = false;
        }

        Disconnected?.Invoke(message);

        if (_autoReconnect)
        {
            // The read thread must not dispose the port itself (Dispose joins it),
            // so hand the broken handle to the reconnect worker.
            StartReconnectLoop();
        }
        else
        {
            _ = Task.Run(() => TakeAndReleasePort());
        }
    }

    private void HandlePortFailure(Win32SerialPort source, string message)
    {
        if (_disposed) return;

        lock (_gate)
        {
            if (!ReferenceEquals(_port, source)) return;
            _isOpen = false;
        }

        Disconnected?.Invoke(message);
        if (_autoReconnect) StartReconnectLoop();
        else _ = Task.Run(() => TakeAndReleasePort(source));
    }

    private void TakeAndReleasePort(Win32SerialPort? expected = null)
    {
        Win32SerialPort? port;
        lock (_gate)
        {
            port = _port;
            if (expected is not null && !ReferenceEquals(port, expected)) return;
            _port = null;
        }
        ReleasePort(port);
    }

    private void StartReconnectLoop()
    {
        int version;
        lock (_gate)
        {
            if (_disposed || _reconnecting) return;
            _reconnecting = true;
            version = ++_retryVersion;
        }
        _ = Task.Run(() => ReconnectLoopAsync(version));
    }

    private async Task ReconnectLoopAsync(int version)
    {
        try
        {
            // Close the broken handle first so Windows releases the COM port before
            // we try CreateFile again.
            TakeAndReleasePort();

            int attempt = 0;
            while (!_disposed && version == Volatile.Read(ref _retryVersion))
            {
                attempt++;
                await DelayBeforeRetryAsync(attempt).ConfigureAwait(false);
                if (_disposed || version != Volatile.Read(ref _retryVersion)) return;

                var port = CreatePort();
                try
                {
                    await Task.Run(() => port.Open(_config)).ConfigureAwait(false);
                }
                catch
                {
                    ReleasePort(port);
                    continue;
                }

                bool accepted;
                lock (_gate)
                {
                    accepted = !_disposed && version == _retryVersion;
                    if (accepted)
                    {
                        _port = port;
                        _isOpen = true;
                        _reconnecting = false;
                    }
                }

                if (!accepted)
                {
                    ReleasePort(port);
                    return;
                }

                Reconnected?.Invoke();
                return;
            }
        }
        catch (Exception ex)
        {
            App.LogError("serial reconnect", ex);
        }
        finally
        {
            // A newer retry generation may already be running.
            lock (_gate)
            {
                if (version == _retryVersion) _reconnecting = false;
            }
        }
    }

    private static int RetryDelayMs(int attempt) => attempt switch
    {
        <= 1 => 750,
        2 => 1500,
        3 => 3000,
        _ => 5000
    };

    private async Task DelayBeforeRetryAsync(int attempt)
    {
        int delay = RetryDelayMs(attempt);
        int waited = 0;
        while (waited < delay && !_disposed && !_immediateRetry)
        {
            await Task.Delay(50).ConfigureAwait(false);
            waited += 50;
        }
        _immediateRetry = false;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SerialConnection));
    }

    public void Dispose()
    {
        Win32SerialPort? port;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _retryVersion++;
            _isOpen = false;
            _reconnecting = false;
            port = _port;
            _port = null;
        }
        ReleasePort(port);
    }
}
