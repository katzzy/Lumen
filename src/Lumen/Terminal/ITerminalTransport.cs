namespace Lumen.Terminal;

public interface ITerminalTransport : IDisposable
{
    bool IsOpen { get; }
    string DisplayName { get; }
    long BytesReceived { get; }
    long BytesSent { get; }

    event Action<ReadOnlyMemory<byte>>? DataReceived;
    event Action<string>? Disconnected;

    Task OpenAsync();
    void Write(ReadOnlySpan<byte> data);
    Task SendBreakAsync();
    void SetDtr(bool enabled);
    void SetRts(bool enabled);
}
