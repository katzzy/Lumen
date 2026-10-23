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

    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

    public ITextRangeProvider[] GetSelection()
    {
        var selection = _terminal.GetAutomationSelection();
        return selection is null
            ? Array.Empty<ITextRangeProvider>()
            : new ITextRangeProvider[] { new TerminalTextRangeProvider(this, selection.Value.Start, selection.Value.End) };
    }

    public ITextRangeProvider[] GetVisibleRanges() => new ITextRangeProvider[] { DocumentRange };

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => DocumentRange;

    public ITextRangeProvider RangeFromPoint(Point screenLocation) => DocumentRange;

    internal void SelectRange(int start, int end) => _terminal.SelectAutomationRange(start, end);

    internal void ClearSelection() => _terminal.ClearSelection();

    internal IRawElementProviderSimple GetElementProvider() => new TerminalElementProvider(this);

    private sealed class TerminalElementProvider : IRawElementProviderSimple
    {
        private readonly TerminalAutomationPeer _peer;

        public TerminalElementProvider(TerminalAutomationPeer peer)
        {
            _peer = peer;
        }

        public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider;

        public IRawElementProviderSimple? HostRawElementProvider => null;

        public object? GetPatternProvider(int patternId) =>
            patternId == TextPattern.Pattern.Id ? _peer.GetPattern(PatternInterface.Text) : null;

        public object? GetPropertyValue(int property)
        {
            if (property == AutomationElement.NameProperty.Id) return _peer.GetName();
            if (property == AutomationElement.HelpTextProperty.Id) return _peer.GetHelpText();
            if (property == AutomationElement.ControlTypeProperty.Id) return _peer.GetAutomationControlType();
            if (property == AutomationElement.IsContentElementProperty.Id) return true;
            if (property == AutomationElement.IsControlElementProperty.Id) return true;
            return null;
        }
    }

    internal string GetTextSnapshot()
    {
        long now = Environment.TickCount64;
        if (_textCache is not null && now - _textCacheAt < 250) return _textCache;
        _textCache = _terminal.GetAutomationText();
        _textCacheAt = now;
        return _textCache;
    }
}
