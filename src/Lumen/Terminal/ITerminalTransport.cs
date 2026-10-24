namespace Lumen.Terminal;

public interface ITerminalTransport : IDisposable
{
    bool IsOpen { get; }
    bool IsReconnecting { get; }
    string DisplayName { get; }
    long BytesReceived { get; }
    long BytesSent { get; }

    event Action<ReadOnlyMemory<byte>>? DataReceived;
    event Action<string>? Disconnected;
    event Action? Reconnected;

    Task OpenAsync();
    void Write(ReadOnlySpan<byte> data);
    Task SendBreakAsync(int durationMs);
    void SetDtr(bool enabled);
    void SetRts(bool enabled);
}
