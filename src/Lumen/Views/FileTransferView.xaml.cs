using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lumen.Terminal;
using Lumen.Transfer;
using Microsoft.Win32;

namespace Lumen.Views;

public partial class FileTransferView : UserControl
{
    private readonly TerminalSession _session;
    private readonly bool _connected;
    private CancellationTokenSource? _cts;
    private bool _running;

    public FileTransferView(TerminalSession session, bool connected)
    {
        InitializeComponent();
        _session = session;
        _connected = connected;

        ProtocolBox.ItemsSource = new[]
        {
            "XMODEM（128 字节，自动 CRC）",
            "XMODEM-1K",
            "XMODEM-Checksum（兼容旧设备）",
            "YMODEM（含文件名和大小）"
        };
        ProtocolBox.SelectedIndex = 0;
        DirectionBox.ItemsSource = new[] { "发送文件", "接收文件" };
        DirectionBox.SelectedIndex = 0;
        UpdateDirectionUi();
        Loaded += (_, _) => PathBox.Focus();
    }

    public event Action<string>? StatusMessage;
    public event Action? Cancelled;

    private bool SendMode => DirectionBox.SelectedIndex == 0;

    private TransferProtocol Protocol => ProtocolBox.SelectedIndex switch
    {
        1 => TransferProtocol.Xmodem1K,
        2 => TransferProtocol.XmodemChecksum,
        3 => TransferProtocol.Ymodem,
        _ => TransferProtocol.Xmodem
    };

    private void DirectionBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateDirectionUi();

    private void UpdateDirectionUi()
    {
        if (StartButton is null) return;
        StartButton.Content = SendMode ? "开始发送" : "开始接收";
        HintText.Text = SendMode
            ? "选择本地文件后让设备进入接收模式（如 bootloader 的 xmodem 命令），再开始发送。"
            : "让设备开始发送文件后立刻点击开始接收；YMODEM 会读取远端文件名和大小。";
        if (string.IsNullOrWhiteSpace(PathBox.Text) && !_running)
        {
            PathBox.Text = SendMode ? "" : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "received.bin");
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (SendMode)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择要发送的文件",
                Filter = "所有文件|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) PathBox.Text = dialog.FileName;
            return;
        }

        var save = new SaveFileDialog
        {
            Title = "选择接收文件保存位置",
            Filter = "所有文件|*.*",
            FileName = string.IsNullOrWhiteSpace(PathBox.Text) ? "received.bin" : Path.GetFileName(PathBox.Text),
            OverwritePrompt = true
        };
        if (save.ShowDialog(Window.GetWindow(this)) == true) PathBox.Text = save.FileName;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (_running) return;

        if (!_connected || !_session.IsConnected)
        {
            ErrorText.Text = "会话未连接，无法传输文件";
            return;
        }

        var path = PathBox.Text.Trim().Trim('"');
        if (path.Length == 0)
        {
            ErrorText.Text = "请选择本地文件";
            return;
        }

        if (SendMode && !File.Exists(path))
        {
            ErrorText.Text = "发送文件不存在";
            return;
        }

        if (!SendMode)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        }

        var options = new TransferOptions(
            Protocol,
            SendMode ? TransferDirection.Send : TransferDirection.Receive,
            path);

        _running = true;
        SetRunningUi(true);
        _cts = new CancellationTokenSource();
        var progress = new Progress<TransferProgress>(OnProgress);
        StatusText.Text = "开始传输…";
        Progress.IsIndeterminate = true;
        PercentText.Text = "";
        ByteText.Text = "";

        try
        {
            await _session.RunFileTransferAsync(options, progress, _cts.Token);
            StatusText.Text = SendMode ? "发送完成" : "接收完成";
            Progress.IsIndeterminate = false;
            Progress.Value = 100;
            PercentText.Text = "100%";
            StatusMessage?.Invoke(SendMode
                ? $"文件发送完成：{Path.GetFileName(path)}"
                : $"文件接收完成：{Path.GetFileName(path)}");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "传输已取消";
            ErrorText.Text = "传输已取消";
            Progress.IsIndeterminate = false;
            StatusMessage?.Invoke("文件传输已取消");
        }
        catch (Exception ex)
        {
            StatusText.Text = "传输失败";
            ErrorText.Text = ex.Message;
            Progress.IsIndeterminate = false;
            StatusMessage?.Invoke($"文件传输失败：{ex.Message}");
        }
        finally
        {
            _running = false;
            _cts?.Dispose();
            _cts = null;
            SetRunningUi(false);
        }
    }

    private void OnProgress(TransferProgress progressValue)
    {
        StatusText.Text = progressValue.Status;
        if (progressValue.TotalBytes > 0)
        {
            double percent = Math.Clamp(
                progressValue.Bytes * 100.0 / progressValue.TotalBytes, 0, 100);
            Progress.IsIndeterminate = false;
            Progress.Value = percent;
            PercentText.Text = $"{percent:0.0}%";
            ByteText.Text = $"{progressValue.Bytes} / {progressValue.TotalBytes} 字节";
        }
        else
        {
            Progress.IsIndeterminate = true;
            PercentText.Text = "";
            ByteText.Text = $"{progressValue.Bytes} 字节";
        }
    }

    private void SetRunningUi(bool running)
    {
        ProtocolBox.IsEnabled = !running;
        DirectionBox.IsEnabled = !running;
        PathBox.IsEnabled = !running;
        StartButton.IsEnabled = !running;
        CancelButton.Content = running ? "取消传输" : "关闭";
        CancelButton.IsEnabled = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _cts?.Cancel();
            StatusText.Text = "正在取消…";
            CancelButton.IsEnabled = false;
            return;
        }

        Cancelled?.Invoke();
    }

    private void Root_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_running)
        {
            _cts?.Cancel();
        }
        else
        {
            Cancelled?.Invoke();
        }
        e.Handled = true;
    }
}
