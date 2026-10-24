namespace Lumen.Terminal;

public enum ScriptStepKind
{
    Send,
    SendLine,
    Wait,
    Expect,
    Log
}

public sealed record ScriptStep(
    ScriptStepKind Kind,
    string Text,
    int TimeoutMs = 10000,
    bool IsRegex = true);

public static class AutomationScript
{
    public const string HelpText =
        "脚本命令：\n" +
        "  send <转义文本>       发送原始字节，支持 \\x1B、\\r\\n、^C\n" +
        "  sendline <文本>       发送文本并追加回车\n" +
        "  wait <毫秒>           等待\n" +
        "  expect <正则> [毫秒]  等待设备输出匹配，超时继续\n" +
        "  expect! <文本> [毫秒] 按字面量等待\n" +
        "  log <文本>           在终端显示本地提示\n" +
        "# 开头为注释";

    public static bool TryParse(string? script, out List<ScriptStep> steps, out string error)
    {
        steps = new List<ScriptStep>();
        error = "";
        var lines = (script ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            int space = line.IndexOf(' ');
            var command = (space < 0 ? line : line[..space]).ToLowerInvariant();
            var argument = space < 0 ? "" : line[(space + 1)..].Trim();

            switch (command)
            {
                case "send":
                    if (argument.Length == 0) return Fail(i, "send 缺少内容", out steps, out error);
                    steps.Add(new ScriptStep(ScriptStepKind.Send, argument));
                    break;
                case "sendline":
                    steps.Add(new ScriptStep(ScriptStepKind.SendLine, argument));
                    break;
                case "wait":
                case "sleep":
                    if (!int.TryParse(argument, out int waitMs) || waitMs < 0 || waitMs > 3600000)
                        return Fail(i, "wait 需要 0 - 3600000 毫秒", out steps, out error);
                    steps.Add(new ScriptStep(ScriptStepKind.Wait, argument, waitMs));
                    break;
                case "expect":
                    if (!TryParseExpect(argument, isRegex: true, out var expectStep, out var expectError))
                        return Fail(i, expectError, out steps, out error);
                    steps.Add(expectStep!);
                    break;
                case "expect!":
                    if (!TryParseExpect(argument, isRegex: false, out var literalStep, out var literalError))
                        return Fail(i, literalError, out steps, out error);
                    steps.Add(literalStep!);
                    break;
                case "log":
                    steps.Add(new ScriptStep(ScriptStepKind.Log, argument));
                    break;
                default:
                    return Fail(i, $"未知脚本命令：{command}", out steps, out error);
            }
        }

        return true;
    }

    private static bool TryParseExpect(
        string argument, bool isRegex, out ScriptStep? step, out string error)
    {
        step = null;
        error = "";
        if (argument.Length == 0)
        {
            error = "expect 缺少匹配模式";
            return false;
        }

        var text = argument;
        int timeout = 10000;
        int space = argument.LastIndexOf(' ');
        if (space > 0 && int.TryParse(argument[(space + 1)..], out int parsed))
        {
            if (parsed < 100 || parsed > 600000)
            {
                error = "expect 超时需在 100 - 600000 毫秒之间";
                return false;
            }
            timeout = parsed;
            text = argument[..space].Trim();
        }

        if (text.Length == 0)
        {
            error = "expect 缺少匹配模式";
            return false;
        }

        if (isRegex)
        {
            try
            {
                _ = new System.Text.RegularExpressions.Regex(
                    text,
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(200));
            }
            catch (Exception ex)
            {
                error = $"expect 正则无效：{ex.Message}";
                return false;
            }
        }

        step = new ScriptStep(ScriptStepKind.Expect, text, timeout, isRegex);
        return true;
    }

    private static bool Fail(
        int lineIndex, string message, out List<ScriptStep> steps, out string error)
    {
        steps = new List<ScriptStep>();
        error = $"第 {lineIndex + 1} 行：{message}";
        return false;
    }
}

public static class AutomationScriptRunner
{
    public static async Task RunAsync(
        IReadOnlyList<ScriptStep> steps,
        TerminalSession session,
        IProgress<string>? progress,
        CancellationToken token)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (session.InputLocked) throw new OperationCanceledException();
            var step = steps[i];
            progress?.Report($"脚本 {i + 1}/{steps.Count}：{step.Kind}");

            switch (step.Kind)
            {
                case ScriptStepKind.Send:
                case ScriptStepKind.SendLine:
                    if (!session.TryEncodeEscaped(step.Text, out var bytes))
                    {
                        session.InjectSystem($"脚本：无法解析转义文本：{step.Text}", true);
                        continue;
                    }
                    if (step.Kind == ScriptStepKind.SendLine)
                    {
                        Array.Resize(ref bytes, bytes.Length + 1);
                        bytes[^1] = 0x0D;
                    }
                    session.SendBytes(bytes);
                    break;

                case ScriptStepKind.Wait:
                    await Task.Delay(step.TimeoutMs, token).ConfigureAwait(false);
                    break;

                case ScriptStepKind.Expect:
                    bool matched = await session.WaitForOutputAsync(
                        step.Text, step.IsRegex, step.TimeoutMs, token).ConfigureAwait(false);
                    if (!matched)
                    {
                        session.InjectSystem($"脚本：等待输出超时：{step.Text}", true);
                    }
                    break;

                case ScriptStepKind.Log:
                    session.InjectSystem($"脚本：{step.Text}");
                    break;
            }
        }

        progress?.Report("脚本执行完成");
    }
}
