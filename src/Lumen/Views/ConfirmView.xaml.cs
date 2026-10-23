using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lumen.Views;

public partial class ConfirmView : UserControl
{
    public ConfirmView(string title, string message, string confirmText)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    public event Action? Confirmed;
    public event Action? Cancelled;

    private void Root_Loaded(object sender, RoutedEventArgs e) => ConfirmButton.Focus();

    private void Confirm_Click(object sender, RoutedEventArgs e) => Confirmed?.Invoke();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();

    private void Root_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancelled?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Confirmed?.Invoke();
            e.Handled = true;
        }
    }
}
