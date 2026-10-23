using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Lumen.Terminal;

namespace Lumen.Controls;

public sealed partial class TerminalView
{
    private FormattedText GetFormattedText(string text, CellFlags flags, uint color)
    {
        var key = (text, color, flags & (CellFlags.Bold | CellFlags.Italic));
        if (_formattedCache.TryGetValue(key, out var cached)) return cached;

        if (_formattedCache.Count >= 4096) _formattedCache.Clear();
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            PickTypeface(flags),
            _fontSize,
            Brush(color),
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = TextAlignment.Left
        };
        _formattedCache[key] = formatted;
        return formatted;
    }

    private static void EvictOldest<TKey, TValue>(Dictionary<TKey, TValue> cache, int count)
        where TKey : notnull
    {
        foreach (var key in cache.Keys.Take(count).ToArray()) cache.Remove(key);
    }

    private SolidColorBrush Brush(uint argb)
    {
        if (_brushes.TryGetValue(argb, out var brush)) return brush;
        if (_brushes.Count >= 2048) EvictOldest(_brushes, 1024);
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
        if (_pens.Count >= 512) EvictOldest(_pens, 256);
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
            _lastScrollbackCount = buffer.ScrollbackCount;
            if (buffer.NeedsFullReflow)
            {
                _fullReflowTimer.Stop();
                _fullReflowTimer.Start();
            }
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

    private double VisibleRows => Math.Max(1, (int)((ActualHeight - PadY * 2) / _cellH));

    private int MaxScrollOffset()
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
        if (_matchesByLine.TryGetValue(lineIndex, out var lineMatches))
        {
            foreach (int matchIndex in lineMatches)
            {
                var match = _matches[matchIndex];
                bool current = matchIndex == _matchIndex;
                uint color = current ? 0x99F0B429u : 0x55F0B429u;
                dc.DrawRectangle(Brush(color), null,
                    new Rect(PadX + match.Col * _cellW, y, match.Len * _cellW, _cellH));
            }
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

            var ft = GetFormattedText(sb.ToString(), runFlags, WithAlpha(runFg, (runFlags & CellFlags.Dim) != 0 ? (byte)150 : (byte)255));

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
        var ft = GetFormattedText(text, cell.Flags, fg);
        double x = SnapX(PadX + col * _cellW);
        double top = SnapY(y + _baselineFromTop - ft.Baseline);
        var guidelines = new GuidelineSet();
        guidelines.GuidelinesX.Add(x);
        guidelines.GuidelinesY.Add(top);
        dc.PushGuidelineSet(guidelines);
        dc.DrawText(ft, new Point(x, top));
        dc.Pop();
    }

    private static Cell GetCell(TerminalLine line, int col)
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

    private static (uint Fg, uint Bg) ResolveColors(Cell cell, bool cursorCell, TerminalPalette palette)
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
        if ((cell.Flags & CellFlags.Reverse) == 0)
        {
            return cell.Bg == 0 ? Palette.DefaultBackground : cell.Bg;
        }
        return ResolveColors(cell, false, Palette).Bg;
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

}
