using Lumen.Models;
using Lumen.Serial;
using Lumen.Services;
using Lumen.Terminal;
using Lumen.Transfer;
using System.IO;
using System.IO.Pipes;
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

// XMODEM / XMODEM-1K / YMODEM loopback round trips
void TransferRoundTrip(TransferProtocol protocol, int size, string name)
{
    string source = Path.Combine(Environment.CurrentDirectory, $"lumen-send-{Guid.NewGuid():N}.bin");
    string target = Path.Combine(Environment.CurrentDirectory, $"lumen-recv-{Guid.NewGuid():N}.bin");
    try
    {
        var data = new byte[size];
        new Random(1234567 + size).NextBytes(data);
        File.WriteAllBytes(source, data);

        var sender = new LoopChannel();
        var receiver = new LoopChannel();
        sender.Connect(receiver);
        receiver.Connect(sender);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var send = FileTransferEngine.RunAsync(
            new TransferOptions(protocol, TransferDirection.Send, source), sender, null, cts.Token);
        var receive = FileTransferEngine.RunAsync(
            new TransferOptions(protocol, TransferDirection.Receive, target), receiver, null, cts.Token);
        Task.WhenAll(send, receive).GetAwaiter().GetResult();
        bool equal = File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(data);
        Check($"{name} round trip", equal,
            equal ? "" : $"expected={size} actual={(File.Exists(target) ? new FileInfo(target).Length : -1)}");
    }
    catch (Exception ex)
    {
        Check($"{name} round trip", false, ex.Message);
    }
    finally
    {
        if (File.Exists(source)) File.Delete(source);
        if (File.Exists(target)) File.Delete(target);
    }
}

TransferRoundTrip(TransferProtocol.Xmodem, 512, "xmodem");
TransferRoundTrip(TransferProtocol.Xmodem1K, 2048, "xmodem-1k");
TransferRoundTrip(TransferProtocol.XmodemChecksum, 512, "xmodem-checksum");
TransferRoundTrip(TransferProtocol.Ymodem, 2500, "ymodem");

// Mouse reporting modes
var mouseBuffer = new ScreenBuffer(80, 24);
var mouseParser = new VtParser(mouseBuffer);
mouseParser.Parse("\x1b[?1002h\x1b[?1006h");
Check("mouse button sgr mode",
    mouseBuffer.MouseTracking == MouseTrackingMode.Button &&
    mouseBuffer.MouseEncoding == MouseEncodingMode.Sgr,
    $"{mouseBuffer.MouseTracking}/{mouseBuffer.MouseEncoding}");
mouseParser.Parse("\x1b[?1002l\x1b[?1006l");
Check("mouse mode reset",
    mouseBuffer.MouseTracking == MouseTrackingMode.None &&
    mouseBuffer.MouseEncoding == MouseEncodingMode.Default,
    $"{mouseBuffer.MouseTracking}/{mouseBuffer.MouseEncoding}");
mouseParser.Parse("\x1b[?9h");
Check("mouse x10 mode", mouseBuffer.MouseTracking == MouseTrackingMode.X10, mouseBuffer.MouseTracking.ToString());
mouseParser.Parse("\x1b[?3h");
Check("deccolm 132", mouseBuffer.PreferredColumns == 132, mouseBuffer.PreferredColumns.ToString());
mouseParser.Parse("\x1b[?3l");
Check("deccolm 80", mouseBuffer.PreferredColumns == 80, mouseBuffer.PreferredColumns.ToString());

// Session input lock blocks every transmit path.
var recording = new RecordingTransport();
var lockProfile = SessionProfile.CreateDefault("lock test");
lockProfile.AutoLog = false;
using (var lockSession = new TerminalSession(
           lockProfile, recording, new AppSettings { AutoLog = false }))
{
    lockSession.ConnectAsync().GetAwaiter().GetResult();
    lockSession.SendText("A");
    lockSession.InputLocked = true;
    lockSession.SendText("B");
    bool lockedWrite = lockSession.SendBytes(new byte[] { 0x43 });
    Check("session input lock",
        !lockedWrite && Encoding.UTF8.GetString(recording.Sent.ToArray()) == "A",
        Encoding.UTF8.GetString(recording.Sent.ToArray()));
}

// Script expect must not reuse output already matched by an earlier step.
var echoTransport = new RecordingTransport();
var echoProfile = SessionProfile.CreateDefault("expect cursor test");
echoProfile.AutoLog = false;
using (var echoSession = new TerminalSession(
           echoProfile, echoTransport, new AppSettings { AutoLog = false }))
{
    echoSession.ConnectAsync().GetAwaiter().GetResult();
    echoTransport.Emit("Switch# ");
    bool firstMatch = echoSession.WaitForOutputAsync(
        "Switch#", isRegex: false, 500, CancellationToken.None).GetAwaiter().GetResult();
    bool staleMatch = echoSession.WaitForOutputAsync(
        "Switch#", isRegex: false, 150, CancellationToken.None).GetAwaiter().GetResult();
    echoTransport.Emit("Switch# ");
    bool nextMatch = echoSession.WaitForOutputAsync(
        "Switch#", isRegex: false, 500, CancellationToken.None).GetAwaiter().GetResult();
    Check("expect consumes matched output", firstMatch && !staleMatch && nextMatch,
        $"{firstMatch}/{staleMatch}/{nextMatch}");

    echoTransport.Emit("old prompt");
    echoSession.MarkExpectBoundary();
    bool beforeBoundary = echoSession.WaitForOutputAsync(
        "old prompt", isRegex: false, 150, CancellationToken.None).GetAwaiter().GetResult();
    echoTransport.Emit("old prompt");
    bool afterBoundary = echoSession.WaitForOutputAsync(
        "old prompt", isRegex: false, 500, CancellationToken.None).GetAwaiter().GetResult();
    Check("expect boundary excludes pre-send output", !beforeBoundary && afterBoundary,
        $"{beforeBoundary}/{afterBoundary}");
}

// Named pipe transport
Check("named pipe normalize",
    NamedPipeTransport.NormalizePipeName(@"\\.\pipe\com_1") == "com_1" &&
    NamedPipeTransport.NormalizePipeName("pipe:com_2") == "com_2");
string pipeName = "lumen-test-" + Guid.NewGuid().ToString("N");
var pipeServer = new NamedPipeServerStream(
    pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
var pipeTransport = new NamedPipeTransport(pipeName);
try
{
    var pipeReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    pipeTransport.DataReceived += data => pipeReceived.TrySetResult(data.ToArray());
    var openTask = pipeTransport.OpenAsync();
    var acceptTask = pipeServer.WaitForConnectionAsync();
    Task.WhenAll(openTask, acceptTask).GetAwaiter().GetResult();

    pipeServer.WriteByte(0xA5);
    pipeServer.Flush();
    bool pipeRx = pipeReceived.Task.Wait(3000) &&
                  pipeReceived.Task.Result.Length == 1 &&
                  pipeReceived.Task.Result[0] == 0xA5;
    Check("named pipe receive", pipeRx);

    var pipeRead = Task.Run(() =>
    {
        var buffer = new byte[2];
        int read = 0;
        while (read < 2)
        {
            int n = pipeServer.Read(buffer, read, 2 - read);
            if (n <= 0) break;
            read += n;
        }
        return buffer;
    });
    pipeTransport.Write(new byte[] { 0x5A, 0x6B });
    bool pipeTx = pipeRead.Wait(3000) &&
                  pipeRead.Result[0] == 0x5A && pipeRead.Result[1] == 0x6B;
    Check("named pipe send", pipeTx);
}
catch (Exception ex)
{
    Check("named pipe transport", false, ex.Message);
}
finally
{
    pipeTransport.Dispose();
    pipeServer.Dispose();
}

// Automation rules and script DSL
Check("expect lines parse",
    AutomationTextParser.TryParseExpectRules(
        "Password: => admin\n!+Switch> => enable", out var expectRules, out _)
    && expectRules.Count == 2
    && expectRules[0].IsRegex && expectRules[0].Once
    && !expectRules[1].IsRegex && !expectRules[1].Once,
    string.Join(",", expectRules.Select(r => $"{r.Pattern}/{r.IsRegex}/{r.Once}")));
Check("bad expect line rejected",
    !AutomationTextParser.TryParseExpectRules("missing separator", out _, out _));
Check("highlight lines parse",
    AutomationTextParser.TryParseHighlightRules(
        "ERROR|fail => #F85149\n!Switch# => #22D3EE", out var highlightRules, out _)
    && highlightRules.Count == 2
    && highlightRules[0].IsRegex && !highlightRules[1].IsRegex,
    string.Join(",", highlightRules.Select(r => r.Pattern)));
Check("bad highlight color rejected",
    !AutomationTextParser.TryParseHighlightRules("ERROR => red", out _, out _));

var automation = new AutomationRuntime();
string automationResponse = "";
automation.SendText = text => automationResponse = text;
automation.Update(expectRules, highlightRules);
automation.Feed("User Access Verification\r\nPassword: ");
Check("expect response", automationResponse == "admin\r", automationResponse);
automationResponse = "";
automation.Feed("Password: ");
Check("expect once", automationResponse.Length == 0, automationResponse);
Check("highlight runtime", automation.Highlights.Count == 2 &&
    automation.Highlights[0].Regex.IsMatch("an ERROR occurred") &&
    automation.Highlights[1].Regex.IsMatch("Switch#"));

int oldAutomationVersion = automation.Version;
automation.Update(expectRules, highlightRules);
Check("automation version bumps on rule edit", automation.Version > oldAutomationVersion,
    $"{oldAutomationVersion} -> {automation.Version}");

// A repeatable rule must not keep matching a prompt that was already consumed.
var repeatAutomation = new AutomationRuntime();
int repeatResponses = 0;
repeatAutomation.SendText = _ => repeatResponses++;
repeatAutomation.Update(new[]
{
    new ExpectRule
    {
        Pattern = "ready",
        IsRegex = false,
        Response = "go",
        Once = false,
        CooldownMs = 0
    }
}, null);
repeatAutomation.Feed("device ready\r\n");
repeatAutomation.Feed("unrelated output");
Check("expect repeat consumes matched text", repeatResponses == 1, repeatResponses.ToString());

// The editable rule text must preserve flags, delimiters and non-default options.
var roundTripRules = new List<ExpectRule>
{
    new()
    {
        Pattern = "+5V",
        IsRegex = false,
        Response = "ok",
        AppendEnter = false,
        Once = false,
        CooldownMs = 0,
        Enabled = false
    },
    new() { Pattern = "a=>b", IsRegex = true, Response = "c=>d" },
    new() { Pattern = "#", IsRegex = false, Response = "hash" },
    new() { Pattern = "@expect", IsRegex = true, Response = "at" }
};
string roundTripText = AutomationTextParser.BuildExpectRules(roundTripRules);
bool roundTripParsed = AutomationTextParser.TryParseExpectRules(
    roundTripText, out var roundTripResult, out string roundTripError);
Check("expect rule text round-trip",
    roundTripParsed && roundTripResult.Count == 4 &&
    roundTripResult[0].Pattern == "+5V" &&
    !roundTripResult[0].IsRegex && !roundTripResult[0].Once &&
    !roundTripResult[0].AppendEnter && roundTripResult[0].CooldownMs == 0 &&
    !roundTripResult[0].Enabled &&
    roundTripResult[1].Pattern == "a=>b" && roundTripResult[1].Response == "c=>d" &&
    roundTripResult[2].Pattern == "#" && !roundTripResult[2].IsRegex &&
    roundTripResult[3].Pattern == "@expect",
    roundTripError + "\n" + roundTripText);

var roundTripHighlights = new List<HighlightRule>
{
    new() { Pattern = "#", IsRegex = false, Color = "#F85149", Enabled = false },
    new() { Pattern = "a=>b", IsRegex = true, Color = "#22D3EE" },
    new() { Pattern = "@highlight", IsRegex = true, Color = "#F0B429" }
};
string highlightText = AutomationTextParser.BuildHighlightRules(roundTripHighlights);
bool highlightParsed = AutomationTextParser.TryParseHighlightRules(
    highlightText, out var highlightResult, out string highlightError);
Check("highlight rule text round-trip",
    highlightParsed && highlightResult.Count == 3 &&
    highlightResult[0].Pattern == "#" && !highlightResult[0].IsRegex &&
    !highlightResult[0].Enabled && highlightResult[0].Color == "#F85149" &&
    highlightResult[1].Pattern == "a=>b" &&
    highlightResult[2].Pattern == "@highlight",
    highlightError + "\n" + highlightText);

var exactRules = new List<ExpectRule>
{
    new()
    {
        Pattern = " leading ",
        IsRegex = false,
        Response = " response\nline2 ",
        AppendEnter = false,
        Once = false,
        CooldownMs = 0,
        Enabled = false
    },
    new() { Pattern = "a\\=>b", IsRegex = true, Response = "x" },
    new() { Pattern = " ", IsRegex = false, Response = "space" }
};
string exactText = AutomationTextParser.BuildExpectRules(exactRules);
bool exactParsed = AutomationTextParser.TryParseExpectRules(
    exactText, out var exactResult, out string exactError);
Check("expect exact rule round-trip",
    exactParsed && exactResult.Count == 3 &&
    exactResult[0].Pattern == " leading " &&
    exactResult[0].Response == " response\nline2 " &&
    !exactResult[0].IsRegex && !exactResult[0].Once &&
    !exactResult[0].AppendEnter && exactResult[0].CooldownMs == 0 &&
    !exactResult[0].Enabled &&
    exactResult[1].Pattern == "a\\=>b" &&
    exactResult[2].Pattern == " " && !exactResult[2].IsRegex,
    exactError + "\n" + exactText);

var exactHighlights = new List<HighlightRule>
{
    new() { Pattern = " padded ", IsRegex = false, Color = "#F85149", Enabled = false }
};
string exactHighlightText = AutomationTextParser.BuildHighlightRules(exactHighlights);
bool exactHighlightParsed = AutomationTextParser.TryParseHighlightRules(
    exactHighlightText, out var exactHighlightResult, out string exactHighlightError);
Check("highlight exact rule round-trip",
    exactHighlightParsed && exactHighlightResult.Count == 1 &&
    exactHighlightResult[0].Pattern == " padded " &&
    !exactHighlightResult[0].IsRegex &&
    !exactHighlightResult[0].Enabled &&
    exactHighlightResult[0].Color == "#F85149",
    exactHighlightError + "\n" + exactHighlightText);

Check("bad expect options rejected",
    !AutomationTextParser.TryParseExpectRules(
        "@expect cooldown=999999\nx => y", out _, out _));
Check("exact expect requires quoted pattern",
    !AutomationTextParser.TryParseExpectRules("@expect pattern=unquoted", out _, out _));
Check("exact expect requires pattern",
    !AutomationTextParser.TryParseExpectRules("@expect response=\"x\"", out _, out _));
Check("orphan expect options rejected",
    !AutomationTextParser.TryParseExpectRules("@expect enabled=false", out _, out _));

var invalidAutomation = new AutomationRuntime();
invalidAutomation.Update(new[] { new ExpectRule { Pattern = "(", IsRegex = true } }, null);
Check("invalid automation regex reported",
    invalidAutomation.LastError is not null && !invalidAutomation.HasExpectRules);

var postedAutomation = new AutomationRuntime();
var postedResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
postedAutomation.SendText = text => postedResponse.TrySetResult(text);
postedAutomation.Update(new[]
{
    new ExpectRule { Pattern = "Password:", IsRegex = false, Response = "admin", Once = true }
}, null);
postedAutomation.Post("Password: ");
Check("automation worker processes queued text",
    postedResponse.Task.Wait(1000) && postedResponse.Task.Result == "admin\r",
    postedResponse.Task.Status.ToString());
postedAutomation.Dispose();

// Hand-edited/imported JSON can contain explicit nulls despite the model defaults.
var malformed = new SessionProfile
{
    Id = "",
    Name = "",
    Group = "",
    VendorId = "",
    Serial = null!,
    QuickCommands = null!,
    ExpectRules = null!,
    HighlightRules = null!
};
malformed.Normalize();
Check("profile normalize repairs nulls",
    malformed.Serial is not null &&
    malformed.QuickCommands is not null &&
    malformed.ExpectRules is not null &&
    malformed.HighlightRules is not null &&
    malformed.Id.Length > 0 && malformed.Name.Length > 0,
    $"id={malformed.Id} name={malformed.Name}");
var clonedMalformed = new SessionProfile
{
    Serial = null!,
    QuickCommands = null!,
    ExpectRules = null!,
    HighlightRules = null!
}.Clone();
Check("profile clone is null safe",
    clonedMalformed.Serial is not null &&
    clonedMalformed.QuickCommands is not null &&
    clonedMalformed.ExpectRules is not null &&
    clonedMalformed.HighlightRules is not null);

Check("script parse",
    AutomationScript.TryParse(
        "sendline enable\nwait 50\nexpect Password: 500\nlog ready", out var scriptSteps, out _)
    && scriptSteps.Count == 4
    && scriptSteps[0].Kind == ScriptStepKind.SendLine
    && scriptSteps[1].Kind == ScriptStepKind.Wait
    && scriptSteps[2].Kind == ScriptStepKind.Expect
    && scriptSteps[2].TimeoutMs == 500
    && scriptSteps[3].Kind == ScriptStepKind.Log);
Check("bad script rejected", !AutomationScript.TryParse("bogus command", out _, out _));
Check("expect explicit timeout",
    AutomationScript.TryParse("expect @500 Password: 500", out var timedSteps, out _) &&
    timedSteps.Count == 1 &&
    timedSteps[0].Text == "Password: 500" &&
    timedSteps[0].TimeoutMs == 500);
Check("expect literal explicit timeout",
    AutomationScript.TryParse("expect! @250 ADC 1023", out var literalSteps, out _) &&
    literalSteps.Count == 1 &&
    literalSteps[0].Text == "ADC 1023" &&
    !literalSteps[0].IsRegex &&
    literalSteps[0].TimeoutMs == 250);
Check("script accepts tab separators",
    AutomationScript.TryParse("sendline\tenable", out var tabSteps, out _) &&
    tabSteps.Count == 1 &&
    tabSteps[0].Kind == ScriptStepKind.SendLine &&
    tabSteps[0].Text == "enable");

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
        _ = new Lumen.Views.NamedPipeConnectView();
        _ = new Lumen.Views.AutomationView(SessionProfile.CreateDefault());
        _ = new Lumen.MainWindow(new AppSettings(), new SessionStore(), Array.Empty<string>());
        using (var demoSpan = new TerminalSession(
                   SessionProfile.CreateDefault("ui transfer", "ui"), new DemoTransport(), new AppSettings()))
        {
            _ = new Lumen.Views.FileTransferView(demoSpan, connected: true);
        }
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

sealed class RecordingTransport : ITerminalTransport
{
    public List<byte> Sent { get; } = new();
    public bool IsOpen { get; private set; }
    public bool IsReconnecting => false;
    public string DisplayName => "recording";
    public long BytesReceived => 0;
    public long BytesSent => Sent.Count;

#pragma warning disable CS0067
    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<string>? Disconnected;
    public event Action? Reconnected;
#pragma warning restore CS0067

    public Task OpenAsync()
    {
        IsOpen = true;
        return Task.CompletedTask;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        foreach (byte value in data) Sent.Add(value);
    }

    public Task SendBreakAsync(int durationMs) => Task.CompletedTask;
    public void SetDtr(bool enabled) { }
    public void SetRts(bool enabled) { }

    public void Emit(string text) => DataReceived?.Invoke(Encoding.UTF8.GetBytes(text));

    public void Dispose()
    {
        IsOpen = false;
    }
}

sealed class LoopChannel : ITransferChannel
{
    private readonly object _gate = new();
    private readonly Queue<byte> _queue = new();
    private readonly SemaphoreSlim _available = new(0);
    private LoopChannel? _peer;

    public void Connect(LoopChannel peer) => _peer = peer;

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var peer = _peer ?? throw new IOException("loop channel not connected");
        peer.Push(data.Span);
        return Task.CompletedTask;
    }

    public async Task<int> ReadByteAsync(int timeoutMs, CancellationToken token)
    {
        if (!await _available.WaitAsync(Math.Max(1, timeoutMs), token))
        {
            return -1;
        }
        lock (_gate)
        {
            return _queue.Count > 0 ? _queue.Dequeue() : -1;
        }
    }

    private void Push(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            foreach (byte value in data) _queue.Enqueue(value);
        }
        _available.Release(data.Length);
    }
}
