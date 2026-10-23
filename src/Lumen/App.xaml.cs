using System.IO;
using System.Windows;
using System.Windows.Threading;
using Lumen.Models;
using Lumen.Services;

namespace Lumen;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static SessionStore Store { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog(args.ExceptionObject as Exception);

        Settings = SettingsService.Load();
        Store = new SessionStore();
        Store.Load();

        var window = new MainWindow(Settings, Store, e.Args);
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        MessageBox.Show(
            "Lumen 遇到未处理的错误：\n\n" + e.Exception.Message + "\n\n详细信息已写入日志。",
            "Lumen Terminal",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void WriteCrashLog(Exception? exception)
    {
        if (exception is null) return;
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.AppendAllText(
                AppPaths.CrashLogFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // best effort
        }
    }
}
