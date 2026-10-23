using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Lumen.Models;
using Lumen.Services;

namespace Lumen;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;

    public static AppSettings Settings { get; private set; } = new();
    public static SessionStore Store { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _instanceMutex = new Mutex(true, @"Local\LumenTerminal.SingleInstance", out bool createdNew);
            _ownsInstanceMutex = createdNew;
            if (!createdNew)
            {
                MessageBox.Show(
                    "Lumen 已在运行，请在任务栏切换到已有窗口。",
                    "Lumen Terminal",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }
        catch
        {
            // If the mutex cannot be created, continue without the guard.
        }

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

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_ownsInstanceMutex) _instanceMutex?.ReleaseMutex();
        }
        catch
        {
            // ignore
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
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
