using Lumen.Models;
using Lumen.Terminal;

namespace Lumen.Serial;

public sealed class SerialConnection : ITerminalTransport
{
    private readonly SerialConfig _config;
    private readonly Win32SerialPort _port = new();
    private bool _disposed;

    public SerialConnection(SerialConfig config)
    {
        _config = config.Clone();
        _port.DataReceived += data => DataReceived?.Invoke(data);
        _port.Faulted += message =>
        {
            IsOpen = false;
            Disconnected?.Invoke(message);
        };
    }

    public bool IsOpen { get; private set; }
    public string DisplayName => _config.Summary;
    public long BytesReceived => _port.BytesReceived;
    public long BytesSent => _port.BytesSent;
    public SerialConfig Config => _config;
    public bool Dtr => _port.Dtr;
    public bool Rts => _port.Rts;

    public event Action<byte[]>? DataReceived;
    public event Action<string>? Disconnected;

    public async Task OpenAsync()
    {
        await Task.Run(() => _port.Open(_config)).ConfigureAwait(false);
        IsOpen = true;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (!IsOpen) return;
        try
        {
            _port.Write(data);
        }
        catch (Exception ex)
        {
            IsOpen = false;
            Disconnected?.Invoke(ex.Message);
        }
    }

    public async Task SendBreakAsync()
    {
        try
        {
            await _port.SendBreakAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Disconnected?.Invoke(ex.Message);
        }
    }

    public void SetDtr(bool enabled) => _port.SetDtr(enabled);
    public void SetRts(bool enabled) => _port.SetRts(enabled);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsOpen = false;
        _port.Dispose();
    }
}
