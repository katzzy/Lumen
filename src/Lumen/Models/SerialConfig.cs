namespace Lumen.Models;

using System.Text.Json.Serialization;

public enum SerialParity
{
    None = 0,
    Odd = 1,
    Even = 2,
    Mark = 3,
    Space = 4
}

public enum SerialStopBits
{
    One = 0,
    OnePointFive = 1,
    Two = 2
}

public enum SerialFlowControl
{
    None = 0,
    RtsCts = 1,
    XonXoff = 2
}

public sealed class SerialConfig
{
    public string PortName { get; set; } = "COM1";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public SerialParity Parity { get; set; } = SerialParity.None;
    public SerialStopBits StopBits { get; set; } = SerialStopBits.One;
    public SerialFlowControl FlowControl { get; set; } = SerialFlowControl.None;
    public bool Dtr { get; set; } = true;
    public bool Rts { get; set; } = true;

    /// <summary>Duration of the BREAK signal pulse, in milliseconds.</summary>
    public int BreakDurationMs { get; set; } = 250;

    public SerialConfig Clone() => (SerialConfig)MemberwiseClone();

    /// <summary>Break duration clamped to the range accepted by the UI.</summary>
    [JsonIgnore]
    public int EffectiveBreakDurationMs => Math.Clamp(BreakDurationMs, 10, 5000);

    [JsonIgnore]
    public string Summary => $"{PortName} · {BaudRate} · {DataBits}{ParityLetter}{StopBitsLetter}";

    private string ParityLetter => Parity switch
    {
        SerialParity.Odd => "O",
        SerialParity.Even => "E",
        SerialParity.Mark => "M",
        SerialParity.Space => "S",
        _ => "N"
    };

    private string StopBitsLetter => StopBits switch
    {
        SerialStopBits.Two => "2",
        SerialStopBits.OnePointFive => "1.5",
        _ => "1"
    };
}
