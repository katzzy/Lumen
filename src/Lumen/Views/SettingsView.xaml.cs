using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lumen.Models;
using Lumen.Services;
using Microsoft.Win32;

namespace Lumen.Views;

public partial class SettingsView : UserControl
{
    private readonly AppSettings _settings;

    public SettingsView(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        FontFamilyBox.ItemsSource = new[] { "Cascadia Mono", "Cascadia Code", "Consolas", "Sarasa Mono SC" };
        FontFamilyBox.SelectedItem = FontFamilyBox.Items.Contains(settings.FontFamily)
            ? settings.FontFamily
            : "Cascadia Mono";
        FontSizeBox.Text = settings.FontSize.ToString("0.#");
        ScrollbackBox.Text = settings.ScrollbackLines.ToString();
        PasteDelayBox.Text = settings.DefaultPasteDelayMs.ToString();
        LogRootBox.Text = settings.LogRoot ?? "";
        AutoLogCheck.IsChecked = settings.AutoLog;
        TimestampCheck.IsChecked = settings.LogTimestamps;
        ConfirmPasteCheck.IsChecked = settings.ConfirmDangerousPaste;
        DataPathText.Text = AppPaths.DataDirectory;
    }

    public event Action<AppSettings>? Saved;
    public event Action? Cancelled;

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        FontFamilyBox.SelectedItem = "Cascadia Mono";
        FontSizeBox.Text = "14";
        ScrollbackBox.Text = "10000";
        PasteDelayBox.Text = "100";
        AutoLogCheck.IsChecked = true;
        TimestampCheck.IsChecked = false;
        ConfirmPasteCheck.IsChecked = true;
    }

    private void BrowseLogRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择日志根目录",
            InitialDirectory = string.IsNullOrWhiteSpace(LogRootBox.Text)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : LogRootBox.Text
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            LogRootBox.Text = dialog.FolderName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (!double.TryParse(FontSizeBox.Text.Trim(), out double fontSize) || fontSize < 9 || fontSize > 40)
        {
            ErrorText.Text = "字号需在 9 - 40 之间";
            return;
        }
        if (!int.TryParse(ScrollbackBox.Text.Trim(), out int scrollback) || scrollback < 1000 || scrollback > 200000)
        {
            ErrorText.Text = "回滚行数需在 1000 - 200000 之间";
            return;
        }
        if (!int.TryParse(PasteDelayBox.Text.Trim(), out int delay) || delay < 0 || delay > 1000)
        {
            ErrorText.Text = "粘贴延迟需在 0 - 1000 毫秒之间";
            return;
        }

        var updated = new AppSettings
        {
            FontFamily = FontFamilyBox.SelectedItem?.ToString() ?? "Cascadia Mono",
            FontSize = fontSize,
            ScrollbackLines = scrollback,
            LogRoot = LogRootBox.Text.Trim(),
            AutoLog = AutoLogCheck.IsChecked == true,
            LogTimestamps = TimestampCheck.IsChecked == true,
            ConfirmDangerousPaste = ConfirmPasteCheck.IsChecked == true,
            DefaultPasteDelayMs = delay,
            LastSessionId = _settings.LastSessionId,
            ShowWelcome = _settings.ShowWelcome
        };

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
    }
}
