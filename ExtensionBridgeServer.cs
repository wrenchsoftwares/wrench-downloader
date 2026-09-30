using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WrenchDownloader;

/// <summary>One live MSE recording session: media bytes appended per track
/// as the browser plays, muxed on finish. Transport-agnostic (SABR, UMS,
/// anything MSE-based) - the page is the demuxer, the app is the recorder.
/// Temp files use .tmp suffixes so the stale-parts cleaner reaps orphans.
/// </summary>
public sealed class MseSession
{
    public string UploadId { get; set; } = "";
    public DownloadItem Item { get; set; } = new DownloadItem();
    public string PartsSubdir { get; set; } = "";
    public string VideoPath { get; set; } = "";
    public string AudioPath { get; set; } = "";
    public long VideoBytes;
    public long AudioBytes;
    public DateTime LastWrite = DateTime.UtcNow;
    public bool Finished;
    public readonly object Lock = new object();
}

public class ExtensionBridgeServer
{
    private HttpListener? _listener;
    private bool _isRunning;
    private readonly Action<(DownloadItem item, bool showPrompt)> _onDownloadRequested;
    private static readonly ConcurrentDictionary<string, MseSession> _mseSessions = new(StringComparer.Ordinal);
    private const int MaxMseSessions = 5;

    public ExtensionBridgeServer(Action<(DownloadItem item, bool showPrompt)> onDownloadRequested)
    {
        _onDownloadRequested = onDownloadRequested;
    }

    public void Start(int port = 45732)
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _isRunning = true;
            try { System.IO.File.AppendAllText(PortablePaths.StartupLogPath, $"[{System.DateTime.Now}] BRIDGE LISTENING ON {port}\n"); } catch { }
            Task.Run(ListenLoop);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to start Extension Bridge Server: {ex.Message}");
            try { System.IO.File.AppendAllText(PortablePaths.StartupLogPath, $"[{System.DateTime.Now}] BRIDGE START FAILED: {ex}\n"); } catch { }
        }
    }

    public void Stop()
    {
        _isRunning = false;
        try { _listener?.Stop(); } catch { }
    }

#if DEBUG
    private static string SafeHost(string? url)
    {
        try { return new Uri(url ?? "").Host; } catch { return "?"; }
    }
#endif

    private async Task ListenLoop()
    {
        while (_isRunning && _listener != null && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context));
            }
            catch
            {
                if (!_isRunning) break;
            }
        }
    }

    /// <summary>Parses a SABR segment list (capped: bounded payload).</summary>
    private static List<StreamSegment>? ParseSegments(JsonElement root, string name)
    {
        try
        {
            if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<StreamSegment>();
            foreach (var s in el.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                string u = s.TryGetProperty("u", out var uEl) ? uEl.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(u)) continue;
                list.Add(new StreamSegment
                {
                    Url = u,
                    Range = s.TryGetProperty("range", out var rEl) ? rEl.GetString() ?? "" : ""
                });
                if (list.Count >= 1500) break;
            }
            return list.Count > 0 ? list : null;
        }
        catch { return null; }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        var req = context.Request;
        var res = context.Response;

        // CORS and Private Network Access headers to permit browser & extension requests
        res.AddHeader("Access-Control-Allow-Origin", "*");
        res.AddHeader("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
        res.AddHeader("Access-Control-Allow-Headers", "Content-Type, Access-Control-Request-Private-Network");
        res.AddHeader("Access-Control-Allow-Private-Network", "true");

        if (req.HttpMethod == "OPTIONS")
        {
            res.StatusCode = 204;
            res.Close();
            return;
        }

        if (req.Url?.AbsolutePath == "/api/health" && req.HttpMethod == "GET")
        {
            byte[] okBytes = Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"app\":\"Wrench Downloader\"}");
            res.ContentType = "application/json";
            res.StatusCode = 200;
            await res.OutputStream.WriteAsync(okBytes, 0, okBytes.Length);
            res.Close();
            return;
        }

        if (req.Url?.AbsolutePath == "/api/download" && req.HttpMethod == "POST")
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            string body = await reader.ReadToEndAsync();

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                // MSE recording session: no stream URL at all - bytes arrive
                // later via /api/segment as the page plays.
                if (root.TryGetProperty("mse", out var mseEl) && mseEl.ValueKind == JsonValueKind.True)
                {
                    string mseUploadId = root.TryGetProperty("uploadId", out var muEl) ? muEl.GetString() ?? "" : "";
                    string mseTitle = root.TryGetProperty("title", out var mtEl) ? mtEl.GetString() ?? "Recording" : "Recording";
                    string msePage = root.TryGetProperty("pageUrl", out var mpEl) ? mpEl.GetString() ?? "" : "";
                    string mseRef = root.TryGetProperty("referrer", out var mrEl) ? mrEl.GetString() ?? "" : "";
                    string mseUa = root.TryGetProperty("userAgent", out var muaEl) ? muaEl.GetString() ?? "" : "";
                    string newId = MseBegin(mseUploadId, mseTitle, msePage, mseRef, mseUa);
                    byte[] mseResp = Encoding.UTF8.GetBytes("{\"success\":true,\"id\":\"" + newId + "\",\"uploadId\":\"" + mseUploadId + "\"}");
                    res.ContentType = "application/json";
                    res.StatusCode = 200;
                    await res.OutputStream.WriteAsync(mseResp, 0, mseResp.Length);
                    res.Close();
                    return;
                }

                string url = root.GetProperty("url").GetString() ?? "";
                string title = root.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "Video Download" : "Video Download";
                string quality = root.TryGetProperty("quality", out var qEl) ? qEl.GetString() ?? "" : "";
                string pageUrl = root.TryGetProperty("pageUrl", out var pEl) ? pEl.GetString() ?? "" : "";
                string referrer = root.TryGetProperty("referrer", out var rEl) ? rEl.GetString() ?? "" : "";
                string userAgent = root.TryGetProperty("userAgent", out var uEl) && !string.IsNullOrWhiteSpace(uEl.GetString())
                    ? uEl.GetString()!
                    : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";
                string poToken = root.TryGetProperty("poToken", out var potEl) ? potEl.GetString() ?? "" : "";
                string audioUrl = root.TryGetProperty("audioUrl", out var aEl) ? aEl.GetString() ?? "" : "";
                string audioReferrer = root.TryGetProperty("audioReferrer", out var arEl) ? arEl.GetString() ?? "" : "";
                bool isSabr = root.TryGetProperty("sabr", out var sabrEl) && sabrEl.ValueKind == JsonValueKind.True;
                int itag = root.TryGetProperty("itag", out var itagEl) && itagEl.ValueKind == JsonValueKind.Number ? itagEl.GetInt32() : 0;
                long expectedBytes = root.TryGetProperty("expectedBytes", out var expEl) && expEl.ValueKind == JsonValueKind.Number ? expEl.GetInt64() : 0;
                string initRange = root.TryGetProperty("initRange", out var irEl) ? irEl.GetString() ?? "" : "";
                string audioInitRange = root.TryGetProperty("audioInitRange", out var airEl) ? airEl.GetString() ?? "" : "";
                List<StreamSegment>? segments = ParseSegments(root, "segments");
                List<StreamSegment>? audioSegments = ParseSegments(root, "audioSegments");
                var streamHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("streamHeaders", out var shEl) && shEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in shEl.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                            streamHeaders[property.Name] = property.Value.GetString() ?? "";
                    }
                }
                List<BrowserCookie>? cookies = null;
                if (root.TryGetProperty("cookies", out var cookiesEl) && cookiesEl.ValueKind == JsonValueKind.Array)
                {
                    cookies = new List<BrowserCookie>();
                    foreach (var c in cookiesEl.EnumerateArray())
                    {
                        if (c.ValueKind != JsonValueKind.Object) continue;
                        string name = c.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(name)) continue;
                        cookies.Add(new BrowserCookie
                        {
                            Name = name,
                            Value = c.TryGetProperty("value", out var vEl) ? vEl.GetString() ?? "" : "",
                            Domain = c.TryGetProperty("domain", out var dEl) ? dEl.GetString() ?? "" : "",
                            Path = c.TryGetProperty("path", out var pEl2) ? pEl2.GetString() ?? "/" : "/",
                            Secure = c.TryGetProperty("secure", out var sEl) && sEl.ValueKind == JsonValueKind.True,
                            Expiry = c.TryGetProperty("expiry", out var eEl) && eEl.ValueKind == JsonValueKind.Number ? eEl.GetInt64() : 0
                        });
                        if (cookies.Count >= 100) break;
                    }
                    if (cookies.Count == 0) cookies = null;
                }

                if (!string.IsNullOrWhiteSpace(url))
                {
                    bool showPrompt = true;
                    if (root.TryGetProperty("prompt", out var promptEl) && promptEl.ValueKind == JsonValueKind.False)
                    {
                        showPrompt = false;
                    }

                    var item = new DownloadItem
                    {
                        Url = url,
                        Title = title,
                        Quality = quality,
                        PageUrl = pageUrl,
                        Referrer = referrer,
                        UserAgent = userAgent,
                        Cookies = cookies,
                        PoToken = poToken,
                        AudioUrl = audioUrl,
                        AudioReferrer = audioReferrer,
                        IsSabr = isSabr,
                        Itag = itag,
                        ExpectedBytes = expectedBytes,
                        InitRange = initRange,
                        AudioInitRange = audioInitRange,
                        Segments = segments,
                        AudioSegments = audioSegments,
                        Status = DownloadStatus.Queued,
                        StatusText = "Added from browser extension"
                    };

                    _onDownloadRequested?.Invoke((item, showPrompt));
#if DEBUG
                    DownloadEngine.Track(item,
                        $"BRIDGE-RECV site={SafeHost(pageUrl)} quality={quality} " +
                        $"cookies={(cookies?.Count ?? 0)} pot={(!string.IsNullOrEmpty(poToken) ? "yes" : "no")} prompt={showPrompt} " +
                        $"sabr={isSabr} segs={(segments?.Count ?? 0)} asegs={(audioSegments?.Count ?? 0)} url={url}");
#endif

                    byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true,\"id\":\"" + item.Id + "\"}");
                    res.ContentType = "application/json";
                    res.StatusCode = 200;
                    await res.OutputStream.WriteAsync(respBytes, 0, respBytes.Length);
                    res.Close();
                    return;
                }
            }
            catch (Exception ex)
            {
                byte[] errBytes = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"" + ex.Message.Replace("\"", "\\\"") + "\"}");
                res.ContentType = "application/json";
                res.StatusCode = 400;
                await res.OutputStream.WriteAsync(errBytes, 0, errBytes.Length);
                res.Close();
                return;
            }
        }

        if (req.Url?.AbsolutePath == "/api/segment" && req.HttpMethod == "POST")
        {
            string uploadId = req.QueryString["uploadId"] ?? "";
            string track = req.QueryString["track"] ?? "video";
            byte[] data;
            using (var ms = new MemoryStream())
            {
                await req.InputStream.CopyToAsync(ms);
                data = ms.ToArray();
            }
            if (MseAppend(uploadId, track, data, out string segError))
            {
                byte[] ok = Encoding.UTF8.GetBytes("{\"success\":true}");
                res.ContentType = "application/json";
                res.StatusCode = 200;
                await res.OutputStream.WriteAsync(ok, 0, ok.Length);
            }
            else
            {
                byte[] err = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"" + segError.Replace("\"", "\\\"") + "\"}");
                res.ContentType = "application/json";
                res.StatusCode = 400;
                await res.OutputStream.WriteAsync(err, 0, err.Length);
            }
            res.Close();
            return;
        }

        if (req.Url?.AbsolutePath == "/api/finish" && req.HttpMethod == "POST")
        {
            string uploadId = req.QueryString["uploadId"] ?? "";
            string reason = req.QueryString["reason"] ?? "stop";
            if (await MseFinishAsync(uploadId, reason))
            {
                byte[] ok = Encoding.UTF8.GetBytes("{\"success\":true}");
                res.ContentType = "application/json";
                res.StatusCode = 200;
                await res.OutputStream.WriteAsync(ok, 0, ok.Length);
            }
            else
            {
                byte[] err = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"unknown upload session\"}");
                res.ContentType = "application/json";
                res.StatusCode = 404;
                await res.OutputStream.WriteAsync(err, 0, err.Length);
            }
            res.Close();
            return;
        }

        res.StatusCode = 404;
        res.Close();
    }

    private void MseLog(string uploadId, string message)
    {
        try
        {
            File.AppendAllText(PortablePaths.StartupLogPath,
                $"[{DateTime.Now}] [mse:{uploadId}] {message}\n");
        }
        catch { }
    }

    private static string MseSafeName(string name)
    {
        try
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim();
            if (name.Length > 60) name = name[..60].Trim();
        }
        catch { }
        return string.IsNullOrWhiteSpace(name) ? "recording" : name;
    }

    /// <summary>Opens a recording session; returns the DownloadItem id.</summary>
    private string MseBegin(string uploadId, string title, string pageUrl, string referrer, string userAgent)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
            uploadId = Guid.NewGuid().ToString("N");
        // Evict sessions idle over 2h (tab closed mid-recording orphans).
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-2);
            foreach (var kv in _mseSessions)
            {
                if (kv.Value.LastWrite < cutoff && _mseSessions.TryRemove(kv.Key, out var old))
                {
                    try { if (Directory.Exists(old.PartsSubdir)) Directory.Delete(old.PartsSubdir, recursive: true); } catch { }
                }
            }
        }
        catch { }
        if (_mseSessions.Count >= MaxMseSessions)
            throw new InvalidOperationException("too many active recordings");

        string safe = MseSafeName(title);
        string partsSubdir = DownloadEngine.CreatePartsSubdir(safe);
        var item = new DownloadItem
        {
            Title = safe,
            Quality = "record",
            PageUrl = pageUrl,
            Referrer = referrer,
            UserAgent = userAgent,
            MseUploadId = uploadId,
            Status = DownloadStatus.Downloading,
            StatusText = "Recording stream - play the video through"
        };
        var session = new MseSession
        {
            UploadId = uploadId,
            Item = item,
            PartsSubdir = partsSubdir,
            VideoPath = Path.Combine(partsSubdir, safe + ".msev.tmp"),
            AudioPath = Path.Combine(partsSubdir, safe + ".msea.tmp")
        };
        _mseSessions[uploadId] = session;
        _onDownloadRequested?.Invoke((item, false));
        MseLog(uploadId, $"BEGIN title={safe} page={pageUrl}");
        return item.Id;
    }

    private bool MseAppend(string uploadId, string track, byte[] data, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(uploadId) || !_mseSessions.TryGetValue(uploadId, out var session))
        {
            error = "unknown upload session";
            return false;
        }
        if (session.Finished)
        {
            error = "session finished";
            return false;
        }
        if (data == null || data.Length == 0 || data.Length > 8 * 1024 * 1024)
        {
            error = "bad chunk";
            return false;
        }
        try
        {
            lock (session.Lock)
            {
                bool audio = string.Equals(track, "audio", StringComparison.OrdinalIgnoreCase);
                string path = audio ? session.AudioPath : session.VideoPath;
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
                fs.Write(data, 0, data.Length);
                if (audio) session.AudioBytes += data.Length;
                else session.VideoBytes += data.Length;
                session.LastWrite = DateTime.UtcNow;
                long total = session.VideoBytes + session.AudioBytes;
                session.Item.SpeedText = $"{DownloadEngine.FormatBytes(total)} recorded";
                session.Item.StatusText = $"Recording stream - {DownloadEngine.FormatBytes(total)} captured";
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private async Task<bool> MseFinishAsync(string uploadId, string reason)
    {
        if (string.IsNullOrWhiteSpace(uploadId) || !_mseSessions.TryRemove(uploadId, out var session))
            return false;
        var item = session.Item;
        try
        {
            MseLog(uploadId, $"FINISH reason={reason} video={session.VideoBytes} audio={session.AudioBytes}");
            if (session.VideoBytes < 64 * 1024 && session.AudioBytes < 64 * 1024)
            {
                item.Status = DownloadStatus.Failed;
                item.StatusText = "Recording too short - play the video while recording";
                item.EtaText = "--";
                try { if (Directory.Exists(session.PartsSubdir)) Directory.Delete(session.PartsSubdir, recursive: true); } catch { }
                return true;
            }
            bool hasVideo = session.VideoBytes >= 64 * 1024 && File.Exists(session.VideoPath);
            bool hasAudio = session.AudioBytes >= 16 * 1024 && File.Exists(session.AudioPath);
            string ext = "mp4";
            string finalTmp = Path.Combine(session.PartsSubdir, MseSafeName(item.Title) + "." + ext);
            if (hasVideo && hasAudio)
            {
                bool muxed = false;
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = DownloadEngine.FfmpegPath,
                        Arguments = $"-y -i \"{session.VideoPath}\" -i \"{session.AudioPath}\" -c copy -movflags +faststart \"{finalTmp}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        await proc.WaitForExitAsync();
                        muxed = proc.ExitCode == 0 && File.Exists(finalTmp) && new FileInfo(finalTmp).Length > 64 * 1024;
                    }
                }
                catch { muxed = false; }
                MseLog(uploadId, $"mux {(muxed ? "OK" : "FAILED, keeping video track")}");
                if (!muxed)
                {
                    try { if (File.Exists(finalTmp)) File.Delete(finalTmp); } catch { }
                    File.Move(session.VideoPath, finalTmp);
                }
            }
            else if (hasVideo)
            {
                try { if (File.Exists(finalTmp)) File.Delete(finalTmp); } catch { }
                File.Move(session.VideoPath, finalTmp);
            }
            else
            {
                ext = "m4a";
                finalTmp = Path.Combine(session.PartsSubdir, MseSafeName(item.Title) + "." + ext);
                try { if (File.Exists(finalTmp)) File.Delete(finalTmp); } catch { }
                File.Move(session.AudioPath, finalTmp);
            }
            string downloadsFolder = SettingsHelper.DownloadFolder;
            var moved = DownloadEngine.MoveFinishedMediaToDownloads(session.PartsSubdir, downloadsFolder, item);
            DownloadEngine.DeletePartsSubdirIfClean(session.PartsSubdir, item);
            string dest = moved.FirstOrDefault().Dest ?? "";
            if (string.IsNullOrEmpty(dest) || !File.Exists(dest))
            {
                dest = DownloadEngine.MoveFileRobust(finalTmp, downloadsFolder, Path.GetFileName(finalTmp));
                try { if (Directory.Exists(session.PartsSubdir)) Directory.Delete(session.PartsSubdir, recursive: true); } catch { }
            }
            item.Progress = 100;
            item.Status = DownloadStatus.Completed;
            item.SpeedText = AppLocalization.Get("download.finished");
            item.EtaText = "--";
            item.SavePath = dest;
            item.Title = Path.GetFileNameWithoutExtension(dest);
            item.StatusText = AppLocalization.Format("download.completedSaved", Path.GetFileName(dest));
            MseLog(uploadId, $"DONE file={dest}");
            return true;
        }
        catch (Exception ex)
        {
            MseLog(uploadId, $"FINISH FAILED: {ex.Message}");
            item.Status = DownloadStatus.Failed;
            item.StatusText = AppLocalization.Format("download.error", ex.Message);
            item.EtaText = "--";
            return true;
        }
    }
}
