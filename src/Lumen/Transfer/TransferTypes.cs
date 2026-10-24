namespace Lumen.Transfer;

public enum TransferProtocol
{
    Xmodem,
    Xmodem1K,
    XmodemChecksum,
    Ymodem
}

public enum TransferDirection
{
    Send,
    Receive
}

public sealed record TransferOptions(TransferProtocol Protocol, TransferDirection Direction, string FilePath);

public readonly record struct TransferProgress(
    long Bytes,
    long TotalBytes,
    int Blocks,
    int Retries,
    string Status);

/// <summary>Small byte channel abstraction so the protocol engine can be unit tested.</summary>
public interface ITransferChannel
{
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token);

    /// <summary>Returns the next byte, or -1 when the timeout expires.</summary>
    Task<int> ReadByteAsync(int timeoutMs, CancellationToken token);
}

public sealed class TransferException : Exception
{
    public TransferException(string message) : base(message)
    {
    }
}
