namespace Lumen.Models;

using System.Text.Json.Serialization;

public enum SessionTransportKind
{
    Serial = 0,
    NamedPipe = 1
}

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
    public SessionTransportKind TransportKind { get; set; } = SessionTransportKind.Serial;
    public string PipeName { get; set; } = "";
    public SerialConfig Serial { get; set; } = new();
    public string Encoding { get; set; } = "utf-8";
    public double FontSize { get; set; } = 14;
    /// <summary>0 = fit the window, otherwise 80 or 132 fixed columns.</summary>
    public int TerminalColumns { get; set; }
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
    public string Script { get; set; } = "";
    public bool ScriptAutoRun { get; set; }
    public List<ExpectRule> ExpectRules { get; set; } = new();
    public List<HighlightRule> HighlightRules { get; set; } = new();

    public static SessionProfile CreateDefault(string name = "新会话", string group = "默认分组") => new()
    {
        Name = name,
        Group = group
    };

    public SessionProfile Clone()
    {
        var clone = (SessionProfile)MemberwiseClone();
        clone.Serial = Serial?.Clone() ?? new SerialConfig();
        clone.QuickCommands = (QuickCommands ?? new())
            .Where(q => q is not null).Select(q => q.Clone()).ToList();
        clone.ExpectRules = (ExpectRules ?? new())
            .Where(r => r is not null).Select(r => r.Clone()).ToList();
        clone.HighlightRules = (HighlightRules ?? new())
            .Where(r => r is not null).Select(r => r.Clone()).ToList();
        clone.Normalize();
        return clone;
    }

    /// <summary>
    /// Repairs profiles that came from older or hand-edited session JSON.
    /// JSON can legally contain nulls even though the C# properties are
    /// non-nullable, and the UI assumes the collections are populated.
    /// </summary>
    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(Name)) Name = "未命名会话";
        if (string.IsNullOrWhiteSpace(Group)) Group = "默认分组";
        if (string.IsNullOrWhiteSpace(VendorId)) VendorId = "auto";
        PipeName ??= "";
        Encoding = string.IsNullOrWhiteSpace(Encoding) ? "utf-8" : Encoding;
        InitCommands ??= "";
        Script ??= "";

        Serial ??= new SerialConfig();
        Serial.PortName = string.IsNullOrWhiteSpace(Serial.PortName) ? "COM1" : Serial.PortName;
        if (Serial.BaudRate is < 300 or > 921600) Serial.BaudRate = 9600;
        Serial.DataBits = Math.Clamp(Serial.DataBits, 5, 8);
        Serial.BreakDurationMs = Math.Clamp(Serial.BreakDurationMs, 10, 5000);

        FontSize = Math.Clamp(FontSize <= 0 ? 14 : FontSize, 8, 40);
        TerminalColumns = TerminalColumns is 80 or 132 ? TerminalColumns : 0;
        PasteLineDelayMs = Math.Clamp(PasteLineDelayMs, 0, 5000);
        SendCharDelayMs = Math.Clamp(SendCharDelayMs, 0, 1000);
        EchoTimeoutMs = Math.Clamp(EchoTimeoutMs, 200, 60000);

        QuickCommands ??= new();
        QuickCommands.RemoveAll(q => q is null);
        foreach (var command in QuickCommands)
        {
            command.Name ??= "";
            command.Command ??= "";
        }

        ExpectRules ??= new();
        ExpectRules.RemoveAll(r => r is null);
        foreach (var rule in ExpectRules)
        {
            rule.Pattern ??= "";
            rule.Response ??= "";
            rule.CooldownMs = Math.Clamp(rule.CooldownMs, 0, 60000);
        }

        HighlightRules ??= new();
        HighlightRules.RemoveAll(r => r is null);
        foreach (var rule in HighlightRules)
        {
            rule.Pattern ??= "";
            if (!IsHexColor(rule.Color)) rule.Color = "#F0B429";
        }
    }

    private static bool IsHexColor(string? color) =>
        color is { Length: 7 } && color[0] == '#' && color[1..].All(Uri.IsHexDigit);

    [JsonIgnore]
    public string DisplayPort => TransportKind == SessionTransportKind.NamedPipe
        ? PipeName
        : Serial.PortName;

    [JsonIgnore]
    public string DisplaySummary => TransportKind == SessionTransportKind.NamedPipe
        ? $"管道 · {PipeName}"
        : $"{Serial.PortName} · {Serial.BaudRate}";
}
