using System;
using System.IO;
using Windows.Storage;

namespace WrenchDownloader;

/// <summary>
/// Persisted app settings (download folder, parallelism, default quality).
/// </summary>
public static class SettingsHelper
{
    private const string FolderKey = "DownloadFolder";
    private const string FragmentsKey = "ConcurrentFragments";
    private const string QualityKey = "DefaultQuality";
    private const string ShowDialogKey = "ShowDownloadDialog";
    private const string CloseToTrayKey = "CloseToTray";
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "WrenchDownloader";

    private static string? _cachedFolder;
    private static int? _cachedFragments;
    private static string? _cachedQuality;
    private static bool? _cachedShowDialog;
    private static bool? _cachedCloseToTray;
    private static bool? _cachedStartWithWindows;

    public static string DefaultDownloadsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public static string DownloadFolder
    {
        get
        {
            if (_cachedFolder != null) return _cachedFolder;
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values[FolderKey] as string;
                if (!string.IsNullOrWhiteSpace(v) && Directory.Exists(v))
                {
                    _cachedFolder = v;
                    return v;
                }
            }
            catch { }
            _cachedFolder = DefaultDownloadsFolder;
            return _cachedFolder;
        }
    }

    public static int ConcurrentFragments
    {
        get
        {
            if (_cachedFragments.HasValue) return _cachedFragments.Value;
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values[FragmentsKey];
                if (v is int i)
                {
                    _cachedFragments = Math.Clamp(i, 1, 32);
                    return _cachedFragments.Value;
                }
            }
            catch { }
            _cachedFragments = 8;
            return 8;
        }
    }

    public static string DefaultQuality
    {
        get
        {
            if (_cachedQuality != null) return _cachedQuality;
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values[QualityKey] as string;
                if (!string.IsNullOrWhiteSpace(v))
                {
                    _cachedQuality = v;
                    return v;
                }
            }
            catch { }
            _cachedQuality = "best";
            return "best";
        }
    }

    public static bool ShowDownloadDialog
    {
        get
        {
            if (_cachedShowDialog.HasValue) return _cachedShowDialog.Value;
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values[ShowDialogKey];
                if (v is bool b)
                {
                    _cachedShowDialog = b;
                    return b;
                }
            }
            catch { }
            _cachedShowDialog = true;
            return true;
        }
    }

    public static bool CloseToTray
    {
        get
        {
#if DEBUG
            return false;
#else
            if (_cachedCloseToTray.HasValue) return _cachedCloseToTray.Value;
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values[CloseToTrayKey];
                if (v is bool b)
                {
                    _cachedCloseToTray = b;
                    return b;
                }
            }
            catch { }
            _cachedCloseToTray = true; // Default to minimize to tray on close
            return true;
#endif
        }
    }

    public static bool StartWithWindows
    {
        get
        {
#if DEBUG
            return false;
#else
            if (_cachedStartWithWindows.HasValue) return _cachedStartWithWindows.Value;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
                _cachedStartWithWindows = key?.GetValue(AppName) != null;
                return _cachedStartWithWindows.Value;
            }
            catch
            {
                _cachedStartWithWindows = false;
                return false;
            }
#endif
        }
    }

    public static void SetStartWithWindows(bool enable)
    {
        _cachedStartWithWindows = enable;
#if !DEBUG
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
            if (key == null) return;

            if (enable)
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AppName, $"\"{exePath}\" --background");
                }
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch { }
#endif
    }

    public static void Save(string folder, int fragments, string quality, bool showDialog, bool closeToTray, bool startWithWindows)
    {
        _cachedFolder = (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)) ? folder : DefaultDownloadsFolder;
        _cachedFragments = Math.Clamp(fragments, 1, 32);
        _cachedQuality = !string.IsNullOrWhiteSpace(quality) ? quality : "best";
        _cachedShowDialog = showDialog;
        _cachedCloseToTray = closeToTray;

        SetStartWithWindows(startWithWindows);

        try
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                values[FolderKey] = folder;
            values[FragmentsKey] = _cachedFragments.Value;
            if (!string.IsNullOrWhiteSpace(quality))
                values[QualityKey] = quality;
            values[ShowDialogKey] = showDialog;
            values[CloseToTrayKey] = closeToTray;
        }
        catch { }
    }

    public static void Save(string folder, int fragments, string quality, bool showDialog)
    {
        Save(folder, fragments, quality, showDialog, CloseToTray, StartWithWindows);
    }
}
