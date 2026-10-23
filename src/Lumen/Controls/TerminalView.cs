using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lumen.Terminal;

namespace Lumen.Controls;

public sealed class TerminalView : FrameworkElement
{
    private const double PadX = 12;
    private const double PadY = 8;

    private readonly Dictionary<uint, SolidColorBrush> _brushes = new();
    private readonly Dictionary<uint, Pen> _pens = new();
    private readonly DispatcherTimer _cursorTimer;
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
    private int _matchIndex = -1;

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

    private SolidColorBrush Brush(uint argb)
    {
        if (_brushes.TryGetValue(argb, out var brush)) return brush;
        var color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        brush = new SolidColorBrush(color);
        brush.Freeze();
        _brushes[argb] = brush;
        return brush;
    }

    private Pen LinePen(uint argb, double thickness = 1)
    {
        uint key = argb ^ (uint)(thickness * 10);
        if (_pens.TryGetValue(key, out var pen)) return pen;
        pen = new Pen(Brush(argb), thickness);
        pen.Freeze();
        _pens[key] = pen;
        return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bg = Palette.DefaultBackground;
        dc.DrawRectangle(Brush(bg), null, new Rect(RenderSize));

        if (_session is null) return;
        var buffer = _session.Buffer;
        if (ActualWidth < 60 || ActualHeight < 40) return;

        int cols = Math.Max(20, (int)((ActualWidth - PadX * 2) / _cellW));
        int rows = Math.Max(3, (int)((ActualHeight - PadY * 2) / _cellH));
        if (buffer.Columns != cols || buffer.Rows != rows)
        {
            buffer.Resize(cols, rows);
            _scrollOffset = Math.Clamp(_scrollOffset, 0, MaxScrollOffset());
        }

        int total = buffer.TotalDisplayLines;
        int maxOffset = Math.Max(0, total - rows);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, maxOffset);
        int startLine = total - rows - _scrollOffset;

        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        if (buffer.TotalPrinted == 0 && _session.IsConnected)
        {
            DrawWaitingHint(dc);
        }
        for (int r = 0; r < rows; r++)
        {
            int lineIndex = startLine + r;
            if (lineIndex < 0) continue;
            var line = buffer.GetDisplayLine(lineIndex);
            if (line is null) continue;
            double y = PadY + r * _cellH;
            DrawLine(dc, line, lineIndex, y, cols, buffer);
        }

        if (maxOffset > 0)
        {
            DrawScrollBar(dc, rows, total, maxOffset);
        }
        dc.Pop();
    }

    private void DrawWaitingHint(DrawingContext dc)
    {
        if (_session is null) return;
        var serial = _session.Profile.Serial;
        var text = $"已连接 {serial.PortName} · {serial.BaudRate}    按 Enter 唤醒设备";
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            _regular,
            Math.Max(11.5, _fontSize - 1.5),
            Brush(WithAlpha(Palette.DefaultForeground, 92)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(
            SnapX(Math.Max(PadX, (ActualWidth - ft.Width) / 2)),
            SnapY(Math.Max(PadY, (ActualHeight - ft.Height) / 2))));
    }

    private double VisibleRows => Math.Max(1, (int)((ActualHeight - PadY * 2) / _cellH));    private int MaxScrollOffset()
    {
        if (_session is null) return 0;
        return Math.Max(0, _session.Buffer.TotalDisplayLines - (int)VisibleRows);
    }

    private void DrawLine(DrawingContext dc, TerminalLine line, int lineIndex, double y, int cols, ScreenBuffer buffer)
    {
        var palette = Palette;
        int cursorLine = buffer.CursorAbsoluteLine;
        bool showCursor = IsFocused && buffer.CursorVisible && _cursorOn && _scrollOffset == 0 && lineIndex == cursorLine;

        SelectionRange(out int selStartLine, out int selStartCol, out int selEndLine, out int selEndCol);

        // backgrounds
        int c = 0;
        while (c < cols)
        {
            var cell = GetCell(line, c);
            uint cellBg = ResolveBg(cell);
            int start = c;
            c++;
            if (cell.Flags.HasFlag(CellFlags.WideTail)) continue;
            while (c < cols)
            {
                var next = GetCell(line, c);
                if (next.Flags.HasFlag(CellFlags.WideTail)) break;
                if (ResolveBg(next) != cellBg) break;
                c++;
            }
            if (cellBg != palette.DefaultBackground)
            {
                dc.DrawRectangle(Brush(cellBg), null, new Rect(PadX + start * _cellW, y, (c - start) * _cellW, _cellH));
            }
        }

        // selection
        if (_hasSelection && lineIndex >= selStartLine && lineIndex <= selEndLine)
        {
            int from = lineIndex == selStartLine ? selStartCol : 0;
            int to = lineIndex == selEndLine ? selEndCol : cols;
            from = Math.Clamp(from, 0, cols);
            to = Math.Clamp(to, 0, cols);
            if (to > from)
            {
                var selBrush = Brush(WithAlpha(palette.SelectionBackground, 110));
                dc.DrawRectangle(selBrush, null, new Rect(PadX + from * _cellW, y, (to - from) * _cellW, _cellH));
            }
        }

        // search hits
        foreach (var match in _matches)
        {
            if (match.Line != lineIndex) continue;
            bool current = _matches.IndexOf(match) == _matchIndex;
            uint color = current ? 0x99F0B429u : 0x55F0B429u;
            dc.DrawRectangle(Brush(color), null, new Rect(PadX + match.Col * _cellW, y, match.Len * _cellW, _cellH));
        }

        // text runs
        if (showCursor && buffer.CursorStyle == 0)
        {
            dc.DrawRectangle(Brush(palette.Cursor), null, new Rect(PadX + buffer.CursorCol * _cellW, y, _cellW, _cellH));
        }

        c = 0;
        while (c < cols)
        {
            var first = GetCell(line, c);
            if (first.Flags.HasFlag(CellFlags.WideTail))
            {
                c++;
                continue;
            }

            bool cursorCell = showCursor && c == buffer.CursorCol;
            var (fg, cellBg) = ResolveColors(first, cursorCell, palette);

            if (IsWide(first))
            {
                DrawCellText(dc, first, c, y, fg, cellBg, cursorCell, palette);
                c++;
                continue;
            }

            if (first.Rune == ' ' && first.Bg == 0 && first.Flags == CellFlags.None && first.Combining is null && !cursorCell)
            {
                c++;
                continue;
            }

            int start = c;
            var sb = new StringBuilder(32);
            CellFlags runFlags = first.Flags;
            uint runFg = fg;
            while (c < cols)
            {
                var cell = GetCell(line, c);
                if (cell.Flags.HasFlag(CellFlags.WideTail) || IsWide(cell) || cell.Combining is not null) break;
                var (cf, cb) = ResolveColors(cell, showCursor && c == buffer.CursorCol, palette);
                if (cf != runFg || cb != cellBg) break;
                if ((cell.Flags & ~(CellFlags.Bold | CellFlags.Dim | CellFlags.Italic | CellFlags.Underline | CellFlags.Strike | CellFlags.Reverse)) != (runFlags & ~(CellFlags.Bold | CellFlags.Dim | CellFlags.Italic | CellFlags.Underline | CellFlags.Strike | CellFlags.Reverse))) break;
                sb.Append(RuneText(cell));
                c++;
            }

            if (sb.Length == 0)
            {
                c = start + 1;
                continue;
            }

            var typeface = PickTypeface(runFlags);
            var brush = Brush(WithAlpha(runFg, (runFlags & CellFlags.Dim) != 0 ? (byte)150 : (byte)255));
            var ft = new FormattedText(
                sb.ToString(),
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                _fontSize,
                brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                TextAlignment = TextAlignment.Left
            };

            double x = SnapX(PadX + start * _cellW);
            double top = SnapY(y + _baselineFromTop - ft.Baseline);
            var guidelines = new GuidelineSet();
            guidelines.GuidelinesX.Add(x);
            guidelines.GuidelinesY.Add(top);
            dc.PushGuidelineSet(guidelines);
            dc.DrawText(ft, new Point(x, top));
            dc.Pop();

            if ((runFlags & CellFlags.Underline) != 0)
            {
                double lineY = SnapY(y + _cellH - 1);
                dc.DrawLine(LinePen(runFg), new Point(x, lineY), new Point(SnapX(x + (c - start) * _cellW), lineY));
            }
            if ((runFlags & CellFlags.Strike) != 0)
            {
                double lineY = SnapY(y + _cellH / 2);
                dc.DrawLine(LinePen(runFg), new Point(x, lineY), new Point(SnapX(x + (c - start) * _cellW), lineY));
            }
        }

        if (showCursor && buffer.CursorStyle != 0)
        {
            double cx = PadX + buffer.CursorCol * _cellW;
            if (buffer.CursorStyle == 1)
            {
                dc.DrawRectangle(Brush(palette.Cursor), null, new Rect(cx, y + _cellH - 2, _cellW, 2));
            }
            else
            {
                dc.DrawRectangle(Brush(palette.Cursor), null, new Rect(cx, y + 1, 2, _cellH - 2));
            }
        }
    }

    private static bool IsWide(Cell cell) => (cell.Flags & CellFlags.Wide) != 0;

    private void DrawCellText(DrawingContext dc, Cell cell, int col, double y, uint fg, uint bg, bool cursorCell, TerminalPalette palette)
    {
        var text = RuneText(cell);
        if (text.Length == 0) return;
        var typeface = PickTypeface(cell.Flags);
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            _fontSize,
            Brush(fg),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double x = SnapX(PadX + col * _cellW);
        double top = SnapY(y + _baselineFromTop - ft.Baseline);
        var guidelines = new GuidelineSet();
        guidelines.GuidelinesX.Add(x);
        guidelines.GuidelinesY.Add(top);
        dc.PushGuidelineSet(guidelines);
        dc.DrawText(ft, new Point(x, top));
        dc.Pop();
    }

    private Cell GetCell(TerminalLine line, int col)
    {
        return col >= 0 && col < line.Cells.Length ? line.Cells[col] : Cell.Empty;
    }

    private Typeface PickTypeface(CellFlags flags)
    {
        bool bold = (flags & CellFlags.Bold) != 0;
        bool italic = (flags & CellFlags.Italic) != 0;
        if (bold && italic) return _boldItalic;
        if (bold) return _bold;
        if (italic) return _italic;
        return _regular;
    }

    private static string RuneText(Cell cell)
    {
        if (cell.Rune == 0) return "";
        string text = cell.Rune <= 0xFFFF ? ((char)cell.Rune).ToString() : char.ConvertFromUtf32(cell.Rune);
        return cell.Combining is null ? text : text + cell.Combining;
    }

    private (uint Fg, uint Bg) ResolveColors(Cell cell, bool cursorCell, TerminalPalette palette)
    {
        uint fg = cell.Fg == 0 ? palette.DefaultForeground : cell.Fg;
        uint bg = cell.Bg == 0 ? palette.DefaultBackground : cell.Bg;

        if ((cell.Flags & CellFlags.Bold) != 0 && cell.Fg != 0)
        {
            for (int i = 0; i < 8; i++)
            {
                if (cell.Fg == palette.Colors[i])
                {
                    fg = palette.Colors[i + 8];
                    break;
                }
            }
        }

        if ((cell.Flags & CellFlags.Reverse) != 0)
        {
            (fg, bg) = (bg, fg);
        }

        if (cursorCell)
        {
            fg = palette.DefaultBackground;
            bg = palette.Cursor;
        }

        if ((cell.Flags & CellFlags.Hidden) != 0) fg = bg;
        return (fg, bg);
    }

    private uint ResolveBg(Cell cell)
    {
        uint bg = cell.Bg;
        if ((cell.Flags & CellFlags.Reverse) != 0 && bg == 0) bg = Palette.DefaultForeground;
        return bg;
    }

    private static uint WithAlpha(uint color, byte alpha) => (color & 0x00FFFFFFu) | ((uint)alpha << 24);

    private void DrawScrollBar(DrawingContext dc, int rows, int total, int maxOffset)
    {
        double trackW = 7;
        double x = ActualWidth - trackW - 5;
        double top = PadY;
        double height = rows * _cellH;
        double thumbH = Math.Max(24, height * rows / total);
        double pos = (height - thumbH) * (_scrollOffset / (double)maxOffset);
        dc.DrawRectangle(Brush(0x22FFFFFF), null, new Rect(x, top, trackW, height));
        dc.DrawRectangle(Brush(0x66FFFFFF), null, new Rect(x, top + (height - thumbH) - pos, trackW, thumbH));
    }

    private void SelectionRange(out int startLine, out int startCol, out int endLine, out int endCol)
    {
        var a = _selAnchor;
        var b = _selEnd;
        if (a.Line < b.Line || (a.Line == b.Line && a.Col <= b.Col))
        {
            startLine = a.Line; startCol = a.Col; endLine = b.Line; endCol = b.Col;
        }
        else
        {
            startLine = b.Line; startCol = b.Col; endLine = a.Line; endCol = a.Col;
        }
    }

    private bool SelectionIsEmpty()
    {
        var a = _selAnchor;
        var b = _selEnd;
        return a.Line == b.Line && a.Col == b.Col;
    }

    private (int Line, int Col) PointToCell(Point point)
    {
        if (_session is null) return (0, 0);
        var buffer = _session.Buffer;
        int rows = (int)VisibleRows;
        int total = buffer.TotalDisplayLines;
        int start = total - rows - _scrollOffset;
        int col = (int)Math.Floor((point.X - PadX) / _cellW);
        int row = (int)Math.Floor((point.Y - PadY) / _cellH);
        col = Math.Clamp(col, 0, buffer.Columns - 1);
        row = Math.Clamp(row, 0, rows - 1);
        return (Math.Max(0, start + row), col);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_session is null) return;
        Focus();
        System.Windows.Input.Keyboard.Focus(this);
        var cell = PointToCell(e.GetPosition(this));
        if (e.ClickCount == 2)
        {
            SelectWord(cell.Line, cell.Col);
            e.Handled = true;
            return;
        }
        if (e.ClickCount >= 3)
        {
            SelectLine(cell.Line);
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _hasSelection)
        {
            _selEnd = cell;
        }
        else
        {
            _selAnchor = cell;
            _selEnd = cell;
            _hasSelection = false;
        }
        _selecting = true;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_selecting || _session is null) return;
        var p = e.GetPosition(this);
        if (p.Y < PadY && _scrollOffset < MaxScrollOffset())
        {
            _scrollOffset = Math.Min(MaxScrollOffset(), _scrollOffset + 2);
        }
        else if (p.Y > ActualHeight - PadY && _scrollOffset > 0)
        {
            _scrollOffset = Math.Max(0, _scrollOffset - 2);
        }
        _selEnd = PointToCell(p);
        _hasSelection = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_selecting) return;
        _selecting = false;
        ReleaseMouseCapture();
        _hasSelection = !SelectionIsEmpty();
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_session is null) return;
        int max = MaxScrollOffset();
        if (max <= 0) return;
        _scrollOffset = Math.Clamp(_scrollOffset + (e.Delta > 0 ? 3 : -3), 0, max);
        InvalidateVisual();
        e.Handled = true;
    }

    public void ScrollToBottom()
    {
        _scrollOffset = 0;
        InvalidateVisual();
    }

    public void ScrollPage(bool up)
    {
        int step = Math.Max(1, (int)VisibleRows - 2);
        _scrollOffset = Math.Clamp(_scrollOffset + (up ? step : -step), 0, MaxScrollOffset());
        InvalidateVisual();
    }

    private void SelectWord(int lineIndex, int col)
    {
        if (_session is null) return;
        var line = _session.Buffer.GetDisplayLine(lineIndex);
        if (line is null || line.Cells.Length == 0) return;
        col = Math.Clamp(col, 0, line.Cells.Length - 1);
        int start = col;
        int end = col;
        while (start > 0 && IsWordCell(line.Cells[start - 1])) start--;
        while (end < line.Cells.Length - 1 && IsWordCell(line.Cells[end + 1])) end++;
        _selAnchor = (lineIndex, start);
        _selEnd = (lineIndex, end + 1);
        _hasSelection = true;
        InvalidateVisual();
    }

    private static bool IsWordCell(Cell cell)
    {
        if (cell.Rune <= 0) return false;
        char ch = cell.Rune <= 0xFFFF ? (char)cell.Rune : ' ';
        return char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or '/' or ':';
    }

    private void SelectLine(int lineIndex)
    {
        if (_session is null) return;
        var line = _session.Buffer.GetDisplayLine(lineIndex);
        if (line is null) return;
        _selAnchor = (lineIndex, 0);
        _selEnd = (lineIndex, line.Cells.Length);
        _hasSelection = true;
        InvalidateVisual();
    }

    public void SelectAll()
    {
        if (_session is null) return;
        var buffer = _session.Buffer;
        _selAnchor = (0, 0);
        _selEnd = (buffer.TotalDisplayLines - 1, buffer.Columns);
        _hasSelection = true;
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        _hasSelection = false;
        _selecting = false;
        _selAnchor = default;
        _selEnd = default;
        InvalidateVisual();
    }

    public string GetSelectedText()
    {
        if (_session is null || !_hasSelection) return "";
        var buffer = _session.Buffer;
        SelectionRange(out int startLine, out int startCol, out int endLine, out int endCol);
        var sb = new StringBuilder();
        for (int lineIndex = startLine; lineIndex <= endLine; lineIndex++)
        {
            var line = buffer.GetDisplayLine(lineIndex);
            if (line is null) continue;
            int from = lineIndex == startLine ? startCol : 0;
            int to = lineIndex == endLine ? endCol : line.Cells.Length;
            from = Math.Clamp(from, 0, line.Cells.Length);
            to = Math.Clamp(to, 0, line.Cells.Length);
            var lineText = new StringBuilder();
            for (int c = from; c < to; c++)
            {
                var cell = line.Cells[c];
                if ((cell.Flags & CellFlags.WideTail) != 0) continue;
                if (cell.Rune == 0) continue;
                lineText.Append(cell.Rune <= 0xFFFF ? (char)cell.Rune : ' ');
                if (cell.Combining is not null) lineText.Append(cell.Combining);
            }
            sb.Append(lineText.ToString().TrimEnd());
            if (lineIndex < endLine && !line.Wrapped) sb.Append("\r\n");
        }
        return sb.ToString();
    }

    public void CopySelection()
    {
        var text = GetSelectedText();
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text);
            CopyPerformed?.Invoke();
        }
        catch
        {
            // clipboard may be locked
        }
    }

    public void ClearScreen()
    {
        _session?.ParseLocal("\x1b[2J\x1b[H");
        ScrollToBottom();
        Cleared?.Invoke();
    }

    public int SetSearch(string query)
    {
        _searchText = query ?? "";
        _matches.Clear();
        _matchIndex = -1;
        if (_session is null || _searchText.Length == 0)
        {
            InvalidateVisual();
            return 0;
        }

        var buffer = _session.Buffer;
        int total = buffer.TotalDisplayLines;
        for (int i = 0; i < total; i++)
        {
            var line = buffer.GetDisplayLine(i);
            if (line is null) continue;
            var text = line.GetText();
            int index = 0;
            while (index < text.Length)
            {
                int found = text.IndexOf(_searchText, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                _matches.Add((i, found, _searchText.Length));
                index = found + Math.Max(1, _searchText.Length);
            }
        }

        InvalidateVisual();
        return _matches.Count;
    }

    public bool FindNext(bool forward)
    {
        if (_matches.Count == 0) return false;
        if (_matchIndex < 0)
        {
            _matchIndex = forward ? 0 : _matches.Count - 1;
        }
        else
        {
            _matchIndex = (_matchIndex + (forward ? 1 : -1) + _matches.Count) % _matches.Count;
        }

        var match = _matches[_matchIndex];
        int rows = (int)VisibleRows;
        int total = _session?.Buffer.TotalDisplayLines ?? 0;
        int start = total - rows - _scrollOffset;
        if (match.Line < start || match.Line >= start + rows)
        {
            _scrollOffset = Math.Clamp(total - rows - match.Line, 0, MaxScrollOffset());
        }
        InvalidateVisual();
        return true;
    }

    public void ClearSearch()
    {
        _searchText = "";
        _matches.Clear();
        _matchIndex = -1;
        InvalidateVisual();
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

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Tab || e.Key == Key.System) return;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_session is null) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

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
