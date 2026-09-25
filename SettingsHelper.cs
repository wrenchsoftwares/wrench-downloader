using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
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

    private static Dictionary<string, JsonElement>? _portableSettings;
    private static string? _cachedFolder;
    private static int? _cachedFragments;
    private static string? _cachedQuality;
    private static bool? _cachedShowDialog;
    private static bool? _cachedCloseToTray;
    private static bool? _cachedStartWithWindows;

    public static bool IsPortable => PortablePaths.IsPortable;

    private static T? ReadValue<T>(string key)
    {
        try
        {
            if (PortablePaths.IsPortable)
            {
                return LoadPortableSettings().TryGetValue(key, out var stored)
                    ? stored.Deserialize<T>()
                    : default;
            }

            var value = ApplicationData.Current.LocalSettings.Values[key];
            return value is T typed ? typed : default;
        }
        catch
        {
            return default;
        }
    }

    private static Dictionary<string, JsonElement> LoadPortableSettings()
    {
        if (_portableSettings != null) return _portableSettings;

        try
        {
            if (File.Exists(PortablePaths.SettingsFilePath))
            {
                _portableSettings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    File.ReadAllText(PortablePaths.SettingsFilePath));
            }
        }
        catch { }

        return _portableSettings ??= new Dictionary<string, JsonElement>();
    }

    public static string DefaultDownloadsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public static string DownloadFolder
    {
        get
        {
            if (_cachedFolder != null) return _cachedFolder;
            try
            {
                var v = ReadValue<string>(FolderKey);
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
                var v = ReadValue<int?>(FragmentsKey);
                if (v.HasValue)
                {
                    _cachedFragments = Math.Clamp(v.Value, 1, 32);
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
                var v = ReadValue<string>(QualityKey);
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
                var v = ReadValue<bool?>(ShowDialogKey);
                if (v.HasValue)
                {
                    _cachedShowDialog = v.Value;
                    return v.Value;
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
                var v = ReadValue<bool?>(CloseToTrayKey);
                if (v.HasValue)
                {
                    _cachedCloseToTray = v.Value;
                    return v.Value;
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
            if (PortablePaths.IsPortable) return false;
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
        if (PortablePaths.IsPortable)
        {
            _cachedStartWithWindows = false;
            return;
        }

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
            if (PortablePaths.IsPortable)
            {
                var values = LoadPortableSettings();
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                    values[FolderKey] = JsonSerializer.SerializeToElement(folder);
                values[FragmentsKey] = JsonSerializer.SerializeToElement(_cachedFragments.Value);
                if (!string.IsNullOrWhiteSpace(quality))
                    values[QualityKey] = JsonSerializer.SerializeToElement(quality);
                values[ShowDialogKey] = JsonSerializer.SerializeToElement(showDialog);
                values[CloseToTrayKey] = JsonSerializer.SerializeToElement(closeToTray);
                File.WriteAllText(PortablePaths.SettingsFilePath, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
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
        }
        catch { }
    }

    public static void Save(string folder, int fragments, string quality, bool showDialog)
    {
        Save(folder, fragments, quality, showDialog, CloseToTray, StartWithWindows);
    }
}
