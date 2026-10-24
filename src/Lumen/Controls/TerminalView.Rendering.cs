using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Lumen.Terminal;

namespace Lumen.Controls;

public sealed partial class TerminalView
{
    private readonly Dictionary<int, (long Hash, DrawingGroup Group)> _rowCache = new();
    private int _rowCacheRows = -1;
    private int _rowCacheCols = -1;

    private FormattedText GetFormattedText(string text, CellFlags flags, uint color)
    {
        var key = (text, color, flags & (CellFlags.Bold | CellFlags.Italic));
        if (_formattedCache.TryGet(key, out var cached)) return cached;

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
        _formattedCache.Add(key, formatted);
        return formatted;
    }

    private SolidColorBrush Brush(uint argb)
    {
        if (_brushes.TryGet(argb, out var brush)) return brush;
        var color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        brush = new SolidColorBrush(color);
        brush.Freeze();
        _brushes.Add(argb, brush);
        return brush;
    }

    private Pen LinePen(uint argb, double thickness = 1)
    {
        uint key = argb ^ (uint)(thickness * 10);
        if (_pens.TryGet(key, out var pen)) return pen;
        pen = new Pen(Brush(argb), thickness);
        pen.Freeze();
        _pens.Add(key, pen);
        return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bg = Palette.DefaultBackground;
        dc.DrawRectangle(Brush(bg), null, new Rect(RenderSize));

        if (_session is null) return;
        var buffer = _session.Buffer;
        if (ActualWidth < 60 || ActualHeight < 40) return;

        int requested = buffer.PreferredColumns is 80 or 132 ? buffer.PreferredColumns : 0;
        int cols = _fixedColumns > 0
            ? _fixedColumns
            : requested > 0
                ? requested
                : Math.Max(20, (int)((ActualWidth - PadX * 2) / _cellW));
        int rows = Math.Max(3, (int)((ActualHeight - PadY * 2) / _cellH));
        lock (buffer.SyncRoot)
        {
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
            if (_rowCacheRows != rows || _rowCacheCols != cols)
            {
                _rowCache.Clear();
                _rowCacheRows = rows;
                _rowCacheCols = cols;
            }
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
                long hash = ComputeRowHash(line, lineIndex, r, rows, cols, buffer);
                if (!_rowCache.TryGetValue(r, out var entry) || entry.Hash != hash)
                {
                    var group = new DrawingGroup();
                    using (var rowContext = group.Open())
                    {
                        DrawLine(rowContext, line, lineIndex, y, cols, buffer);
                    }
                    entry = (hash, group);
                    _rowCache[r] = entry;
                }
                dc.DrawDrawing(entry.Group);
            }

            if (maxOffset > 0)
            {
                DrawScrollBar(dc, rows, total, maxOffset);
            }
            if (_inputLocked)
            {
                var text = new FormattedText(
                    "已锁定（只读）",
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    _regular,
                    Math.Max(11, _fontSize - 2),
                    Brush(0xFFFFC857),
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                double width = text.Width + 18;
                double x = Math.Max(PadX, ActualWidth - width - 14);
                dc.DrawRoundedRectangle(
                    Brush(0xDD1A2029), LinePen(0x66F0B429), new Rect(x, PadY + 2, width, text.Height + 8), 6, 6);
                dc.DrawText(text, new Point(x + 9, PadY + 6));
            }
            dc.Pop();
        }
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

    private long ComputeRowHash(TerminalLine line, int lineIndex, int row, int rows, int cols, ScreenBuffer buffer)
    {
        var hash = new HashCode();
        hash.Add(lineIndex);
        hash.Add(row);
        hash.Add(rows);
        hash.Add(cols);
        hash.Add(_cellW);
        hash.Add(_cellH);
        hash.Add(_fontSize);
        hash.Add(_searchVersion);
        hash.Add(_matchIndex);
        hash.Add(_hasSelection);
        if (_hasSelection)
        {
            hash.Add(_selAnchor.Line);
            hash.Add(_selAnchor.Col);
            hash.Add(_selEnd.Line);
            hash.Add(_selEnd.Col);
        }
        hash.Add(IsFocused);
        hash.Add(line.Wrapped);

        bool cursorLine = IsFocused && buffer.CursorVisible && _scrollOffset == 0 && lineIndex == buffer.CursorAbsoluteLine;
        if (cursorLine)
        {
            hash.Add(_cursorOn);
            hash.Add(buffer.CursorCol);
            hash.Add(buffer.CursorStyle);
        }

        foreach (var cell in line.Cells)
        {
            hash.Add(cell.Rune);
            hash.Add(cell.Fg);
            hash.Add(cell.Bg);
            hash.Add((ushort)cell.Flags);
            if (cell.Combining is not null) hash.Add(cell.Combining);
        }
        return hash.ToHashCode();
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

        DrawHighlights(dc, line, y, cols);

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

    private void DrawHighlights(DrawingContext dc, TerminalLine line, double y, int cols)
    {
        var patterns = _session?.Automation.Highlights;
        if (patterns is null || patterns.Count == 0) return;

        var (text, cellAtChar) = line.GetTextWithCells();
        if (text.Length == 0 || cellAtChar.Length == 0) return;

        foreach (var pattern in patterns)
        {
            int index = 0;
            while (index < text.Length)
            {
                System.Text.RegularExpressions.Match match;
                try
                {
                    match = pattern.Regex.Match(text, index);
                }
                catch
                {
                    break;
                }
                if (!match.Success) break;

                int endChar = match.Index + match.Length - 1;
                if (match.Length > 0 && endChar < cellAtChar.Length)
                {
                    int startCell = cellAtChar[match.Index];
                    int endCell = cellAtChar[endChar];
                    int width = endCell < line.Cells.Length &&
                                (line.Cells[endCell].Flags & CellFlags.Wide) != 0 ? 2 : 1;
                    int length = Math.Max(1, endCell - startCell + width);
                    startCell = Math.Clamp(startCell, 0, cols);
                    length = Math.Min(length, cols - startCell);
                    if (length > 0)
                    {
                        dc.DrawRectangle(
                            Brush(WithAlpha(pattern.Color, 70)), null,
                            new Rect(PadX + startCell * _cellW, y, length * _cellW, _cellH));
                    }
                }

                index = match.Index + Math.Max(1, match.Length);
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
