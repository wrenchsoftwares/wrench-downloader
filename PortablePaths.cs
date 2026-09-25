using System;
using System.IO;

namespace WrenchDownloader;

public static class PortablePaths
{
    private static bool? _isPortable;

    public static string PackageDirectory
    {
        get
        {
            string appDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            return Directory.GetParent(appDirectory)?.FullName ?? appDirectory;
        }
    }

    public static bool IsPortable => _isPortable ??= File.Exists(Path.Combine(PackageDirectory, "portable.mode"));

    public static string DataDirectory
    {
        get
        {
            string dataDirectory = IsPortable
                ? Path.Combine(PackageDirectory, "Data")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WrenchDownloader");
            Directory.CreateDirectory(dataDirectory);
            return dataDirectory;
        }
    }

    public static string HistoryFilePath => Path.Combine(DataDirectory, "history.json");
    public static string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");
    public static string StartupLogPath => Path.Combine(DataDirectory, "startup.log");
}