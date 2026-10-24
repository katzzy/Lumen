using Lumen.Models;
using Lumen.Serial;
using Lumen.Services;
using Lumen.Terminal;
using System.IO;
using System.Text;
using System.Windows;

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

var ringBuffer = new ScreenBuffer(40, 3) { ScrollbackLimit = 10 };
for (int i = 0; i < 25; i++)
{
    ringBuffer.PrintString($"ring{i:D2}");
    ringBuffer.CarriageReturn();
    ringBuffer.LineFeed();
}
Check("scrollback ring cap", ringBuffer.ScrollbackCount == 10 && ringBuffer.ScrollbackLimit == 10,
    $"count={ringBuffer.ScrollbackCount} cap={ringBuffer.ScrollbackLimit}");

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

var candidate = Lumen.Controls.ImeCandidatePositioner.GetCandidatePoint(new Point(10, 20), 18);
Check("ime candidate below caret", candidate == new Point(10, 38), candidate.ToString());

string logPath = Path.Combine(Environment.CurrentDirectory, $"lumen-test-{Guid.NewGuid():N}.log");
try
{
    using var log = new LogService();
    log.Start(logPath, timestamp: false, Encoding.UTF8);
    log.Write("received line\r\n");
    log.Write("\x1b[31mansi red\x1b[0m\r\n");
    log.WriteTx("show version\r");
    log.Stop();
    string logText = File.ReadAllText(logPath);
    Check("tx log", logText.Contains("received line") && logText.Contains("[TX] show version"), logText);
    Check("ansi stripped", logText.Contains("ansi red") && !logText.Contains('\x1b'), logText);
}
finally
{
    if (File.Exists(logPath)) File.Delete(logPath);
}

// Arbitrary byte input
Check("hex bytes", ByteSequenceParser.TryParse("1B 5B 32 4A", ByteInputMode.Hex, Encoding.UTF8, out var hexBytes, out _)
    && hexBytes.SequenceEqual(new byte[] { 0x1B, 0x5B, 0x32, 0x4A }), string.Join(",", hexBytes));
Check("hex bytes continuous and prefixed",
    ByteSequenceParser.TryParse("0x1B5B", ByteInputMode.Hex, Encoding.UTF8, out var hexBytes2, out _)
    && hexBytes2.SequenceEqual(new byte[] { 0x1B, 0x5B }), string.Join(",", hexBytes2));
Check("escape bytes", ByteSequenceParser.TryParse("\\x1b[2J\\r\\n", ByteInputMode.Escape, Encoding.UTF8, out var escBytes, out _)
    && escBytes.SequenceEqual(new byte[] { 0x1B, 0x5B, 0x32, 0x4A, 0x0D, 0x0A }), string.Join(",", escBytes));
Check("control caret", ByteSequenceParser.TryParse("^C", ByteInputMode.Escape, Encoding.UTF8, out var ctrlBytes, out _)
    && ctrlBytes.SequenceEqual(new byte[] { 0x03 }), string.Join(",", ctrlBytes));
Check("unicode escape", ByteSequenceParser.TryParse("\\u4E2D", ByteInputMode.Escape, Encoding.UTF8, out var uBytes, out _)
    && uBytes.SequenceEqual(new byte[] { 0xE4, 0xB8, 0xAD }), string.Join(",", uBytes));
Check("bad escape rejected", !ByteSequenceParser.TryParse("\\q", ByteInputMode.Escape, Encoding.UTF8, out _, out _));
Check("text mode", ByteSequenceParser.TryParse("AB", ByteInputMode.Text, Encoding.ASCII, out var textBytes, out _)
    && textBytes.SequenceEqual(new byte[] { 0x41, 0x42 }), string.Join(",", textBytes));

// Break duration configuration
var serialConfig = new SerialConfig();
Check("break default", serialConfig.EffectiveBreakDurationMs == 250, serialConfig.EffectiveBreakDurationMs.ToString());
serialConfig.BreakDurationMs = 9999;
Check("break clamp high", serialConfig.EffectiveBreakDurationMs == 5000, serialConfig.EffectiveBreakDurationMs.ToString());
serialConfig.BreakDurationMs = 1;
Check("break clamp low", serialConfig.EffectiveBreakDurationMs == 10, serialConfig.EffectiveBreakDurationMs.ToString());

// Hex buffer
var hexBuffer = new HexBuffer(1024);
var payload = new byte[1200];
for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
hexBuffer.Append(payload);
Check("hex buffer total", hexBuffer.TotalBytes == 1200, hexBuffer.TotalBytes.ToString());
Check("hex buffer cap", hexBuffer.Count <= 1024 && hexBuffer.Count % HexBuffer.BytesPerLine == 0,
    $"count={hexBuffer.Count}");
var firstLine = new byte[HexBuffer.BytesPerLine];
int copied = hexBuffer.CopyLine(0, firstLine);
Check("hex buffer trimmed offset", copied == 16 && firstLine[0] == payload[(int)hexBuffer.BaseOffset],
    $"copied={copied} first={firstLine[0]} base={hexBuffer.BaseOffset}");
Check("hex buffer line offsets", hexBuffer.LineOffset(1) - hexBuffer.LineOffset(0) == HexBuffer.BytesPerLine);

// Raw log preserves escapes and control characters
string rawLogPath = Path.Combine(Environment.CurrentDirectory, $"lumen-raw-{Guid.NewGuid():N}.log");
try
{
    using var rawLog = new LogService();
    rawLog.Start(rawLogPath, timestamp: false, Encoding.UTF8, raw: true);
    Check("raw log active", rawLog.RawEnabled);
    rawLog.Write("\x1b[31mraw red\x1b[0m\r\n");
    rawLog.Stop();
    string rawText = File.ReadAllText(rawLogPath);
    Check("raw log keeps ansi", rawText.Contains('\x1b') && rawText.Contains("raw red"), rawText);
    Check("raw log keeps crlf", rawText.Contains("\r\n"), rawText.Replace("\r", "<CR>").Replace("\n", "<LF>"));
}
finally
{
    if (File.Exists(rawLogPath)) File.Delete(rawLogPath);
}

// XAML smoke test: load every overlay view so broken resources or bindings fail here.
Exception? uiError = null;
var uiThread = new Thread(() =>
{
    try
    {
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Lumen;component/Themes/Colors.xaml")
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Lumen;component/Themes/Controls.xaml")
        });

        _ = new Lumen.Views.SendBytesView(Encoding.UTF8, connected: true);
        _ = new Lumen.Views.SettingsView(new AppSettings());
        _ = new Lumen.Views.SessionEditorView(SessionProfile.CreateDefault(), new List<SerialPortInfo>(), isNew: true);
        _ = new Lumen.Controls.HexView();
        app.Shutdown();
    }
    catch (Exception ex)
    {
        uiError = ex;
    }
});
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.Start();
if (!uiThread.Join(TimeSpan.FromSeconds(20)))
{
    Check("xaml views load", false, "UI thread timed out");
}
else
{
    Check("xaml views load", uiError is null, uiError?.ToString() ?? "");
}

Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
return failures == 0 ? 0 : 1;
