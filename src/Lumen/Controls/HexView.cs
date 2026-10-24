using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lumen.Terminal;

namespace Lumen.Controls;

/// <summary>
/// Read-only live hex dump of the bytes received by a terminal session.
/// </summary>
public sealed class HexView : FrameworkElement
{
    private const double PadX = 12;
    private const double PadY = 8;

    private static readonly Brush BackgroundBrush = Frozen(0xFF0B0E13);
    private static readonly Brush OffsetBrush = Frozen(0xFF6B7488);
    private static readonly Brush HexBrush = Frozen(0xFFD7DEEA);
    private static readonly Brush AsciiBrush = Frozen(0xFF9ECE6A);
    private static readonly Brush DotBrush = Frozen(0xFF505A70);
    private static readonly Brush ScrollTrackBrush = Frozen(0x33242B36);
    private static readonly Brush ScrollThumbBrush = Frozen(0x884C8DFF);

    private TerminalSession? _session;
    private readonly Typeface _typeface = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private double _fontSize = 12.5;
    private double _cellW = 8;
    private double _cellH = 17;
    private int _scrollOffset;
    private int _lastLineCount;
    private bool _renderQueued;

    public HexView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Cursor = Cursors.Arrow;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);

        ContextMenu = BuildContextMenu();
        ContextMenuOpening += (_, _) => UpdateContextMenu();
        RecalculateMetrics();
    }

    public TerminalSession? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value)) return;
            if (_session is not null) _session.Updated -= OnSessionUpdated;
            _session = value;
            _scrollOffset = 0;
            _lastLineCount = value?.Hex.LineCount ?? 0;
            if (_session is not null) _session.Updated += OnSessionUpdated;
            RequestRender();
        }
    }

    public double HexFontSize
    {
        get => _fontSize;
        set
        {
            var clamped = Math.Clamp(value, 8, 32);
            if (Math.Abs(clamped - _fontSize) < 0.01) return;
            _fontSize = clamped;
            RecalculateMetrics();
            InvalidateVisual();
        }
    }

    public void ClearBuffer()
    {
        _session?.Hex.Clear();
        _scrollOffset = 0;
        _lastLineCount = 0;
        InvalidateVisual();
    }

    public void ScrollToBottom()
    {
        _scrollOffset = 0;
        InvalidateVisual();
    }

    private void RecalculateMetrics()
    {
        try
        {
            var ft = new FormattedText(
                "0",
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                _typeface,
                _fontSize,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            _cellW = Math.Max(4, Math.Ceiling(ft.WidthIncludingTrailingWhitespace));
            _cellH = Math.Max(10, Math.Ceiling(_fontSize * 1.42));
        }
        catch
        {
            _cellW = 8;
            _cellH = 17;
        }
    }

    private void OnSessionUpdated()
    {
        int count = _session?.Hex.LineCount ?? 0;
        if (_scrollOffset > 0 && count > _lastLineCount)
        {
            _scrollOffset += count - _lastLineCount;
        }
        _lastLineCount = count;
        RequestRender();
    }

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

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RecalculateMetrics();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));
        if (_session is null || ActualWidth < 80 || ActualHeight < 40) return;

        var buffer = _session.Hex;
        int rows = Math.Max(1, (int)((ActualHeight - PadY * 2) / _cellH));
        int total = buffer.LineCount;
        int maxOffset = Math.Max(0, total - rows);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, maxOffset);
        int first = Math.Max(0, total - rows - _scrollOffset);

        double offsetX = PadX;
        double hexX = offsetX + HexBuffer.BytesPerLine / 2.0 * _cellW + 2 * _cellW;
        double asciiX = hexX + (HexBuffer.BytesPerLine * 3 + 1) * _cellW;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        var bytes = new byte[HexBuffer.BytesPerLine];
        for (int row = 0; row < rows; row++)
        {
            int lineIndex = first + row;
            if (lineIndex >= total) break;
            int count = buffer.CopyLine(lineIndex, bytes);
            if (count <= 0) continue;

            double y = PadY + row * _cellH;
            long offset = buffer.LineOffset(lineIndex);
            var offsetText = new FormattedText(
                $"{offset:X8}",
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                _typeface,
                _fontSize,
                OffsetBrush,
                pixelsPerDip);
            dc.DrawText(offsetText, new Point(offsetX, y));

            var hex = new StringBuilder(HexBuffer.BytesPerLine * 3);
            var ascii = new StringBuilder(HexBuffer.BytesPerLine);
            for (int i = 0; i < HexBuffer.BytesPerLine; i++)
            {
                if (i < count)
                {
                    byte b = bytes[i];
                    hex.Append(b.ToString("X2")).Append(' ');
                    ascii.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
                }
                else
                {
                    hex.Append("   ");
                    ascii.Append(' ');
                }

                if (i == 7) hex.Append(' ');
            }

            dc.DrawText(new FormattedText(
                hex.ToString(),
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                _typeface,
                _fontSize,
                HexBrush,
                pixelsPerDip), new Point(hexX, y));

            dc.DrawText(new FormattedText(
                ascii.ToString(),
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                _typeface,
                _fontSize,
                AsciiBrush,
                pixelsPerDip), new Point(asciiX, y));
        }

        if (maxOffset > 0) DrawScrollBar(dc, rows, total, maxOffset);
        dc.Pop();
    }

    private void DrawScrollBar(DrawingContext dc, int rows, int total, int maxOffset)
    {
        double trackX = ActualWidth - 7;
        double trackTop = PadY;
        double trackHeight = Math.Max(10, ActualHeight - PadY * 2);
        dc.DrawRoundedRectangle(ScrollTrackBrush, null, new Rect(trackX, trackTop, 3, trackHeight), 1.5, 1.5);

        double ratio = (double)rows / total;
        double thumbHeight = Math.Max(24, trackHeight * ratio);
        double position = maxOffset - _scrollOffset;
        double travel = trackHeight - thumbHeight;
        double thumbY = trackTop + (maxOffset == 0 ? 0 : travel * position / maxOffset);
        dc.DrawRoundedRectangle(ScrollThumbBrush, null, new Rect(trackX, thumbY, 3, thumbHeight), 1.5, 1.5);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        int max = MaxScrollOffset();
        if (max <= 0) return;
        _scrollOffset = Math.Clamp(_scrollOffset - Math.Sign(e.Delta) * 3, 0, max);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_session is null) return;

        int max = MaxScrollOffset();
        int page = Math.Max(1, (int)((ActualHeight - PadY * 2) / _cellH) - 1);
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        switch (e.Key)
        {
            case Key.PageUp:
                _scrollOffset = Math.Clamp(_scrollOffset + page, 0, max);
                e.Handled = true;
                break;
            case Key.PageDown:
                _scrollOffset = Math.Clamp(_scrollOffset - page, 0, max);
                e.Handled = true;
                break;
            case Key.Home:
                _scrollOffset = max;
                e.Handled = true;
                break;
            case Key.End:
                _scrollOffset = 0;
                e.Handled = true;
                break;
            case Key.C when ctrl:
                CopyAll();
                e.Handled = true;
                break;
        }

        if (e.Handled) InvalidateVisual();
    }

    private int MaxScrollOffset()
    {
        if (_session is null) return 0;
        int rows = Math.Max(1, (int)((ActualHeight - PadY * 2) / _cellH));
        return Math.Max(0, _session.Hex.LineCount - rows);
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "复制全部", InputGestureText = "Ctrl+C", Name = "CopyAllItem" };
        var bottom = new MenuItem { Header = "回到底部", Name = "BottomItem" };
        var clear = new MenuItem { Header = "清空 Hex 缓冲", Name = "ClearItem" };
        copy.Click += (_, _) => CopyAll();
        bottom.Click += (_, _) => ScrollToBottom();
        clear.Click += (_, _) => ClearBuffer();
        menu.Items.Add(copy);
        menu.Items.Add(bottom);
        menu.Items.Add(new Separator());
        menu.Items.Add(clear);
        return menu;
    }

    private void UpdateContextMenu()
    {
        if (ContextMenu is null) return;
        if (ContextMenu.Items[0] is MenuItem copy) copy.IsEnabled = _session?.Hex.TotalBytes > 0;
        if (ContextMenu.Items[1] is MenuItem bottom) bottom.IsEnabled = _scrollOffset > 0;
        if (ContextMenu.Items[3] is MenuItem clear) clear.IsEnabled = _session?.Hex.TotalBytes > 0;
    }

    private void CopyAll()
    {
        if (_session is null || _session.Hex.TotalBytes == 0) return;
        try
        {
            Clipboard.SetText(GetAllText());
        }
        catch
        {
            // clipboard can be locked by another process
        }
    }

    public string GetAllText()
    {
        if (_session is null) return "";
        var buffer = _session.Hex;
        int total = buffer.LineCount;

        var sb = new StringBuilder(total * 4);
        var bytes = new byte[HexBuffer.BytesPerLine];
        for (int lineIndex = 0; lineIndex < total; lineIndex++)
        {
            int count = buffer.CopyLine(lineIndex, bytes);
            if (count <= 0) continue;
            sb.Append(buffer.LineOffset(lineIndex).ToString("X8")).Append("  ");
            for (int i = 0; i < HexBuffer.BytesPerLine; i++)
            {
                if (i < count) sb.Append(bytes[i].ToString("X2"));
                else sb.Append("  ");
                sb.Append(i == 7 ? "  " : " ");
            }
            sb.Append(" |");
            for (int i = 0; i < count; i++)
            {
                byte b = bytes[i];
                sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
            }
            sb.Append('|').AppendLine();
        }
        return sb.ToString();
    }

    private static Brush Frozen(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24),
            (byte)(argb >> 16),
            (byte)(argb >> 8),
            (byte)argb));
        brush.Freeze();
        return brush;
    }
}
