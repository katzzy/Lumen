using System.IO;
using Lumen.Terminal;

namespace Lumen.Transfer;

/// <summary>
/// Bridges a terminal transport to the transfer engine. While a transfer is
/// active, received bytes are routed here instead of into the VT parser.
/// </summary>
public sealed class TransportTransferChannel : ITransferChannel, IDisposable
{
    private readonly ITerminalTransport _transport;
    private readonly object _gate = new();
    private readonly Queue<byte> _queue = new();
    private readonly SemaphoreSlim _available = new(0);
    private volatile bool _aborted;

    public TransportTransferChannel(ITerminalTransport transport)
    {
        _transport = transport;
    }

    public void Push(ReadOnlySpan<byte> data)
    {
        if (_aborted || data.IsEmpty) return;
        lock (_gate)
        {
            foreach (byte value in data) _queue.Enqueue(value);
        }
        _available.Release(data.Length);
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_aborted) throw new IOException("文件传输已中止。");
        if (data.IsEmpty) return Task.CompletedTask;
        _transport.Write(data.Span);
        return Task.CompletedTask;
    }

    public async Task<int> ReadByteAsync(int timeoutMs, CancellationToken token)
    {
        if (_aborted) return -1;
        if (!await _available.WaitAsync(Math.Max(1, timeoutMs), token).ConfigureAwait(false))
        {
            return -1;
        }

        lock (_gate)
        {
            if (_queue.Count > 0) return _queue.Dequeue();
        }
        return -1;
    }

    public byte[] TakeRemaining()
    {
        lock (_gate)
        {
            if (_queue.Count == 0) return Array.Empty<byte>();
            var remaining = _queue.ToArray();
            _queue.Clear();
            return remaining;
        }
    }

    public void Abort()
    {
        _aborted = true;
        lock (_gate)
        {
            _queue.Clear();
        }
        try
        {
            _available.Release();
        }
        catch
        {
            // best effort wake-up
        }
    }

    public void Dispose()
    {
        Abort();
    }
}
