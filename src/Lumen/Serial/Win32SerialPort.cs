using System.ComponentModel;
using System.Runtime.InteropServices;
using Lumen.Models;

namespace Lumen.Serial;

internal sealed class Win32SerialPort : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint PurgeTxClear = 0x0004;
    private const uint PurgeRxClear = 0x0008;
    private const uint SetBreakFunc = 8;
    private const uint ClearBreakFunc = 9;
    private const uint SetRtsFunc = 3;
    private const uint ClearRtsFunc = 4;
    private const uint SetDtrFunc = 5;
    private const uint ClearDtrFunc = 6;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    private IntPtr _handle = InvalidHandleValue;
    private Thread? _readThread;
    private volatile bool _running;
    private readonly object _writeLock = new();
    private readonly object _stateLock = new();
    private bool _dtr = true;
    private bool _rts = true;

    public event Action<byte[]>? DataReceived;
    public event Action<string>? Faulted;

    public bool IsOpen
    {
        get
        {
            lock (_stateLock)
            {
                return _handle != InvalidHandleValue && _handle != IntPtr.Zero;
            }
        }
    }

    public long BytesReceived { get; private set; }
    public long BytesSent { get; private set; }

    public void Open(SerialConfig config)
    {
        var name = config.PortName.Trim();
        if (name.StartsWith(@"\\.\", StringComparison.Ordinal)) name = name[4..];
        if (!name.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
        {
            throw new SerialException($"无效的串口名称：{config.PortName}");
        }

        var handle = CreateFile(
            @"\\.\" + name,
            GenericRead | GenericWrite,
            0,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle == InvalidHandleValue)
        {
            int err = Marshal.GetLastWin32Error();
            throw new SerialException(FriendlyError(name, err), new Win32Exception(err));
        }

        try
        {
            SetupComm(handle, 65536, 65536);

            var dcb = new Dcb { DCBlength = (uint)Marshal.SizeOf<Dcb>() };
            if (!GetCommState(handle, ref dcb))
            {
                throw new SerialException($"无法读取 {name} 的串口参数。", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            dcb.BaudRate = (uint)config.BaudRate;
            dcb.ByteSize = (byte)Math.Clamp(config.DataBits, 5, 8);
            dcb.Parity = (byte)config.Parity;
            dcb.StopBits = (byte)config.StopBits;

            uint flags = 0x0001; // fBinary
            if (config.Parity != SerialParity.None) flags |= 0x0002; // fParity

            switch (config.FlowControl)
            {
                case SerialFlowControl.RtsCts:
                    flags |= 0x0004;          // fOutxCtsFlow
                    flags |= 2u << 12;        // RTS_CONTROL_HANDSHAKE
                    break;
                case SerialFlowControl.XonXoff:
                    flags |= 0x0100 | 0x0200; // fOutX | fInX
                    flags |= 1u << 12;        // RTS enable
                    break;
                default:
                    flags |= config.Rts ? 1u << 12 : 0u;
                    break;
            }

            flags |= config.Dtr ? 1u << 4 : 0u; // DTR_CONTROL_ENABLE
            dcb.Flags = flags;

            if (!SetCommState(handle, ref dcb))
            {
                throw new SerialException($"无法配置 {name}（{config.BaudRate} 波特率）。", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            var timeouts = new CommTimeouts
            {
                ReadIntervalTimeout = 30,
                ReadTotalTimeoutMultiplier = 0,
                ReadTotalTimeoutConstant = 30,
                WriteTotalTimeoutMultiplier = 0,
                WriteTotalTimeoutConstant = 800
            };
            if (!SetCommTimeouts(handle, ref timeouts))
            {
                throw new SerialException($"无法设置 {name} 的读写超时。", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            PurgeComm(handle, PurgeRxClear | PurgeTxClear);
            EscapeCommFunction(handle, config.Dtr ? SetDtrFunc : ClearDtrFunc);
            if (config.FlowControl != SerialFlowControl.RtsCts)
            {
                EscapeCommFunction(handle, config.Rts ? SetRtsFunc : ClearRtsFunc);
            }

            lock (_stateLock)
            {
                _handle = handle;
                _dtr = config.Dtr;
                _rts = config.Rts;
                _running = true;
            }

            _readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = $"Lumen serial {name}"
            };
            _readThread.Start();
        }
        catch
        {
            CloseHandle(handle);
            throw;
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[8192];
        while (_running)
        {
            IntPtr handle;
            lock (_stateLock) handle = _handle;
            if (handle == InvalidHandleValue || handle == IntPtr.Zero) break;

            bool ok;
            uint read;
            try
            {
                ok = ReadFile(handle, buffer, (uint)buffer.Length, out read, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                if (_running) Faulted?.Invoke(ex.Message);
                break;
            }

            if (!ok)
            {
                if (!_running) break;
                int err = Marshal.GetLastWin32Error();
                Faulted?.Invoke(FriendlyError("串口", err));
                break;
            }

            if (read == 0)
            {
                Thread.Sleep(4);
                continue;
            }

            var data = new byte[read];
            Buffer.BlockCopy(buffer, 0, data, 0, (int)read);
            BytesReceived += read;
            try
            {
                DataReceived?.Invoke(data);
            }
            catch
            {
                // listener failures must not kill the read loop
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        var bytes = data.ToArray();
        lock (_writeLock)
        {
            IntPtr handle;
            lock (_stateLock) handle = _handle;
            if (handle == InvalidHandleValue || handle == IntPtr.Zero)
            {
                throw new SerialException("串口未打开。");
            }

            int offset = 0;
            while (offset < bytes.Length)
            {
                var chunk = offset == 0 ? bytes : bytes[offset..];
                if (!WriteFile(handle, chunk, (uint)chunk.Length, out uint written, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new SerialException(FriendlyError("串口写入", err), new Win32Exception(err));
                }
                if (written == 0)
                {
                    throw new SerialException($"串口写入返回 0 字节（已写 {offset}/{bytes.Length}）。");
                }
                offset += (int)written;
            }
            BytesSent += offset;
        }
    }

    public async Task SendBreakAsync()
    {
        IntPtr handle;
        lock (_stateLock) handle = _handle;
        if (handle == InvalidHandleValue || handle == IntPtr.Zero) return;

        EscapeCommFunction(handle, SetBreakFunc);
        try
        {
            await Task.Delay(250).ConfigureAwait(false);
        }
        finally
        {
            lock (_stateLock) handle = _handle;
            if (handle != InvalidHandleValue && handle != IntPtr.Zero)
            {
                EscapeCommFunction(handle, ClearBreakFunc);
            }
        }
    }

    public void SetDtr(bool enabled)
    {
        _dtr = enabled;
        IntPtr handle;
        lock (_stateLock) handle = _handle;
        if (handle == InvalidHandleValue || handle == IntPtr.Zero) return;
        EscapeCommFunction(handle, enabled ? SetDtrFunc : ClearDtrFunc);
    }

    public void SetRts(bool enabled)
    {
        _rts = enabled;
        IntPtr handle;
        lock (_stateLock) handle = _handle;
        if (handle == InvalidHandleValue || handle == IntPtr.Zero) return;
        EscapeCommFunction(handle, enabled ? SetRtsFunc : ClearRtsFunc);
    }

    public bool Dtr => _dtr;
    public bool Rts => _rts;

    public void Dispose()
    {
        Thread? thread;
        IntPtr handle;
        lock (_stateLock)
        {
            if (!_running && _handle == InvalidHandleValue) return;
            _running = false;
            handle = _handle;
            thread = _readThread;
        }

        if (handle != InvalidHandleValue && handle != IntPtr.Zero)
        {
            CancelIoEx(handle, IntPtr.Zero);
        }

        if (thread is not null && thread.IsAlive)
        {
            thread.Join(800);
        }

        lock (_stateLock)
        {
            if (_handle != InvalidHandleValue && _handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
            }
            _handle = InvalidHandleValue;
            _readThread = null;
        }
    }

    private static string FriendlyError(string port, int error)
    {
        return error switch
        {
            2 => $"{port} 不存在，请检查设备是否已连接。",
            5 => $"{port} 被其他程序占用，请关闭占用该串口的软件后重试。",
            31 => $"{port} 无法访问，设备可能已拔出。",
            1167 => $"{port} 未连接设备。",
            _ => $"无法打开 {port}（错误 {error}）。"
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public uint DCBlength;
        public uint BaudRate;
        public uint Flags;
        public ushort wReserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EvtChar;
        public ushort wReserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCommState(IntPtr hFile, ref Dcb lpDcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCommState(IntPtr hFile, ref Dcb lpDcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCommTimeouts(IntPtr hFile, ref CommTimeouts lpCommTimeouts);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupComm(IntPtr hFile, uint dwInQueue, uint dwOutQueue);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PurgeComm(IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EscapeCommFunction(IntPtr hFile, uint dwFunc);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);
}

public sealed class SerialException : Exception
{
    public SerialException(string message) : base(message)
    {
    }

    public SerialException(string message, Exception inner) : base(message, inner)
    {
    }
}
