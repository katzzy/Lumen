namespace Lumen.Terminal;

public enum MouseTrackingMode
{
    None = 0,
    /// <summary>Legacy X10 mode: button press reports only.</summary>
    X10 = 1,
    /// <summary>DECSET 1000: VT200 normal tracking, press/release only.</summary>
    Normal = 2,
    /// <summary>DECSET 1002: press/release plus drag motion.</summary>
    Button = 3,
    /// <summary>DECSET 1003: all mouse motion is reported.</summary>
    AnyEvent = 4
}

public enum MouseEncodingMode
{
    Default = 0,
    Utf8 = 1,
    Sgr = 2
}
