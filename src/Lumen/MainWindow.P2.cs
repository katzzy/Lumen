using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Lumen.Models;
using Lumen.Services;
using Lumen.Views;
using Microsoft.Win32;

namespace Lumen;

public partial class MainWindow
{
    private void More_Click(object sender, RoutedEventArgs e)
    {
        MoreMenu.PlacementTarget = MoreButton;
        MoreMenu.Placement = PlacementMode.Bottom;
        MoreMenu.Width = 232;
        MoreMenu.HorizontalOffset = MoreButton.ActualWidth - MoreMenu.Width;
        MoreMenu.VerticalOffset = 4;
        MoreMenu.MaxHeight = Math.Clamp(ActualHeight - 96, 180, 620);
        MoreMenu.IsOpen = true;
    }

    private void MoreMenu_Opened(object sender, RoutedEventArgs e) => UpdateMoreMenuChecks();

    private void UpdateMoreMenuChecks()
    {
        if (BroadcastMenuItem is null) return;
        BroadcastMenuItem.IsChecked = _broadcast;
        // Must stay clickable while broadcast is on, even if every target has
        // gone away, otherwise the only visible way to turn it off is disabled.
        BroadcastMenuItem.IsEnabled = _broadcast || BroadcastTargets().Any();
        LockMenuItem.IsChecked = _active?.Locked == true;
        LockMenuItem.IsEnabled = _active is not null;
        bool scriptRunning = _active?.Session.IsScriptRunning == true;
        RunScriptMenuItem.Header = scriptRunning ? "停止会话脚本" : "运行会话脚本";
        RunScriptMenuItem.IsEnabled = _active is not null &&
                                      (scriptRunning ||
                                       (_active.Session.IsConnected && !_active.Locked));
        CommandBarMenuItem.IsChecked = CommandBar.Visibility == Visibility.Visible;
        CommandBarMenuItem.IsEnabled = _active is not null;
        bool has = _active is not null;
        QuickCommandMenuItem.IsEnabled = has && _active?.Locked != true;
        ColumnsMenuItem.IsEnabled = has;
        ClearMenuItem.IsEnabled = has;
        FindMenuItem.IsEnabled = has;
        ColumnsMenuItem.Header = _active?.Profile.TerminalColumns switch
        {
            80 => "终端列宽：80 列",
            132 => "终端列宽：132 列",
            _ => "终端列宽：自适应"
        };
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e) =>
        SetSidebarCollapsed(!_sidebarCollapsed, userInitiated: true);

    private void SetSidebarCollapsed(bool collapsed, bool userInitiated = false)
    {
        if (userInitiated) _sidebarManualState = collapsed;
        _sidebarCollapsed = collapsed;
        SidebarColumn.Width = collapsed ? new GridLength(0) : new GridLength(256);
        SidebarDividerColumn.Width = collapsed ? new GridLength(0) : new GridLength(1);
        Sidebar.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarDivider.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarToggleButton.ToolTip = collapsed ? "显示会话栏" : "隐藏会话栏";
        UpdateToolbarDensity();
    }

    private void UpdateResponsiveLayout()
    {
        if (!IsLoaded || ActualWidth <= 0) return;

        bool narrow = ActualWidth < 900;
        if (_sidebarManualState is null && narrow != _lastNarrowLayout)
        {
            SetSidebarCollapsed(narrow);
        }
        _lastNarrowLayout = narrow;

        UpdateToolbarDensity();
        ThroughputText.Visibility = ActualWidth >= 760 ? Visibility.Visible : Visibility.Collapsed;
        CursorText.Visibility = ActualWidth >= 720 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.MaxWidth = Math.Clamp(ActualWidth - 360, 160, 540);

        if (OverlayHost.Visibility == Visibility.Visible &&
            OverlayHost.Children.Count > 0 &&
            OverlayHost.Children[0] is FrameworkElement content)
        {
            ApplyOverlaySize(content);
        }
    }

    private void UpdateToolbarDensity()
    {
        if (QuickCommandButton is null) return;
        double workspace = Math.Max(0, ActualWidth - (_sidebarCollapsed ? 0 : 257));
        bool compact = workspace < 900;
        bool tiny = workspace < 700;
        bool veryTiny = workspace < 480;
        QuickCommandButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        var actionVisibility = tiny ? Visibility.Collapsed : Visibility.Visible;
        SendBytesButton.Visibility = actionVisibility;
        TransferButton.Visibility = actionVisibility;
        HexToggle.Visibility = actionVisibility;

        var signalVisibility = veryTiny ? Visibility.Collapsed : Visibility.Visible;
        DtrToggle.Visibility = signalVisibility;
        RtsToggle.Visibility = signalVisibility;
        DsrText.Visibility = signalVisibility;
    }

    private void ApplyOverlaySize(FrameworkElement content)
    {
        double width = OverlayHost.ActualWidth > 0 ? OverlayHost.ActualWidth : ActualWidth;
        double height = OverlayHost.ActualHeight > 0 ? OverlayHost.ActualHeight : ActualHeight;
        content.MaxWidth = Math.Max(320, width - 24);
        content.MaxHeight = Math.Max(300, height - 24);
    }

    private void Automation_Click(object sender, RoutedEventArgs e)
    {
        var tab = _active;
        if (tab is null) return;
        OpenAutomationForProfile(tab.Profile, _sessions.Contains(tab.Profile));
    }

    private void OpenAutomationForProfile(SessionProfile profile, bool persist)
    {
        var view = new AutomationView(profile.Clone());
        view.Saved += updated =>
        {
            profile.Script = updated.Script;
            profile.ScriptAutoRun = updated.ScriptAutoRun;
            profile.ExpectRules = updated.ExpectRules.Select(r => r.Clone()).ToList();
            profile.HighlightRules = updated.HighlightRules.Select(r => r.Clone()).ToList();
            if (persist) _store.Update(profile);

            var tab = _tabs.FirstOrDefault(t => t.Profile.Id == profile.Id);
            if (tab is not null) ApplyAutomationToTab(tab, profile);

            _sessionsView.Refresh();
            HideOverlay();
            if (tab?.Session.Automation.LastError is { Length: > 0 } automationError)
            {
                SetStatus($"自动化已保存，但有规则未生效：{automationError}", true);
            }
            else
            {
                SetStatus($"自动化已保存：{profile.Name}");
            }
        };
        view.Cancelled += HideOverlay;
        ShowOverlay(view);
    }

    private void ApplyAutomationToTab(TabEntry tab, SessionProfile source)
    {
        tab.Profile.Script = source.Script;
        tab.Profile.ScriptAutoRun = source.ScriptAutoRun;
        tab.Profile.ExpectRules = source.ExpectRules.Select(r => r.Clone()).ToList();
        tab.Profile.HighlightRules = source.HighlightRules.Select(r => r.Clone()).ToList();

        tab.Session.Profile.Script = source.Script;
        tab.Session.Profile.ScriptAutoRun = source.ScriptAutoRun;
        tab.Session.Profile.ExpectRules = source.ExpectRules.Select(r => r.Clone()).ToList();
        tab.Session.Profile.HighlightRules = source.HighlightRules.Select(r => r.Clone()).ToList();
        tab.Session.UpdateAutomationRules();
    }

    private void RunScript_Click(object sender, RoutedEventArgs e)
    {
        var tab = _active;
        if (tab is null) return;
        if (tab.Session.IsScriptRunning)
        {
            tab.Session.CancelScript();
            SetStatus("正在停止脚本…");
            return;
        }
        if (!tab.Session.IsConnected)
        {
            SetStatus("会话未连接，无法运行脚本", true);
            return;
        }
        if (tab.Session.IsTransferActive)
        {
            SetStatus("文件传输进行中，无法运行脚本", true);
            return;
        }
        if (tab.Locked)
        {
            SetStatus("会话已锁定，无法运行脚本", true);
            return;
        }
        if (string.IsNullOrWhiteSpace(tab.Profile.Script))
        {
            SetStatus("当前会话没有脚本", true);
            return;
        }

        var progress = new Progress<string>(message => SetStatus(message));
        _ = tab.Session.RunScriptAsync(progress, CancellationToken.None);
    }

    private void BroadcastMenuItem_Click(object sender, RoutedEventArgs e) => SetBroadcast(!_broadcast);

    private void SetBroadcast(bool enabled)
    {
        if (enabled && !BroadcastTargets().Any())
        {
            _broadcast = false;
            UpdateToolbar();
            SetStatus("没有可群发的已连接会话，无法开启群发", true);
            return;
        }

        _broadcast = enabled;
        UpdateToolbar();
        int count = BroadcastTargets().Count();
        SetStatus(enabled ? $"多会话群发已开启（{count} 个会话）" : "多会话群发已关闭");
    }

    private IEnumerable<TabEntry> BroadcastTargets() =>
        _tabs.Where(t => t.Session.IsConnected && !t.Locked && !t.Session.IsTransferActive);

    private void UpdateBroadcastRoutes()
    {
        foreach (var tab in _tabs)
        {
            if (_broadcast)
            {
                tab.View.BroadcastText = BroadcastText;
                tab.View.BroadcastBytes = BroadcastBytes;
            }
            else
            {
                tab.View.BroadcastText = null;
                tab.View.BroadcastBytes = null;
            }
        }
    }

    private void BroadcastText(string text)
    {
        foreach (var tab in BroadcastTargets().ToList())
        {
            tab.Session.SendText(text);
        }
    }

    private void BroadcastBytes(byte[] bytes)
    {
        foreach (var tab in BroadcastTargets().ToList())
        {
            tab.Session.SendBytes(bytes);
        }
    }

    private void LockMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_active is not null) ToggleSessionLock(_active);
    }

    private void ToggleSessionLock(TabEntry tab)
    {
        tab.Locked = !tab.Locked;
        tab.View.InputLocked = tab.Locked;
        tab.Session.InputLocked = tab.Locked;
        if (tab.Locked)
        {
            tab.Session.CancelScript();
            tab.Session.AbortFileTransfer();
            tab.Session.CancelPaste();
            // Drop pending expect input without re-arming once-only rules.
            tab.Session.Automation.ClearBuffer();
        }
        UpdateTabVisual(tab);
        UpdateToolbar();
        SetStatus(tab.Locked ? $"会话已锁定：{tab.Profile.Name}" : $"会话已解锁：{tab.Profile.Name}");
    }

    private void CommandBarMenuItem_Click(object sender, RoutedEventArgs e) =>
        SetCommandBarVisible(CommandBar.Visibility != Visibility.Visible);

    private void SetCommandBarVisible(bool visible)
    {
        if (visible && _active is null)
        {
            SetStatus("没有活动会话", true);
            return;
        }
        CommandBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TerminalPanes.Margin = visible ? new Thickness(0, 0, 0, 54) : new Thickness(0);
        CommandBarMenuItem.IsChecked = visible;
        if (visible)
        {
            CommandInput.Clear();
            ResetCommandNavigation();
            CommandInput.Focus();
            SetStatus("命令栏：Enter 发送，↑/↓ 历史，Tab 补全，Esc 关闭");
        }
        else
        {
            CommandInput.Clear();
            ResetCommandNavigation();
            if (_active is not null)
            {
                if (_active.HexMode) _active.HexView.Focus();
                else _active.View.Focus();
            }
        }
    }

    private void ResetCommandNavigation()
    {
        _completionPrefix = "";
        _completionIndex = 0;
        _completionActive = false;
        _completionLast = "";
        if (_active is not null)
        {
            _active.CommandHistoryIndex = _active.CommandHistory.Count;
            _active.CommandDraft = "";
        }
    }

    private void CommandInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SetCommandBarVisible(false);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            SendCommandLine();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Up || e.Key == Key.Down)
        {
            NavigateCommandHistory(e.Key == Key.Up);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Tab)
        {
            CompleteCommand();
            e.Handled = true;
        }
    }

    private void CommandSend_Click(object sender, RoutedEventArgs e) => SendCommandLine();

    private void SendCommandLine()
    {
        var tab = _active;
        if (tab is null) return;
        if (tab.Locked)
        {
            SetStatus("会话已锁定，无法发送命令", true);
            return;
        }
        if (!tab.Session.IsConnected)
        {
            SetStatus("会话未连接，无法发送命令", true);
            return;
        }
        if (tab.Session.IsTransferActive)
        {
            SetStatus("文件传输进行中，无法发送命令", true);
            return;
        }
        if (_broadcast && !BroadcastTargets().Any())
        {
            SetStatus("没有可群发的已连接会话", true);
            return;
        }

        string text = CommandInput.Text;
        if (text.Length == 0) return;

        if (tab.CommandHistory.Count == 0 || tab.CommandHistory[^1] != text)
        {
            tab.CommandHistory.Add(text);
            if (tab.CommandHistory.Count > 200) tab.CommandHistory.RemoveAt(0);
        }
        tab.CommandHistoryIndex = tab.CommandHistory.Count;
        tab.CommandDraft = "";
        _completionPrefix = "";
        _completionIndex = 0;
        _completionActive = false;
        _completionLast = "";

        if (_broadcast) BroadcastText(text + "\r");
        else tab.Session.SendText(text + "\r");

        CommandInput.Clear();
        CommandInput.Focus();
        SetStatus(_broadcast ? $"已群发命令：{text}" : $"已发送：{text}");
    }

    private void NavigateCommandHistory(bool up)
    {
        var tab = _active;
        if (tab is null || tab.CommandHistory.Count == 0) return;
        _completionActive = false;
        _completionLast = "";

        if (tab.CommandHistoryIndex < 0 || tab.CommandHistoryIndex > tab.CommandHistory.Count)
        {
            tab.CommandHistoryIndex = tab.CommandHistory.Count;
        }

        if (up)
        {
            if (tab.CommandHistoryIndex == tab.CommandHistory.Count)
            {
                tab.CommandDraft = CommandInput.Text;
            }
            tab.CommandHistoryIndex = Math.Max(0, tab.CommandHistoryIndex - 1);
        }
        else
        {
            tab.CommandHistoryIndex = Math.Min(tab.CommandHistory.Count, tab.CommandHistoryIndex + 1);
        }

        CommandInput.Text = tab.CommandHistoryIndex >= tab.CommandHistory.Count
            ? tab.CommandDraft
            : tab.CommandHistory[tab.CommandHistoryIndex];
        CommandInput.CaretIndex = CommandInput.Text.Length;
    }

    private void CompleteCommand()
    {
        var tab = _active;
        if (tab is null) return;

        string current = CommandInput.Text;
        bool cycling = _completionActive &&
                       current.StartsWith(_completionPrefix, StringComparison.Ordinal) &&
                       (current == _completionLast || _completionIndex == 0);
        if (!cycling)
        {
            _completionPrefix = current;
            _completionIndex = 0;
            _completionActive = true;
        }

        var candidates = tab.CommandHistory
            .AsEnumerable()
            .Reverse()
            .Concat(tab.Profile.QuickCommands.Select(q => q.Command))
            .Where(value => value.Length > 0 &&
                            value.StartsWith(_completionPrefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0)
        {
            _completionActive = false;
            return;
        }

        string completed = candidates[_completionIndex % candidates.Count];
        CommandInput.Text = completed;
        CommandInput.CaretIndex = completed.Length;
        _completionLast = completed;
        _completionIndex++;
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        var tab = _active;
        if (tab is null) return;

        var text = tab.View.GetPrintableText();
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("终端没有可打印内容", true);
            return;
        }

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length > 5000)
        {
            bool proceed = await ConfirmAsync(
                "打印内容较长",
                $"当前终端有 {lines.Length:N0} 行，打印可能需要较长时间。\n\n是否继续？",
                "继续打印");
            if (!proceed) return;
        }

        try
        {
            var dialog = new PrintDialog();
            if (dialog.ShowDialog() != true) return;

            var document = new FlowDocument
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 9,
                PagePadding = new Thickness(36),
                // PagePadding is inside PrintableAreaWidth; adding it back to
                // ColumnWidth would push every line past the right margin.
                ColumnWidth = Math.Max(1, dialog.PrintableAreaWidth - 72)
            };
            document.PageWidth = dialog.PrintableAreaWidth;
            document.PageHeight = dialog.PrintableAreaHeight;
            document.Blocks.Add(new Paragraph(new Run(
                $"{tab.Profile.Name} · {DateTime.Now:yyyy-MM-dd HH:mm:ss}"))
            {
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            });

            // Split the transcript into bounded paragraphs instead of one giant
            // Run; FlowDocument paginates large blocks much more predictably.
            const int LinesPerParagraph = 200;
            for (int start = 0; start < lines.Length; start += LinesPerParagraph)
            {
                int count = Math.Min(LinesPerParagraph, lines.Length - start);
                string chunk = string.Join('\n', lines, start, count);
                document.Blocks.Add(new Paragraph(new Run(chunk))
                {
                    Margin = new Thickness(0),
                    LineHeight = 12
                });
            }

            dialog.PrintDocument(
                ((IDocumentPaginatorSource)document).DocumentPaginator,
                $"Lumen - {tab.Profile.Name}");
            SetStatus("已发送到打印机");
        }
        catch (Exception ex)
        {
            SetStatus($"打印失败：{ex.Message}", true);
        }
    }

    private void ImportSessions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入会话",
            Filter = "Lumen 会话 (*.json)|*.json|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var imported = _store.Import(dialog.FileName);
            foreach (var profile in imported) _sessions.Add(profile);
            _sessionsView.Refresh();
            UpdateSessionCount();
            SetStatus($"已导入 {imported.Count} 个会话");
        }
        catch (Exception ex)
        {
            SetStatus($"导入失败：{ex.Message}", true);
        }
    }

    private void ExportSessions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出全部会话",
            Filter = "Lumen 会话 (*.json)|*.json",
            FileName = $"lumen-sessions-{DateTime.Now:yyyyMMdd}.json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _store.Export(dialog.FileName);
            SetStatus($"已导出 {_store.Sessions.Count} 个会话");
        }
        catch (Exception ex)
        {
            SetStatus($"导出失败：{ex.Message}", true);
        }
    }
}
