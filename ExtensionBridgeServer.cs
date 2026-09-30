using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WrenchDownloader;

public class ExtensionBridgeServer
{
    private HttpListener? _listener;
    private bool _isRunning;
    private readonly Action<(DownloadItem item, bool showPrompt)> _onDownloadRequested;

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

        res.StatusCode = 404;
        res.Close();
    }
}
