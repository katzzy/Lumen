using Lumen.Serial;
using Lumen.Terminal;

int failures = 0;

void Check(string name, bool condition, string detail = "")
{
    if (condition)
    {
        Console.WriteLine($"OK   {name}");
    }
    else
    {
        failures++;
        Console.WriteLine($"FAIL {name} {detail}");
    }
}

static TerminalLine Line(string text, int width, bool wrapped = false)
{
    var line = new TerminalLine(width);
    for (int i = 0; i < text.Length && i < width; i++)
    {
        line.Cells[i] = new Cell { Rune = text[i] };
    }
    line.Wrapped = wrapped;
    return line;
}

// Reflow
var wrapped = new[]
{
    Line("abcdefghij", 10, wrapped: true),
    Line("klmnopqrst", 10)
};
var reflow = TerminalReflow.Reflow(wrapped, 4, 1, 2);
Check("reflow line count", reflow.Lines.Count == 5, $"count={reflow.Lines.Count}");
Check("reflow first line", reflow.Lines[0].GetText() == "abcd", reflow.Lines[0].GetText());
Check("reflow wrapped flags", reflow.Lines[0].Wrapped && !reflow.Lines[^1].Wrapped);
Check("reflow cursor", reflow.CursorLine == 3 && reflow.CursorColumn == 0,
    $"cursor={reflow.CursorLine},{reflow.CursorColumn}");

var wideLine = new TerminalLine(4);
wideLine.Cells[0] = new Cell { Rune = 'a' };
wideLine.Cells[1] = new Cell { Rune = 'a' };
wideLine.Cells[2] = new Cell { Rune = 'a' };
wideLine.Cells[3] = new Cell { Rune = '中', Flags = CellFlags.Wide };
var wide = TerminalReflow.Reflow(new[] { wideLine }, 4, 0, 3);
Check("wide glyph preserved", wide.Lines[0].GetText() == "aaa" && wide.Lines[1].GetText().StartsWith("中"),
    string.Join("|", wide.Lines.Select(l => l.GetText())));

var prompt = TerminalReflow.Reflow(new[] { Line("Switch#", 10) }, 5, 0, 7);
Check("prompt cursor after #", prompt.CursorLine == 1 && prompt.CursorColumn == 2,
    $"cursor={prompt.CursorLine},{prompt.CursorColumn}");
var boundary = TerminalReflow.Reflow(new[] { Line("abcde", 5) }, 5, 0, 5);
Check("boundary cursor on last cell", boundary.CursorLine == 0 && boundary.CursorColumn == 4,
    $"cursor={boundary.CursorLine},{boundary.CursorColumn}");

// Screen resizing
var buffer = new ScreenBuffer(10, 3);
buffer.PrintString("abcdefghij");
buffer.CarriageReturn();
buffer.LineFeed();
buffer.PrintString("klmno");
buffer.Resize(5, 3);
string text = string.Join("|", Enumerable.Range(0, buffer.TotalDisplayLines)
    .Select(i => buffer.GetDisplayLine(i)?.GetText() ?? ""));
Check("width resize preserves text",
    text.Contains("abcde") && text.Contains("fghij") && text.Contains("klmno"), text);

var heightBuffer = new ScreenBuffer(20, 5);
for (int i = 0; i < 4; i++)
{
    heightBuffer.PrintString($"line{i}");
    heightBuffer.CarriageReturn();
    heightBuffer.LineFeed();
}
heightBuffer.Resize(20, 2);
string heightText = string.Join("|", Enumerable.Range(0, heightBuffer.TotalDisplayLines)
    .Select(i => heightBuffer.GetDisplayLine(i)?.GetText() ?? ""));
Check("height resize preserves text",
    heightText.Contains("line0") && heightText.Contains("line3") && heightBuffer.Rows == 2, heightText);

var fullBuffer = new ScreenBuffer(100, 5);
for (int i = 0; i < 700; i++)
{
    fullBuffer.PrintString($"line {i:D4} " + new string('x', 30));
    fullBuffer.CarriageReturn();
    fullBuffer.LineFeed();
}
fullBuffer.Resize(60, 5);
Check("full reflow pending", fullBuffer.NeedsFullReflow);
fullBuffer.ReflowAll();
Check("full reflow rewraps", !fullBuffer.NeedsFullReflow &&
    Enumerable.Range(0, fullBuffer.TotalDisplayLines)
        .All(i => fullBuffer.GetDisplayLine(i)?.Cells.Length == 60));

// Text / unicode
var emojiLine = new TerminalLine(8);
emojiLine.Cells[0] = new Cell { Rune = 'A' };
emojiLine.Cells[1] = new Cell { Rune = 0x1F600, Flags = CellFlags.Wide };
emojiLine.Cells[2] = new Cell { Flags = CellFlags.WideTail };
emojiLine.Cells[3] = new Cell { Rune = 'B' };
Check("emoji text", emojiLine.GetText() == "A😀B", emojiLine.GetText());
var (emojiText, emojiMap) = emojiLine.GetTextWithCells();
Check("emoji text map", emojiText == "A😀B" && emojiMap.SequenceEqual(new[] { 0, 1, 1, 3 }),
    string.Join(",", emojiMap));

var cjkLine = new TerminalLine(4);
cjkLine.Cells[0] = new Cell { Rune = '中', Flags = CellFlags.Wide };
cjkLine.Cells[1] = new Cell { Flags = CellFlags.WideTail };
cjkLine.Cells[2] = new Cell { Rune = 'A' };
var (cjkText, cjkMap) = cjkLine.GetTextWithCells();
Check("cjk text map", cjkText == "中A" && cjkMap.SequenceEqual(new[] { 0, 2 }),
    string.Join(",", cjkMap));

var bluetooth = new SerialPortInfo("COM5", "蓝牙链接上的标准串行", "Standard Serial over Bluetooth link");
Check("bluetooth badge", bluetooth.IsBluetooth && bluetooth.KindLabel == "BT");

Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
return failures == 0 ? 0 : 1;
