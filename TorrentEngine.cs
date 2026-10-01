using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

namespace WrenchDownloader;

/// <summary>One file inside a torrent (path relative to the torrent root).</summary>
public sealed record TorrentFileEntry(string Path, long Length);

/// <summary>Metadata resolved from a magnet (no bytes downloaded yet).</summary>
public sealed class TorrentMetadata
{
    public string MagnetUri { get; init; } = "";
    public string Name { get; init; } = "";
    public long TotalBytes { get; init; }
    public List<TorrentFileEntry> Files { get; init; } = new();
    internal TorrentManager Manager { get; init; } = null!;
}

/// <summary>
/// Torrent support (magnet links): metadata resolve for the file picker,
/// then selective multi-file download driven by the shared MonoTorrent
/// engine (DHT + trackers). One manager per magnet per session; partial
/// files on disk resume automatically on re-add.
/// </summary>
public static class TorrentEngine
{
    private static readonly SemaphoreSlim _engineLock = new(1, 1);
    private static ClientEngine? _engine;
    private static readonly ConcurrentDictionary<string, TorrentManager> _managers =
        new(StringComparer.OrdinalIgnoreCase);

    private static void Log(string message)
    {
        try { File.AppendAllText(PortablePaths.StartupLogPath, $"[{DateTime.Now}] [torrent] {message}\n"); } catch { }
    }

    private sealed record LiveEntry(ITorrentManagerFile Live, string RelPath, long Length, bool IsPadding);

    public static bool IsTorrentLink(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.TrimStart().StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase);

    public static async Task<ClientEngine> GetEngineAsync()
    {
        if (_engine != null) return _engine;
        await _engineLock.WaitAsync();
        try
        {
            if (_engine != null) return _engine;
            string cacheDir = Path.Combine(PortablePaths.DataDirectory, "torrent-cache");
            Directory.CreateDirectory(cacheDir);
            var settings = new EngineSettingsBuilder
            {
                AllowPortForwarding = true,
                AutoSaveLoadDhtCache = true,
                AutoSaveLoadFastResume = true,
                AutoSaveLoadMagnetLinkMetadata = true,
                CacheDirectory = cacheDir,
                MaximumConnections = 60,
                MaximumUploadRate = 100 * 1024,
            }.ToSettings();
            _engine = new ClientEngine(settings);
            return _engine;
        }
        finally { _engineLock.Release(); }
    }

    private static async Task<TorrentManager> GetOrAddManagerAsync(
        ClientEngine engine, string magnetUri, string saveDir, CancellationToken ct)
    {
        string key = magnetUri.Trim();
        if (_managers.TryGetValue(key, out var existing)) return existing;
        if (!MagnetLink.TryParse(key, out var magnet) || magnet == null)
            throw new ArgumentException("Not a valid magnet link.");
        Directory.CreateDirectory(saveDir);
        var manager = await engine.AddAsync(magnet, saveDir);
        _managers[key] = manager;
        return manager;
    }

    private static MonoTorrent.Torrent RequireTorrent(TorrentManager manager)
    {
        return manager.Torrent ?? throw new InvalidOperationException("Torrent metadata unavailable.");
    }

    private static string RelPath(TorrentManager manager, ITorrentManagerFile f)
    {
        try
        {
            string root = (manager.SavePath ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string full = (f.FullPath ?? "").Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (!string.IsNullOrEmpty(root) && full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return full[root.Length..];
        }
        catch { }
        try { return Path.GetFileName(f.FullPath ?? ""); } catch { return ""; }
    }

    /// <summary>Live files paired with metadata (length + padding detection).</summary>
    private static List<LiveEntry> LiveEntries(TorrentManager manager)
    {
        var list = new List<LiveEntry>();
        try
        {
            var metaByPath = new Dictionary<string, ITorrentFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in RequireTorrent(manager).Files)
            {
                try
                {
                    string key = (m.Path ?? "").Replace('\\', '/');
                    if (!metaByPath.ContainsKey(key)) metaByPath[key] = m;
                }
                catch { }
            }
            foreach (var f in manager.Files)
            {
                try
                {
                    string rel = RelPath(manager, f).Replace(Path.DirectorySeparatorChar, '/');
                    long len = 0;
                    bool pad = false;
                    if (metaByPath.TryGetValue(rel, out var m))
                    {
                        len = m.Length;
                        try { pad = m is TorrentFile tf && tf.Padding > 0 && tf.Length == tf.Padding; } catch { }
                    }
                    list.Add(new LiveEntry(f, rel, len, pad));
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>Resolve name + file list for the picker. Downloads nothing.</summary>
    public static async Task<TorrentMetadata> FetchMetadataAsync(string magnetUri, CancellationToken ct)
    {
        var engine = await GetEngineAsync();
        var manager = await GetOrAddManagerAsync(engine, magnetUri, SettingsHelper.DownloadFolder, ct);
        Log($"metadata resolve start magnet={ShortHash(magnetUri)}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await manager.WaitForMetadataAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log($"metadata TIMEOUT magnet={ShortHash(magnetUri)} (no DHT/tracker response)");
            throw new InvalidOperationException("Torrent not found (no DHT/tracker response) - check connectivity or try another torrent.");
        }
        var torrent = RequireTorrent(manager);
        var entries = LiveEntries(manager);
        Log($"metadata resolved magnet={ShortHash(magnetUri)} name={torrent.Name} files={entries.Count}");
        var files = entries
            .Where(e => !e.IsPadding)
            .Select(e => new TorrentFileEntry(e.RelPath, e.Length))
            .ToList();
        return new TorrentMetadata
        {
            MagnetUri = magnetUri,
            Name = torrent.Name,
            TotalBytes = torrent.Size,
            Files = files,
            Manager = manager
        };
    }

    /// <summary>Drop a metadata-only manager (dialog cancelled). Stopped, kept registered.</summary>
    public static async Task DiscardAsync(TorrentMetadata meta)
    {
        try { await meta.Manager.StopAsync(); } catch { }
    }

    private static async Task ApplySelectionAsync(TorrentManager manager, IEnumerable<string> selectedPaths)
    {
        var selected = new HashSet<string>(
            (selectedPaths ?? Enumerable.Empty<string>()).Select(p => p.Replace('\\', '/')),
            StringComparer.OrdinalIgnoreCase);
        bool selectAll = selected.Count == 0;
        foreach (var e in LiveEntries(manager))
        {
            if (e.IsPadding) continue;
            if (selectAll || selected.Contains(e.RelPath)) continue;
            try { await manager.SetFilePriorityAsync(e.Live, Priority.DoNotDownload); } catch { }
        }
    }

    private static long SelectedBytes(TorrentManager manager, HashSet<string> selected, bool selectAll)
    {
        long total = 0;
        foreach (var e in LiveEntries(manager))
        {
            if (e.IsPadding) continue;
            if (selectAll || selected.Contains(e.RelPath)) total += e.Length;
        }
        return total;
    }

    /// <summary>Download entry point used by DownloadEngine (queue/pause/retry reuse).</summary>
    public static async Task DownloadItemAsync(DownloadItem item, CancellationToken ct)
    {
        string magnet = !string.IsNullOrWhiteSpace(item.TorrentMagnet) ? item.TorrentMagnet : item.Url;
        if (!IsTorrentLink(magnet))
            throw new ArgumentException("Not a torrent magnet link.");
        string saveDir = !string.IsNullOrWhiteSpace(item.TargetFolder) && Directory.Exists(item.TargetFolder)
            ? item.TargetFolder
            : SettingsHelper.DownloadFolder;
        Directory.CreateDirectory(saveDir);

        var engine = await GetEngineAsync();
        var manager = await GetOrAddManagerAsync(engine, magnet, saveDir, ct);
        // Metadata has no progress signal: bound it, or a peerless torrent
        // hangs the queue slot forever. The download phase itself runs until
        // cancel/complete (pausable), like any download manager.
        using (var metaTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            metaTimeout.CancelAfter(TimeSpan.FromMinutes(2));
            try { await manager.WaitForMetadataAsync(metaTimeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException("Torrent metadata not found (no DHT/tracker response) - check connectivity or try another torrent.");
            }
        }
        await ApplySelectionAsync(manager, item.TorrentFiles);

        try
        {
            if (string.IsNullOrWhiteSpace(item.Title) || item.Title.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                item.Title = RequireTorrent(manager).Name;
        }
        catch { }
        var selected = new HashSet<string>(
            (item.TorrentFiles ?? Enumerable.Empty<string>()).Select(p => p.Replace('\\', '/')),
            StringComparer.OrdinalIgnoreCase);
        bool selectAll = selected.Count == 0;
        long total = SelectedBytes(manager, selected, selectAll);
        item.SizeText = total > 0 ? DownloadEngine.FormatBytes(total) : "";
        item.Status = DownloadStatus.Downloading;
        item.StatusText = "Torrent connecting (DHT/trackers)…";
        item.EtaText = "--";

        try
        {
            await manager.StartAsync();
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(500, ct);
                    double pct;
                    try { pct = Math.Clamp(manager.PartialProgress, 0, 100); }
                    catch { pct = item.Progress; }
                    long rate = 0;
                    string state = "";
                    int peers = 0;
                    try
                    {
                        rate = manager.Monitor.DownloadRate;
                        state = manager.State.ToString();
                        peers = manager.OpenConnections;
                    }
                    catch { }
                    item.Progress = pct;
                    item.SpeedText = rate > 0 ? $"{DownloadEngine.FormatBytes(rate)}/s" : "connecting…";
                    if (rate > 0 && total > 0 && pct < 100)
                    {
                        double remaining = total * (1 - pct / 100.0);
                        item.EtaText = FormatEta((long)(remaining / Math.Max(1, rate)));
                    }
                    else item.EtaText = "--";
                    item.StatusText = $"Torrent {pct:F1}% • {peers} peers • {state}";
                    bool done = pct >= 100;
                    try { done = done || manager.Complete; } catch { }
                    if (done) break;
                }
            }
            finally
            {
                try { await manager.StopAsync(); } catch { }
            }
        }
        catch (OperationCanceledException)
        {
            try { await manager.StopAsync(); } catch { }
            item.Status = DownloadStatus.Paused;
            item.SpeedText = AppLocalization.Get("download.pausedSpeed");
            item.StatusText = AppLocalization.Get("download.paused");
            item.EtaText = "--";
            return;
        }

        var entries = LiveEntries(manager).Where(e => !e.IsPadding).ToList();
        var chosen = entries.Where(e => selectAll || selected.Contains(e.RelPath)).ToList();
        if (chosen.Count == 0) chosen = entries;
        string dest = chosen.Count == 1
            ? Path.Combine(saveDir, chosen[0].RelPath.Replace('/', Path.DirectorySeparatorChar))
            : saveDir;
        item.Progress = 100;
        item.Status = DownloadStatus.Completed;
        item.SpeedText = AppLocalization.Get("download.finished");
        item.EtaText = "--";
        item.SavePath = dest;
        if (chosen.Count == 1)
        {
            try { item.Title = Path.GetFileNameWithoutExtension(dest); } catch { }
        }
        item.StatusText = AppLocalization.Format("download.completedSaved", Path.GetFileName(dest.TrimEnd(Path.DirectorySeparatorChar)));
    }

    private static string ShortHash(string magnetUri)
    {
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(magnetUri ?? "", @"btih:([0-9a-zA-Z]+)");
            if (m.Success) return m.Groups[1].Value[..8];
        }
        catch { }
        return "magnet";
    }

    private static string FormatEta(long totalSeconds)
    {
        try
        {
            if (totalSeconds < 0) return "--";
            var ts = TimeSpan.FromSeconds(totalSeconds);
            return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
        catch { return "--"; }
    }
}
