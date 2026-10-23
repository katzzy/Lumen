using System.Windows.Automation.Peers;

namespace Lumen.Controls;

public sealed class TerminalAutomationPeer : FrameworkElementAutomationPeer
{
    private readonly TerminalView _terminal;

    public TerminalAutomationPeer(TerminalView owner) : base(owner)
    {
        _terminal = owner;
    }

    protected override string GetClassNameCore() => nameof(TerminalView);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;

    protected override string GetNameCore() =>
        _terminal.Session?.Profile.Name is { Length: > 0 } name ? name : "终端";

    protected override string GetHelpTextCore() => "串口终端";

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;
}
