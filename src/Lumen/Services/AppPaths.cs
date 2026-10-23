using System.IO;

namespace Lumen.Services;

public static class AppPaths
{
    private static string? _dataDir;

    public static string ExeDirectory => AppContext.BaseDirectory;

    public static bool IsPortable =>
        File.Exists(Path.Combine(ExeDirectory, "portable.flag")) ||
        Directory.Exists(Path.Combine(ExeDirectory, "data"));

    public static string DataDirectory
    {
        get
        {
            if (_dataDir is not null) return _dataDir;
            if (IsPortable)
            {
                _dataDir = Path.Combine(ExeDirectory, "data");
                Directory.CreateDirectory(_dataDir);
                return _dataDir;
            }

            var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumen");
            try
            {
                Directory.CreateDirectory(roaming);
                _dataDir = roaming;
            }
            catch
            {
                _dataDir = Path.Combine(ExeDirectory, "data");
                Directory.CreateDirectory(_dataDir);
            }
            return _dataDir;
        }
    }

    public static string SessionsFile => Path.Combine(DataDirectory, "sessions.json");
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string CrashLogFile => Path.Combine(DataDirectory, "lumen.log");

    public static string DefaultLogRoot => IsPortable
        ? Path.Combine(ExeDirectory, "logs")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Lumen Logs");

    public static string EnsureLogRoot(string? configured)
    {
        var dir = string.IsNullOrWhiteSpace(configured) ? DefaultLogRoot : configured;
        Directory.CreateDirectory(dir);
        return dir;
    }
}
