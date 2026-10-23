using System.Text;
using Lumen.Terminal;

namespace Lumen.Serial;

public sealed class DemoTransport : ITerminalTransport
{
    private readonly StringBuilder _input = new();
    private bool _privileged = true;
    private bool _disposed;
    private bool _escapeSequence;
    private int _bytesSent;

    public bool IsOpen { get; private set; }
    public string DisplayName => "演示 · Cisco Catalyst 2960";
    public long BytesReceived { get; private set; }
    public long BytesSent => _bytesSent;

    public event Action<byte[]>? DataReceived;
    public event Action<string>? Disconnected;

    public async Task OpenAsync()
    {
        IsOpen = true;
        await Task.Delay(120);
        _ = RunScriptAsync();
    }

    private async Task RunScriptAsync()
    {
        Emit("Cisco IOS Software, C2960 Software (C2960-LANBASEK9-M), Version 15.0(2)SE11, RELEASE SOFTWARE (fc3)\r\n");
        Emit("Technical Support: http://www.cisco.com/techsupport\r\n");
        Emit("Copyright (c) 1986-2017 by Cisco Systems, Inc.\r\n");
        Emit("\r\n");
        Emit("Press RETURN to get started.\r\n\r\n");
        await Task.Delay(900);
        Emit("show version\r\n");
        await Task.Delay(320);
        Emit("Switch Ports Model              SW Version            SW Image\r\n");
        Emit("------ ----- -----              ----------            ----------\r\n");
        Emit("*    1 24    WS-C2960-24TT-L     15.0(2)SE11           C2960-LANBASEK9-M\r\n");
        Emit("\r\n");
        Emit("Switch uptime is 12 weeks, 4 days, 3 hours, 21 minutes\r\n");
        Emit("System returned to ROM by power-on\r\n");
        Emit("System image file is \"flash:c2960-lanbasek9-mz.150-2.SE11.bin\"\r\n");
        Emit("\r\n");
        Emit("Configuration register is 0xF\r\n\r\n");
        Emit(Prompt);
        await Task.Delay(1400);
        Emit("show ip interface brief\r\n");
        await Task.Delay(300);
        Emit("Interface              IP-Address      OK? Method Status                Protocol\r\n");
        Emit("Vlan1                  10.10.10.2      YES NVRAM  up                    up      \r\n");
        Emit("FastEthernet0/1        unassigned      YES unset  up                    up      \r\n");
        Emit("FastEthernet0/2        unassigned      YES unset  up                    up      \r\n");
        Emit("FastEthernet0/3        unassigned      YES unset  down                  down    \r\n");
        Emit("FastEthernet0/24       unassigned      YES unset  up                    up      \r\n");
        Emit("GigabitEthernet0/1     unassigned      YES unset  up                    up      \r\n");
        Emit("GigabitEthernet0/2     unassigned      YES unset  down                  down    \r\n");
        Emit("\r\n");
        Emit(Prompt);
    }

    private string Prompt => _privileged ? "Switch# " : "Switch> ";

    public void Write(ReadOnlySpan<byte> data)
    {
        if (!IsOpen) return;
        foreach (byte b in data)
        {
            _bytesSent++;
            HandleByte(b);
        }
    }

    private void HandleByte(byte b)
    {
        if (_escapeSequence)
        {
            if (b >= 0x40 && b <= 0x7E) _escapeSequence = false;
            return;
        }

        switch (b)
        {
            case 0x1B:
                _escapeSequence = true;
                return;
            case 0x0D:
                Emit("\r\n");
                var line = _input.ToString();
                _input.Clear();
                HandleCommand(line);
                return;
            case 0x7F:
            case 0x08:
                if (_input.Length > 0)
                {
                    _input.Length--;
                    Emit("\b \b");
                }
                return;
            case 0x03:
                Emit("^C\r\n");
                _input.Clear();
                Emit(Prompt);
                return;
        }

        if (b >= 0x20 && b < 0x7F)
        {
            _input.Append((char)b);
            Emit(((char)b).ToString());
        }
    }

    private void HandleCommand(string line)
    {
        var cmd = line.Trim();
        switch (cmd)
        {
            case "":
                break;
            case "enable":
                _privileged = true;
                break;
            case "disable":
                _privileged = false;
                break;
            case "show version":
                break;
            case "show ip interface brief":
            case "show ip int brief":
                break;
            case "show running-config":
            case "show run":
                Emit("Building configuration...\r\n\r\n");
                Emit("Current configuration : 4127 bytes\r\n!\r\n");
                Emit("version 15.0\r\nservice timestamps debug datetime msec\r\n!\r\n");
                Emit("hostname Core-SW-01\r\n!\r\nenable secret 5 $1$mERr$9cTjUIEqNGurQiFU.ZeCi1\r\n!\r\n");
                Emit("interface Vlan1\r\n ip address 10.10.10.2 255.255.255.0\r\n!\r\n");
                Emit("interface GigabitEthernet0/1\r\n switchport mode trunk\r\n!\r\n");
                Emit("line con 0\r\n logging synchronous\r\n stopbits 1\r\n!\r\nend\r\n\r\n");
                break;
            case "show interfaces status":
                Emit("Port      Name               Status       Vlan       Duplex  Speed Type\r\n");
                Emit("Fa0/1                        connected    1          a-full  a-100 10/100BaseTX\r\n");
                Emit("Fa0/2                        connected    1          a-full  a-100 10/100BaseTX\r\n");
                Emit("Fa0/3                        notconnect   1            auto   auto 10/100BaseTX\r\n");
                Emit("Gi0/1                        connected    trunk      a-full a-1000 10/100/1000BaseTX\r\n\r\n");
                break;
            case "terminal length 0":
            case "screen-length 0 temporary":
                break;
            case "?":
                Emit("  show      Show running system information\r\n");
                Emit("  enable    Turn on privileged commands\r\n");
                Emit("  exit      Exit from the EXEC\r\n\r\n");
                break;
            case "exit":
                Emit("\r\n[Connection to 10.10.10.2 closed by foreign host]\r\n");
                IsOpen = false;
                Disconnected?.Invoke("演示会话已结束");
                return;
            default:
                Emit($"% Invalid input detected at '^' marker.\r\n\r\n");
                break;
        }
        Emit(Prompt);
    }

    private void Emit(string text)
    {
        if (_disposed) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        BytesReceived += bytes.Length;
        DataReceived?.Invoke(bytes);
    }

    public async Task SendBreakAsync()
    {
        _input.Clear();
        Emit("\r\n*** Break ***\r\n");
        await Task.Delay(80);
        Emit(Prompt);
    }

    public void SetDtr(bool enabled)
    {
    }

    public void SetRts(bool enabled)
    {
    }

    public void Dispose()
    {
        _disposed = true;
        IsOpen = false;
    }
}
