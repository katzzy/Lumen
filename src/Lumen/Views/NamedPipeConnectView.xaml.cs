using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lumen.Serial;

namespace Lumen.Views;

public partial class NamedPipeConnectView : UserControl
{
    public NamedPipeConnectView()
    {
        InitializeComponent();
        LoadPipes();
        Loaded += (_, _) => PipeBox.Focus();
    }

    public event Action<string>? ConnectRequested;
    public event Action? Cancelled;

    private void LoadPipes()
    {
        var names = NamedPipeEnumerator.GetPipeNames();
        PipeBox.ItemsSource = names;
        HintText.Text = names.Count > 0
            ? $"已发现 {names.Count} 个管道。可直接输入名称，也可从列表选择。"
            : "未发现可用管道，可直接输入名称，例如 com_1。";
    }

    private void RefreshPipes_Click(object sender, RoutedEventArgs e)
    {
        LoadPipes();
        PipeBox.Focus();
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var name = NamedPipeTransport.NormalizePipeName(PipeBox.Text);
        if (name.Length == 0)
        {
            ErrorText.Text = "请输入管道名称";
            PipeBox.Focus();
            return;
        }

        ErrorText.Text = "";
        ConnectRequested?.Invoke(name);
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
            Connect_Click(sender, e);
            e.Handled = true;
        }
    }
}
