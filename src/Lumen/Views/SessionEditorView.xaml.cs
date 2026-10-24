using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lumen.Models;
using Lumen.Serial;
using Microsoft.Win32;

namespace Lumen.Views;

public partial class SessionEditorView : UserControl
{
    private readonly SessionProfile _profile;
    private readonly ObservableCollection<QuickCommand> _commands;
    private readonly bool _isNew;
    private List<SerialPortInfo> _ports;
    private bool _vendorInitializing;

    public SessionEditorView(SessionProfile profile, IReadOnlyList<SerialPortInfo> ports, bool isNew = false)
    {
        InitializeComponent();
        _profile = profile;
        _isNew = isNew;
        _ports = ports.ToList();
        _commands = new ObservableCollection<QuickCommand>(profile.QuickCommands.Select(q => q.Clone()));
        CommandsList.ItemsSource = _commands;

        HeaderText.Text = isNew ? "新建会话" : "编辑会话";

        _vendorInitializing = true;
        VendorBox.ItemsSource = VendorCatalog.All;
        VendorBox.SelectedItem = VendorCatalog.Find(profile.VendorId);
        AutoPagingCheck.IsChecked = profile.AutoPaging;
        UpdateVendorHint();
        _vendorInitializing = false;

        DataBitsBox.ItemsSource = new[] { "5", "6", "7", "8" };
        ParityBox.ItemsSource = new[] { "无 (N)", "奇 (O)", "偶 (E)", "Mark (M)", "Space (S)" };
        StopBitsBox.ItemsSource = new[] { "1", "1.5", "2" };
        FlowBox.ItemsSource = new[] { "无", "RTS / CTS 硬件流控", "XON / XOFF 软件流控" };
        EncodingBox.ItemsSource = new[] { "UTF-8", "GBK", "ASCII" };
        TransportBox.ItemsSource = new[] { "串口", "命名管道" };
        ColumnsBox.ItemsSource = new[] { "自适应窗口", "80 列", "132 列" };

        NameBox.Text = profile.Name;
        GroupBox.Text = profile.Group;
        PipeNameBox.Text = profile.PipeName;
        TransportBox.SelectedIndex = profile.TransportKind == SessionTransportKind.NamedPipe ? 1 : 0;
        ColumnsBox.SelectedIndex = profile.TerminalColumns switch
        {
            80 => 1,
            132 => 2,
            _ => 0
        };
        BaudBox.Text = profile.Serial.BaudRate.ToString();
        DataBitsBox.SelectedItem = Math.Clamp(profile.Serial.DataBits, 5, 8).ToString();
        ParityBox.SelectedIndex = (int)profile.Serial.Parity;
        StopBitsBox.SelectedIndex = (int)profile.Serial.StopBits;
        FlowBox.SelectedIndex = (int)profile.Serial.FlowControl;
        EncodingBox.SelectedIndex = EncodingIndex(profile.Encoding);
        FontSizeBox.Text = profile.FontSize.ToString("0.#");
        PasteDelayBox.Text = profile.PasteLineDelayMs.ToString();
        BreakDurationBox.Text = profile.Serial.BreakDurationMs.ToString();
        SendCharDelayBox.Text = profile.SendCharDelayMs.ToString();
        EchoTimeoutBox.Text = profile.EchoTimeoutMs.ToString();
        WaitEchoCheck.IsChecked = profile.WaitForEcho;
        AutoReconnectCheck.IsChecked = profile.AutoReconnect;
        RawLogCheck.IsChecked = profile.RawLog;
        AutoLogCheck.IsChecked = profile.AutoLog;
        ConfirmPasteCheck.IsChecked = profile.ConfirmDangerousPaste;
        InitCommandsBox.Text = profile.InitCommands;
        LogDirBox.Text = profile.LogDirectory ?? "";

        LoadPorts(profile.Serial.PortName);
        UpdateTransportFields();
        Loaded += (_, _) =>
        {
            if (isNew)
            {
                NameBox.Focus();
                NameBox.SelectAll();
            }
        };
    }

    public event Action<SessionProfile>? Saved;
    public event Action? Cancelled;

    private void LoadPorts(string preferredPort)
    {
        if (_ports.Count == 0)
        {
            _ports.Add(new SerialPortInfo("COM1", "未检测到串口设备", ""));
        }
        else if (_ports.All(p => !p.PortName.Equals(preferredPort, StringComparison.OrdinalIgnoreCase)))
        {
            _ports.Insert(0, new SerialPortInfo(preferredPort, "当前配置", ""));
        }

        PortBox.ItemsSource = null;
        PortBox.ItemsSource = _ports;
        PortBox.SelectedItem = _ports.FirstOrDefault(p => p.PortName.Equals(preferredPort, StringComparison.OrdinalIgnoreCase))
                               ?? _ports[0];
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e)
    {
        var selected = (PortBox.SelectedItem as SerialPortInfo)?.PortName ?? _profile.Serial.PortName;
        _ports = SerialPortEnumerator.GetPorts();
        LoadPorts(selected);
    }

    private void TransportBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateTransportFields();

    private void UpdateTransportFields()
    {
        bool pipe = TransportBox.SelectedIndex == 1;
        PipeNameBox.IsEnabled = pipe;
        PortBox.IsEnabled = !pipe;
        BaudBox.IsEnabled = !pipe;
        DataBitsBox.IsEnabled = !pipe;
        ParityBox.IsEnabled = !pipe;
        StopBitsBox.IsEnabled = !pipe;
        FlowBox.IsEnabled = !pipe;
    }

    private static int EncodingIndex(string encoding)
    {
        return (encoding ?? "").Trim().ToLowerInvariant() switch
        {
            "gbk" or "gb2312" or "gb18030" or "cp936" or "936" => 1,
            "ascii" or "us-ascii" => 2,
            _ => 0
        };
    }

    private static string EncodingName(int index) => index switch
    {
        1 => "gbk",
        2 => "ascii",
        _ => "utf-8"
    };

    private void BrowseLogDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择日志目录",
            InitialDirectory = string.IsNullOrWhiteSpace(LogDirBox.Text)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : LogDirBox.Text
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            LogDirBox.Text = dialog.FolderName;
        }
    }

    private void AddCommand_Click(object sender, RoutedEventArgs e)
    {
        _commands.Add(new QuickCommand { Name = "新命令", Command = "" });
    }

    private void VendorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vendorInitializing) return;
        UpdateVendorHint();

        if (_isNew && VendorBox.SelectedItem is VendorProfile vendor && vendor.Id != "auto")
        {
            ApplyVendor(vendor);
        }
    }

    private void ApplyVendor_Click(object sender, RoutedEventArgs e)
    {
        if (VendorBox.SelectedItem is VendorProfile vendor && vendor.Id != "auto")
        {
            ApplyVendor(vendor);
        }
    }

    private void ApplyVendor(VendorProfile vendor)
    {
        BaudBox.Text = vendor.DefaultBaud.ToString();
        EncodingBox.SelectedIndex = EncodingIndex(vendor.Encoding);
        AutoPagingCheck.IsChecked = !string.IsNullOrWhiteSpace(vendor.PagingCommand);

        _commands.Clear();
        foreach (var command in vendor.QuickCommands)
        {
            _commands.Add(command.Clone());
        }

        UpdateVendorHint();
    }

    private void UpdateVendorHint()
    {
        var vendor = VendorBox.SelectedItem as VendorProfile;
        if (vendor is null)
        {
            VendorHint.Text = "";
            return;
        }

        if (vendor.Id == "auto")
        {
            VendorHint.Text = "根据设备启动信息自动识别厂商；识别后会按厂商命令自动关闭分页。";
            return;
        }

        VendorHint.Text = string.IsNullOrWhiteSpace(vendor.Notes)
            ? VendorCatalog.Describe(vendor)
            : $"{VendorCatalog.Describe(vendor)} · {vendor.Notes}";
    }

    private void RemoveCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QuickCommand command })
        {
            _commands.Remove(command);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";

        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ErrorText.Text = "请输入会话名称";
            NameBox.Focus();
            return;
        }

        bool pipeTransport = TransportBox.SelectedIndex == 1;
        int baud = _profile.Serial.BaudRate;
        if (pipeTransport)
        {
            if (int.TryParse(BaudBox.Text.Trim(), out int pipeBaud) && pipeBaud is >= 300 and <= 921600)
            {
                baud = pipeBaud;
            }
        }
        else if (!int.TryParse(BaudBox.Text.Trim(), out baud) || baud < 300 || baud > 921600)
        {
            ErrorText.Text = "波特率需在 300 - 921600 之间";
            BaudBox.Focus();
            BaudBox.SelectAll();
            return;
        }

        string pipeName = NamedPipeTransport.NormalizePipeName(PipeNameBox.Text);
        if (pipeTransport && pipeName.Length == 0)
        {
            ErrorText.Text = "请输入命名管道名称";
            PipeNameBox.Focus();
            return;
        }

        if (!double.TryParse(FontSizeBox.Text.Trim(), out double fontSize) || fontSize < 9 || fontSize > 40)
        {
            ErrorText.Text = "字号需在 9 - 40 之间";
            FontSizeBox.Focus();
            FontSizeBox.SelectAll();
            return;
        }

        if (!int.TryParse(PasteDelayBox.Text.Trim(), out int delay) || delay < 0 || delay > 5000)
        {
            ErrorText.Text = "粘贴行延迟需在 0 - 5000 毫秒之间";
            PasteDelayBox.Focus();
            PasteDelayBox.SelectAll();
            return;
        }

        if (!int.TryParse(BreakDurationBox.Text.Trim(), out int breakDuration) ||
            breakDuration < 10 || breakDuration > 5000)
        {
            ErrorText.Text = "Break 脉冲需在 10 - 5000 毫秒之间";
            BreakDurationBox.Focus();
            BreakDurationBox.SelectAll();
            return;
        }

        if (!int.TryParse(SendCharDelayBox.Text.Trim(), out int charDelay) || charDelay < 0 || charDelay > 1000)
        {
            ErrorText.Text = "字符延迟需在 0 - 1000 毫秒之间";
            SendCharDelayBox.Focus();
            SendCharDelayBox.SelectAll();
            return;
        }

        if (!int.TryParse(EchoTimeoutBox.Text.Trim(), out int echoTimeout) ||
            echoTimeout < 200 || echoTimeout > 60000)
        {
            ErrorText.Text = "回显超时需在 200 - 60000 毫秒之间";
            EchoTimeoutBox.Focus();
            EchoTimeoutBox.SelectAll();
            return;
        }

        var port = PortBox.SelectedItem as SerialPortInfo;
        var serial = _profile.Serial.Clone();
        serial.PortName = port?.PortName ?? _profile.Serial.PortName;
        serial.BaudRate = baud;
        serial.DataBits = int.TryParse(DataBitsBox.SelectedItem?.ToString(), out int bits) ? bits : 8;
        serial.Parity = (SerialParity)Math.Clamp(ParityBox.SelectedIndex, 0, 4);
        serial.StopBits = (SerialStopBits)Math.Clamp(StopBitsBox.SelectedIndex, 0, 2);
        serial.FlowControl = (SerialFlowControl)Math.Clamp(FlowBox.SelectedIndex, 0, 2);
        serial.BreakDurationMs = breakDuration;

        var updated = _profile.Clone();
        updated.Name = name;
        updated.Group = string.IsNullOrWhiteSpace(GroupBox.Text) ? "我的设备" : GroupBox.Text.Trim();
        updated.VendorId = (VendorBox.SelectedItem as VendorProfile)?.Id ?? "auto";
        updated.TransportKind = pipeTransport ? SessionTransportKind.NamedPipe : SessionTransportKind.Serial;
        updated.PipeName = pipeTransport ? pipeName : "";
        updated.Serial = serial;
        updated.Encoding = EncodingName(EncodingBox.SelectedIndex);
        updated.FontSize = fontSize;
        updated.TerminalColumns = ColumnsBox.SelectedIndex switch { 1 => 80, 2 => 132, _ => 0 };
        updated.LogDirectory = string.IsNullOrWhiteSpace(LogDirBox.Text) ? null : LogDirBox.Text.Trim();
        updated.AutoLog = AutoLogCheck.IsChecked == true;
        updated.AutoPaging = AutoPagingCheck.IsChecked == true;
        updated.ConfirmDangerousPaste = ConfirmPasteCheck.IsChecked == true;
        updated.PasteLineDelayMs = delay;
        updated.SendCharDelayMs = charDelay;
        updated.WaitForEcho = WaitEchoCheck.IsChecked == true;
        updated.EchoTimeoutMs = echoTimeout;
        updated.RawLog = RawLogCheck.IsChecked == true;
        updated.AutoReconnect = AutoReconnectCheck.IsChecked == true;
        updated.InitCommands = InitCommandsBox.Text.Trim();
        updated.QuickCommands = _commands.ToList();

        Saved?.Invoke(updated);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();

    private void Root_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancelled?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Save_Click(sender, e);
            e.Handled = true;
        }
    }
}
