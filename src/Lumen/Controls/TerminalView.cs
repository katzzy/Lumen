using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
using Lumen.Terminal;

namespace Lumen.Controls;

public sealed partial class TerminalView : FrameworkElement
{
    private const double PadX = 12;
    private const double PadY = 8;

    private readonly LruCache<uint, SolidColorBrush> _brushes = new(2048);
    private readonly LruCache<uint, Pen> _pens = new(512);
    private readonly LruCache<(string Text, uint Color, CellFlags Flags), FormattedText> _formattedCache = new(4096);
    private readonly DispatcherTimer _cursorTimer;
    private readonly DispatcherTimer _fullReflowTimer;
    private Typeface _regular = null!;
    private Typeface _bold = null!;
    private Typeface _italic = null!;
    private Typeface _boldItalic = null!;

    private TerminalSession? _session;
    private double _fontSize = 14;
    private string _fontFamily = "Cascadia Mono";
    private double _cellW = 8;
    private double _cellH = 18;
    private double _baselineFromTop = 14;
    private double _dpiX = 1;
    private double _dpiY = 1;
    private int _scrollOffset;
    private int _lastScrollbackCount;
    private bool _selecting;
    private bool _cursorOn = true;
    private bool _renderQueued;
    private (int Line, int Col) _selAnchor;
    private (int Line, int Col) _selEnd;
    private bool _hasSelection;
    private string _searchText = "";
    private readonly List<(int Line, int Col, int Len)> _matches = new();
    private readonly Dictionary<int, List<int>> _matchesByLine = new();
    private int _matchIndex = -1;
    private int _searchVersion;
    private int _fixedColumns;
    private bool _mousePressed;
    private int _mouseButton = -1;
    private long _lastMouseReportAt;
    private int _lastMouseReportCol = -1;
    private int _lastMouseReportRow = -1;
    private int _lastMouseReportButton = -1;

    public TerminalView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);

        _regular = CreateTypeface(FontStyles.Normal, FontWeights.Normal);
        _bold = CreateTypeface(FontStyles.Normal, FontWeights.Bold);
        _italic = CreateTypeface(FontStyles.Italic, FontWeights.Normal);
        _boldItalic = CreateTypeface(FontStyles.Italic, FontWeights.Bold);

        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _cursorTimer.Tick += (_, _) =>
        {
            _cursorOn = !_cursorOn;
            InvalidateVisual();
        };

        _fullReflowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _fullReflowTimer.Tick += async (_, _) =>
        {
            _fullReflowTimer.Stop();
            if (_session is null) return;
            var buffer = _session.Buffer;
            var job = buffer.CreateReflowJob();
            if (job is null) return;

            try
            {
                var result = await Task.Run(() =>
                    TerminalReflow.Reflow(job.Source, job.Columns, job.CursorLine, job.CursorColumn));
                if (buffer.IsReflowJobValid(job))
                {
                    buffer.ApplyReflow(job, result);
                    _lastScrollbackCount = buffer.ScrollbackCount;
                    InvalidateVisual();
                }
                else if (buffer.NeedsFullReflow)
                {
                    _fullReflowTimer.Start();
                }
            }
            catch (Exception ex)
            {
                App.LogError("full reflow", ex);
            }
        };

        TextCompositionManager.AddPreviewTextInputStartHandler(this, OnImeComposition);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(this, OnImeComposition);

        ContextMenu = BuildContextMenu();
        ContextMenuOpening += (_, _) => UpdateContextMenu();
        RecalculateFont();
    }

    public TerminalSession? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value)) return;
            if (_session is not null)
            {
                _session.Updated -= OnSessionUpdated;
                _session.StateChanged -= OnSessionStateChanged;
            }
            _session = value;
            _scrollOffset = 0;
            _lastScrollbackCount = value?.Buffer.ScrollbackCount ?? 0;
            _fullReflowTimer.Stop();
            ClearSelection();
            ClearSearch();
            if (_session is not null)
            {
                _session.Updated += OnSessionUpdated;
                _session.StateChanged += OnSessionStateChanged;
                _lastScrollbackCount = _session.Buffer.ScrollbackCount;
            }
            RequestRender();
        }
    }

    public TerminalPalette Palette { get; set; } = TerminalPalette.Dark;

    protected override AutomationPeer OnCreateAutomationPeer() => new TerminalAutomationPeer(this);

    /// <summary>
    /// Text exposed to UI Automation / screen readers. Limited to the most recent
    /// 2000 display lines to avoid walking an unbounded scrollback on every query.
    /// </summary>
    public string GetAutomationText()
    {
        if (_session is null) return "";
        var buffer = _session.Buffer;
        lock (buffer.SyncRoot)
        {
            int total = buffer.TotalDisplayLines;
            int start = Math.Max(0, total - 2000);
            var sb = new StringBuilder();
            for (int i = start; i < total; i++)
            {
                var line = buffer.GetDisplayLine(i);
                if (line is null) continue;
                sb.Append(line.GetText()).Append('\n');
            }
            return sb.ToString();
        }
    }

    public double TerminalFontSize
    {
        get => _fontSize;
        set
        {
            var clamped = Math.Clamp(value, 8, 40);
            if (Math.Abs(clamped - _fontSize) < 0.01) return;
            _fontSize = clamped;
            RecalculateFont();
            InvalidateVisual();
        }
    }

    public string TerminalFontFamily
    {
        get => _fontFamily;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _fontFamily) return;
            _fontFamily = value;
            RecalculateFont();
            InvalidateVisual();
        }
    }

    /// <summary>0 follows the window width; 80 or 132 pins the terminal columns.</summary>
    public int FixedColumns
    {
        get => _fixedColumns;
        set
        {
            int columns = value is 80 or 132 ? value : 0;
            if (columns == _fixedColumns) return;
            _fixedColumns = columns;
            RequestRender();
        }
    }

    public bool HasSelection => _hasSelection && !SelectionIsEmpty();

    public event Action<string>? PasteRequested;
    public event Action? FindRequested;
    public event Action? CopyPerformed;
    public event Action? Cleared;

    private Typeface CreateTypeface(FontStyle style, FontWeight weight)
    {
        return new Typeface(ResolveMonoFamily(), style, weight, FontStretches.Normal);
    }

    private FontFamily ResolveMonoFamily()
    {
        var installed = new HashSet<string>(
            Fonts.SystemFontFamilies.Select(f => f.Source),
            StringComparer.OrdinalIgnoreCase);
        if (installed.Contains(_fontFamily)) return new FontFamily(_fontFamily);
        if (installed.Contains("Consolas")) return new FontFamily("Consolas");
        if (installed.Contains("Courier New")) return new FontFamily("Courier New");
        return new FontFamily("Consolas");
    }

    private void RecalculateFont()
    {
        try
        {
            var family = ResolveMonoFamily();
            _regular = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            _bold = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            _italic = new Typeface(family, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
            _boldItalic = new Typeface(family, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);

            var ft = new FormattedText(
                "M",
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                _regular,
                _fontSize,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var dpi = VisualTreeHelper.GetDpi(this);
            _dpiX = dpi.DpiScaleX;
            _dpiY = dpi.DpiScaleY;
            _cellW = Math.Max(4, SnapUpX(ft.WidthIncludingTrailingWhitespace));
            _cellH = Math.Max(10, SnapUpY(_fontSize * 1.38));
            double baseline = _fontSize * 0.82;
            if (_regular.TryGetGlyphTypeface(out var glyph))
            {
                baseline = glyph.Baseline * _fontSize;
                double em = glyph.Height * _fontSize;
                _baselineFromTop = SnapY((_cellH - em) / 2 + baseline);
            }
            else
            {
                _baselineFromTop = SnapY(_fontSize);
            }
            _brushCacheClear();
            _formattedCache.Clear();
        }
        catch
        {
            _cellW = 8;
            _cellH = 18;
        }
    }

    private void _brushCacheClear()
    {
        _brushes.Clear();
        _pens.Clear();
    }

    private double SnapX(double value) => _dpiX <= 0 ? value : Math.Round(value * _dpiX) / _dpiX;
    private double SnapY(double value) => _dpiY <= 0 ? value : Math.Round(value * _dpiY) / _dpiY;
    private double SnapUpX(double value) => _dpiX <= 0 ? value : Math.Ceiling(value * _dpiX) / _dpiX;
    private double SnapUpY(double value) => _dpiY <= 0 ? value : Math.Ceiling(value * _dpiY) / _dpiY;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RecalculateFont();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    private void OnSessionUpdated()
    {
        if (_session is null) return;
        int count = _session.Buffer.ScrollbackCount;
        if (_scrollOffset > 0 && count > _lastScrollbackCount)
        {
            _scrollOffset += count - _lastScrollbackCount;
        }
        _lastScrollbackCount = count;
        RequestRender();
    }

    private void OnSessionStateChanged() => RequestRender();

    private void RequestRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _renderQueued = false;
            InvalidateVisual();
        }));
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _cursorTimer.Start();
        _cursorOn = true;
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _cursorTimer.Stop();
        _cursorOn = false;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_session is null) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (e.Key == Key.F5 && !shift)
        {
            _ = _session.SendBreakAsync();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Cancel || (e.Key == Key.Pause && ctrl))
        {
            _ = _session.SendBreakAsync();
            e.Handled = true;
            return;
        }

        if (ctrl && shift && e.Key == Key.C)
        {
            CopySelection();
            e.Handled = true;
            return;
        }
        if (ctrl && shift && e.Key == Key.V)
        {
            RequestPaste();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.Insert)
        {
            CopySelection();
            e.Handled = true;
            return;
        }
        if (shift && e.Key == Key.Insert)
        {
            RequestPaste();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.C)
        {
            if (HasSelection)
            {
                CopySelection();
                e.Handled = true;
                return;
            }
            _session.SendBytes(new byte[] { 0x03 });
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.V)
        {
            RequestPaste();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.F)
        {
            FindRequested?.Invoke();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.A && shift)
        {
            SelectAll();
            e.Handled = true;
            return;
        }
        if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            TerminalFontSize += 1;
            e.Handled = true;
            return;
        }
        if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            TerminalFontSize -= 1;
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.D0)
        {
            TerminalFontSize = 14;
            e.Handled = true;
            return;
        }
        if (shift && e.Key == Key.PageUp)
        {
            ScrollPage(true);
            e.Handled = true;
            return;
        }
        if (shift && e.Key == Key.PageDown)
        {
            ScrollPage(false);
            e.Handled = true;
            return;
        }
        if (shift && e.Key == Key.Home)
        {
            _scrollOffset = MaxScrollOffset();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (shift && e.Key == Key.End)
        {
            ScrollToBottom();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _session.IsPasting)
        {
            _session.CancelPaste();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _hasSelection)
        {
            ClearSelection();
            e.Handled = true;
            return;
        }

        var bytes = TerminalKeyEncoder.Encode(e.Key, Keyboard.Modifiers, _session.Buffer);
        if (bytes is not null)
        {
            _session.SendBytes(bytes);
            e.Handled = true;
        }
    }

    private void OnImeComposition(object sender, TextCompositionEventArgs e) =>
        UpdateImeCandidatePosition();

    private void UpdateImeCandidatePosition()
    {
        if (_session is null || !IsVisible) return;
        var window = Window.GetWindow(this);
        if (window is null) return;

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var buffer = _session.Buffer;
        int row = Math.Clamp(buffer.CursorRow, 0, Math.Max(0, buffer.Rows - 1));
        int col = Math.Clamp(buffer.CursorCol, 0, Math.Max(0, buffer.Columns - 1));
        var caret = ImeCandidatePositioner.GetCandidatePoint(
            new Point(PadX + col * _cellW, PadY + row * _cellH),
            _cellH);
        try
        {
            ImeCandidatePositioner.Set(hwnd, PointToScreen(caret));
        }
        catch
        {
            // The visual may not be connected to a presentation source yet.
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (_session is null || string.IsNullOrEmpty(e.Text)) return;
        _session.SendText(e.Text);
        e.Handled = true;
    }

    private void RequestPaste()
    {
        string text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        }
        catch
        {
            return;
        }
        if (text.Length > 0) PasteRequested?.Invoke(text);
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "复制", InputGestureText = "Ctrl+Shift+C", Name = "CopyItem" });
        menu.Items.Add(new MenuItem { Header = "粘贴", InputGestureText = "Ctrl+V", Name = "PasteItem" });
        menu.Items.Add(new MenuItem { Header = "全选", InputGestureText = "Ctrl+Shift+A", Name = "SelectAllItem" });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "查找", InputGestureText = "Ctrl+F", Name = "FindItem" });
        menu.Items.Add(new MenuItem { Header = "清屏", Name = "ClearItem" });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "回到底部", Name = "BottomItem" });

        ((MenuItem)menu.Items[0]).Click += (_, _) => CopySelection();
        ((MenuItem)menu.Items[1]).Click += (_, _) => RequestPaste();
        ((MenuItem)menu.Items[2]).Click += (_, _) => SelectAll();
        ((MenuItem)menu.Items[4]).Click += (_, _) => FindRequested?.Invoke();
        ((MenuItem)menu.Items[5]).Click += (_, _) => ClearScreen();
        ((MenuItem)menu.Items[7]).Click += (_, _) => ScrollToBottom();
        return menu;
    }

    private void UpdateContextMenu()
    {
        if (ContextMenu is null) return;
        if (ContextMenu.Items[0] is MenuItem copy) copy.IsEnabled = HasSelection;
        if (ContextMenu.Items[7] is MenuItem bottom) bottom.IsEnabled = _scrollOffset > 0;
        if (ContextMenu.Items[4] is MenuItem find) find.IsEnabled = _session is not null;
    }
}
