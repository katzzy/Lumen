namespace Lumen.Models;

using System.Text.Json.Serialization;

public sealed class QuickCommand
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public bool Confirm { get; set; }

    public QuickCommand Clone() => new() { Name = Name, Command = Command, Confirm = Confirm };
}

public sealed class SessionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新会话";
    public string Group { get; set; } = "默认分组";
    public string VendorId { get; set; } = "auto";
    public SerialConfig Serial { get; set; } = new();
    public string Encoding { get; set; } = "utf-8";
    public double FontSize { get; set; } = 14;
    public bool AutoLog { get; set; } = true;
    public bool AutoPaging { get; set; } = true;
    public string? LogDirectory { get; set; }
    public int PasteLineDelayMs { get; set; } = 100;
    public int SendCharDelayMs { get; set; }
    public bool WaitForEcho { get; set; }
    public int EchoTimeoutMs { get; set; } = 3000;
    public bool RawLog { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public string InitCommands { get; set; } = "";
    public bool ConfirmDangerousPaste { get; set; } = true;
    public List<QuickCommand> QuickCommands { get; set; } = new();

    public static SessionProfile CreateDefault(string name = "新会话", string group = "默认分组") => new()
    {
        Name = name,
        Group = group
    };

    public SessionProfile Clone()
    {
        var clone = (SessionProfile)MemberwiseClone();
        clone.Serial = Serial.Clone();
        clone.QuickCommands = QuickCommands.Select(q => q.Clone()).ToList();
        return clone;
    }

    [JsonIgnore]
    public string DisplayPort => Serial.PortName;

    [JsonIgnore]
    public string DisplaySummary => $"{Serial.PortName} · {Serial.BaudRate}";
}
