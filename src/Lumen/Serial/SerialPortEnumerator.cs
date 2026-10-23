using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Lumen.Serial;

public sealed record SerialPortInfo(string PortName, string DisplayName, string Description)
{
    public bool IsUsb =>
        DisplayName.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
        Description.Contains("USB", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{PortName}  —  {DisplayName}";
}

public static partial class SerialPortEnumerator
{
    private static readonly Guid PortsClassGuid = new("4d36e978-e325-11ce-bfc1-08002be10318");
    private const uint DigcfPresent = 0x00000002;
    private const uint SpdrpDeviceDesc = 0x00000000;
    private const uint SpdrpFriendlyName = 0x0000000C;

    public static List<SerialPortInfo> GetPorts()
    {
        var result = new Dictionary<string, SerialPortInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var classGuid = PortsClassGuid;
            var handle = SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, DigcfPresent);
            if (handle != new IntPtr(-1))
            {
                try
                {
                    var data = new SpDevInfoData { cbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
                    for (uint index = 0; SetupDiEnumDeviceInfo(handle, index, ref data); index++)
                    {
                        string friendly = GetProperty(handle, ref data, SpdrpFriendlyName) ?? "";
                        string desc = GetProperty(handle, ref data, SpdrpDeviceDesc) ?? "";
                        var match = ComRegex().Match(friendly);
                        if (!match.Success) match = ComRegex().Match(desc);
                        if (!match.Success) continue;

                        string port = match.Value.ToUpperInvariant();
                        string display = friendly.Length > 0 ? friendly : desc;
                        display = ParenthesizedCom().Replace(display, "").Trim();
                        if (display.Length == 0) display = port;
                        result[port] = new SerialPortInfo(port, display, desc);
                    }
                }
                finally
                {
                    SetupDiDestroyDeviceInfoList(handle);
                }
            }
        }
        catch
        {
            // fall through to registry
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key is not null)
            {
                foreach (var valueName in key.GetValueNames())
                {
                    if (key.GetValue(valueName) is not string port) continue;
                    port = port.ToUpperInvariant();
                    if (!result.ContainsKey(port))
                    {
                        string desc = valueName.Replace(@"\Device\", "").Trim();
                        result[port] = new SerialPortInfo(port, desc.Length > 0 ? desc : port, desc);
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return result.Values
            .OrderBy(p => PortNumber(p.PortName))
            .ThenBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int PortNumber(string name)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int n) ? n : int.MaxValue;
    }

    private static string? GetProperty(IntPtr handle, ref SpDevInfoData data, uint property)
    {
        var buffer = new byte[1024];
        if (SetupDiGetDeviceRegistryProperty(handle, ref data, property, out _, buffer, (uint)buffer.Length, out uint required))
        {
            int length = (int)Math.Min(required, (uint)buffer.Length);
            return Encoding.Unicode.GetString(buffer, 0, length).TrimEnd('\0');
        }
        return null;
    }

    [GeneratedRegex(@"\bCOM\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex ComRegex();

    [GeneratedRegex(@"\s*\(\s*COM\d+\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex ParenthesizedCom();

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[] propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
