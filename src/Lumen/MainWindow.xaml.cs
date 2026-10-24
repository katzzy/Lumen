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
    private CancellationTokenSource? _findSearchCts;

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
            _ = DwmSetWindowAttribute(_hwnd, 20, ref dark, sizeof(int));
            int round = 2;
            _ = DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));
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
            foreach (var tab in _tabs)
            {
                if (tab.Session.Transport is SerialConnection serial) serial.NotifyDevicesChanged();
            }
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
            ApplySettingsDefaults(profile);
            StartSession(profile, CreateTransport(profile));
            return;
        }

        var sessionId = GetArg("--session");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var profile = _sessions.FirstOrDefault(s => s.Id == sessionId);
            if (profile is not null) StartSession(profile, CreateTransport(profile));
        }
    }

    private static SerialConnection CreateTransport(SessionProfile profile) =>
        new(profile.Serial, profile.AutoReconnect);

    private void ApplySettingsDefaults(SessionProfile profile)
    {
        profile.AutoLog = _settings.AutoLog;
        profile.RawLog = _settings.RawLog;
        profile.AutoReconnect = _settings.AutoReconnect;
        profile.FontSize = _settings.FontSize;
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
        LogText.Text = session.IsLogging
            ? session.Profile.RawLog ? "● 原始日志" : "● 日志记录中"
            : "○ 未记录";
        LogText.Foreground = session.IsLogging ? Res("Brush.Green") : Res("Brush.TextMuted");
        StatusDot.Background = session.IsConnected
            ? Res("Brush.Green")
            : _active.Connecting || session.IsReconnecting ? Res("Brush.Amber") : Res("Brush.Red");
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
        if (OverlayHost.Visibility == Visibility.Visible) return;

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
        if (ctrl && shift && e.Key == Key.H)
        {
            SendBytes_Click(sender, e);
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

    private void TabScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TabScroll.ScrollableWidth <= 0) return;
        TabScroll.ScrollToHorizontalOffset(TabScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
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
        var previous = _ports;
        _ports = SerialPortEnumerator.GetPorts();

        var openPort = _active?.Profile.Serial.PortName;
        if (!string.IsNullOrWhiteSpace(openPort) &&
            previous.Any(p => p.PortName.Equals(openPort, StringComparison.OrdinalIgnoreCase)) &&
            !_ports.Any(p => p.PortName.Equals(openPort, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"{openPort} 已断开，等待设备重新插入…", true);
        }

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
            PortHint.Text = string.IsNullOrEmpty(selected.KindLabel)
                ? selected.DisplayName
                : $"{selected.DisplayName} · {selected.KindLabel}";
        }
        QuickConnectButton.IsEnabled = _ports.Count > 0;
    }

    private void PortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PortCombo.SelectedItem is SerialPortInfo port)
        {
            PortHint.Text = string.IsNullOrEmpty(port.KindLabel)
                ? port.DisplayName
                : $"{port.DisplayName} · {port.KindLabel}";
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

        var busy = FindBusyTabForPort(port.PortName);
        if (busy is not null)
        {
            ActivateTab(busy);
            if (busy.Profile.Serial.BaudRate != baud)
            {
                SetStatus($"该串口已在标签中打开（{busy.Profile.Serial.BaudRate} baud），请先关闭标签再切换波特率", true);
            }
            return;
        }

        var profile = SessionProfile.CreateDefault($"{port.PortName} · {baud}", "快速连接");
        profile.Id = $"quick-{port.PortName}-{baud}";
        profile.Serial.PortName = port.PortName;
        profile.Serial.BaudRate = baud;
        ApplySettingsDefaults(profile);
        StartSession(profile, CreateTransport(profile));
    }

    private TabEntry? FindBusyTabForPort(string portName)
    {
        return _tabs.FirstOrDefault(t =>
            (t.Connecting || t.Session.IsConnected || t.Session.IsReconnecting) &&
            string.Equals(t.Profile.Serial.PortName, portName, StringComparison.OrdinalIgnoreCase));
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        var tab = _active;
        if (tab is null || tab.Profile.Id == "demo-session") return;

        var profile = tab.Profile;
        string port = profile.Serial.PortName;
        CloseTab(tab);
        SetStatus($"正在重新连接 {port}…");
        StartSession(profile, CreateTransport(profile));
    }

    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        var profile = SessionProfile.CreateDefault("新会话", "我的设备");
        profile.Serial.PortName = _ports.Count > 0 ? _ports[0].PortName : "COM1";
        ApplySettingsDefaults(profile);
        ShowSessionEditor(profile, true);
    }

    private void Demo_Click(object sender, RoutedEventArgs e) => OpenDemoSession();

    private async Task SendQuickCommandAsync(SessionProfile profile, QuickCommand command)
    {
        var tab = _active;
        if (tab is null || !tab.Session.IsConnected) return;

        if (command.Confirm)
        {
            bool ok = await ConfirmAsync("确认发送", $"即将发送命令：\n\n{command.Command}", "发送");
            if (!ok) return;
        }

        if (!tab.Session.IsConnected) return;
        tab.Session.SendText(command.Command + "\r");
        HideOverlay();
        SetStatus($"已发送：{command.Name}");
    }

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
        public HexView HexView { get; set; } = null!;
        public bool HexMode { get; set; }
        public Grid Container { get; set; } = null!;
        public Border Header { get; set; } = null!;
        public TextBlock TitleText { get; set; } = null!;
        public Border Dot { get; set; } = null!;
        public Button CloseButton { get; set; } = null!;
        public bool Connecting { get; set; }
        public bool AutoVendor { get; set; }
    }
}
