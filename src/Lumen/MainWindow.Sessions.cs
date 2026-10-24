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
    private void SessionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SessionList.SelectedItem is SessionProfile profile)
        {
            StartSession(profile, CreateTransport(profile));
        }
    }

    private void SessionList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is not null) item.IsSelected = true;
    }

    private void SessionContext_Connect(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionProfile profile)
        {
            StartSession(profile, CreateTransport(profile));
        }
    }

    private void SessionContext_Edit(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionProfile profile) ShowSessionEditor(profile, false);
    }

    private void SessionContext_Duplicate(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionProfile profile) return;
        var copy = profile.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = profile.Name + " 副本";
        _store.Add(copy);
        _sessions.Add(copy);
        _sessionsView.Refresh();
        UpdateSessionCount();
        SetStatus($"已复制会话：{copy.Name}");
    }

    private async void SessionContext_Delete(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionProfile profile) return;
        bool ok = await ConfirmAsync("删除会话", $"确定删除「{profile.Name}」吗？此操作不会删除已经保存的日志文件。", "删除");
        if (!ok) return;

        var open = _tabs.FirstOrDefault(t => t.Profile.Id == profile.Id);
        if (open is not null) CloseTab(open);
        _store.Remove(profile);
        _sessions.Remove(profile);
        _sessionsView.Refresh();
        UpdateSessionCount();
        SetStatus($"已删除会话：{profile.Name}");
    }

    private void SessionSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SessionSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _sessionsView.Refresh();
        UpdateSessionCount();
    }

    private bool FilterSession(SessionProfile? profile)
    {
        if (profile is null) return false;
        var query = SessionSearch.Text.Trim();
        if (query.Length == 0) return true;
        return profile.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
               || profile.Group.Contains(query, StringComparison.OrdinalIgnoreCase)
               || profile.Serial.PortName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var view = new SettingsView(_settings.Clone());
        view.Saved += settings =>
        {
            _settings = settings;
            SettingsService.Save(settings);
            foreach (var tab in _tabs)
            {
                tab.View.TerminalFontSize = settings.FontSize;
                tab.View.TerminalFontFamily = settings.FontFamily;
                tab.HexView.HexFontSize = Math.Max(10, settings.FontSize - 1.5);
                tab.Session.Buffer.ScrollbackLimit = Math.Max(1000, settings.ScrollbackLines);
            }
            HideOverlay();
            SetStatus("设置已保存");
        };
        view.Cancelled += HideOverlay;
        ShowOverlay(view);
    }

    private void ShowSessionEditor(SessionProfile profile, bool isNew)
    {
        var view = new SessionEditorView(profile.Clone(), _ports, isNew);
        view.Saved += updated =>
        {
            if (isNew)
            {
                _store.Add(updated);
                _sessions.Add(updated);
            }
            else
            {
                _store.Update(updated);
                var index = _sessions.ToList().FindIndex(s => s.Id == updated.Id);
                if (index >= 0) _sessions[index] = updated;
            }

            _sessionsView.Refresh();
            UpdateSessionCount();
            var tab = _tabs.FirstOrDefault(t => t.Profile.Id == updated.Id);
            if (tab is not null)
            {
                tab.Profile.Name = updated.Name;
                tab.Profile.Group = updated.Group;
                tab.Profile.VendorId = updated.VendorId;
                tab.Profile.Encoding = updated.Encoding;
                tab.Profile.AutoPaging = updated.AutoPaging;
                tab.Profile.QuickCommands = updated.QuickCommands.Select(q => q.Clone()).ToList();
                tab.Profile.PasteLineDelayMs = updated.PasteLineDelayMs;
                tab.Profile.SendCharDelayMs = updated.SendCharDelayMs;
                tab.Profile.WaitForEcho = updated.WaitForEcho;
                tab.Profile.EchoTimeoutMs = updated.EchoTimeoutMs;
                tab.Profile.RawLog = updated.RawLog;
                tab.Profile.AutoReconnect = updated.AutoReconnect;
                tab.Profile.Serial.BreakDurationMs = updated.Serial.BreakDurationMs;
                tab.Profile.LogDirectory = updated.LogDirectory;
                tab.Profile.ConfirmDangerousPaste = updated.ConfirmDangerousPaste;
                tab.Profile.InitCommands = updated.InitCommands;
                tab.Profile.AutoLog = updated.AutoLog;

                // Runtime session settings that can change without reconnecting.
                tab.Session.Profile.Name = updated.Name;
                tab.Session.Profile.LogDirectory = updated.LogDirectory;
                tab.Session.Profile.RawLog = updated.RawLog;
                tab.Session.Profile.AutoLog = updated.AutoLog;
                tab.Session.Profile.AutoReconnect = updated.AutoReconnect;
                tab.Session.Profile.Serial.BreakDurationMs = updated.Serial.BreakDurationMs;
                if (tab.Session.Transport is SerialConnection serial) serial.SetAutoReconnect(updated.AutoReconnect);

                tab.TitleText.Text = updated.Name;
                tab.View.TerminalFontSize = updated.FontSize;
                tab.HexView.HexFontSize = Math.Max(10, updated.FontSize - 1.5);
                UpdateTabVisual(tab);
                if (ReferenceEquals(_active, tab))
                {
                    ActiveSubtitle.Text = BuildSubtitle(tab);
                    EncodingText.Text = updated.Encoding.ToUpperInvariant();
                }
            }
            HideOverlay();
            SetStatus(isNew
                ? $"已创建会话：{updated.Name}"
                : tab is not null
                    ? $"已保存会话：{updated.Name}（串口参数、编码与初始化命令将在重新连接后生效）"
                    : $"已保存会话：{updated.Name}");
        };
        view.Cancelled += HideOverlay;
        ShowOverlay(view);
    }

}
