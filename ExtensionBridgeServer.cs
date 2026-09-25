using System;
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
            Task.Run(ListenLoop);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to start Extension Bridge Server: {ex.Message}");
        }
    }

    public void Stop()
    {
        _isRunning = false;
        try { _listener?.Stop(); } catch { }
    }

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
                string userAgent = root.TryGetProperty("userAgent", out var uEl) ? uEl.GetString() ?? "" : "";

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
                        Status = DownloadStatus.Queued,
                        StatusText = "Added from browser extension"
                    };

                    _onDownloadRequested?.Invoke((item, showPrompt));

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
