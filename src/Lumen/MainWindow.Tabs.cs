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
    private async void StartSession(SessionProfile profile, ITerminalTransport transport)
    {
        var existing = _tabs.FirstOrDefault(t => t.Profile.Id == profile.Id);
        if (existing is not null)
        {
            ActivateTab(existing);
            return;
        }

        var session = new TerminalSession(profile, transport, _settings);
        var tab = new TabEntry(profile, session) { Connecting = true, AutoVendor = VendorCatalog.IsAuto(profile.VendorId) };
        var view = new TerminalView
        {
            Session = session,
            TerminalFontSize = profile.FontSize > 0 ? profile.FontSize : _settings.FontSize,
            TerminalFontFamily = _settings.FontFamily
        };
        tab.View = view;

        var container = new Grid { Visibility = Visibility.Collapsed };
        container.Children.Add(view);
        tab.Container = container;
        TerminalPanes.Children.Add(container);

        view.PasteRequested += text => _ = PasteIntoSessionAsync(tab, text);
        view.FindRequested += () => ShowFind();
        view.CopyPerformed += () => SetStatus("已复制到剪贴板");
        view.Cleared += () => SetStatus("已清屏（仅本机）");
        session.PasteProgress += (current, total) => SetStatus($"正在粘贴 {current}/{total} 行…");
        session.StateChanged += () =>
        {
            UpdateTabVisual(tab);
            if (ReferenceEquals(_active, tab)) UpdateToolbar();
        };
        session.TitleChanged += () =>
        {
            if (ReferenceEquals(_active, tab) && !string.IsNullOrWhiteSpace(session.Title))
            {
                SetStatus($"设备标题：{session.Title}");
            }
        };
        session.VendorDetected += vendor =>
        {
            tab.Profile.VendorId = vendor.Id;
            tab.Profile.AutoPaging = !string.IsNullOrWhiteSpace(vendor.PagingCommand);
            if (tab.AutoVendor)
            {
                tab.Profile.QuickCommands = vendor.QuickCommands.Select(q => q.Clone()).ToList();
            }
            if (string.Equals(session.Profile.Encoding, vendor.Encoding, StringComparison.OrdinalIgnoreCase))
            {
                tab.Profile.Encoding = vendor.Encoding;
            }

            UpdateTabVisual(tab);
            if (ReferenceEquals(_active, tab))
            {
                UpdateToolbar();
                ActiveSubtitle.Text = BuildSubtitle(tab);
                EncodingText.Text = tab.Profile.Encoding.ToUpperInvariant();
                SetStatus($"已识别厂商：{vendor.DisplayName}");
            }
        };

        BuildTabHeader(tab);
        _tabs.Add(tab);
        ActivateTab(tab);
        UpdateEmptyState();
        UpdateSessionCount();

        SetStatus($"正在连接 {profile.Name}…");
        bool ok = await session.ConnectAsync();
        tab.Connecting = false;
        UpdateTabVisual(tab);
        UpdateToolbar();

        if (ok)
        {
            SetStatus($"已连接 {profile.Serial.PortName} · {profile.Serial.BaudRate}");
            view.Focus();
            var serial = session.Transport as SerialConnection;
            if (serial is not null)
            {
                _suppressToggle = true;
                DtrToggle.IsChecked = serial.Dtr;
                RtsToggle.IsChecked = serial.Rts;
                _suppressToggle = false;
            }
        }
        else
        {
            SetStatus($"连接失败：{profile.Serial.PortName}", true);
        }
    }

    private void BuildTabHeader(TabEntry tab)
    {
        var header = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 5, 4, 5),
            Margin = new Thickness(2, 0, 2, 0),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            MaxWidth = 232,
            VerticalAlignment = VerticalAlignment.Center
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = Res("Brush.TextMuted"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        };
        var title = new TextBlock
        {
            Text = tab.Profile.Name,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("Brush.TextSecondary")
        };
        var close = new Button
        {
            Style = (Style)FindResource("BtnIcon"),
            Content = "\uE711",
            Width = 20,
            Height = 20,
            FontSize = 8,
            Margin = new Thickness(6, 0, 0, 0),
            Opacity = 0.55,
            ToolTip = "关闭会话"
        };
        close.Click += (_, e) =>
        {
            e.Handled = true;
            CloseTab(tab);
        };
        System.Windows.Automation.AutomationProperties.SetName(close, "关闭会话");

        Grid.SetColumn(dot, 0);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(close, 2);
        grid.Children.Add(dot);
        grid.Children.Add(title);
        grid.Children.Add(close);
        header.Child = grid;

        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            ActivateTab(tab);
            e.Handled = true;
        };
        header.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                CloseTab(tab);
                e.Handled = true;
            }
        };
        header.MouseEnter += (_, _) =>
        {
            if (!ReferenceEquals(_active, tab)) header.Background = Res("Brush.Surface");
        };
        header.MouseLeave += (_, _) => UpdateTabVisual(tab);

        tab.Header = header;
        tab.TitleText = title;
        tab.Dot = dot;
        tab.CloseButton = close;
        TabStrip.Children.Add(header);
    }

    private void ActivateTab(TabEntry? tab)
    {
        _active = tab;
        foreach (var item in _tabs)
        {
            item.Container.Visibility = ReferenceEquals(item, tab) ? Visibility.Visible : Visibility.Collapsed;
            UpdateTabVisual(item);
        }

        if (tab is null)
        {
            UpdateToolbar();
            UpdateEmptyState();
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        ActiveTitle.Text = tab.Profile.Name;
        ActiveSubtitle.Text = BuildSubtitle(tab);
        EncodingText.Text = tab.Profile.Encoding.ToUpperInvariant();
        UpdateToolbar();
        tab.View.Focus();
        HideFind();
        if (tab.Session.IsConnected) SetStatus($"已连接 {tab.Profile.Serial.PortName} · {tab.Profile.Serial.BaudRate}");
    }

    private void UpdateTabVisual(TabEntry tab)
    {
        bool active = ReferenceEquals(_active, tab);
        tab.Header.Background = active ? Res("Brush.SurfaceAlt") : Brushes.Transparent;
        tab.Header.BorderBrush = active ? Res("Brush.Border") : Brushes.Transparent;
        tab.TitleText.Foreground = active ? Res("Brush.Text") : Res("Brush.TextSecondary");
        tab.TitleText.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        tab.CloseButton.Opacity = active ? 0.9 : 0.45;

        tab.Dot.Background = tab.Session.IsConnected
            ? Res("Brush.Green")
            : tab.Connecting ? Res("Brush.Amber") : Res("Brush.TextMuted");

        if (active)
        {
            ActiveSubtitle.Text = BuildSubtitle(tab);
        }
    }

    private static string BuildSubtitle(TabEntry tab)
    {
        var s = tab.Profile.Serial;
        var vendor = VendorCatalog.Find(tab.Profile.VendorId);
        var vendorName = vendor.Id == "auto" ? "自动" : vendor.Name;
        var state = tab.Session.IsConnected ? "已连接" : tab.Connecting ? "连接中…" : "未连接";
        return $"{vendorName} · {s.PortName} · {s.BaudRate} · {s.DataBits}{ParityLetter(s.Parity)}{StopLetter(s.StopBits)} · {state}";
    }

    private static string ParityLetter(SerialParity parity) => parity switch
    {
        SerialParity.Odd => "O",
        SerialParity.Even => "E",
        SerialParity.Mark => "M",
        SerialParity.Space => "S",
        _ => "N"
    };

    private static string StopLetter(SerialStopBits stop) => stop switch
    {
        SerialStopBits.Two => "2",
        SerialStopBits.OnePointFive => "1.5",
        _ => "1"
    };

    private void UpdateToolbar()
    {
        var session = _active?.Session;
        bool has = session is not null;
        bool connected = session?.IsConnected == true;

        ActiveDot.Background = connected
            ? Res("Brush.Green")
            : has ? Res("Brush.Amber") : Res("Brush.TextMuted");
        ActiveTitle.Text = _active?.Profile.Name ?? "未连接";
        ActiveSubtitle.Text = _active is null
            ? "选择左侧会话，或使用快速连接"
            : BuildSubtitle(_active);

        BreakButton.IsEnabled = connected;
        ReconnectButton.IsEnabled = has && _active?.Profile.Id != "demo-session";
        LogButton.IsEnabled = has;
        LogFolderButton.IsEnabled = has;
        QuickCommandButton.IsEnabled = has;
        ClearButton.IsEnabled = has;
        FindButton.IsEnabled = has;

        var serial = session?.Transport as SerialConnection;
        DtrToggle.IsEnabled = serial is not null;
        RtsToggle.IsEnabled = serial is not null;
        _suppressToggle = true;
        DtrToggle.IsChecked = serial?.Dtr ?? false;
        RtsToggle.IsChecked = serial?.Rts ?? false;
        _suppressToggle = false;

        LogButton.Content = session?.IsLogging == true ? "停止日志" : "日志";
        EncodingText.Text = _active?.Profile.Encoding.ToUpperInvariant() ?? "UTF-8";
        Title = _active is null ? "Lumen Terminal" : $"Lumen Terminal — {_active.Profile.Name}";
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = _tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSessionCount()
    {
        SessionCount.Text = _sessionsView.Cast<object>().Count().ToString();
    }

}
