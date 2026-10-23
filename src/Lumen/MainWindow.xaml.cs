using System.Collections.ObjectModel;
using System.ComponentModel;
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

public partial class MainWindow : Window
{
    private readonly SessionStore _store;
    private readonly string[] _args;
    private readonly ObservableCollection<SessionProfile> _sessions;
    private readonly ICollectionView _sessionsView;
    private readonly List<TabEntry> _tabs = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _deviceRefreshTimer;
    private AppSettings _settings;
    private IReadOnlyList<SerialPortInfo> _ports = Array.Empty<SerialPortInfo>();
    private TabEntry? _active;
    private IntPtr _hwnd;
    private bool _suppressToggle;

    public MainWindow(AppSettings settings, SessionStore store, string[] args)
    {
        _settings = settings;
        _store = store;
        _args = args;

        InitializeComponent();

        _sessions = new ObservableCollection<SessionProfile>(_store.Sessions);
        _sessionsView = CollectionViewSource.GetDefaultView(_sessions);
        _sessionsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SessionProfile.Group)));
        _sessionsView.Filter = item => FilterSession(item as SessionProfile);
        SessionList.ItemsSource = _sessionsView;

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pollTimer.Tick += (_, _) => UpdateStatusBar();

        _deviceRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _deviceRefreshTimer.Tick += (_, _) =>
        {
            _deviceRefreshTimer.Stop();
            RefreshPorts();
        };

        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;

        RefreshPorts();
        UpdateSessionCount();
        UpdateEmptyState();
        UpdateToolbar();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _pollTimer.Start();
        HandleStartupArgs();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyDwmEffects();
        if (HwndSource.FromHwnd(_hwnd) is { } source)
        {
            source.AddHook(WndProc);
        }
    }

    private void ApplyDwmEffects()
    {
        try
        {
            int dark = 1;
            DwmSetWindowAttribute(_hwnd, 20, ref dark, sizeof(int));
            int round = 2;
            DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));
        }
        catch
        {
            // Windows 10 or older: keep the flat border
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmDeviceChange = 0x0219;
        const int WmGetMinMaxInfo = 0x0024;

        if (msg == WmDeviceChange)
        {
            _deviceRefreshTimer.Stop();
            _deviceRefreshTimer.Start();
        }
        else if (msg == WmGetMinMaxInfo)
        {
            ApplyMaximizedBounds(hwnd, lParam);
            handled = true;
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private static void ApplyMaximizedBounds(IntPtr hwnd, IntPtr lParam)
    {
        try
        {
            var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var monitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var work = info.rcWork;
                var screen = info.rcMonitor;
                mmi.ptMaxPosition.X = work.Left - screen.Left;
                mmi.ptMaxPosition.Y = work.Top - screen.Top;
                mmi.ptMaxSize.X = work.Right - work.Left;
                mmi.ptMaxSize.Y = work.Bottom - work.Top;
                mmi.ptMaxTrackSize.X = work.Right - work.Left;
                mmi.ptMaxTrackSize.Y = work.Bottom - work.Top;
                Marshal.StructureToPtr(mmi, lParam, true);
            }
        }
        catch
        {
            // keep the default behavior
        }
    }

    private void HandleStartupArgs()
    {
        bool demo = _args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase))
                    || File.Exists(Path.Combine(AppPaths.ExeDirectory, "demo.flag"));
        if (demo)
        {
            OpenDemoSession();
            return;
        }

        var port = GetArg("--port");
        if (!string.IsNullOrWhiteSpace(port))
        {
            var profile = SessionProfile.CreateDefault($"{port} 控制台", "快速连接");
            profile.Id = "quick-" + port;
            profile.Serial.PortName = port;
            if (int.TryParse(GetArg("--baud"), out int baud)) profile.Serial.BaudRate = baud;
            profile.AutoLog = _settings.AutoLog;
            StartSession(profile, new SerialConnection(profile.Serial));
            return;
        }

        var sessionId = GetArg("--session");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var profile = _sessions.FirstOrDefault(s => s.Id == sessionId);
            if (profile is not null) StartSession(profile, new SerialConnection(profile.Serial));
        }
    }

    private string? GetArg(string name)
    {
        for (int i = 0; i < _args.Length - 1; i++)
        {
            if (_args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return _args[i + 1];
        }
        return null;
    }

    private void OpenDemoSession()
    {
        var profile = SessionProfile.CreateDefault("演示会话 · Cisco C2960", "演示");
        profile.Id = "demo-session";
        profile.Serial.PortName = "DEMO";
        profile.Serial.BaudRate = 9600;
        profile.AutoLog = false;
        profile.FontSize = _settings.FontSize;
        StartSession(profile, new DemoTransport());
    }

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
            FontSize = 11.5,
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
        LogButton.IsEnabled = has;
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

    private void UpdateStatusBar()
    {
        if (_active is null)
        {
            ThroughputText.Text = "";
            CursorText.Text = "";
            LogText.Text = "○ 未记录";
            return;
        }

        var session = _active.Session;
        var buffer = session.Buffer;
        ThroughputText.Text = $"RX {FormatBytes(session.BytesReceived)}    TX {FormatBytes(session.BytesSent)}";
        CursorText.Text = $"{buffer.Columns} × {buffer.Rows}  ·  {buffer.CursorRow + 1},{buffer.CursorCol + 1}";
        LogText.Text = session.IsLogging ? "● 日志记录中" : "○ 未记录";
        LogText.Foreground = session.IsLogging ? Res("Brush.Green") : Res("Brush.TextMuted");
        StatusDot.Background = session.IsConnected
            ? Res("Brush.Green")
            : _active.Connecting ? Res("Brush.Amber") : Res("Brush.Red");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes / (1024.0 * 1024.0):0.0} MB";
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        if (error)
        {
            StatusDot.Background = Res("Brush.Red");
        }
        else if (message == "就绪")
        {
            StatusDot.Background = Res("Brush.TextMuted");
        }
        else
        {
            StatusDot.Background = Res("Brush.Green");
        }
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private void CloseTab(TabEntry tab)
    {
        tab.Session.Dispose();
        TerminalPanes.Children.Remove(tab.Container);
        TabStrip.Children.Remove(tab.Header);
        _tabs.Remove(tab);

        if (ReferenceEquals(_active, tab))
        {
            _active = null;
            if (_tabs.Count > 0) ActivateTab(_tabs[^1]);
            else
            {
                UpdateToolbar();
                UpdateEmptyState();
                SetStatus("就绪");
            }
        }
        UpdateSessionCount();
    }

    private void CloseAllTabs()
    {
        foreach (var tab in _tabs.ToList())
        {
            tab.Session.Dispose();
        }
        _tabs.Clear();
        TerminalPanes.Children.Clear();
        TabStrip.Children.Clear();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        SettingsService.Save(_settings);
        _pollTimer.Stop();
        CloseAllTabs();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        RootBorder.Margin = maximized ? new Thickness(7) : new Thickness(0);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        try
        {
            DragMove();
        }
        catch
        {
            // drag can fail while maximizing
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T typed) return typed;
            current = current is ContentElement content
                ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
                : VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl && shift && e.Key == Key.T)
        {
            NewSession_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (ctrl && shift && e.Key == Key.W && _active is not null)
        {
            CloseTab(_active);
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.Tab)
        {
            CycleTab(shift ? -1 : 1);
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            int index = e.Key - Key.D1;
            if (index < _tabs.Count)
            {
                ActivateTab(_tabs[index]);
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.F11)
        {
            ToggleMaximize();
            e.Handled = true;
        }
    }

    private void CycleTab(int direction)
    {
        if (_tabs.Count == 0) return;
        int index = _active is null ? 0 : _tabs.IndexOf(_active);
        index = (index + direction + _tabs.Count) % _tabs.Count;
        ActivateTab(_tabs[index]);
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void RefreshPorts_Click(object sender, RoutedEventArgs e)
    {
        RefreshPorts();
        SetStatus(_ports.Count > 0 ? $"已发现 {_ports.Count} 个串口" : "未检测到串口设备");
    }

    private void RefreshPorts()
    {
        _ports = SerialPortEnumerator.GetPorts();
        var selectedPort = (PortCombo.SelectedItem as SerialPortInfo)?.PortName;
        if (_ports.Count == 0)
        {
            PortCombo.ItemTemplate = null;
            PortCombo.DisplayMemberPath = null;
            PortCombo.ItemsSource = new[] { "未检测到串口" };
            PortCombo.SelectedIndex = 0;
            PortHint.Text = "请检查 USB 转串口驱动是否安装";
        }
        else
        {
            PortCombo.DisplayMemberPath = null;
            PortCombo.ItemTemplate = (DataTemplate)FindResource("PortItemTemplate");
            PortCombo.ItemsSource = _ports;
            var selected = _ports.FirstOrDefault(p => p.PortName == selectedPort) ?? _ports[0];
            PortCombo.SelectedItem = selected;
            PortHint.Text = selected.DisplayName;
        }
        QuickConnectButton.IsEnabled = _ports.Count > 0;
    }

    private void PortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PortCombo.SelectedItem is SerialPortInfo port)
        {
            PortHint.Text = port.IsUsb ? port.DisplayName + " · USB" : port.DisplayName;
        }
    }

    private void QuickConnect_Click(object sender, RoutedEventArgs e)
    {
        if (PortCombo.SelectedItem is not SerialPortInfo port)
        {
            SetStatus("未检测到串口设备，请检查 USB 转串口驱动", true);
            return;
        }

        if (!int.TryParse(BaudText.Text.Trim(), out int baud) || baud < 300 || baud > 921600)
        {
            SetStatus("波特率无效，请输入 300 - 921600 之间的数值", true);
            BaudText.Focus();
            BaudText.SelectAll();
            return;
        }

        var profile = SessionProfile.CreateDefault($"{port.PortName} · {baud}", "快速连接");
        profile.Id = "quick-" + port.PortName;
        profile.Serial.PortName = port.PortName;
        profile.Serial.BaudRate = baud;
        profile.AutoLog = _settings.AutoLog;
        profile.FontSize = _settings.FontSize;
        StartSession(profile, new SerialConnection(profile.Serial));
    }

    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        var profile = SessionProfile.CreateDefault("新会话", "我的设备");
        profile.Serial.PortName = (_ports.FirstOrDefault()?.PortName) ?? "COM1";
        profile.AutoLog = _settings.AutoLog;
        profile.FontSize = _settings.FontSize;
        ShowSessionEditor(profile, true);
    }

    private void Demo_Click(object sender, RoutedEventArgs e) => OpenDemoSession();

    private void SessionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SessionList.SelectedItem is SessionProfile profile)
        {
            StartSession(profile, new SerialConnection(profile.Serial));
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
            StartSession(profile, new SerialConnection(profile.Serial));
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
                tab.TitleText.Text = updated.Name;
                tab.View.TerminalFontSize = updated.FontSize;
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
                    ? $"已保存会话：{updated.Name}（串口参数将在重新连接后生效）"
                    : $"已保存会话：{updated.Name}");
        };
        view.Cancelled += HideOverlay;
        ShowOverlay(view);
    }

    private void ShowOverlay(FrameworkElement content)
    {
        OverlayHost.Children.Clear();
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
        if (_active is not null) _active.View.Focus();
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
        delay = Math.Clamp(delay, 0, 1000);

        SetStatus($"正在粘贴（每行延迟 {delay} ms）…");
        await tab.Session.PasteAsync(text, delay);
        SetStatus("粘贴完成");
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

    private void Break_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        _ = SendBreakAsync(_active);
    }

    private async Task SendBreakAsync(TabEntry tab)
    {
        await tab.Session.SendBreakAsync();
        SetStatus("已发送 Break");
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

    private void Clear_Click(object sender, RoutedEventArgs e) => _active?.View.ClearScreen();

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
        if (FindBar.Visibility != Visibility.Visible) return;
        FindBar.Visibility = Visibility.Collapsed;
        _active?.View.ClearSearch();
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_active is null) return;
        int count = _active.View.SetSearch(FindBox.Text);
        FindCount.Text = $"{count} 项";
        if (count > 0) _active.View.FindNext(true);
    }

    private void FindPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        _active.View.SetSearch(FindBox.Text);
        _active.View.FindNext(false);
    }

    private void FindNext_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        _active.View.SetSearch(FindBox.Text);
        _active.View.FindNext(true);
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
            FontSize = 11.5,
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
            row.Children.Add(new TextBlock { Text = command.Name, FontSize = 12.5 });
            row.Children.Add(new TextBlock
            {
                Text = command.Command,
                FontFamily = (FontFamily)FindResource("Font.Mono"),
                FontSize = 10.5,
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

    private async Task SendQuickCommandAsync(SessionProfile profile, QuickCommand command)
    {
        if (_active is null || !_active.Session.IsConnected) return;
        if (command.Confirm)
        {
            bool ok = await ConfirmAsync("确认发送", $"即将发送命令：\n\n{command.Command}", "发送");
            if (!ok) return;
        }
        _active.Session.SendText(command.Command + "\r");
        HideOverlay();
        SetStatus($"已发送：{command.Name}");
    }

    private void FindButton_Click(object sender, RoutedEventArgs e) => ShowFind();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point ptReserved;
        public Point ptMaxSize;
        public Point ptMaxPosition;
        public Point ptMinTrackSize;
        public Point ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    private sealed class TabEntry
    {
        public TabEntry(SessionProfile profile, TerminalSession session)
        {
            Profile = profile;
            Session = session;
        }

        public SessionProfile Profile { get; }
        public TerminalSession Session { get; }
        public TerminalView View { get; set; } = null!;
        public Grid Container { get; set; } = null!;
        public Border Header { get; set; } = null!;
        public TextBlock TitleText { get; set; } = null!;
        public Border Dot { get; set; } = null!;
        public Button CloseButton { get; set; } = null!;
        public bool Connecting { get; set; }
        public bool AutoVendor { get; set; }
    }
}
