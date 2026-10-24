using System.IO;
using System.IO.Pipes;
using Lumen.Terminal;

namespace Lumen.Serial;

/// <summary>
/// Terminal transport for a Windows named pipe, used by com0com and other
/// virtual serial-port bridges.
/// </summary>
public sealed class NamedPipeTransport : ITerminalTransport
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private readonly object _writeLock = new();
    private volatile bool _isOpen;
    private volatile bool _disposed;
    private long _bytesReceived;
    private long _bytesSent;

    public NamedPipeTransport(string pipeName)
    {
        _pipeName = NormalizePipeName(pipeName);
    }

    public bool IsOpen => _isOpen;
    public bool IsReconnecting => false;
    public string DisplayName => $"管道 · {_pipeName}";
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public string PipeName => _pipeName;

    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<string>? Disconnected;
#pragma warning disable CS0067 // named pipes do not auto reconnect
    public event Action? Reconnected;
#pragma warning restore CS0067

    public static string NormalizePipeName(string? name)
    {
        var value = (name ?? "").Trim();
        if (value.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase)) value = value[9..];
        else if (value.StartsWith(@"\\?\pipe\", StringComparison.OrdinalIgnoreCase)) value = value[10..];
        else if (value.StartsWith("pipe:", StringComparison.OrdinalIgnoreCase)) value = value[5..];
        return value.Trim('\\');
    }

    public async Task OpenAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NamedPipeTransport));
        if (string.IsNullOrWhiteSpace(_pipeName))
            throw new IOException("命名管道名称不能为空。");
        if (_isOpen) return;

        var stream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await Task.Run(() => stream.Connect(5000)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            stream.Dispose();
            throw new IOException($"连接命名管道 {_pipeName} 超时，请确认服务端已启动。");
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        if (_disposed)
        {
            stream.Dispose();
            return;
        }

        _stream = stream;
        _isOpen = true;
        _readCts = new CancellationTokenSource();
        var token = _readCts.Token;
        _readTask = Task.Run(() => ReadLoopAsync(stream, token));
    }

    private async Task ReadLoopAsync(NamedPipeClientStream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0)
                {
                    RaiseDisconnected("命名管道已关闭");
                    return;
                }

                Interlocked.Add(ref _bytesReceived, read);
                try
                {
                    DataReceived?.Invoke(new ReadOnlyMemory<byte>(buffer, 0, read));
                }
                catch
                {
                    // listener failures must not kill the read loop
                }
            }
        }
        catch (OperationCanceledException)
        {
            // dispose or reconnect
        }
        catch (Exception ex)
        {
            if (!_disposed) RaiseDisconnected(ex.Message);
        }
        finally
        {
            _isOpen = false;
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed || !_isOpen || data.Length == 0) return;
        var stream = _stream;
        if (stream is null) return;

        lock (_writeLock)
        {
            try
            {
                stream.Write(data);
                Interlocked.Add(ref _bytesSent, data.Length);
            }
            catch (Exception ex)
            {
                RaiseDisconnected(ex.Message);
            }
        }
    }

    public Task SendBreakAsync(int durationMs) => Task.CompletedTask;

    public void SetDtr(bool enabled)
    {
    }

    public void SetRts(bool enabled)
    {
    }

    private void RaiseDisconnected(string message)
    {
        if (_disposed) return;
        _isOpen = false;
        Disconnected?.Invoke(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isOpen = false;

        var cts = _readCts;
        _readCts = null;
        try
        {
            cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        var stream = _stream;
        _stream = null;
        try
        {
            stream?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            _readTask?.Wait(500);
        }
        catch
        {
            // ignore
        }
        cts?.Dispose();
        _readTask = null;
    }
}

public static class NamedPipeEnumerator
{
    public static List<string> GetPipeNames()
    {
        try
        {
            return Directory.GetFiles(@"\\.\pipe\")
                .Select(path => path.Length > 9 ? path[9..] : "")
                .Where(name => name.Length > 0 && !name.Contains('\\'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }
}
