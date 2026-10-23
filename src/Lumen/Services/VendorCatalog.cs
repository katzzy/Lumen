using System.Text.RegularExpressions;

namespace Lumen.Models;

public static class VendorCatalog
{
    public static VendorProfile Auto { get; } = new()
    {
        Id = "auto",
        Name = "自动识别",
        Family = "",
        Notes = "根据设备启动信息自动判断厂商，并在登录后自动关闭分页。"
    };

    private static readonly VendorProfile Cisco = new()
    {
        Id = "cisco",
        Name = "Cisco",
        Family = "IOS / IOS-XE",
        DefaultBaud = 9600,
        Encoding = "utf-8",
        PagingCommand = "terminal length 0",
        PromptPattern = @"^[A-Za-z0-9_.\-]+[>#]\s*$",
        DetectPatterns = new[]
        {
            @"Cisco\s+IOS",
            @"Cisco\s+Internetwork\s+Operating\s+System",
            @"Cisco\s+Systems",
            @"IOS-XE"
        },
        Notes = "IOS / IOS-XE 通用。",
        QuickCommands = Commands(
            Q("特权模式", "enable"),
            Q("查看版本", "show version"),
            Q("查看配置", "show running-config", true),
            Q("接口状态", "show ip interface brief"),
            Q("关闭分页", "terminal length 0"))
    };

    private static readonly VendorProfile H3c = new()
    {
        Id = "h3c",
        Name = "H3C",
        Family = "Comware",
        DefaultBaud = 9600,
        Encoding = "gbk",
        PagingCommand = "screen-length disable",
        PromptPattern = @"^<[^>]+>\s*$",
        DetectPatterns = new[]
        {
            @"H3C\s+Comware",
            @"Comware\s+Software",
            @"H3C\s+Technologies",
            @"Hangzhou\s+H3C"
        },
        Notes = "中文输出通常为 GBK。",
        QuickCommands = Commands(
            Q("查看版本", "display version"),
            Q("查看配置", "display current-configuration", true),
            Q("接口状态", "display interface brief"),
            Q("关闭分页", "screen-length disable"),
            Q("系统视图", "system-view"))
    };

    private static readonly VendorProfile Huawei = new()
    {
        Id = "huawei",
        Name = "华为",
        Family = "VRP",
        DefaultBaud = 9600,
        Encoding = "gbk",
        PagingCommand = "screen-length 0 temporary",
        PromptPattern = @"^<[^>]+>\s*$",
        DetectPatterns = new[]
        {
            @"Huawei\s+Versatile\s+Routing\s+Platform",
            @"Huawei\s+VRP",
            @"VRP\s+\(R\)\s+software",
            @"Huawei\s+Technologies"
        },
        Notes = "中文输出通常为 GBK。",
        QuickCommands = Commands(
            Q("查看版本", "display version"),
            Q("查看配置", "display current-configuration", true),
            Q("接口状态", "display interface brief"),
            Q("关闭分页", "screen-length 0 temporary"),
            Q("系统视图", "system-view"))
    };

    private static readonly VendorProfile Zte = new()
    {
        Id = "zte",
        Name = "中兴",
        Family = "ZXR10",
        DefaultBaud = 9600,
        Encoding = "gbk",
        PagingCommand = "terminal length 0",
        PromptPattern = @"^[A-Za-z0-9_.\-]+[>#]\s*$",
        DetectPatterns = new[]
        {
            @"ZTE\s+Corporation",
            @"ZXR10",
            @"ZTE\s+ROS",
            @"ZTE\s+Technologies"
        },
        Notes = "部分新机型为 115200；以设备手册为准。",
        QuickCommands = Commands(
            Q("查看版本", "show version"),
            Q("查看配置", "show running-config", true),
            Q("接口状态", "show interface brief"),
            Q("关闭分页", "terminal length 0"))
    };

    private static readonly VendorProfile Ruijie = new()
    {
        Id = "ruijie",
        Name = "锐捷",
        Family = "RGOS",
        DefaultBaud = 9600,
        Encoding = "gbk",
        PagingCommand = "terminal length 0",
        PromptPattern = @"^[A-Za-z0-9_.\-]+[>#]\s*$",
        DetectPatterns = new[]
        {
            @"Ruijie\s+Networks",
            @"RGOS",
            @"Ruijie"
        },
        Notes = "命令体系接近 Cisco。",
        QuickCommands = Commands(
            Q("特权模式", "enable"),
            Q("查看版本", "show version"),
            Q("查看配置", "show running-config", true),
            Q("接口状态", "show ip interface brief"),
            Q("关闭分页", "terminal length 0"))
    };

    private static readonly VendorProfile Maipu = new()
    {
        Id = "maipu",
        Name = "迈普",
        Family = "MyPower",
        DefaultBaud = 9600,
        Encoding = "gbk",
        PagingCommand = "terminal length 0",
        PromptPattern = @"^[A-Za-z0-9_.\-]+[>#]\s*$",
        DetectPatterns = new[]
        {
            @"Maipu",
            @"MyPower"
        },
        Notes = "",
        QuickCommands = Commands(
            Q("查看版本", "show version"),
            Q("查看配置", "show running-config", true),
            Q("接口状态", "show interface brief"),
            Q("关闭分页", "terminal length 0"))
    };

    private static readonly VendorProfile TpLink = new()
    {
        Id = "tplink",
        Name = "TP-Link",
        Family = "JetStream",
        DefaultBaud = 9600,
        Encoding = "gbk",
        PagingCommand = "terminal length 0",
        PromptPattern = @"^[A-Za-z0-9_.\-]+[>#]\s*$",
        DetectPatterns = new[]
        {
            @"TP-Link",
            @"JetStream"
        },
        Notes = "",
        QuickCommands = Commands(
            Q("查看版本", "show version"),
            Q("查看配置", "show running-config", true),
            Q("接口状态", "show interface status"),
            Q("关闭分页", "terminal length 0"))
    };

    private static readonly VendorProfile Juniper = new()
    {
        Id = "juniper",
        Name = "Juniper",
        Family = "Junos",
        DefaultBaud = 9600,
        Encoding = "utf-8",
        PagingCommand = "set cli screen-length 0",
        PromptPattern = @"^[^@\s]+@[^>\s]+>\s*$",
        DetectPatterns = new[]
        {
            @"Juniper\s+Networks",
            @"JUNOS",
            @"Junos"
        },
        Notes = "Junos 操作模式。",
        QuickCommands = Commands(
            Q("查看版本", "show version"),
            Q("查看配置", "show configuration | display set", true),
            Q("接口状态", "show interfaces terse"),
            Q("关闭分页", "set cli screen-length 0"))
    };

    private static readonly VendorProfile Arista = new()
    {
        Id = "arista",
        Name = "Arista",
        Family = "EOS",
        DefaultBaud = 9600,
        Encoding = "utf-8",
        PagingCommand = "terminal length 0",
        PromptPattern = @"^[A-Za-z0-9_.\-]+[>#]\s*$",
        DetectPatterns = new[]
        {
            @"Arista\s+Networks",
            @"Arista\s+EOS",
            @"EOS\s+Software"
        },
        Notes = "命令体系接近 Cisco。",
        QuickCommands = Commands(
            Q("特权模式", "enable"),
            Q("查看版本", "show version"),
            Q("查看配置", "show running-config", true),
            Q("接口状态", "show interfaces status"),
            Q("关闭分页", "terminal length 0"))
    };

    private static readonly VendorProfile Aruba = new()
    {
        Id = "aruba",
        Name = "Aruba",
        Family = "HPE / ProCurve",
        DefaultBaud = 9600,
        Encoding = "utf-8",
        PagingCommand = "no page",
        PromptPattern = @"^[A-Za-z0-9_.\-]+[>#]\s*$",
        DetectPatterns = new[]
        {
            @"Aruba",
            @"ProCurve",
            @"ArubaOS"
        },
        Notes = "",
        QuickCommands = Commands(
            Q("查看版本", "show version"),
            Q("查看配置", "show running-config", true),
            Q("接口状态", "show interface brief"),
            Q("关闭分页", "no page"))
    };

    private static readonly VendorProfile Dell = new()
    {
        Id = "dell",
        Name = "Dell",
        Family = "OS10 / OS9",
        DefaultBaud = 9600,
        Encoding = "utf-8",
        PagingCommand = "terminal length 0",
        PromptPattern = @"^[A-Za-z0-9_.\-]+[>#]\s*$",
        DetectPatterns = new[]
        {
            @"Dell\s+EMC",
            @"Dell\s+Networking",
            @"Dell\s+OS10"
        },
        Notes = "",
        QuickCommands = Commands(
            Q("查看版本", "show version"),
            Q("查看配置", "show running-configuration", true),
            Q("接口状态", "show interface status"),
            Q("关闭分页", "terminal length 0"))
    };

    private static readonly VendorProfile Extreme = new()
    {
        Id = "extreme",
        Name = "Extreme",
        Family = "EXOS",
        DefaultBaud = 9600,
        Encoding = "utf-8",
        PagingCommand = "disable clipaging",
        PromptPattern = @"^[A-Za-z0-9_.\-]+[>#]\s*$",
        DetectPatterns = new[]
        {
            @"ExtremeXOS",
            @"Extreme\s+Networks",
            @"EXOS"
        },
        Notes = "",
        QuickCommands = Commands(
            Q("查看版本", "show version"),
            Q("查看配置", "show configuration", true),
            Q("关闭分页", "disable clipaging"))
    };

    private static readonly VendorProfile MikroTik = new()
    {
        Id = "mikrotik",
        Name = "MikroTik",
        Family = "RouterOS",
        DefaultBaud = 115200,
        Encoding = "utf-8",
        PagingCommand = null,
        PromptPattern = @"^\[[^\]]+\]\s*>\s*$",
        DetectPatterns = new[]
        {
            @"MikroTik",
            @"RouterOS"
        },
        Notes = "RouterOS 控制台默认 115200 8N1。",
        QuickCommands = Commands(
            Q("系统资源", "/system resource print"),
            Q("导出配置", "/export", true),
            Q("接口列表", "/interface print"))
    };

    private static readonly VendorProfile Generic = new()
    {
        Id = "generic",
        Name = "通用",
        Family = "VT100",
        DefaultBaud = 9600,
        Encoding = "utf-8",
        PagingCommand = null,
        PromptPattern = null,
        Notes = "通用 VT100/ANSI 串口，不自动发送任何命令。",
        QuickCommands = Array.Empty<QuickCommand>()
    };

    public static IReadOnlyList<VendorProfile> All { get; } = new List<VendorProfile>
    {
        Auto,
        Cisco,
        H3c,
        Huawei,
        Zte,
        Ruijie,
        Maipu,
        TpLink,
        Juniper,
        Arista,
        Aruba,
        Dell,
        Extreme,
        MikroTik,
        Generic
    };

    public static bool IsAuto(string? vendorId) =>
        string.IsNullOrWhiteSpace(vendorId) || vendorId.Equals("auto", StringComparison.OrdinalIgnoreCase);

    public static VendorProfile Find(string? vendorId)
    {
        if (IsAuto(vendorId)) return Auto;
        return All.FirstOrDefault(v => v.Id.Equals(vendorId, StringComparison.OrdinalIgnoreCase)) ?? Auto;
    }

    public static VendorProfile? Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var vendor in All)
        {
            if (vendor.Id is "auto" or "generic") continue;
            foreach (var pattern in vendor.DetectPatterns)
            {
                try
                {
                    if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    {
                        return vendor;
                    }
                }
                catch
                {
                    // ignore malformed user patterns
                }
            }
        }
        return null;
    }

    public static void ApplyDefaults(SessionProfile profile, VendorProfile vendor)
    {
        profile.VendorId = vendor.Id;
        if (vendor.Id is "auto" or "generic") return;

        profile.Serial.BaudRate = vendor.DefaultBaud;
        profile.Encoding = vendor.Encoding;
        profile.AutoPaging = !string.IsNullOrWhiteSpace(vendor.PagingCommand);
        profile.QuickCommands = vendor.QuickCommands.Select(q => q.Clone()).ToList();
    }

    public static string Describe(VendorProfile vendor)
    {
        var parts = new List<string> { $"{vendor.DefaultBaud} 8N1" };
        if (!string.IsNullOrWhiteSpace(vendor.Encoding)) parts.Add(vendor.Encoding.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(vendor.PagingCommand)) parts.Add($"分页：{vendor.PagingCommand}");
        return string.Join(" · ", parts);
    }

    private static List<QuickCommand> Commands(params QuickCommand[] commands) => commands.ToList();

    private static QuickCommand Q(string name, string command, bool confirm = false) =>
        new() { Name = name, Command = command, Confirm = confirm };
}
