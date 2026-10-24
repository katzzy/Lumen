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
            TerminalFontFamily = _settings.FontFamily,
            FixedColumns = profile.TerminalColumns
        };
        tab.View = view;

        var hexView = new HexView
        {
            Session = session,
            HexFontSize = Math.Max(10, profile.FontSize > 0 ? profile.FontSize - 1.5 : _settings.FontSize - 1.5),
            Visibility = Visibility.Collapsed
        };
        tab.HexView = hexView;

        var container = new Grid { Visibility = Visibility.Collapsed };
        container.Children.Add(view);
        container.Children.Add(hexView);
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
            SetStatus(profile.AutoReconnect
                ? $"连接失败：{profile.Serial.PortName}，正在自动重试…"
                : $"连接失败：{profile.Serial.PortName}", true);
        }
    }

    private void ToggleHexMode()
    {
        var tab = _active;
        if (tab is null) return;

        tab.HexMode = !tab.HexMode;
        tab.View.Visibility = tab.HexMode ? Visibility.Collapsed : Visibility.Visible;
        tab.HexView.Visibility = tab.HexMode ? Visibility.Visible : Visibility.Collapsed;
        if (tab.HexMode)
        {
            tab.HexView.Focus();
            SetStatus("已切换到 Hex 视图（滚轮滚动，Ctrl+C 复制全部）");
        }
        else
        {
            tab.View.Focus();
            SetStatus("已切换到终端视图");
        }
        UpdateToolbar();
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
        if (CommandBar.Visibility == Visibility.Visible)
        {
            CommandInput.Clear();
            ResetCommandNavigation();
            CommandInput.Focus();
        }
        else if (tab.HexMode) tab.HexView.Focus();
        else tab.View.Focus();
        HideFind();
        if (tab.Session.IsConnected)
        {
            SetStatus(tab.Profile.TransportKind == SessionTransportKind.NamedPipe
                ? $"已连接命名管道 {tab.Profile.PipeName}"
                : $"已连接 {tab.Profile.Serial.PortName} · {tab.Profile.Serial.BaudRate}");
        }
    }

    private void UpdateTabVisual(TabEntry tab)
    {
        bool active = ReferenceEquals(_active, tab);
        tab.Header.Background = active ? Res("Brush.SurfaceAlt") : Brushes.Transparent;
        tab.Header.BorderBrush = active ? Res("Brush.Border") : Brushes.Transparent;
        tab.TitleText.Text = tab.Locked ? $"[锁定] {tab.Profile.Name}" : tab.Profile.Name;
        tab.TitleText.Foreground = active ? Res("Brush.Text") : Res("Brush.TextSecondary");
        tab.TitleText.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        tab.CloseButton.Opacity = active ? 0.9 : 0.45;

        tab.Dot.Background = tab.Session.IsConnected
            ? Res("Brush.Green")
            : tab.Connecting || tab.Session.IsReconnecting ? Res("Brush.Amber") : Res("Brush.TextMuted");

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
        var state = tab.Session.IsConnected
            ? "已连接"
            : tab.Connecting
                ? "连接中…"
                : tab.Session.IsReconnecting ? "自动重连中…" : "未连接";
        var endpoint = tab.Profile.TransportKind == SessionTransportKind.NamedPipe
            ? $"管道 {tab.Profile.PipeName}"
            : $"{s.PortName} · {s.BaudRate} · {s.DataBits}{ParityLetter(s.Parity)}{StopLetter(s.StopBits)}";
        return $"{vendorName} · {endpoint} · {state}";
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
        bool locked = _active?.Locked == true;

        ActiveDot.Background = connected
            ? Res("Brush.Green")
            : has ? Res("Brush.Amber") : Res("Brush.TextMuted");
        SendBytesButton.IsEnabled = connected && _active?.Locked != true;
        TransferButton.IsEnabled = connected && _active?.Locked != true &&
                                   session?.IsTransferActive != true;
        ActiveTitle.Text = _active?.Profile.Name ?? "未连接";
        ActiveSubtitle.Text = _active is null
            ? "选择左侧会话，或使用快速连接"
            : BuildSubtitle(_active);
        ActiveSubtitle.ToolTip = ActiveSubtitle.Text;
        ActiveTitle.ToolTip = ActiveSubtitle.Text;

        BreakButton.IsEnabled = connected && _active?.Locked != true &&
                                session?.Transport is not NamedPipeTransport;
        ReconnectButton.IsEnabled = has && _active?.Profile.Id != "demo-session";
        HexToggle.IsEnabled = has;
        LogButton.IsEnabled = has;
        LogFolderMenuItem.IsEnabled = has;
        QuickCommandButton.IsEnabled = has && _active?.Locked != true;

        var serial = session?.Transport as SerialConnection;
        // DTR/RTS and baud changes poke the hardware; a read-only session must
        // not be able to reset the device through them.
        DtrToggle.IsEnabled = serial is not null && !locked;
        RtsToggle.IsEnabled = serial is not null && !locked;
        _suppressToggle = true;
        DtrToggle.IsChecked = serial?.Dtr ?? false;
        RtsToggle.IsChecked = serial?.Rts ?? false;
        _suppressToggle = false;

        BaudCombo.IsEnabled = connected && serial is not null && !locked;
        int baud = _active?.Profile.Serial.BaudRate ?? 9600;
        EnsureBaudRate(baud);
        _suppressBaud = true;
        BaudCombo.SelectedItem = baud;
        _suppressBaud = false;

        LogButton.Content = session?.IsLogging == true ? "停止日志" : "日志";
        _suppressToggle = true;
        HexToggle.IsChecked = _active?.HexMode == true;
        _suppressToggle = false;
        EncodingText.Text = _active?.Profile.Encoding.ToUpperInvariant() ?? "UTF-8";
        Title = _active is null ? "Lumen Terminal" : $"Lumen Terminal — {_active.Profile.Name}";
        var modes = new List<string>();
        if (_broadcast) modes.Add($"群发 {BroadcastTargets().Count()}");
        if (_active?.Locked == true) modes.Add("锁定");
        ModeText.Text = string.Join(" · ", modes);
        ModeText.Visibility = modes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateMoreMenuChecks();
        UpdateBroadcastRoutes();
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
