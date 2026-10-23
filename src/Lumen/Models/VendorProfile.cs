namespace Lumen.Models;

public sealed class VendorProfile
{
    public string Id { get; init; } = "generic";
    public string Name { get; init; } = "通用";
    public string Family { get; init; } = "";
    public int DefaultBaud { get; init; } = 9600;
    public string Encoding { get; init; } = "utf-8";
    public string? PagingCommand { get; init; }
    public string? PromptPattern { get; init; }
    public IReadOnlyList<string> DetectPatterns { get; init; } = Array.Empty<string>();
    public string Notes { get; init; } = "";
    public IReadOnlyList<QuickCommand> QuickCommands { get; init; } = Array.Empty<QuickCommand>();

    public string DisplayName => string.IsNullOrWhiteSpace(Family) ? Name : $"{Name} {Family}";

    public override string ToString() => DisplayName;
}
