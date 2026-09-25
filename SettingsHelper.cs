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

    public static string DefaultDownloadsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public static string DownloadFolder
    {
        get
        {
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values[FolderKey] as string;
                if (!string.IsNullOrWhiteSpace(v) && Directory.Exists(v)) return v;
            }
            catch { }
            return DefaultDownloadsFolder;
        }
    }

    public static int ConcurrentFragments
    {
        get
        {
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values[FragmentsKey];
                if (v is int i) return Math.Clamp(i, 1, 16);
            }
            catch { }
            return 4;
        }
    }

    public static string DefaultQuality
    {
        get
        {
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values[QualityKey] as string;
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            catch { }
            return "best";
        }
    }

    public static void Save(string folder, int fragments, string quality)
    {
        var values = ApplicationData.Current.LocalSettings.Values;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            values[FolderKey] = folder;
        values[FragmentsKey] = Math.Clamp(fragments, 1, 16);
        if (!string.IsNullOrWhiteSpace(quality))
            values[QualityKey] = quality;
    }
}
