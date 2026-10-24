using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lumen.Terminal;

namespace Lumen.Views;

public partial class SendBytesView : UserControl
{
    private static readonly (string Label, ByteInputMode Mode)[] Modes =
    {
        ("文本", ByteInputMode.Text),
        ("转义序列（\\x1B、\\r\\n、^C）", ByteInputMode.Escape),
        ("十六进制（1B 5B 32 4A）", ByteInputMode.Hex)
    };

    private readonly Encoding _encoding;
    private readonly bool _connected;
    private byte[] _bytes = Array.Empty<byte>();

    public SendBytesView(Encoding encoding, bool connected)
    {
        InitializeComponent();
        _encoding = encoding;
        _connected = connected;

        ModeBox.ItemsSource = Modes.Select(m => m.Label).ToArray();
        ModeBox.SelectedIndex = 2;
        RebuildPreview();
        Loaded += (_, _) => InputBox.Focus();
    }

    public event Action<byte[]>? SendRequested;
    public event Action? Cancelled;

    private ByteInputMode CurrentMode => ModeBox.SelectedIndex is >= 0 and < 3
        ? Modes[ModeBox.SelectedIndex].Mode
        : ByteInputMode.Hex;

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ModeHint.Text = CurrentMode switch
        {
            ByteInputMode.Text => $"按会话编码（{_encoding.WebName.ToUpperInvariant()}）发送纯文本。",
            ByteInputMode.Escape => "支持 \\xHH、\\uHHHH、\\0 \\a \\b \\e \\f \\n \\r \\t \\v，以及 ^C 形式的控制字符。",
            _ => "每两个十六进制数字表示一个字节，支持空格、逗号、冒号、连字符和 0x 前缀。"
        };
        RebuildPreview();
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildPreview();

    private void RebuildPreview()
    {
        if (ModeBox.SelectedIndex < 0) return;

        ErrorText.Text = "";
        if (ByteSequenceParser.TryParse(InputBox.Text, CurrentMode, _encoding, out var bytes, out var error))
        {
            _bytes = bytes;
            ByteCountText.Text = $"{bytes.Length} 字节";
            SendButton.IsEnabled = _connected && bytes.Length > 0;
            PreviewText.Text = FormatHex(bytes);
            PreviewAsciiText.Text = FormatAscii(bytes);
            if (!_connected) ErrorText.Text = "会话未连接";
        }
        else
        {
            _bytes = Array.Empty<byte>();
            SendButton.IsEnabled = false;
            ByteCountText.Text = "—";
            PreviewText.Text = "";
            PreviewAsciiText.Text = "";
            ErrorText.Text = error;
        }
    }

    private static string FormatHex(byte[] bytes)
    {
        if (bytes.Length == 0) return "（空）";
        const int limit = 256;
        var sb = new StringBuilder(Math.Min(bytes.Length, limit) * 3);
        for (int i = 0; i < bytes.Length && i < limit; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }
        if (bytes.Length > limit) sb.Append($" …（共 {bytes.Length} 字节）");
        return sb.ToString();
    }

    private static string FormatAscii(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        const int limit = 256;
        var sb = new StringBuilder(Math.Min(bytes.Length, limit));
        for (int i = 0; i < bytes.Length && i < limit; i++)
        {
            byte b = bytes[i];
            sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
        }
        if (bytes.Length > limit) sb.Append("…");
        return sb.ToString();
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        if (!_connected)
        {
            ErrorText.Text = "会话未连接，无法发送";
            return;
        }

        if (_bytes.Length == 0)
        {
            ErrorText.Text = "没有可发送的字节";
            return;
        }

        var payload = _bytes;
        SendRequested?.Invoke(payload);

        if (ClearAfterSendCheck.IsChecked == true)
        {
            InputBox.Clear();
            InputBox.Focus();
        }
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
            Send_Click(sender, e);
            e.Handled = true;
        }
    }
}
