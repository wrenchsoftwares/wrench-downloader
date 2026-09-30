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
    private const string MaximumConcurrentDownloadsKey = "MaximumConcurrentDownloads";
    private const string LanguageKey = "Language";
    private const string MaximumDownloadRateKey = "MaximumDownloadRateKBps";
    private const string ClipboardMonitorKey = "ClipboardMonitorEnabled";
    private const string OrganizeDownloadsKey = "OrganizeDownloadsByType";
    private const string ThemeKey = "AppTheme";
    private const string ShowCompleteDialogKey = "ShowCompleteDialog";
    private const string PlaySoundOnCompleteKey = "PlaySoundOnComplete";
    private const string ConfirmOnDeleteFileKey = "ConfirmOnDeleteFile";
    private const string AutoResumeInterruptedKey = "AutoResumeInterrupted";
    private const string AutoRetryCountKey = "AutoRetryCount";
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "WrenchDownloader";

    private static Dictionary<string, JsonElement>? _portableSettings;
    private static string? _cachedFolder;
    private static int? _cachedFragments;
    private static string? _cachedQuality;
    private static bool? _cachedShowDialog;
    private static bool? _cachedCloseToTray;
    private static bool? _cachedStartWithWindows;
    private static int? _cachedMaximumConcurrentDownloads;
    private static string? _cachedLanguage;
    private static int? _cachedMaximumDownloadRateKBps;
    private static bool? _cachedClipboardMonitorEnabled;
    private static bool? _cachedOrganizeDownloadsByType;
    private static string? _cachedTheme;
    private static bool? _cachedShowCompleteDialog;
    private static bool? _cachedPlaySoundOnComplete;
    private static bool? _cachedConfirmOnDeleteFile;
    private static bool? _cachedAutoResumeInterrupted;
    private static int? _cachedAutoRetryCount;

    public static bool IsPortable => PortablePaths.IsPortable;

    public static string Language
    {
        get
        {
            if (_cachedLanguage != null) return _cachedLanguage;
            string? stored = ReadValue<string>(LanguageKey);
            _cachedLanguage = stored is "fr" or "ar" ? stored : "en";
            return _cachedLanguage;
        }
    }

    public static void SaveLanguage(string language)
    {
        _cachedLanguage = language is "fr" or "ar" ? language : "en";
        try
        {
            if (PortablePaths.IsPortable)
            {
                var values = LoadPortableSettings();
                values[LanguageKey] = JsonSerializer.SerializeToElement(_cachedLanguage);
                File.WriteAllText(PortablePaths.SettingsFilePath, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                ApplicationData.Current.LocalSettings.Values[LanguageKey] = _cachedLanguage;
            }
        }
        catch { }
    }

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

            try
            {
                var value = ApplicationData.Current.LocalSettings.Values[key];
                return value is T typed ? typed : default;
            }
            catch
            {
                return LoadPortableSettings().TryGetValue(key, out var stored)
                    ? stored.Deserialize<T>()
                    : default;
            }
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

    public static int MaximumConcurrentDownloads
    {
        get
        {
            if (_cachedMaximumConcurrentDownloads.HasValue) return _cachedMaximumConcurrentDownloads.Value;
            var value = ReadValue<int?>(MaximumConcurrentDownloadsKey);
            _cachedMaximumConcurrentDownloads = Math.Clamp(value ?? 1, 1, 8);
            return _cachedMaximumConcurrentDownloads.Value;
        }
    }

    public static int MaximumDownloadRateKBps
    {
        get
        {
            if (_cachedMaximumDownloadRateKBps.HasValue) return _cachedMaximumDownloadRateKBps.Value;
            _cachedMaximumDownloadRateKBps = Math.Clamp(ReadValue<int?>(MaximumDownloadRateKey) ?? 0, 0, 1_000_000);
            return _cachedMaximumDownloadRateKBps.Value;
        }
    }

    public static bool ClipboardMonitorEnabled
    {
        get
        {
            if (_cachedClipboardMonitorEnabled.HasValue) return _cachedClipboardMonitorEnabled.Value;
            _cachedClipboardMonitorEnabled = ReadValue<bool?>(ClipboardMonitorKey) ?? false;
            return _cachedClipboardMonitorEnabled.Value;
        }
    }

    public static bool OrganizeDownloadsByType
    {
        get
        {
            if (_cachedOrganizeDownloadsByType.HasValue) return _cachedOrganizeDownloadsByType.Value;
            _cachedOrganizeDownloadsByType = ReadValue<bool?>(OrganizeDownloadsKey) ?? false;
            return _cachedOrganizeDownloadsByType.Value;
        }
    }

    /// <summary>Display mode: "system", "light" or "dark".</summary>
    public static string Theme
    {
        get
        {
            if (_cachedTheme != null) return _cachedTheme;
            string? stored = ReadValue<string>(ThemeKey);
            _cachedTheme = stored is "light" or "dark" ? stored : "system";
            return _cachedTheme;
        }
    }

    public static void SaveTheme(string theme)
    {
        _cachedTheme = theme is "light" or "dark" ? theme : "system";
        WriteValue(ThemeKey, _cachedTheme);
    }

    /// <summary>IDM-style: show a dialog with open options when a download completes.</summary>
    public static bool ShowCompleteDialog
    {
        get
        {
            if (_cachedShowCompleteDialog.HasValue) return _cachedShowCompleteDialog.Value;
            _cachedShowCompleteDialog = ReadValue<bool?>(ShowCompleteDialogKey) ?? false;
            return _cachedShowCompleteDialog.Value;
        }
    }

    /// <summary>IDM-style sounds: play a system sound when a download completes.</summary>
    public static bool PlaySoundOnComplete
    {
        get
        {
            if (_cachedPlaySoundOnComplete.HasValue) return _cachedPlaySoundOnComplete.Value;
            _cachedPlaySoundOnComplete = ReadValue<bool?>(PlaySoundOnCompleteKey) ?? true;
            return _cachedPlaySoundOnComplete.Value;
        }
    }

    /// <summary>Confirm before permanently deleting files from disk.</summary>
    public static bool ConfirmOnDeleteFile
    {
        get
        {
            if (_cachedConfirmOnDeleteFile.HasValue) return _cachedConfirmOnDeleteFile.Value;
            _cachedConfirmOnDeleteFile = ReadValue<bool?>(ConfirmOnDeleteFileKey) ?? true;
            return _cachedConfirmOnDeleteFile.Value;
        }
    }

    /// <summary>Resume downloads interrupted by app restart instead of leaving them paused.</summary>
    public static bool AutoResumeInterrupted
    {
        get
        {
            if (_cachedAutoResumeInterrupted.HasValue) return _cachedAutoResumeInterrupted.Value;
            _cachedAutoResumeInterrupted = ReadValue<bool?>(AutoResumeInterruptedKey) ?? false;
            return _cachedAutoResumeInterrupted.Value;
        }
    }

    /// <summary>How many times a failed download is automatically retried (0 = no retry).</summary>
    public static int AutoRetryCount
    {
        get
        {
            if (_cachedAutoRetryCount.HasValue) return _cachedAutoRetryCount.Value;
            _cachedAutoRetryCount = Math.Clamp(ReadValue<int?>(AutoRetryCountKey) ?? 2, 0, 10);
            return _cachedAutoRetryCount.Value;
        }
    }

    private static void WriteValue<T>(string key, T value)
    {
        try
        {
            if (PortablePaths.IsPortable)
            {
                var values = LoadPortableSettings();
                values[key] = JsonSerializer.SerializeToElement(value);
                File.WriteAllText(PortablePaths.SettingsFilePath, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                try
                {
                    ApplicationData.Current.LocalSettings.Values[key] = value;
                }
                catch
                {
                    var values = LoadPortableSettings();
                    values[key] = JsonSerializer.SerializeToElement(value);
                    File.WriteAllText(PortablePaths.SettingsFilePath, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
        catch { }
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

    public static void Save(string folder, int fragments, string quality, bool showDialog, bool closeToTray, bool startWithWindows, int maximumConcurrentDownloads, int maximumDownloadRateKBps, bool clipboardMonitorEnabled, bool organizeDownloadsByType, bool showCompleteDialog, bool playSoundOnComplete, bool confirmOnDeleteFile, bool autoResumeInterrupted, int autoRetryCount)
    {
        _cachedFolder = (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)) ? folder : DefaultDownloadsFolder;
        _cachedFragments = Math.Clamp(fragments, 1, 32);
        _cachedQuality = !string.IsNullOrWhiteSpace(quality) ? quality : "best";
        _cachedShowDialog = showDialog;
        _cachedCloseToTray = closeToTray;
        _cachedMaximumConcurrentDownloads = Math.Clamp(maximumConcurrentDownloads, 1, 8);
        _cachedMaximumDownloadRateKBps = Math.Clamp(maximumDownloadRateKBps, 0, 1_000_000);
        _cachedClipboardMonitorEnabled = clipboardMonitorEnabled;
        _cachedOrganizeDownloadsByType = organizeDownloadsByType;
        _cachedShowCompleteDialog = showCompleteDialog;
        _cachedPlaySoundOnComplete = playSoundOnComplete;
        _cachedConfirmOnDeleteFile = confirmOnDeleteFile;
        _cachedAutoResumeInterrupted = autoResumeInterrupted;
        _cachedAutoRetryCount = Math.Clamp(autoRetryCount, 0, 10);

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
                values[MaximumConcurrentDownloadsKey] = JsonSerializer.SerializeToElement(_cachedMaximumConcurrentDownloads.Value);
                values[MaximumDownloadRateKey] = JsonSerializer.SerializeToElement(_cachedMaximumDownloadRateKBps.Value);
                values[ClipboardMonitorKey] = JsonSerializer.SerializeToElement(_cachedClipboardMonitorEnabled.Value);
                values[OrganizeDownloadsKey] = JsonSerializer.SerializeToElement(_cachedOrganizeDownloadsByType.Value);
                values[ShowCompleteDialogKey] = JsonSerializer.SerializeToElement(_cachedShowCompleteDialog.Value);
                values[PlaySoundOnCompleteKey] = JsonSerializer.SerializeToElement(_cachedPlaySoundOnComplete.Value);
                values[ConfirmOnDeleteFileKey] = JsonSerializer.SerializeToElement(_cachedConfirmOnDeleteFile.Value);
                values[AutoResumeInterruptedKey] = JsonSerializer.SerializeToElement(_cachedAutoResumeInterrupted.Value);
                values[AutoRetryCountKey] = JsonSerializer.SerializeToElement(_cachedAutoRetryCount.Value);
                File.WriteAllText(PortablePaths.SettingsFilePath, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
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
                    values[MaximumConcurrentDownloadsKey] = _cachedMaximumConcurrentDownloads.Value;
                    values[MaximumDownloadRateKey] = _cachedMaximumDownloadRateKBps.Value;
                    values[ClipboardMonitorKey] = _cachedClipboardMonitorEnabled.Value;
                    values[OrganizeDownloadsKey] = _cachedOrganizeDownloadsByType.Value;
                    values[ShowCompleteDialogKey] = _cachedShowCompleteDialog.Value;
                    values[PlaySoundOnCompleteKey] = _cachedPlaySoundOnComplete.Value;
                    values[ConfirmOnDeleteFileKey] = _cachedConfirmOnDeleteFile.Value;
                    values[AutoResumeInterruptedKey] = _cachedAutoResumeInterrupted.Value;
                    values[AutoRetryCountKey] = _cachedAutoRetryCount.Value;
                }
                catch
                {
                    var values = LoadPortableSettings();
                    if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                        values[FolderKey] = JsonSerializer.SerializeToElement(folder);
                    values[FragmentsKey] = JsonSerializer.SerializeToElement(_cachedFragments.Value);
                    if (!string.IsNullOrWhiteSpace(quality))
                        values[QualityKey] = JsonSerializer.SerializeToElement(quality);
                    values[ShowDialogKey] = JsonSerializer.SerializeToElement(showDialog);
                    values[CloseToTrayKey] = JsonSerializer.SerializeToElement(closeToTray);
                    values[MaximumConcurrentDownloadsKey] = JsonSerializer.SerializeToElement(_cachedMaximumConcurrentDownloads.Value);
                    values[MaximumDownloadRateKey] = JsonSerializer.SerializeToElement(_cachedMaximumDownloadRateKBps.Value);
                    values[ClipboardMonitorKey] = JsonSerializer.SerializeToElement(_cachedClipboardMonitorEnabled.Value);
                    values[OrganizeDownloadsKey] = JsonSerializer.SerializeToElement(_cachedOrganizeDownloadsByType.Value);
                    values[ShowCompleteDialogKey] = JsonSerializer.SerializeToElement(_cachedShowCompleteDialog.Value);
                    values[PlaySoundOnCompleteKey] = JsonSerializer.SerializeToElement(_cachedPlaySoundOnComplete.Value);
                    values[ConfirmOnDeleteFileKey] = JsonSerializer.SerializeToElement(_cachedConfirmOnDeleteFile.Value);
                    values[AutoResumeInterruptedKey] = JsonSerializer.SerializeToElement(_cachedAutoResumeInterrupted.Value);
                    values[AutoRetryCountKey] = JsonSerializer.SerializeToElement(_cachedAutoRetryCount.Value);
                    File.WriteAllText(PortablePaths.SettingsFilePath, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
        catch { }
    }

    public static void Save(string folder, int fragments, string quality, bool showDialog)
    {
        Save(folder, fragments, quality, showDialog, CloseToTray, StartWithWindows, MaximumConcurrentDownloads, MaximumDownloadRateKBps, ClipboardMonitorEnabled, OrganizeDownloadsByType, ShowCompleteDialog, PlaySoundOnComplete, ConfirmOnDeleteFile, AutoResumeInterrupted, AutoRetryCount);
    }

    public static void Save(string folder, int fragments, string quality, bool showDialog, bool closeToTray, bool startWithWindows)
    {
        Save(folder, fragments, quality, showDialog, closeToTray, startWithWindows, MaximumConcurrentDownloads, MaximumDownloadRateKBps, ClipboardMonitorEnabled, OrganizeDownloadsByType, ShowCompleteDialog, PlaySoundOnComplete, ConfirmOnDeleteFile, AutoResumeInterrupted, AutoRetryCount);
    }

    public static void Save(string folder, int fragments, string quality, bool showDialog, bool closeToTray, bool startWithWindows, int maximumConcurrentDownloads, int maximumDownloadRateKBps, bool clipboardMonitorEnabled, bool organizeDownloadsByType)
    {
        Save(folder, fragments, quality, showDialog, closeToTray, startWithWindows, maximumConcurrentDownloads, maximumDownloadRateKBps, clipboardMonitorEnabled, organizeDownloadsByType, ShowCompleteDialog, PlaySoundOnComplete, ConfirmOnDeleteFile, AutoResumeInterrupted, AutoRetryCount);
    }
}
