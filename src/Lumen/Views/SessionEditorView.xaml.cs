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
    private List<SerialPortInfo> _ports;

    public SessionEditorView(SessionProfile profile, IReadOnlyList<SerialPortInfo> ports, bool isNew = false)
    {
        InitializeComponent();
        _profile = profile;
        _ports = ports.ToList();
        _commands = new ObservableCollection<QuickCommand>(profile.QuickCommands.Select(q => q.Clone()));
        CommandsList.ItemsSource = _commands;

        HeaderText.Text = isNew ? "新建会话" : "编辑会话";

        DataBitsBox.ItemsSource = new[] { "5", "6", "7", "8" };
        ParityBox.ItemsSource = new[] { "无 (N)", "奇 (O)", "偶 (E)", "Mark (M)", "Space (S)" };
        StopBitsBox.ItemsSource = new[] { "1", "1.5", "2" };
        FlowBox.ItemsSource = new[] { "无", "RTS / CTS 硬件流控", "XON / XOFF 软件流控" };
        EncodingBox.ItemsSource = new[] { "UTF-8", "GBK", "ASCII" };

        NameBox.Text = profile.Name;
        GroupBox.Text = profile.Group;
        BaudBox.Text = profile.Serial.BaudRate.ToString();
        DataBitsBox.SelectedItem = Math.Clamp(profile.Serial.DataBits, 5, 8).ToString();
        ParityBox.SelectedIndex = (int)profile.Serial.Parity;
        StopBitsBox.SelectedIndex = (int)profile.Serial.StopBits;
        FlowBox.SelectedIndex = (int)profile.Serial.FlowControl;
        EncodingBox.SelectedIndex = EncodingIndex(profile.Encoding);
        FontSizeBox.Text = profile.FontSize.ToString("0.#");
        PasteDelayBox.Text = profile.PasteLineDelayMs.ToString();
        AutoLogCheck.IsChecked = profile.AutoLog;
        ConfirmPasteCheck.IsChecked = profile.ConfirmDangerousPaste;
        InitCommandsBox.Text = profile.InitCommands;
        LogDirBox.Text = profile.LogDirectory ?? "";

        LoadPorts(profile.Serial.PortName);
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

        if (!int.TryParse(BaudBox.Text.Trim(), out int baud) || baud < 300 || baud > 921600)
        {
            ErrorText.Text = "波特率需在 300 - 921600 之间";
            BaudBox.Focus();
            BaudBox.SelectAll();
            return;
        }

        if (!double.TryParse(FontSizeBox.Text.Trim(), out double fontSize) || fontSize < 9 || fontSize > 40)
        {
            ErrorText.Text = "字号需在 9 - 40 之间";
            FontSizeBox.Focus();
            FontSizeBox.SelectAll();
            return;
        }

        if (!int.TryParse(PasteDelayBox.Text.Trim(), out int delay) || delay < 0 || delay > 1000)
        {
            ErrorText.Text = "粘贴延迟需在 0 - 1000 毫秒之间";
            PasteDelayBox.Focus();
            PasteDelayBox.SelectAll();
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

        var updated = _profile.Clone();
        updated.Name = name;
        updated.Group = string.IsNullOrWhiteSpace(GroupBox.Text) ? "我的设备" : GroupBox.Text.Trim();
        updated.Serial = serial;
        updated.Encoding = EncodingName(EncodingBox.SelectedIndex);
        updated.FontSize = fontSize;
        updated.LogDirectory = string.IsNullOrWhiteSpace(LogDirBox.Text) ? null : LogDirBox.Text.Trim();
        updated.AutoLog = AutoLogCheck.IsChecked == true;
        updated.ConfirmDangerousPaste = ConfirmPasteCheck.IsChecked == true;
        updated.PasteLineDelayMs = delay;
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
