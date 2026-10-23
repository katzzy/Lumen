using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace Lumen.Controls;

public sealed class TerminalAutomationPeer : FrameworkElementAutomationPeer, ITextProvider
{
    private readonly TerminalView _terminal;
    private string? _textCache;
    private long _textCacheAt;

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

    public override object GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Text ? this : base.GetPattern(patternInterface);

    public ITextRangeProvider DocumentRange =>
        new TerminalTextRangeProvider(this, 0, GetTextSnapshot().Length);

    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.None;

    public ITextRangeProvider[] GetSelection() => Array.Empty<ITextRangeProvider>();

    public ITextRangeProvider[] GetVisibleRanges() => new ITextRangeProvider[] { DocumentRange };

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => DocumentRange;

    public ITextRangeProvider RangeFromPoint(Point screenLocation) => DocumentRange;

    internal string GetTextSnapshot()
    {
        long now = Environment.TickCount64;
        if (_textCache is not null && now - _textCacheAt < 250) return _textCache;
        _textCache = _terminal.GetAutomationText();
        _textCacheAt = now;
        return _textCache;
    }
}
