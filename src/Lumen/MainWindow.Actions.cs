using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lumen.Controls;
using Lumen.Models;
using Lumen.Serial;
using Lumen.Services;
using Lumen.Terminal;
using Lumen.Views;
using Microsoft.Win32;

namespace Lumen;


public partial class MainWindow
{
    private void ShowOverlay(FrameworkElement content)
    {
        OverlayHost.Children.Clear();
        ApplyOverlaySize(content);
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        var translate = new TranslateTransform(0, 12);
        content.RenderTransform = translate;
        OverlayHost.Children.Add(content);
        OverlayHost.Visibility = Visibility.Visible;
        OverlayHost.Opacity = 0;
        OverlayHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void HideOverlay()
    {
        OverlayHost.Visibility = Visibility.Collapsed;
        OverlayHost.Children.Clear();
        if (_active is not null)
        {
            if (_active.HexMode) _active.HexView.Focus();
            else _active.View.Focus();
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var tcs = new TaskCompletionSource<bool>();
        var view = new ConfirmView(title, message, confirmText);
        view.Confirmed += () =>
        {
            tcs.TrySetResult(true);
            HideOverlay();
        };
        view.Cancelled += () =>
        {
            tcs.TrySetResult(false);
            HideOverlay();
        };
        ShowOverlay(view);
        return await tcs.Task;
    }

    private async Task PasteIntoSessionAsync(TabEntry tab, string text)
    {
        if (!tab.Session.IsConnected)
        {
            SetStatus("会话未连接，无法粘贴", true);
            return;
        }
        if (tab.Locked)
        {
            SetStatus("会话已锁定，无法粘贴", true);
            return;
        }
        if (tab.Session.IsTransferActive)
        {
            SetStatus("文件传输进行中，无法粘贴", true);
            return;
        }

        if (tab.Profile.ConfirmDangerousPaste && ContainsDangerousCommand(text))
        {
            bool ok = await ConfirmAsync(
                "确认粘贴",
                "剪贴板中包含可能影响设备运行状态的命令。\n\n" + PreviewText(text),
                "仍然粘贴");
            if (!ok) return;
        }

        int delay = tab.Profile.PasteLineDelayMs > 0
            ? tab.Profile.PasteLineDelayMs
            : _settings.DefaultPasteDelayMs;
        delay = Math.Clamp(delay, 0, 5000);
        int charDelay = Math.Clamp(tab.Profile.SendCharDelayMs, 0, 1000);
        int echoTimeout = Math.Clamp(tab.Profile.EchoTimeoutMs, 200, 60000);
        var echoHint = tab.Profile.WaitForEcho ? $"，每行等待回显（最多 {echoTimeout} ms）" : "";

        if (_broadcast)
        {
            var targets = BroadcastTargets().ToList();
            if (targets.Count == 0)
            {
                SetStatus("没有可群发的已连接会话", true);
                return;
            }

            SetStatus($"正在群发粘贴到 {targets.Count} 个会话…");
            var tasks = targets
                .Select(target => target.Session.PasteAsync(text, delay, charDelay, false, echoTimeout))
                .ToArray();
            bool[] results = await Task.WhenAll(tasks);
            SetStatus($"群发粘贴完成：{results.Count(ok => ok)}/{targets.Count}");
            return;
        }

        SetStatus($"正在粘贴（字符 {charDelay} ms / 行 {delay} ms{echoHint}，Esc 取消）…");
        bool completed = await tab.Session.PasteAsync(
            text, delay, charDelay, tab.Profile.WaitForEcho, echoTimeout);
        SetStatus(completed ? "粘贴完成" : "粘贴已取消");
    }

    private static bool ContainsDangerousCommand(string text)
    {
        return Regex.IsMatch(
            text,
            @"(?im)^\s*(erase|reload|write\s+erase|delete|format|shutdown|factory-reset|reset\s+saved-configuration)\b");
    }

    private static string PreviewText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Take(3)
            .Select(l => l.Length > 80 ? l[..80] + "…" : l);
        return string.Join(Environment.NewLine, lines);
    }

    private void NamedPipe_Click(object sender, RoutedEventArgs e)
    {
        var view = new NamedPipeConnectView();
        view.ConnectRequested += name =>
        {
            HideOverlay();
            ConnectNamedPipe(name);
        };
        view.Cancelled += HideOverlay;
        ShowOverlay(view);
    }

    private void ConnectNamedPipe(string pipeName)
    {
        pipeName = NamedPipeTransport.NormalizePipeName(pipeName);
        if (pipeName.Length == 0)
        {
            SetStatus("命名管道名称不能为空", true);
            return;
        }

        var busy = FindBusyTabForEndpoint(SessionTransportKind.NamedPipe, pipeName);
        if (busy is not null)
        {
            ActivateTab(busy);
            SetStatus($"命名管道 {pipeName} 已在标签中打开");
            return;
        }

        var profile = SessionProfile.CreateDefault($"管道 · {pipeName}", "快速连接");
        profile.Id = $"pipe-{pipeName}";
        profile.TransportKind = SessionTransportKind.NamedPipe;
        profile.PipeName = pipeName;
        ApplySettingsDefaults(profile);
        StartSession(profile, CreateTransport(profile));
    }

    private void Break_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        _ = SendBreakAsync(_active);
    }

    private async Task SendBreakAsync(TabEntry tab)
    {
        if (tab.Locked)
        {
            SetStatus("会话已锁定，无法发送 Break", true);
            return;
        }
        await tab.Session.SendBreakAsync();
        SetStatus($"已发送 Break（{tab.Session.BreakDurationMs} ms）");
    }

    private void BreakDuration_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        if (sender is not MenuItem { Tag: string value } || !int.TryParse(value, out int duration)) return;

        var tab = _active;
        tab.Session.Profile.Serial.BreakDurationMs = duration;
        tab.Profile.Serial.BreakDurationMs = duration;
        if (_sessions.Contains(tab.Profile)) _store.Update(tab.Profile);
        SetStatus($"Break 脉冲时长已设为 {duration} ms");
    }

    private void SendBytes_Click(object sender, RoutedEventArgs e)
    {
        var tab = _active;
        if (tab is null)
        {
            SetStatus("没有活动会话", true);
            return;
        }
        if (tab.Locked)
        {
            SetStatus("会话已锁定，无法发送字节", true);
            return;
        }
        if (tab.Session.IsTransferActive)
        {
            SetStatus("文件传输进行中，无法发送原始字节", true);
            return;
        }

        var view = new SendBytesView(
            Lumen.Terminal.EncodingUtil.Resolve(tab.Profile.Encoding),
            tab.Session.IsConnected);
        view.SendRequested += bytes =>
        {
            tab.Session.SendBytes(bytes);
            HideOverlay();
            SetStatus($"已发送 {bytes.Length} 字节原始数据");
        };
        view.Cancelled += HideOverlay;
        ShowOverlay(view);
    }

    private void FileTransfer_Click(object sender, RoutedEventArgs e)
    {
        var tab = _active;
        if (tab is null || !tab.Session.IsConnected)
        {
            SetStatus("会话未连接，无法传输文件", true);
            return;
        }
        if (tab.Locked)
        {
            SetStatus("会话已锁定，无法传输文件", true);
            return;
        }
        if (tab.Session.IsScriptRunning)
        {
            SetStatus("脚本正在运行，无法开始文件传输", true);
            return;
        }
        if (tab.Session.IsTransferActive)
        {
            SetStatus("已有文件传输正在进行", true);
            return;
        }

        var view = new FileTransferView(tab.Session, tab.Session.IsConnected);
        view.StatusMessage += message => SetStatus(message);
        view.Cancelled += HideOverlay;
        ShowOverlay(view);
    }

    private void HexToggle_Click(object sender, RoutedEventArgs e) => ToggleHexMode();

    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        var tab = _active;
        if (tab is null) return;
        ApplyColumns(tab.Profile.TerminalColumns switch
        {
            0 => 80,
            80 => 132,
            _ => 0
        });
    }

    private void ColumnsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value } || !int.TryParse(value, out int columns)) return;
        ApplyColumns(columns is 80 or 132 ? columns : 0);
    }

    private void ApplyColumns(int columns)
    {
        var tab = _active;
        if (tab is null) return;
        columns = columns is 80 or 132 ? columns : 0;
        tab.Profile.TerminalColumns = columns;
        tab.Session.Profile.TerminalColumns = columns;
        tab.View.FixedColumns = columns;
        if (_sessions.Contains(tab.Profile)) _store.Update(tab.Profile);
        UpdateToolbar();
        SetStatus(columns == 0
            ? "终端列宽已设为自适应窗口"
            : $"终端列宽已设为 {columns} 列");
    }

    private void Log_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        var session = _active.Session;
        if (session.IsLogging)
        {
            session.StopLog();
            SetStatus("日志已停止");
        }
        else
        {
            session.StartLog();
            if (session.LogPath is not null)
            {
                StatusText.ToolTip = session.LogPath;
                SetStatus($"日志：{Path.GetFileName(session.LogPath)}");
            }
            else
            {
                SetStatus("无法创建日志文件，请检查日志目录设置", true);
            }
        }
        UpdateToolbar();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        if (_active.HexMode)
        {
            _active.HexView.ClearBuffer();
            SetStatus("已清空 Hex 缓冲（仅本机）");
        }
        else
        {
            _active.View.ClearScreen();
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        string? directory = null;
        if (_active?.Session.LogPath is { } logPath)
        {
            directory = Path.GetDirectoryName(logPath);
        }
        directory ??= _active?.Profile.LogDirectory;
        if (string.IsNullOrWhiteSpace(directory)) directory = _settings.EffectiveLogRoot;

        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"无法打开日志目录：{ex.Message}", true);
        }
    }

    private void Find_Click(object sender, RoutedEventArgs e) => ShowFind();

    private void ShowFind()
    {
        if (_active is null) return;
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus();
        FindBox.SelectAll();
        if (FindBox.Text.Length > 0) FindBox_TextChanged(FindBox, null!);
    }

    private void HideFind()
    {
        _findSearchCts?.Cancel();
        _findSearchCts = null;
        if (FindBar.Visibility != Visibility.Visible) return;
        FindBar.Visibility = Visibility.Collapsed;
        _active?.View.ClearSearch();
    }

    private async void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _findSearchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _findSearchCts = cts;
        var tab = _active;
        if (tab is null)
        {
            FindCount.Text = "0 项";
            return;
        }

        try
        {
            await Task.Delay(150, cts.Token);
            int count = await tab.View.SetSearchAsync(FindBox.Text, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(tab, _active)) return;
            FindCount.Text = $"{count} 项";
            if (count > 0) tab.View.FindNext(true);
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke superseded this search.
        }
    }

    private async void FindPrev_Click(object sender, RoutedEventArgs e)
    {
        var tab = _active;
        if (tab is null) return;
        _findSearchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _findSearchCts = cts;
        int count = await tab.View.SetSearchAsync(FindBox.Text, cts.Token);
        if (cts.IsCancellationRequested || !ReferenceEquals(tab, _active)) return;
        FindCount.Text = $"{count} 项";
        if (count > 0) tab.View.FindNext(false);
    }

    private async void FindNext_Click(object sender, RoutedEventArgs e)
    {
        var tab = _active;
        if (tab is null) return;
        _findSearchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _findSearchCts = cts;
        int count = await tab.View.SetSearchAsync(FindBox.Text, cts.Token);
        if (cts.IsCancellationRequested || !ReferenceEquals(tab, _active)) return;
        FindCount.Text = $"{count} 项";
        if (count > 0) tab.View.FindNext(true);
    }

    private void FindClose_Click(object sender, RoutedEventArgs e)
    {
        HideFind();
        _active?.View.Focus();
    }

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideFind();
            _active?.View.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            FindNext_Click(sender, e);
            e.Handled = true;
        }
    }

    private void DtrToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle || _active?.Session.Transport is not SerialConnection serial) return;
        serial.SetDtr(DtrToggle.IsChecked == true);
        SetStatus(DtrToggle.IsChecked == true ? "DTR 已置位" : "DTR 已复位");
    }

    private void RtsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle || _active?.Session.Transport is not SerialConnection serial) return;
        serial.SetRts(RtsToggle.IsChecked == true);
        SetStatus(RtsToggle.IsChecked == true ? "RTS 已置位" : "RTS 已复位");
    }

    private void BaudCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBaud || BaudCombo.SelectedItem is not int baud) return;
        ApplyLiveBaud(baud);
    }

    private void ApplyLiveBaud(int baud)
    {
        var tab = _active;
        if (tab is null || tab.Session.Transport is not SerialConnection serial || !tab.Session.IsConnected)
        {
            RevertBaudSelection();
            SetStatus("当前会话不是已连接的串口，无法修改波特率", true);
            return;
        }
        if (tab.Locked)
        {
            RevertBaudSelection();
            SetStatus("会话已锁定，无法修改波特率", true);
            return;
        }

        if (baud < 300 || baud > 921600)
        {
            RevertBaudSelection();
            SetStatus("波特率需在 300 - 921600 之间", true);
            return;
        }

        if (baud == tab.Profile.Serial.BaudRate)
        {
            RevertBaudSelection();
            return;
        }

        if (!serial.SetBaudRate(baud))
        {
            RevertBaudSelection();
            SetStatus("切换波特率失败，请检查串口连接", true);
            return;
        }

        tab.Profile.Serial.BaudRate = baud;
        tab.Session.Profile.Serial.BaudRate = baud;
        if (_sessions.Contains(tab.Profile)) _store.Update(tab.Profile);
        ActiveSubtitle.Text = BuildSubtitle(tab);
        SetStatus($"波特率已切换为 {baud} baud");
        _suppressBaud = true;
        BaudCombo.SelectedItem = baud;
        _suppressBaud = false;
    }

    private void RevertBaudSelection()
    {
        int baud = _active?.Profile.Serial.BaudRate ?? 9600;
        EnsureBaudRate(baud);
        _suppressBaud = true;
        BaudCombo.SelectedItem = baud;
        _suppressBaud = false;
    }

    private void QuickCommands_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        var profile = _active.Profile;
        var panel = new Border
        {
            Width = 380,
            Background = Res("Brush.Surface"),
            BorderBrush = Res("Brush.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18)
        };

        var stack = new StackPanel();
        var header = new Grid();
        header.Children.Add(new TextBlock
        {
            Text = "快捷命令",
            FontFamily = (FontFamily)FindResource("Font.Display"),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var close = new Button
        {
            Style = (Style)FindResource("BtnIcon"),
            Content = "\uE711",
            Width = 24,
            Height = 24,
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "关闭"
        };
        System.Windows.Automation.AutomationProperties.SetName(close, "关闭快捷命令");
        close.Click += (_, _) => HideOverlay();
        header.Children.Add(close);
        stack.Children.Add(header);
        stack.Children.Add(new TextBlock
        {
            Text = $"发送到 {profile.Name}",
            FontSize = 12,
            Foreground = Res("Brush.TextMuted"),
            Margin = new Thickness(0, 3, 0, 14)
        });

        foreach (var command in profile.QuickCommands)
        {
            var button = new Button
            {
                Style = (Style)FindResource("BtnSecondary"),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(12, 8, 12, 8),
                ToolTip = command.Command
            };
            var row = new StackPanel();
            row.Children.Add(new TextBlock { Text = command.Name, FontSize = 13 });
            row.Children.Add(new TextBlock
            {
                Text = command.Command,
                FontFamily = (FontFamily)FindResource("Font.Mono"),
                FontSize = 11,
                Foreground = Res("Brush.TextMuted"),
                Margin = new Thickness(0, 2, 0, 0)
            });
            button.Content = row;
            button.Click += async (_, _) => await SendQuickCommandAsync(profile, command);
            stack.Children.Add(button);
        }

        var edit = new Button
        {
            Content = "编辑快捷命令",
            Style = (Style)FindResource("BtnGhost"),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(8, 5, 8, 5)
        };
        edit.Click += (_, _) =>
        {
            HideOverlay();
            ShowSessionEditor(profile, false);
        };
        stack.Children.Add(edit);

        panel.Child = stack;
        ShowOverlay(panel);
    }

}
