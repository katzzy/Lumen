namespace Lumen.Models;

using System.Text.Json.Serialization;

public sealed class AppSettings
{
    public double FontSize { get; set; } = 14;
    public string FontFamily { get; set; } = "Consolas";
    public int ScrollbackLines { get; set; } = 10000;
    public string LogRoot { get; set; } = "";
    public bool AutoLog { get; set; } = true;
    public bool LogTimestamps { get; set; }
    public bool ConfirmDangerousPaste { get; set; } = true;
    public int DefaultPasteDelayMs { get; set; } = 100;

    [JsonIgnore]
    public string EffectiveLogRoot => string.IsNullOrWhiteSpace(LogRoot)
        ? Services.AppPaths.DefaultLogRoot
        : LogRoot;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
