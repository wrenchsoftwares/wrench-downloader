using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WrenchDownloader;

public class DownloadEngine
{
    private static readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10
    });

    static DownloadEngine()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
    }

    public static async Task StartDownloadAsync(DownloadItem item, CancellationToken cancellationToken = default)
    {
        // Immediately yield so the caller (especially UI thread) returns instantly without blocking
        await Task.Yield();

#if DEBUG
        TrackStart(item);
#endif

        // MSE recording items are assembled by the bridge (/api/segment +
        // /api/finish) as the page plays - the engine must not touch them.
        if (!string.IsNullOrEmpty(item.MseUploadId))
        {
            item.Status = DownloadStatus.Downloading;
            item.SpeedText = AppLocalization.Get("download.resolving");
            item.StatusText = "Recording stream - play the video through";
            item.EtaText = "--";
            return;
        }

        string configuredDownloadFolder = SettingsHelper.DownloadFolder;
        string downloadsFolder = item.TargetFolder;
        if (string.IsNullOrWhiteSpace(downloadsFolder) || !Directory.Exists(downloadsFolder))
        {
            downloadsFolder = configuredDownloadFolder;
            if (!string.IsNullOrWhiteSpace(item.SavePath))
            {
                // A resumed item's SavePath points at its Parts temp file:
                // that folder is scratch, never a destination (moving finished
                // files there would strand - or delete - them on cleanup).
                string? requestedFolder = Path.GetDirectoryName(item.SavePath);
                if (!string.IsNullOrWhiteSpace(requestedFolder) && Directory.Exists(requestedFolder) &&
                    !IsUnderPartsDir(requestedFolder))
                    downloadsFolder = requestedFolder;
            }
        }

        if (SettingsHelper.OrganizeDownloadsByType &&
            Path.GetFullPath(downloadsFolder).Equals(Path.GetFullPath(configuredDownloadFolder), StringComparison.OrdinalIgnoreCase))
        {
            downloadsFolder = Path.Combine(downloadsFolder, GetDownloadCategory(item));
        }

        if (!Directory.Exists(downloadsFolder)) Directory.CreateDirectory(downloadsFolder);
        CleanupStalePartsOnce();

        // Check if the URL needs stream demuxing (page or manifest) vs direct fetch.
        if (IsStreamingSite(item))
        {
            await DownloadStreamingSiteAsync(item, downloadsFolder, cancellationToken);
        }
        else
        {
            await DownloadDirectHttpAsync(item, downloadsFolder, cancellationToken);
        }
    }

    private static string GetDownloadCategory(DownloadItem item)
    {
        if (item.DownloadPlaylist) return "Playlists";
        if (string.Equals(item.Quality, "audio", StringComparison.OrdinalIgnoreCase)) return "Audio";
        if (IsStreamingSite(item)) return "Videos";

        string extension = "";
        try { extension = Path.GetExtension(new Uri(item.Url).AbsolutePath); } catch { }
        if (string.IsNullOrEmpty(extension)) extension = Path.GetExtension(item.Title);

        return extension.ToLowerInvariant() switch
        {
            ".mp3" or ".m4a" or ".aac" or ".wav" or ".flac" or ".ogg" => "Audio",
            ".mp4" or ".mkv" or ".webm" or ".mov" or ".avi" or ".ts" => "Videos",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" => "Images",
            ".pdf" or ".doc" or ".docx" or ".txt" or ".rtf" or ".xls" or ".xlsx" or ".ppt" or ".pptx" => "Documents",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "Archives",
            _ => "Other"
        };
    }

    // No per-site lists anywhere: a URL needs resolving when it structurally
    // looks like a stream (manifest/playlist/chunk) or a page (no static
    // file extension). Anything else downloads directly; if the server
    // answers with a stream content-type we re-route (see DownloadDirectHttpAsync).
    // NOTE: playlist endpoints often carry no file extension at all
    // (manifest.googlevideo.com HLS masters etc.) - matched by path markers.
    private static bool IsManifestUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string lower = url.ToLowerInvariant();
        return lower.Contains(".m3u8") ||
               lower.Contains(".mpd") ||
               lower.Contains(".m3u") ||
               lower.Contains("hls_playlist") ||
               lower.Contains("hls_variant") ||
               lower.Contains("manifest.googlevideo.com") ||
               lower.Contains("/manifest/");
    }

    private static bool IsCapturedYouTubePlaybackUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        // SABR redirectors (?aitags= list) play only inside the page's live
        // handshake - never directly downloadable (the HLS master with the
        // same renditions + audio is the downloadable form).
        if (url.Contains("aitags=", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var uri = new Uri(url);
            return uri.Host.EndsWith("googlevideo.com", StringComparison.OrdinalIgnoreCase) &&
                   uri.AbsolutePath.EndsWith("/videoplayback", StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(System.Web.HttpUtility.ParseQueryString(uri.Query)["itag"]);
        }
        catch { return false; }
    }

    /// <summary>SABR handshake-only URL (see above) - must never be downloaded.</summary>
    private static bool IsSabrRedirector(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.Contains("/videoplayback", StringComparison.OrdinalIgnoreCase) &&
        url.Contains("aitags=", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extension-resolved stream: the companion extension captured the exact
    /// authorized rendition URL (videoplayback + itag) the browser is playing.
    /// The app must download those bytes directly - never re-resolve the page,
    /// which is what age/login gates block.
    /// </summary>
    private static bool IsExtensionResolvedPlayback(DownloadItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Url)) return false;
        if (!IsCapturedYouTubePlaybackUrl(item.Url)) return false;
        if (string.Equals(item.Quality, "file", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(item.Quality, "audio", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Progressive (muxed A/V) itags need no audio companion.</summary>
    private static bool IsProgressiveItag(string? url)
    {
        try
        {
            var m = Regex.Match(url ?? "", @"[?&]itag=(\d+)\b", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out int itag)) return false;
            return itag == 17 || itag == 18 || itag == 36 || itag == 43 || itag == 22;
        }
        catch { return false; }
    }

    private static string WithPot(string url, string? poToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(poToken) || string.IsNullOrWhiteSpace(url)) return url;
            if (url.Contains("pot=", StringComparison.OrdinalIgnoreCase)) return url;
            string token = Regex.Replace(poToken.Trim(), @"[^A-Za-z0-9\-_]", "");
            if (string.IsNullOrEmpty(token)) return url;
            return url + (url.Contains('?') ? "&" : "?") + "pot=" + token;
        }
        catch { return url; }
    }

    private static string BuildCookieHeader(List<BrowserCookie>? cookies, string? requestUrl = null)
    {
        if (cookies == null || cookies.Count == 0) return "";
        Uri? uri = null;
        if (!string.IsNullOrWhiteSpace(requestUrl)) Uri.TryCreate(requestUrl, UriKind.Absolute, out uri);
        return string.Join("; ", cookies
            .Where(c => !string.IsNullOrEmpty(c.Name))
            .Where(c =>
            {
                if (uri == null) return true;
                string domain = (c.Domain ?? "").TrimStart('.');
                bool domainMatches = string.IsNullOrEmpty(domain) ||
                    uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
                bool pathMatches = string.IsNullOrEmpty(c.Path) ||
                    uri.AbsolutePath.StartsWith(c.Path, StringComparison.Ordinal);
                return domainMatches && pathMatches && (!c.Secure || uri.Scheme == Uri.UriSchemeHttps);
            })
            .Take(100)
            .Select(c => $"{c.Name}={c.Value ?? ""}"));
    }

    /// <summary>
    /// Resolves a stream edge host. System DNS first (correct CDN routing);
    /// public DNS-over-HTTPS as fallback when local DNS (VPN/WARP filters)
    /// fails but the name exists. Returns Resolved=false when the name is
    /// dead everywhere (stale captured link) and Pin set when DoH saved it.
    /// </summary>
    private static async Task<(bool Resolved, IPAddress? Pin)> ResolveStreamHostAsync(
        string host, DownloadItem item, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var addrs = await Dns.GetHostAddressesAsync(host, cts.Token);
            if (addrs != null && addrs.Length > 0) return (true, null);
        }
        catch (Exception ex)
        {
            LogDiag(item, $"system DNS failed for {host}: {ex.Message}");
        }
        foreach (string template in new[]
        {
            "https://dns.google/resolve?name={0}&type=A",
            "https://cloudflare-dns.com/dns-query?name={0}&type=A"
        })
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    string.Format(template, Uri.EscapeDataString(host)));
                req.Headers.TryAddWithoutValidation("accept", "application/dns-json");
                using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                resp.EnsureSuccessStatusCode();
                string json = await resp.Content.ReadAsStringAsync(cts.Token);
                var m = Regex.Match(json, "\"data\"\\s*:\\s*\"(\\d{1,3}(?:\\.\\d{1,3}){3})\"");
                if (m.Success && IPAddress.TryParse(m.Groups[1].Value, out var ip))
                    return (true, ip);
            }
            catch (Exception ex)
            {
                LogDiag(item, $"DoH failed for {host} via {template[8..Math.Min(template.Length, 30)]}: {ex.Message}");
            }
        }
        return (false, null);
    }

    /// <summary>Signed stream URLs carry ?expire=unix (or e=epoch on some
    /// CDNs); past = dead link.</summary>
    private static bool IsExpiredSignedUrl(string? url)
    {
        try
        {
            var m = Regex.Match(url ?? "", @"[?&]expire=(\d+)", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                var e2 = Regex.Match(url ?? "", @"[?&]e=(\d{10})(?:\D|$)");
                if (!e2.Success) return false;
                if (!long.TryParse(e2.Groups[1].Value, out long ev)) return false;
                if (ev < 1_000_000_000L || ev > 4_000_000_000L) return false;
                return ev < DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
            }
            if (!long.TryParse(m.Groups[1].Value, out long exp)) return false;
            return exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
        }
        catch { return false; }
    }

    private static bool IsDnsFailure(Exception ex)
    {
        string msg = ex.Message ?? "";
        return ex is SocketException ||
               msg.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("11001") ||
               msg.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Downloads extension-resolved streams directly (no yt-dlp extraction):
    /// video bytes + audio companion bytes with the live browser session,
    /// muxed to MP4 via ffmpeg when both tracks exist. Returns true when a
    /// playable file was moved to the downloads folder.
    /// </summary>
    private static async Task<bool> TryDownloadExtensionResolvedAsync(
        DownloadItem item, string downloadsFolder, string partsSubdir,
        string safeTitle, string ext, CancellationToken cancellationToken)
    {
        if (IsSabrRedirector(item.Url))
            throw new InvalidOperationException("SABR handshake URL is not downloadable - need the HLS master: reload the page, play the video, then retry");
        string videoUrl = WithPot(item.Url, item.PoToken);
        string audioUrl = WithPot(item.AudioUrl ?? "", item.PoToken);
        // Signed links die at ?expire: fail fast with a replay hint instead
        // of burning minutes on DNS/410 errors for a stale capture.
        if (IsExpiredSignedUrl(videoUrl))
            throw new InvalidOperationException("stream link expired - replay the video (at the wanted quality) for a fresh link, then retry");
        bool hasAudio = IsCapturedYouTubePlaybackUrl(audioUrl);
        if (hasAudio && IsExpiredSignedUrl(audioUrl))
        {
            LogDiag(item, "audio companion expired, continuing video-only");
            hasAudio = false;
            audioUrl = "";
        }
        bool progressive = IsProgressiveItag(videoUrl);
        LogDiag(item, $"extension-resolved direct: video itag progressive={progressive} audio={(hasAudio ? "yes" : "no")}");

        item.Status = DownloadStatus.Downloading;
        item.StatusText = AppLocalization.Get("download.connectingStream");
        item.SpeedText = AppLocalization.Get("download.resolving");

        var streamHeaders = item.StreamHeaders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool hasCapturedBrowserHeaders = streamHeaders.Count > 0;
        // When Chrome supplied the exact request headers, use its Cookie
        // header (if any). Page cookies are for youtube.com and may not match
        // the googlevideo request that IDM successfully captured.
        string cookieHeader = hasCapturedBrowserHeaders
            ? streamHeaders.FirstOrDefault(h => h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)).Value ?? ""
            : BuildCookieHeader(item.Cookies, videoUrl);
        string referer = !string.IsNullOrWhiteSpace(item.Referrer) ? item.Referrer
            : !string.IsNullOrWhiteSpace(item.PageUrl) ? item.PageUrl
            : "https://www.youtube.com/";
        string userAgent = !string.IsNullOrWhiteSpace(item.UserAgent)
            ? item.UserAgent
            : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

        // Stream edge hosts sometimes fail on the system resolver (VPN/WARP
        // DNS) while the browser resolves them fine. Resolve up front: system
        // first (correct edge routing), public DoH pinned to IP as fallback
        // (TLS SNI stays the original host, certificates still validate).
        // Neither resolving = dead/expired link: fail fast with a replay hint
        // instead of a cryptic DNS error after long timeouts.
        var pinnedIps = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        foreach (string u in new[] { videoUrl, hasAudio ? audioUrl : "" })
        {
            if (string.IsNullOrWhiteSpace(u)) continue;
            string host;
            try { host = new Uri(u).Host; } catch { continue; }
            if (pinnedIps.ContainsKey(host)) continue;
            var res = await ResolveStreamHostAsync(host, item, cancellationToken);
            if (!res.Resolved)
                throw new InvalidOperationException(
                    $"stream host {host} does not resolve - link expired, replay the video for a fresh link");
            if (res.Pin != null)
            {
                pinnedIps[host] = res.Pin;
                LogDiag(item, $"DoH-pinned {host} -> {res.Pin}");
            }
        }

        using var pinHandler = pinnedIps.Count > 0
            ? new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                ConnectCallback = async (ctx, ct) =>
                {
                    if (pinnedIps.TryGetValue(ctx.DnsEndPoint.Host, out var pin))
                    {
                        var sock = new Socket(pin.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            await sock.ConnectAsync(new IPEndPoint(pin, ctx.DnsEndPoint.Port), ct);
                            return (Stream)new NetworkStream(sock, ownsSocket: true);
                        }
                        catch { sock.Dispose(); throw; }
                    }
                    var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
                    Exception? last = null;
                    foreach (var addr in addresses)
                    {
                        var s = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            await s.ConnectAsync(new IPEndPoint(addr, ctx.DnsEndPoint.Port), ct);
                            return (Stream)new NetworkStream(s, ownsSocket: true);
                        }
                        catch (Exception ex) { last = ex; s.Dispose(); }
                    }
                    throw last ?? new InvalidOperationException("connect failed");
                }
            }
            : null;
        using HttpClient? ownedClient = pinHandler != null ? new HttpClient(pinHandler) : null;
        HttpClient dlClient = ownedClient ?? _httpClient;

        async Task<bool> TryDownloadGoogleVideoWithAria2cAsync(string url, string destPath, string label, CancellationToken ct)
        {
            string? aria2cPath;
            try { aria2cPath = Aria2cHelper.FindAria2c(); }
            catch { aria2cPath = null; }
            if (string.IsNullOrWhiteSpace(aria2cPath) || !File.Exists(aria2cPath) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var streamUri) ||
                !streamUri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase))
                return false;

            Process? process = null;
            try
            {
                try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
                int connections = Math.Clamp(SettingsHelper.ConcurrentFragments, 2, 8);
                var psi = new ProcessStartInfo
                {
                    FileName = aria2cPath,
                    WorkingDirectory = Path.GetDirectoryName(destPath) ?? downloadsFolder,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                void AddArg(string name, string value)
                {
                    psi.ArgumentList.Add(name);
                    psi.ArgumentList.Add(value);
                }
                AddArg("--dir", Path.GetDirectoryName(destPath) ?? downloadsFolder);
                AddArg("--out", Path.GetFileName(destPath));
                AddArg("--split", connections.ToString());
                AddArg("--max-connection-per-server", connections.ToString());
                AddArg("--min-split-size", "1M");
                AddArg("--file-allocation", "none");
                psi.ArgumentList.Add("--allow-overwrite=true");
                psi.ArgumentList.Add("--auto-file-renaming=false");
                psi.ArgumentList.Add("--continue=false");
                AddArg("--max-tries", "3");
                AddArg("--retry-wait", "1");
                AddArg("--connect-timeout", "20");
                AddArg("--timeout", "30");
                AddArg("--console-log-level", "error");
                if (SettingsHelper.MaximumDownloadRateKBps > 0)
                    AddArg("--max-download-limit", $"{SettingsHelper.MaximumDownloadRateKBps}K");

                var emittedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void AddHeader(string name, string value)
                {
                    if (string.IsNullOrWhiteSpace(value) || !emittedHeaders.Add(name)) return;
                    AddArg("--header", $"{name}: {value}");
                }
                if (hasCapturedBrowserHeaders)
                {
                    foreach (var header in streamHeaders)
                    {
                        if (header.Key.Equals("Range", StringComparison.OrdinalIgnoreCase) ||
                            header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                        AddHeader(header.Key, header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                            ? cookieHeader : header.Value);
                    }
                }
                AddHeader("Cookie", cookieHeader);
                AddHeader("User-Agent", userAgent);
                AddHeader("Referer", referer);
                AddArg("--", url);

                process = Process.Start(psi);
                if (process == null) return false;
                var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = process.StandardError.ReadToEndAsync(ct);
                var timer = Stopwatch.StartNew();
                var sample = Stopwatch.StartNew();
                long lastBytes = 0;
                while (!process.HasExited)
                {
                    await Task.Delay(500, ct);
                    long currentBytes = 0;
                    try { currentBytes = new FileInfo(destPath).Length; } catch { }
                    if (sample.ElapsedMilliseconds >= 500)
                    {
                        double bytesPerSecond = (currentBytes - lastBytes) / Math.Max(0.01, sample.Elapsed.TotalSeconds);
                        item.SpeedText = $"{FormatBytes((long)Math.Max(0, bytesPerSecond))}/s";
                        item.StatusText = AppLocalization.Format("download.transferred", FormatBytes(currentBytes), label);
                        lastBytes = currentBytes;
                        sample.Restart();
                    }
                }
                await process.WaitForExitAsync(ct);
                _ = await stdoutTask;
                string ariaDiagnostics = await stderrTask;
                long downloadedBytes = File.Exists(destPath) ? new FileInfo(destPath).Length : 0;
                if (process.ExitCode == 0 && downloadedBytes > 256 * 1024)
                {
                    LogDiag(item, $"aria2c multi-connection {label} completed bytes={downloadedBytes} elapsed={timer.Elapsed.TotalSeconds:F1}s connections={connections}");
                    return true;
                }

                string safeDiagnostics = Regex.Replace(ariaDiagnostics, @"https?://\S+", "<stream-url>");
                if (safeDiagnostics.Length > 180) safeDiagnostics = safeDiagnostics[..180];
                LogDiag(item, $"aria2c multi-connection {label} unavailable (exit={process.ExitCode}){(string.IsNullOrWhiteSpace(safeDiagnostics) ? "" : $": {safeDiagnostics.Trim()}")}; retrying with HTTP client");
                try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
                return false;
            }
            catch (OperationCanceledException)
            {
                try { if (process != null && !process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                try { if (process != null && !process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
                LogDiag(item, $"aria2c multi-connection {label} unavailable ({ex.GetType().Name}); retrying with HTTP client");
                return false;
            }
            finally
            {
                process?.Dispose();
            }
        }

        async Task<string> DownloadUrlToFileAsync(string url, string destPath, string label, CancellationToken ct)
        {
            if (await TryDownloadGoogleVideoWithAria2cAsync(url, destPath, label, ct))
                return destPath;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (hasCapturedBrowserHeaders)
            {
                foreach (var header in streamHeaders)
                {
                    if (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(cookieHeader))
                            req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                    }
                    else
                    {
                        req.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }
            else
            {
                req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                req.Headers.TryAddWithoutValidation("Referer", referer);
                req.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                req.Headers.TryAddWithoutValidation("Accept", "*/*");
                req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                if (!string.IsNullOrEmpty(cookieHeader))
                    req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }
            HttpResponseMessage resp;
            try
            {
                resp = await dlClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (IsDnsFailure(ex))
            {
                throw new InvalidOperationException(
                    $"{label} host does not resolve - link expired, replay the video for a fresh link");
            }
            using (resp)
            {
                if ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 401)
                    throw new InvalidOperationException($"{label} rejected ({(int)resp.StatusCode} - session expired, replay the video and retry)");
                if ((int)resp.StatusCode == 410)
                    throw new InvalidOperationException($"{label} expired (410 Gone - replay the video for a fresh link)");
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;
                using var net = await resp.Content.ReadAsStreamAsync(ct);
                using var file = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 524288, true);
                byte[] buffer = new byte[524288];
                long totalRead = 0;
                int n;
                var sw = Stopwatch.StartNew();
                var timer = Stopwatch.StartNew();
                long lastBytes = 0;
                while ((n = await net.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await file.WriteAsync(buffer, 0, n, ct);
                    totalRead += n;
                    if (total.HasValue && total.Value > 0)
                        item.Progress = Math.Min(99, (double)totalRead / total.Value * (hasAudio && !progressive ? 50 : 100) + (label == "audio" && !progressive ? 50 : 0));
                    if (sw.ElapsedMilliseconds >= 500)
                    {
                        double speed = (totalRead - lastBytes) / Math.Max(0.01, sw.Elapsed.TotalSeconds);
                        item.SpeedText = $"{FormatBytes((long)speed)}/s";
                        item.StatusText = total.HasValue && total.Value > 0
                            ? AppLocalization.Format("download.progressSpeed", item.Progress, FormatBytes(total.Value), item.SpeedText)
                            : AppLocalization.Format("download.transferred", FormatBytes(totalRead), label);
                        lastBytes = totalRead;
                        sw.Restart();
                    }
                    _ = timer;
                }
                return destPath;
            }
        }

        string videoTmp = Path.Combine(partsSubdir, safeTitle + ".video.tmp");
        string audioTmp = Path.Combine(partsSubdir, safeTitle + ".audio.tmp");
        string outTmp = Path.Combine(partsSubdir, safeTitle + "." + ext);

        await DownloadUrlToFileAsync(videoUrl, videoTmp, "video", cancellationToken);
        var videoInfo = new FileInfo(videoTmp);
        LogDiag(item, $"video bytes={videoInfo.Exists} size={(videoInfo.Exists ? videoInfo.Length : 0)}");
        if (!videoInfo.Exists || videoInfo.Length < 256 * 1024)
            throw new InvalidOperationException("video stream too small - link likely expired, replay the video and retry");

        if (hasAudio && !progressive)
        {
            item.StatusText = AppLocalization.Get("download.resolving");
            LogDiag(item, "audio companion download starting (throttled hosts take minutes, no progress = still working)");
            await DownloadUrlToFileAsync(audioUrl, audioTmp, "audio", cancellationToken);
            var audioInfo = new FileInfo(audioTmp);
            LogDiag(item, $"audio bytes={(audioInfo.Exists ? audioInfo.Length : 0)}");
            if (!audioInfo.Exists || audioInfo.Length < 16 * 1024)
                throw new InvalidOperationException("audio stream too small - replay the video and retry");

            // Mux without re-encode. Failure keeps the video track rather
            // than failing the whole download.
            bool muxed = false;
            try
            {
                string ffmpeg = FindFfmpegPath();
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-y -i \"{videoTmp}\" -i \"{audioTmp}\" -c copy -movflags +faststart \"{outTmp}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync(cancellationToken);
                    muxed = proc.ExitCode == 0 && File.Exists(outTmp) && new FileInfo(outTmp).Length > 256 * 1024;
                }
            }
            catch { muxed = false; }
            if (!muxed)
            {
                // No ffmpeg: fall back to the video-only file so the user
                // still gets picture instead of an error.
                try { if (File.Exists(outTmp)) File.Delete(outTmp); } catch { }
                File.Move(videoTmp, outTmp);
            }
            else
            {
                try { File.Delete(videoTmp); } catch { }
            }
            try { if (File.Exists(audioTmp)) File.Delete(audioTmp); } catch { }
        }
        else
        {
            try { if (File.Exists(outTmp)) File.Delete(outTmp); } catch { }
            File.Move(videoTmp, outTmp);
        }

        var moved = MoveFinishedMediaToDownloads(partsSubdir, downloadsFolder, item);
        DeletePartsSubdirIfClean(partsSubdir, item);
        string dest = moved.FirstOrDefault().Dest ?? "";
        if (string.IsNullOrEmpty(dest) || !File.Exists(dest))
        {
            // Scratch layout unexpected: move the file ourselves.
            dest = MoveFileRobust(outTmp, downloadsFolder, Path.GetFileName(outTmp));
            try { if (Directory.Exists(partsSubdir)) Directory.Delete(partsSubdir, recursive: true); } catch { }
        }
        LogDiag(item, $"extension-resolved direct completed: {dest}");
        item.Progress = 100;
        item.Status = DownloadStatus.Completed;
        item.SpeedText = AppLocalization.Get("download.finished");
        item.EtaText = "--";
        item.SavePath = dest;
        item.StatusText = AppLocalization.Format("download.completedSaved", Path.GetFileName(dest));
#if DEBUG
        TrackEnd(item, $"OK extension-resolved file={dest}");
#endif
        return true;
    }

    private static (long Start, long? End) ParseRangeStart(string? range)
    {
        try
        {
            var m = Regex.Match(range ?? "", @"bytes=(\d+)(?:-(\d*))?", RegexOptions.IgnoreCase);
            if (!m.Success || !long.TryParse(m.Groups[1].Value, out long s)) return (long.MaxValue, null);
            long? e = null;
            if (m.Groups[2].Success && !string.IsNullOrEmpty(m.Groups[2].Value) &&
                long.TryParse(m.Groups[2].Value, out long ev)) e = ev;
            return (s, e);
        }
        catch { return (long.MaxValue, null); }
    }

    private static (long Start, long? End) ParseInitRange(string? range)
    {
        try
        {
            var m = Regex.Match(range ?? "", @"(\d+)\s*-\s*(\d+)?");
            if (!m.Success || !long.TryParse(m.Groups[1].Value, out long s)) return (-1, null);
            long? e = null;
            if (m.Groups[2].Success && !string.IsNullOrEmpty(m.Groups[2].Value) &&
                long.TryParse(m.Groups[2].Value, out long ev)) e = ev;
            return (s, e);
        }
        catch { return (-1, null); }
    }

    /// <summary>
    /// Reassembles a SABR rendition from the browser's captured segment
    /// traffic: full-GET attempt first (some edges serve it whole), else
    /// replay each byte range in order with the live session and concatenate.
    /// Init range first so fMP4/DASH output is playable. Then mux audio.
    /// Returns true when a playable file reached the downloads folder.
    /// </summary>
    private static async Task<bool> TryDownloadSabrAsync(
        DownloadItem item, string downloadsFolder, string partsSubdir,
        string safeTitle, string ext, CancellationToken cancellationToken)
    {
        var segments = (item.Segments ?? new List<StreamSegment>())
            .Where(s => !string.IsNullOrWhiteSpace(s.Url)).ToList();
        var audioSegments = (item.AudioSegments ?? new List<StreamSegment>())
            .Where(s => !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (segments.Count == 0)
            throw new InvalidOperationException("no SABR segments captured - play the video through (at the wanted quality) so the tab fetches every part");

        item.Status = DownloadStatus.Downloading;
        item.StatusText = AppLocalization.Get("download.connectingStream");
        item.SpeedText = AppLocalization.Get("download.resolving");

        string cookieHeader = BuildCookieHeader(item.Cookies);
        string referer = !string.IsNullOrWhiteSpace(item.Referrer) ? item.Referrer
            : !string.IsNullOrWhiteSpace(item.PageUrl) ? item.PageUrl
            : "https://www.youtube.com/";
        string userAgent = !string.IsNullOrWhiteSpace(item.UserAgent)
            ? item.UserAgent
            : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

        // Resolve + pin hosts (system DNS first, DoH fallback).
        var pinnedIps = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        foreach (string u in segments.Select(s => s.Url)
            .Concat(audioSegments.Select(s => s.Url)).Distinct())
        {
            string host;
            try { host = new Uri(u).Host; } catch { continue; }
            if (pinnedIps.ContainsKey(host)) continue;
            var res = await ResolveStreamHostAsync(host, item, cancellationToken);
            if (!res.Resolved)
                throw new InvalidOperationException(
                    $"stream host {host} does not resolve - replay the video for fresh links, then retry");
            if (res.Pin != null) pinnedIps[host] = res.Pin;
        }
        using var pinHandler = pinnedIps.Count > 0
            ? new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                ConnectCallback = async (ctx, ct) =>
                {
                    if (pinnedIps.TryGetValue(ctx.DnsEndPoint.Host, out var pin))
                    {
                        var sock = new Socket(pin.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            await sock.ConnectAsync(new IPEndPoint(pin, ctx.DnsEndPoint.Port), ct);
                            return (Stream)new NetworkStream(sock, ownsSocket: true);
                        }
                        catch { sock.Dispose(); throw; }
                    }
                    var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
                    Exception? last = null;
                    foreach (var addr in addresses)
                    {
                        var s = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            await s.ConnectAsync(new IPEndPoint(addr, ctx.DnsEndPoint.Port), ct);
                            return (Stream)new NetworkStream(s, ownsSocket: true);
                        }
                        catch (Exception ex) { last = ex; s.Dispose(); }
                    }
                    throw last ?? new InvalidOperationException("connect failed");
                }
            }
            : null;
        using HttpClient? ownedClient = pinHandler != null ? new HttpClient(pinHandler) : null;
        HttpClient dlClient = ownedClient ?? _httpClient;

        long totalDone = 0;
        long grandTotal = item.ExpectedBytes;

        async Task<byte[]> GetBytesAsync(string url, string? range, string label, CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(180));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            req.Headers.TryAddWithoutValidation("Referer", referer);
            req.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
            req.Headers.TryAddWithoutValidation("Accept", "*/*");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            if (!string.IsNullOrEmpty(cookieHeader))
                req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            var (rs, re) = ParseRangeStart(range);
            if (rs != long.MaxValue)
                req.Headers.Range = new RangeHeaderValue(rs, re);
            HttpResponseMessage resp;
            try
            {
                resp = await dlClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            }
            catch (Exception ex) when (IsDnsFailure(ex))
            {
                throw new InvalidOperationException($"{label} host does not resolve - replay the video for fresh links");
            }
            using (resp)
            {
                if ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 401)
                    throw new InvalidOperationException($"{label} rejected ({(int)resp.StatusCode} - session expired, replay the video and retry)");
                if ((int)resp.StatusCode == 410)
                    throw new InvalidOperationException($"{label} expired (410 Gone - replay the video for a fresh link)");
                if ((int)resp.StatusCode == 416)
                    throw new InvalidOperationException($"{label} range not satisfiable - links went stale, replay the video");
                resp.EnsureSuccessStatusCode();
                byte[] data = await resp.Content.ReadAsByteArrayAsync(timeoutCts.Token);
                totalDone += data.Length;
                if (grandTotal > 0)
                    item.Progress = Math.Min(99, (double)totalDone / grandTotal * 100);
                item.SpeedText = $"{FormatBytes(data.Length)} part";
                item.StatusText = grandTotal > 0
                    ? AppLocalization.Format("download.progressSpeed", item.Progress, FormatBytes(grandTotal), item.SpeedText)
                    : AppLocalization.Format("download.transferred", FormatBytes(totalDone), label);
                return data;
            }
        }

        // One track (video or audio): full GET first, else init + ranges.
        async Task<string> BuildTrackAsync(
            string trackPath, string repUrl, string initRange,
            List<StreamSegment> segs, long expected, string label, CancellationToken ct)
        {
            // (1) Full-file attempt on the representative URL.
            try
            {
                byte[] full = await GetBytesAsync(repUrl, null, label + " whole", ct);
                if (full.Length >= 256 * 1024 && (expected <= 0 || full.Length >= expected * 9 / 10))
                {
                    await File.WriteAllBytesAsync(trackPath, full, ct);
                    LogDiag(item, $"{label} whole-file OK bytes={full.Length}");
                    return trackPath;
                }
                LogDiag(item, $"{label} whole-file short ({full.Length} vs {expected}), segment replay");
            }
            catch (Exception ex)
            {
                LogDiag(item, $"{label} whole-file failed ({ex.Message}), segment replay");
            }
            // (2) Segment replay in byte order with overlap skipping.
            var ordered = segs
                .Select(s => (Seg: s, R: ParseRangeStart(s.Range)))
                .Where(x => x.R.Start != long.MaxValue)
                .OrderBy(x => x.R.Start)
                .ToList();
            if (ordered.Count == 0)
                throw new InvalidOperationException($"{label}: no ranged segments captured - play the video through (at the wanted quality) so the tab fetches every part");
            using var file = new FileStream(trackPath, FileMode.Create, FileAccess.Write, FileShare.None, 524288, true);
            long coveredUntil = -1;
            long written = 0;
            // Init range first (explicit fetch beats hoping the tab got it).
            var (initStart, initEnd) = ParseInitRange(initRange);
            if (initStart >= 0)
            {
                try
                {
                    byte[] init = await GetBytesAsync(repUrl, $"bytes={initStart}-{initEnd?.ToString() ?? ""}", label + " init", ct);
                    await file.WriteAsync(init, 0, init.Length, ct);
                    written += init.Length;
                    coveredUntil = initEnd ?? (initStart + init.Length - 1);
                    // Exact init end unknown: trust fetched length.
                    if (initEnd == null) coveredUntil = initStart + init.Length - 1;
                    LogDiag(item, $"{label} init bytes={init.Length}");
                }
                catch (Exception ex)
                {
                    LogDiag(item, $"{label} init fetch failed ({ex.Message}), relying on captured segments");
                }
            }
            int fetched = 0, skipped = 0;
            foreach (var (seg, r) in ordered)
            {
                long segEnd = r.End ?? long.MaxValue;
                if (r.Start <= coveredUntil && segEnd <= coveredUntil) { skipped++; continue; }
                byte[] data;
                try
                {
                    data = await GetBytesAsync(seg.Url, seg.Range, label + " part", ct);
                }
                catch
                {
                    // One dead segment must not kill the file; the player
                    // tolerates small gaps far better than a failed download.
                    skipped++;
                    continue;
                }
                // Trim overlap with already-written prefix.
                long overlap = coveredUntil >= r.Start ? coveredUntil - r.Start + 1 : 0;
                if (overlap > 0)
                {
                    if (overlap >= data.Length) { skipped++; continue; }
                    await file.WriteAsync(data, (int)overlap, data.Length - (int)overlap, ct);
                    written += data.Length - overlap;
                    coveredUntil = Math.Max(coveredUntil, r.Start + data.Length - 1);
                }
                else
                {
                    await file.WriteAsync(data, 0, data.Length, ct);
                    written += data.Length;
                    coveredUntil = Math.Max(coveredUntil, r.Start + data.Length - 1);
                }
                fetched++;
            }
            await file.FlushAsync(ct);
            LogDiag(item, $"{label} segments fetched={fetched} skipped={skipped} bytes={written} expected={expected}");
            if (written < 256 * 1024)
                throw new InvalidOperationException($"{label}: only {FormatBytes(written)} reassembled - play the video through so the tab fetches every part");
            if (expected > 0 && written < expected / 2)
                throw new InvalidOperationException($"{label}: only {FormatBytes(written)} of {FormatBytes(expected)} captured - seek through the whole video, then retry");
            return trackPath;
        }

        string videoTmp = Path.Combine(partsSubdir, safeTitle + ".sabr-video.tmp");
        string audioTmp = Path.Combine(partsSubdir, safeTitle + ".sabr-audio.tmp");
        string outTmp = Path.Combine(partsSubdir, safeTitle + "." + ext);

        string repUrl = !string.IsNullOrWhiteSpace(item.Url) ? item.Url : segments[0].Url;
        await BuildTrackAsync(videoTmp, repUrl, item.InitRange, segments, item.ExpectedBytes, "video", cancellationToken);

        bool hasAudioTrack = false;
        if (audioSegments.Count > 0)
        {
            string audioRep = !string.IsNullOrWhiteSpace(item.AudioUrl) ? item.AudioUrl : audioSegments[0].Url;
            try
            {
                await BuildTrackAsync(audioTmp, audioRep, item.AudioInitRange, audioSegments, 0, "audio", cancellationToken);
                hasAudioTrack = true;
            }
            catch (Exception ex)
            {
                LogDiag(item, $"audio track failed, continuing video-only: {ex.Message}");
                try { if (File.Exists(audioTmp)) File.Delete(audioTmp); } catch { }
            }
        }

        if (hasAudioTrack)
        {
            bool muxed = false;
            try
            {
                string ffmpeg = FindFfmpegPath();
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-y -i \"{videoTmp}\" -i \"{audioTmp}\" -c copy -movflags +faststart \"{outTmp}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync(cancellationToken);
                    muxed = proc.ExitCode == 0 && File.Exists(outTmp) && new FileInfo(outTmp).Length > 256 * 1024;
                }
            }
            catch { muxed = false; }
            if (!muxed)
            {
                try { if (File.Exists(outTmp)) File.Delete(outTmp); } catch { }
                File.Move(videoTmp, outTmp);
            }
            else
            {
                try { File.Delete(videoTmp); } catch { }
            }
            try { if (File.Exists(audioTmp)) File.Delete(audioTmp); } catch { }
        }
        else
        {
            try { if (File.Exists(outTmp)) File.Delete(outTmp); } catch { }
            File.Move(videoTmp, outTmp);
        }

        var moved = MoveFinishedMediaToDownloads(partsSubdir, downloadsFolder, item);
        DeletePartsSubdirIfClean(partsSubdir, item);
        string dest = moved.FirstOrDefault().Dest ?? "";
        if (string.IsNullOrEmpty(dest) || !File.Exists(dest))
        {
            dest = MoveFileRobust(outTmp, downloadsFolder, Path.GetFileName(outTmp));
            try { if (Directory.Exists(partsSubdir)) Directory.Delete(partsSubdir, recursive: true); } catch { }
        }
        LogDiag(item, $"SABR reassembly completed: {dest}");
        item.Progress = 100;
        item.Status = DownloadStatus.Completed;
        item.SpeedText = AppLocalization.Get("download.finished");
        item.EtaText = "--";
        item.SavePath = dest;
        item.StatusText = AppLocalization.Format("download.completedSaved", Path.GetFileName(dest));
#if DEBUG
        TrackEnd(item, $"OK sabr file={dest}");
#endif
        return true;
    }

    private static bool IsStreamingSite(DownloadItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Url)) return false;

        // If the browser extension explicitly categorized this as a file download, do direct download
        if (string.Equals(item.Quality, "file", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string url = item.Url;
        string lower = url.ToLowerInvariant();

        // 1. Stream manifests, playlists, and chunk endpoints
        if (IsManifestUrl(url) ||
            lower.Contains("/master") ||
            lower.Contains("/playlist") ||
            lower.Contains("/videoplayback") ||
            lower.Contains("blob:"))
        {
            return true;
        }

        // 2. If the URL has a filename query parameter (e.g. slug=win64.exe.zip, file=abc.zip), direct download
        try
        {
            var uri = new Uri(url);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            foreach (string? key in query.AllKeys)
            {
                if (key != null && (key.Equals("slug", StringComparison.OrdinalIgnoreCase) || 
                                    key.Equals("file", StringComparison.OrdinalIgnoreCase) || 
                                    key.Equals("filename", StringComparison.OrdinalIgnoreCase)))
                {
                    string val = query[key] ?? "";
                    if (val.Contains('.') && !val.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !val.EndsWith(".php", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            string ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext) || ext == ".html" || ext == ".htm" || ext == ".php" || ext == ".asp" || ext == ".aspx")
            {
                return true;
            }
        }
        catch { }

        return false;
    }

    private static async Task DownloadStreamingSiteAsync(DownloadItem item, string downloadsFolder, CancellationToken cancellationToken)
    {
        bool isDirectManifest = IsManifestUrl(item.Url);

        item.Status = DownloadStatus.Downloading;
        item.StatusText = AppLocalization.Get(isDirectManifest ? "download.connectingStream" : "download.extracting");
        item.SpeedText = AppLocalization.Get("download.resolving");

        string safeTitle = SanitizeFileName(item.Title);
        // Clean off quality suffixes if present in item.Title (e.g. "Title - 1080p" -> "Title")
        string strippedTitle = Regex.Replace(safeTitle, @"\s*[-–—]\s*(?:\d{3,4}p|best|audio|video)$", "", RegexOptions.IgnoreCase).Trim();

        bool isGenericTitle = string.IsNullOrWhiteSpace(strippedTitle) || 
                               strippedTitle.Equals("master", StringComparison.OrdinalIgnoreCase) || 
                               strippedTitle.Equals("index", StringComparison.OrdinalIgnoreCase) ||
                               strippedTitle.Equals("video", StringComparison.OrdinalIgnoreCase) ||
                               strippedTitle.Equals("Direct Download", StringComparison.OrdinalIgnoreCase) ||
                               strippedTitle.Equals("Video_Download", StringComparison.OrdinalIgnoreCase) ||
                               strippedTitle.Equals("Video Download", StringComparison.OrdinalIgnoreCase) ||
                               strippedTitle.Equals("YouTube", StringComparison.OrdinalIgnoreCase) ||
                               strippedTitle.StartsWith("view_video", StringComparison.OrdinalIgnoreCase) ||
                               strippedTitle.StartsWith("watch", StringComparison.OrdinalIgnoreCase);

        if (isGenericTitle)
        {
            safeTitle = "Video_Download";
        }
        else
        {
            safeTitle = strippedTitle;
        }

        bool isAudio = string.Equals(item.Quality, "audio", StringComparison.OrdinalIgnoreCase) ||
                       safeTitle.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(item.Quality))
            item.Quality = SettingsHelper.DefaultQuality;

        // Strip extension if already part of title
        if (safeTitle.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            safeTitle = safeTitle[..^4].TrimEnd();
        if (safeTitle.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            safeTitle = safeTitle[..^4].TrimEnd();
        if (safeTitle.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            safeTitle = safeTitle[..^5].TrimEnd();
        if (safeTitle.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
            safeTitle = safeTitle[..^4].TrimEnd();

        string ext = isAudio ? "mp3" : "mp4";

        // Isolated scratch space: yt-dlp writes EVERYTHING here (fragments,
        // .part, merges). Collision numbering happens at move time, when the
        // finished file lands in the real downloads folder.
        string partsSubdir = ResolvePartsSubdir(item, safeTitle);

        // If title is generic or from a webpage script, let yt-dlp determine the real video title!
        // %(autonumber)s or yt-dlp template can be used, and after download completion we also ensure collision number.
        string outputTemplate = item.DownloadPlaylist
            ? Path.Combine(partsSubdir, "%(playlist_title)s", "%(playlist_index)03d - %(title)s.%(ext)s")
            : isGenericTitle
                ? Path.Combine(partsSubdir, "%(title)s.%(ext)s")
                : Path.Combine(partsSubdir, $"{safeTitle}.%(ext)s");
        string temporaryFolder = partsSubdir;

        // SABR fast path: pure-SABR sessions (gated videos) have no single
        // downloadable URL - only the browser's live segment traffic, which
        // the extension collected per rendition. Replay ranges in byte order,
        // concatenate, mux. Zero page re-resolve.
        if (!item.DownloadPlaylist && item.IsSabr && item.Segments != null && item.Segments.Count > 0)
        {
            try
            {
                if (await TryDownloadSabrAsync(
                    item, downloadsFolder, partsSubdir, safeTitle, ext, cancellationToken))
                    return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogDiag(item, $"SABR reassembly failed, falling back to yt-dlp: {ex.Message}");
                item.Progress = 0;
                item.SavePath = "";
                item.Status = DownloadStatus.Downloading;
                item.StatusText = AppLocalization.Get("download.extracting");
                item.SpeedText = AppLocalization.Get("download.resolving");
            }
        }

        // Extension-resolved fast path: the companion extension captured the
        // exact authorized stream bytes the browser is already playing
        // (videoplayback + itag, fresh signed params). Download those bytes
        // directly with the live browser session and mux - zero page
        // re-resolve, which is precisely what age/login gates block.
        // yt-dlp below remains as the fallback when this fails.
        if (!item.DownloadPlaylist && !isAudio && IsExtensionResolvedPlayback(item))
        {
            try
            {
                bool directOk = await TryDownloadExtensionResolvedAsync(
                    item, downloadsFolder, partsSubdir, safeTitle, ext, cancellationToken);
                if (directOk) return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogDiag(item, $"extension-resolved direct failed, falling back to yt-dlp: {ex.Message}");
                item.Progress = 0;
                item.SavePath = "";
                item.Status = DownloadStatus.Downloading;
                item.StatusText = AppLocalization.Get("download.extracting");
                item.SpeedText = AppLocalization.Get("download.resolving");
            }
        }

        // Locate yt-dlp.exe
        string ytdlpPath = "yt-dlp";
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string pythonYtDlp = Path.Combine(localAppData, @"Programs\Python\Python312\Scripts\yt-dlp.exe");
        if (File.Exists(pythonYtDlp)) ytdlpPath = pythonYtDlp;
        LogDiag(item, $"start quality={item.Quality} ytdlp={ytdlpPath} version={YtDlpVersion(ytdlpPath)}");

        // Multi-connection plain-HTTP downloads (IDM-style). HLS/DASH stay on
        // yt-dlp's native downloader (--concurrent-fragments); aria2c only
        // handles generic http(s), so passing it globally is safe.
        string? aria2cPath = null;
        try
        {
            aria2cPath = Aria2cHelper.FindAria2c();
            if (aria2cPath == null)
            {
                _ = Task.Run(async () =>
                {
                    try { await Aria2cHelper.EnsureAria2cAsync(); } catch { }
                });
            }
        }
        catch { aria2cPath = null; }

        // PO-token provider for YouTube gates (session + token together open
        // age/bot walls). One-time per session, only for YouTube downloads.
        try
        {
            if (PotProviderHelper.IsYouTubeUrl(item.Url) || PotProviderHelper.IsYouTubeUrl(item.PageUrl))
                await PotProviderHelper.EnsureAsync(ytdlpPath, cancellationToken);
        }
        catch { }

        var argsBuilder = new StringBuilder();
        // IDM-style parallel segment download flags with robust retry & timeout settings to ensure it completes
        argsBuilder.Append(item.DownloadPlaylist ? "--yes-playlist " : "--no-playlist ");
        argsBuilder.Append("--no-warnings --continue --socket-timeout 20 --retries 10 --fragment-retries 20 ");
        argsBuilder.Append($"--concurrent-fragments {SettingsHelper.ConcurrentFragments} ");
        if (SettingsHelper.MaximumDownloadRateKBps > 0)
        {
            argsBuilder.Append($"--limit-rate {SettingsHelper.MaximumDownloadRateKBps}K ");
        }
        // Maximize network buffer & chunking to avoid server-side rate-limiting and maximize throughput
        argsBuilder.Append("--buffer-size 64K --http-chunk-size 10M --throttled-rate 100K ");
        // Chrome TLS fingerprint: CDNs (e.g. phncdn) reject python-requests
        // fingerprints with 410/412 even when headers are perfect. Impersonating
        // Chrome makes yt-dlp's requests indistinguishable from the browser's.
        // Requires curl_cffi (bundled with yt-dlp); silently ignored otherwise.
        argsBuilder.Append("--impersonate chrome ");
        LogDiag(item, "chrome impersonation enabled");

        // aria2c multi-connection for direct files (single-connection CDNs
        // throttle to ~500KB/s; 8 connections reach IDM-class ~2MB/s).
        // Kept OUT of commonArgs: if a strict host rejects aria2c, the same
        // URL is retried natively without rebuilding the plan.
        string ariaSuffix = "";
        try
        {
            ariaSuffix = Aria2cHelper.BuildDownloaderArgs(aria2cPath,
                SettingsHelper.ConcurrentFragments, SettingsHelper.MaximumDownloadRateKBps);
            if (!string.IsNullOrEmpty(ariaSuffix))
                LogDiag(item, $"aria2c enabled: {ariaSuffix.Trim()}");
        }
        catch { ariaSuffix = ""; }
        bool ariaActive = !string.IsNullOrEmpty(ariaSuffix);

        // Some pages obfuscate high-quality stream URLs with JavaScript.
        // Without a JS runtime yt-dlp fails with "PhantomJS not found". Use node/deno when available.
        string jsRuntimeArgs = FindJsRuntimeArgs();
        if (!string.IsNullOrEmpty(jsRuntimeArgs))
        {
            argsBuilder.Append(jsRuntimeArgs);
        }

        if (isAudio)
        {
            argsBuilder.Append("-x --audio-format mp3 ");
        }
        else if (string.IsNullOrWhiteSpace(item.Quality) ||
                 item.Quality.Equals("best", StringComparison.OrdinalIgnoreCase) ||
                 item.Quality.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            // "Best available" from the extension: must include video track! Never fall back to standalone audio.
            argsBuilder.Append("-f \"bv*+ba/b\" ");
            // Automatically remux to mp4
            argsBuilder.Append("--remux-video mp4 ");
        }
        else
        {
            int targetDim = 1080;
            var matchHeight = Regex.Match(item.Quality ?? "", @"\d+");
            if (matchHeight.Success && int.TryParse(matchHeight.Value, out int parsedHeight) && parsedHeight > 0)
            {
                targetDim = parsedHeight;
            }
            int verticalLength = (int)Math.Round(targetDim * 16.0 / 9.0);
            // Landscape first (height cap), portrait fallback (height+width cap), never fall back to audio-only.
            argsBuilder.Append($"-f \"bv*[height<={targetDim}]+ba/b[height<={targetDim}]/bv*[height<={verticalLength}][width<={targetDim}]+ba/b[height<={verticalLength}][width<={targetDim}]/bv*+ba/b\" ");
            // Automatically remux to mp4
            argsBuilder.Append("--remux-video mp4 ");
        }

        // Add Referer and User-Agent if available (critical for protected HLS CDNs)
        if (!string.IsNullOrWhiteSpace(item.Referrer))
        {
            argsBuilder.Append($"--referer \"{item.Referrer}\" ");
        }
        else if (!string.IsNullOrWhiteSpace(item.PageUrl))
        {
            argsBuilder.Append($"--referer \"{item.PageUrl}\" ");
        }

        if (!string.IsNullOrWhiteSpace(item.UserAgent))
        {
            argsBuilder.Append($"--user-agent \"{item.UserAgent}\" ");
        }

        string commonArgs = argsBuilder.ToString();

        // Live browser session from the extension (cookies + browser-minted
        // PO token). Lesson from 26.2.2: the FIRST attempt must look exactly
        // like 26.2.2 (no session attached) because automation-exported
        // sessions can trip bot walls that anonymous requests pass. The
        // session is therefore held back as retry ammo for gated errors only
        // (see the failure branch below), never baked into every attempt.
        string? extensionCookieFile = null;
        string sessionCookieArgs = "";
        if (item.Cookies != null && item.Cookies.Count > 0)
        {
            string jarHostUrl = !string.IsNullOrWhiteSpace(item.PageUrl) ? item.PageUrl : item.Url;
            try { extensionCookieFile = ChallengeSolver.WriteCookieJar(jarHostUrl, item.Cookies); }
            catch { extensionCookieFile = null; }
            if (extensionCookieFile != null)
            {
                sessionCookieArgs = $"--cookies \"{extensionCookieFile}\" ";
                if (!string.IsNullOrWhiteSpace(item.PoToken) &&
                    (PotProviderHelper.IsYouTubeUrl(item.Url) || PotProviderHelper.IsYouTubeUrl(item.PageUrl)))
                {
                    string token = Regex.Replace(item.PoToken.Trim(), @"[^A-Za-z0-9\-_]", "");
                    if (!string.IsNullOrEmpty(token))
                        sessionCookieArgs += $"--extractor-args \"youtube:po_token=web.gvs+{token}\" ";
                }
                LogDiag(item, $"browser session ready ({item.Cookies.Count} cookies)");
            }
        }
        bool hasSessionJar = extensionCookieFile != null;

        // Some video pages gate playback behind an inline JS cookie-challenge
        // in their HTML (a script sets a session cookie, then reloads) while
        // captured CDN links are short-lived signed URLs that die with
        // 410 Gone. yt-dlp can only clear that gate via the legacy PhantomJS
        // binary (node/deno do NOT substitute there), so the reliable path is
        // the source page combined with a solved or browser session.
        // Detection is structural: the page HTML is probed for challenge
        // markers (never matched by site name). Probing is skipped when there
        // is no page URL, since the challenge can only live in page HTML, and
        // when the extension already attached a live session (the solved jar
        // would add nothing on top of it).
        string? challengeCookieFile = null;
        if (extensionCookieFile == null && !string.IsNullOrWhiteSpace(item.PageUrl))
        {
            try { challengeCookieFile = await ChallengeSolver.CreateCookieFileAsync(item.PageUrl, cancellationToken); }
            catch { challengeCookieFile = null; }
        }
        bool isChallengePage = challengeCookieFile != null;
        LogDiag(item, isChallengePage
            ? "page cookie-challenge solved, session cookie ready"
            : "no page cookie-challenge detected");

        // Extension-resolved URLs (captured renditions, fresh manifests) go
        // FIRST: they are the exact authorized bytes (gates already passed
        // in the tab). The page is only the fallback for expired links -
        // page-first ordering is what made every gated download fail, since
        // yt-dlp cannot re-resolve age/login walls from outside the browser.
        // Exception: playlists must resolve from the page to enumerate items.
        var candidateUrls = new List<string>();
        if (IsSabrRedirector(item.Url))
        {
            // Never attempt the handshake-only URL (generic extractor would
            // download garbage); the page fallback is the only hope.
            LogDiag(item, "SABR redirector dropped from candidates, page fallback only");
            if (!string.IsNullOrWhiteSpace(item.PageUrl)) candidateUrls.Add(item.PageUrl);
        }
        else if (item.DownloadPlaylist)
        {
            if (!string.IsNullOrWhiteSpace(item.PageUrl)) candidateUrls.Add(item.PageUrl);
            if (!string.IsNullOrWhiteSpace(item.Url) &&
                !item.Url.Equals(item.PageUrl, StringComparison.OrdinalIgnoreCase))
                candidateUrls.Add(item.Url);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(item.Url)) candidateUrls.Add(item.Url);
            if (!string.IsNullOrWhiteSpace(item.PageUrl) &&
                !item.PageUrl.Equals(item.Url, StringComparison.OrdinalIgnoreCase))
            {
                candidateUrls.Add(item.PageUrl);
            }
        }

        bool succeeded = false;
        // Per-attempt plan: (url, extra yt-dlp args). Challenge-gated pages
        // always try the page first (fresh signed links beat stale captured
        // ones): natively-solved session cookie first, browser session second,
        // plain third. The solved cookie is always fresh, unlike exported
        // browser cookies which may be stale or single-use.
        var plan = new List<(string Url, string? CookieArgs)>();
        if (isChallengePage)
        {
            if (!string.IsNullOrWhiteSpace(item.PageUrl))
            {
                if (challengeCookieFile != null)
                    plan.Add((item.PageUrl, $"--cookies \"{challengeCookieFile}\" "));
                plan.Add((item.PageUrl, "--cookies-from-browser chrome "));
                plan.Add((item.PageUrl, null));
            }
            foreach (string u in candidateUrls)
            {
                if (!plan.Any(p => p.Url.Equals(u, StringComparison.OrdinalIgnoreCase)))
                    plan.Add((u, null));
            }
            if (plan.Count == 0 && !string.IsNullOrWhiteSpace(item.Url))
                plan.Add((item.Url, null));
        }
        else
        {
            // Signed direct URLs (manifests, captured renditions) often 410
            // anonymous requests while serving the browser fine: lead with
            // the live extension session instead of burning the short-lived
            // link on a doomed plain attempt. Page URLs keep plain-first
            // (bot walls sometimes pass anonymous but trip on automation).
            bool first = true;
            foreach (string u in candidateUrls)
            {
                string? lead = null;
                if (first && hasSessionJar && !string.IsNullOrEmpty(sessionCookieArgs) &&
                    (IsManifestUrl(u) || IsCapturedYouTubePlaybackUrl(u)))
                {
                    lead = sessionCookieArgs;
                    LogDiag(item, "leading with extension session for signed URL");
                }
                plan.Add((u, lead));
                first = false;
            }
        }
        var pendingUrls = new Queue<(string Url, string? CookieArgs)>(plan);
        int sameUrlRetries = 0;
        var authBrowsersTried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int stuckFailures = 0;
        string? lastHead = null;
#if DEBUG
        int attemptCount = 0;
#endif
        bool firstTry = true;

        while (pendingUrls.Count > 0 && !succeeded)
        {
            var attempt = pendingUrls.Peek();
#if DEBUG
            attemptCount++;
#endif
            string attemptUrl = attempt.Url;
            // aria2c is a byte-range beast for plain files, but it mangles
            // HLS/DASH timing (parallel segments + retries = timestamp gaps =
            // VLC macroblocking). Manifests always use yt-dlp's native
            // fragment downloader, which validates per-segment retries.
            bool ariaForAttempt = ariaActive && !IsManifestUrl(attemptUrl);
            string attemptArgs = commonArgs + (attempt.CookieArgs ?? "") + (ariaForAttempt ? ariaSuffix : "");
            if (!firstTry)
            {
                item.Progress = 0;
                item.SavePath = "";
                item.EtaText = "--";
                item.SpeedText = AppLocalization.Get("download.resolving");
            }
            firstTry = false;

            var psi = new ProcessStartInfo
            {
                FileName = ytdlpPath,
                Arguments = $"{attemptArgs}-P \"temp:{temporaryFolder}\" -o \"{outputTemplate}\" \"{attemptUrl}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Aria2cHelper.ConfigureEnvironment(psi, aria2cPath);

        try
        {
            using var proc = new Process { StartInfo = psi };
            string lastError = "";
            bool cookiesFailed = false;
            
            // Regexes for parsing yt-dlp progress (handling ~ in HLS estimates and ETA)
            var progressRegex = new Regex(@"\[download\]\s+([\d\.]+)%\s+of\s+(?:~?\s*)([^\s]+)\s+at\s+([^\s]+)(?:\s+ETA\s+([^\s]+))?", RegexOptions.Compiled);
            // aria2c external-downloader lines: [#d896bb 606MiB/612MiB(99%) CN:5 DL:1.4MiB ETA:4s]
            var ariaRegex = new Regex(@"\[#[0-9a-f]+\s+(\S+)/(\S+)\((\d+)%\)", RegexOptions.Compiled);
            var ariaRateRegex = new Regex(@"DL:(\S+)", RegexOptions.Compiled);
            var ariaEtaRegex = new Regex(@"ETA:(\S+)", RegexOptions.Compiled);
            var destRegex = new Regex(@"\[Merger\] Merging formats into ""([^""]+)""", RegexOptions.Compiled);
            var destDirectRegex = new Regex(@"\[download\] Destination: (.+)", RegexOptions.Compiled);
            var remuxRegex = new Regex(@"\[VideoRemuxer\] Remuxing video from [^ ]+ to ""?([^""]+)""?", RegexOptions.Compiled);

            proc.OutputDataReceived += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                string line = e.Data.Trim();

                var m = progressRegex.Match(line);
                if (!m.Success)
                {
                    var am = ariaRegex.Match(line);
                    if (am.Success &&
                        double.TryParse(am.Groups[3].Value, out double apct))
                    {
                        string asize = am.Groups[1].Value;
                        string atotal = am.Groups[2].Value;
                        var rm = ariaRateRegex.Match(line);
                        string aspeed = rm.Success ? rm.Groups[1].Value + "/s" : "";
                        item.Progress = apct;
                        item.SizeText = $"{asize} / {atotal}";
                        var em = ariaEtaRegex.Match(line);
                        string aeta = em.Success ? em.Groups[1].Value : "";
                        if (!string.IsNullOrEmpty(aspeed))
                        {
                            item.SpeedText = aspeed;
                            item.StatusText = AppLocalization.Format("download.progressSpeed", apct, atotal, aspeed);
                        }
                        else
                        {
                            item.StatusText = $"{apct:F1}% of {atotal}";
                        }
                        item.EtaText = string.IsNullOrEmpty(aeta)
                            ? "--"
                            : AppLocalization.Format("download.timeLeft", aeta);
                        return;
                    }
                }
                if (m.Success)
                {
                    if (double.TryParse(m.Groups[1].Value, out double pct))
                    {
                        item.Progress = pct;
                    }
                    string size = m.Groups[2].Value;
                    string speed = m.Groups[3].Value;
                    string eta = m.Groups[4].Success ? m.Groups[4].Value.Trim() : "";

                    item.SizeText = size;
                    item.SpeedText = speed;
                    item.EtaText = string.IsNullOrEmpty(eta)
                        ? "--"
                        : AppLocalization.Format("download.timeLeft", eta);
                    item.StatusText = string.IsNullOrEmpty(eta)
                        ? AppLocalization.Format("download.progressSpeed", pct, size, speed)
                        : AppLocalization.Format("download.progressEta", pct, size, eta);
                }
                else if (line.Contains("[Merger]"))
                {
                    item.StatusText = AppLocalization.Get("download.muxing");
                    var destMatch = destRegex.Match(line);
                    if (destMatch.Success)
                    {
                        item.SavePath = destMatch.Groups[1].Value;
                        if (isGenericTitle)
                        {
                            item.Title = Path.GetFileNameWithoutExtension(item.SavePath);
                        }
                    }
                }
                else if (line.Contains("[download] Destination:"))
                {
                    var mDest = destDirectRegex.Match(line);
                    if (mDest.Success)
                    {
                        item.SavePath = mDest.Groups[1].Value;
                        if (isGenericTitle)
                        {
                            item.Title = Path.GetFileNameWithoutExtension(item.SavePath);
                        }
                    }
                }
                else if (line.Contains("[VideoRemuxer]"))
                {
                    item.StatusText = AppLocalization.Get("download.remuxing");
                    var mRemux = remuxRegex.Match(line);
                    if (mRemux.Success)
                    {
                        item.SavePath = mRemux.Groups[1].Value.Trim().Trim('"');
                        if (isGenericTitle)
                        {
                            item.Title = Path.GetFileNameWithoutExtension(item.SavePath);
                        }
                    }
                }
            };

            proc.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    string errLine = e.Data.Trim();
                    if (errLine.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                    {
                        lastError = errLine;
                        LogDiag(item, $"yt-dlp error: {errLine}");
                    }
                    else if (errLine.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
                    {
                        LogDiag(item, $"yt-dlp warning: {errLine}");
                    }
                    // Browser-session unavailable (no Chrome, locked profile, ...):
                    // cookie attempts would all fail identically, skip them.
                    // NB: only genuine export failures count. Extractor errors
                    // that merely ADVISE "--cookies-from-browser" (e.g. age or
                    // login walls) must NOT set this, or the queue head is
                    // never dequeued and the download loops forever.
                    if (errLine.Contains("ould not copy", StringComparison.OrdinalIgnoreCase) &&
                        errLine.Contains("cookie", StringComparison.OrdinalIgnoreCase))
                    {
                        cookiesFailed = true;
                    }
                    else if (errLine.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) &&
                             errLine.Contains("cookies-from-browser", StringComparison.OrdinalIgnoreCase) &&
                             Regex.IsMatch(errLine, @"could not|failed|not found|unable to|error (getting|extracting|copying|reading)|no .*cookies?", RegexOptions.IgnoreCase))
                    {
                        cookiesFailed = true;
                    }
                }
            };

            LogDiag(item, $"attempt url={attemptUrl} cookies={(attempt.CookieArgs != null ? attempt.CookieArgs.Trim() : "none")}");
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var reg = cancellationToken.Register(() =>
            {
                try { proc.Kill(true); } catch { }
            });

            await proc.WaitForExitAsync(cancellationToken);

            string finalFile = await EnsurePlayableMediaFileAsync(partsSubdir, safeTitle, ext, item.SavePath, isDirectManifest, cancellationToken);

            if (proc.ExitCode == 0 || (!string.IsNullOrEmpty(finalFile) && File.Exists(finalFile) && new FileInfo(finalFile).Length > 1024 * 1024))
            {
                // Finished files leave the scratch folder for the real
                // downloads folder (with collision numbering). The scratch
                // folder is removed only when nothing preservable remains.
                var moved = MoveFinishedMediaToDownloads(partsSubdir, downloadsFolder, item);
                DeletePartsSubdirIfClean(partsSubdir, item);
                string dest = "";
                if (!string.IsNullOrEmpty(finalFile))
                {
                    var match = moved.FirstOrDefault(m => m.Source.Equals(finalFile, StringComparison.OrdinalIgnoreCase));
                    dest = match.Dest ?? moved.FirstOrDefault().Dest ?? "";
                }
                else
                {
                    dest = moved.FirstOrDefault().Dest ?? "";
                }
                LogDiag(item, string.IsNullOrEmpty(dest)
                    ? "completed but no media file found to move"
                    : $"moved to downloads: {dest}");
                item.Progress = 100;
                item.Status = DownloadStatus.Completed;
                item.SpeedText = AppLocalization.Get("download.finished");
                item.EtaText = "--";
                if (!string.IsNullOrEmpty(dest) && File.Exists(dest))
                {
                    item.SavePath = dest;
                    string currentName = Path.GetFileNameWithoutExtension(dest);
                    if (isGenericTitle)
                    {
                        item.Title = currentName;
                    }
                }
                item.StatusText = AppLocalization.Format("download.completedSaved", Path.GetFileName(item.SavePath));
                succeeded = true;
#if DEBUG
                TrackEnd(item, $"OK file={item.SavePath} attempts={attemptCount}");
#endif
            }
            else
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    item.Status = DownloadStatus.Paused;
                    item.SpeedText = AppLocalization.Get("download.pausedSpeed");
                    item.StatusText = AppLocalization.Get("download.paused");
                    item.EtaText = "--";
#if DEBUG
                    TrackEnd(item, $"PAUSED attempts={attemptCount}");
#endif
                    break;
                }

                // Browser session unavailable: drop every remaining browser-cookie
                // attempt (same guaranteed failure), keep the plain ones.
                if (cookiesFailed)
                {
                    var remaining = pendingUrls
                        .Where(p => p.CookieArgs == null || !p.CookieArgs.Contains("cookies-from-browser"))
                        .ToList();
                    pendingUrls.Clear();
                    foreach (var p in remaining) pendingUrls.Enqueue(p);
                }

                // Host rejected the aria2c burst (e.g. HTTP 503 on every
                // connection): fall back to the native single-connection
                // downloader for the same URL instead of failing outright.
                if (ariaForAttempt && lastError.Contains("aria2c", StringComparison.OrdinalIgnoreCase))
                {
                    ariaActive = false;
                    sameUrlRetries = 0;
                    LogDiag(item, "aria2c rejected, retrying natively");
                    item.Progress = 0;
                    item.SavePath = "";
                    item.StatusText = AppLocalization.Get("download.resolving");
                    item.SpeedText = AppLocalization.Get("download.resolving");
                    continue;
                }

                bool linkExpired = lastError.Contains("410", StringComparison.OrdinalIgnoreCase) &&
                                   (lastError.Contains("gone", StringComparison.OrdinalIgnoreCase) ||
                                    lastError.Contains("unable to download", StringComparison.OrdinalIgnoreCase));
                // Circuit breaker: the same identical attempt (URL + args) must
                // never fail forever. Legit same-attempt retries (rate-limit
                // waits, aria2c fallback) resolve within 3 consecutive
                // failures; anything beyond that is a stuck loop, so drop the
                // head and move on instead of spinning until the user kills us.
                string headKey = $"{attemptUrl}|{attempt.CookieArgs}";
                if (headKey.Equals(lastHead, StringComparison.OrdinalIgnoreCase)) stuckFailures++;
                else { stuckFailures = 1; lastHead = headKey; }
                if (stuckFailures > 3)
                {
                    LogDiag(item, "attempt stuck without progress, dropping it");
                    stuckFailures = 0;
                    lastHead = null;
                    sameUrlRetries = 0;
                    if (pendingUrls.Count > 0) pendingUrls.Dequeue();
                    if (pendingUrls.Count > 0)
                    {
                        item.Progress = 0;
                        item.SavePath = "";
                        item.EtaText = "--";
                        item.StatusText = AppLocalization.Get("download.retryViaPage");
                        item.SpeedText = AppLocalization.Get("download.resolving");
                        continue;
                    }
                    item.Status = DownloadStatus.Failed;
                    item.EtaText = "--";
                    LogDiag(item, $"terminal failure: {lastError}");
                    item.StatusText = !string.IsNullOrWhiteSpace(lastError) && lastError.Length > 90
                        ? lastError[..90] + "..."
                        : lastError;
#if DEBUG
                    string stuckShort = lastError ?? "";
                    if (stuckShort.Length > 200) stuckShort = stuckShort[..200];
                    TrackEnd(item, $"FAILED stuck attempts={attemptCount} error={stuckShort}");
#endif
                    break;
                }
                // Gated content (age checks, login walls, bot/PO-token walls,
                // "reload" playability walls, private videos the user can
                // access): plain requests fail while the user's browser
                // already plays the same video - exactly how browser-attached
                // downloaders get these files. Two phases, both error-driven
                // (no site lists):
                //  A. attach a session: live extension jar first, then
                //     chrome -> edge -> brave export.
                //  B. once an attempt carried a session yet still hit the
                //     wall, keep the session and switch to an unchallenged
                //     player client: web_safari (HLS needs no PO token),
                //     then tv. Session + client answers both gates at once.
                // Skipped when cookie export itself is broken.
                if (!cookiesFailed && IsGatedError(lastError))
                {
                    bool sessionAttached = (attempt.CookieArgs?.Contains("cookies", StringComparison.OrdinalIgnoreCase) ?? false);
                    string? nextArgs = null;
                    if (!sessionAttached)
                    {
                        // Session options in order: live extension jar first
                        // (fresh, proven in the tab), then browser export.
                        if (hasSessionJar && authBrowsersTried.Add($"{attemptUrl}|extjar"))
                        {
                            LogDiag(item, "gated content detected, retrying with extension session");
                            nextArgs = sessionCookieArgs;
                        }
                        else
                        {
                            string? nextBrowser = NextAuthBrowser(attempt.CookieArgs, attemptUrl, authBrowsersTried);
                            if (nextBrowser != null)
                            {
                                authBrowsersTried.Add($"{attemptUrl}|{nextBrowser}");
                                LogDiag(item, $"gated content detected, retrying with {nextBrowser} session");
                                nextArgs = $"--cookies-from-browser {nextBrowser} ";
                            }
                        }
                    }
                    else if (PotProviderHelper.IsYouTubeUrl(item.Url) || PotProviderHelper.IsYouTubeUrl(item.PageUrl))
                    {
                        string? clientArgs = NextClientVariant(attempt.CookieArgs, attemptUrl, authBrowsersTried);
                        if (clientArgs != null)
                        {
                            LogDiag(item, $"session attached yet still gated, retrying {clientArgs.Trim()}");
                            nextArgs = clientArgs;
                        }
                    }
                    if (nextArgs != null)
                    {
                        pendingUrls.Dequeue();
                        var rest = pendingUrls.ToList();
                        pendingUrls.Clear();
                        pendingUrls.Enqueue((attemptUrl, nextArgs));
                        foreach (var p in rest) pendingUrls.Enqueue(p);
                        item.Progress = 0;
                        item.SavePath = "";
                        item.EtaText = "--";
                        item.StatusText = AppLocalization.Get("download.resolving");
                        item.SpeedText = AppLocalization.Get("download.resolving");
                        continue;
                    }
                }
                // Client-variant chain continuation: a player_client attempt
                // failed with anything (e.g. "format not available") - move to
                // the next client instead of giving up. Bounded by tried-set.
                if (!cookiesFailed &&
                    (attempt.CookieArgs?.Contains("player_client", StringComparison.OrdinalIgnoreCase) ?? false) &&
                    (PotProviderHelper.IsYouTubeUrl(item.Url) || PotProviderHelper.IsYouTubeUrl(item.PageUrl)))
                {
                    string? clientArgs = NextClientVariant(attempt.CookieArgs, attemptUrl, authBrowsersTried);
                    if (clientArgs != null)
                    {
                        LogDiag(item, $"client variant failed, retrying {clientArgs.Trim()}");
                        pendingUrls.Dequeue();
                        var rest = pendingUrls.ToList();
                        pendingUrls.Clear();
                        pendingUrls.Enqueue((attemptUrl, clientArgs));
                        foreach (var p in rest) pendingUrls.Enqueue(p);
                        item.Progress = 0;
                        item.SavePath = "";
                        item.EtaText = "--";
                        item.StatusText = AppLocalization.Get("download.resolving");
                        item.SpeedText = AppLocalization.Get("download.resolving");
                        continue;
                    }
                }
                // Flaky endpoints (rate limits, Cloudflare walls, empty JSON):
                // retry the same URL briefly before giving up on it.
                // Skipped for expired signed links (410: retrying is pointless)
                // and for cookie-setup failures (not transient).
                bool rateLimited = !linkExpired && !cookiesFailed &&
                    Regex.IsMatch(lastError, @"\b429\b|rate.?limit|too many requests|timed out|timeout|failed to parse json|unable to download.*json|\b50[234]\b|bad gateway|service unavailable|gateway timeout|temporary failure", RegexOptions.IgnoreCase);
                if (rateLimited && sameUrlRetries < 2)
                {
                    sameUrlRetries++;
                    item.StatusText = AppLocalization.Format("download.rateLimited", sameUrlRetries);
                    item.SpeedText = AppLocalization.Get("download.waiting");
                    try { await Task.Delay(5000, cancellationToken); } catch { }
                    continue;
                }
                sameUrlRetries = 0;
                // Cookie-failed attempts were already excluded by the rebuild
                // above, so the queue head is the next untried attempt.
                if (!cookiesFailed)
                    pendingUrls.Dequeue();
                if (pendingUrls.Count > 0)
                {
                    // Next candidate source (e.g. the page re-resolves expired links).
                    item.Progress = 0;
                    item.SavePath = "";
                    item.EtaText = "--";
                    item.StatusText = AppLocalization.Get("download.retryViaPage");
                    item.SpeedText = AppLocalization.Get("download.resolving");
                    continue;
                }
                item.Status = DownloadStatus.Failed;
                item.EtaText = "--";
                LogDiag(item, $"terminal failure: {lastError}");
#if DEBUG
                string failShort = lastError ?? "";
                if (failShort.Length > 200) failShort = failShort[..200];
                TrackEnd(item, $"FAILED attempts={attemptCount} error={failShort}");
#endif
                if (!string.IsNullOrWhiteSpace(lastError))
                {
                    bool expired = lastError.Contains("410", StringComparison.OrdinalIgnoreCase) &&
                                   (lastError.Contains("gone", StringComparison.OrdinalIgnoreCase) ||
                                    lastError.Contains("unable to download", StringComparison.OrdinalIgnoreCase));
                    item.StatusText = expired
                        ? AppLocalization.Get("download.linkExpired")
                        : lastError.Length > 90 ? lastError[..90] + "..." : lastError;
                }
                else
                {
                    item.StatusText = AppLocalization.Format("download.failedCode", proc.ExitCode);
                }
                break;
            }
        }
        catch (OperationCanceledException)
        {
            item.Status = DownloadStatus.Paused;
            item.SpeedText = AppLocalization.Get("download.pausedSpeed");
            item.StatusText = AppLocalization.Get("download.paused");
#if DEBUG
            TrackEnd(item, "PAUSED (cancelled)");
#endif
            break;
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                item.Status = DownloadStatus.Paused;
                item.SpeedText = AppLocalization.Get("download.pausedSpeed");
                item.StatusText = AppLocalization.Get("download.paused");
#if DEBUG
                TrackEnd(item, "PAUSED (cancelled)");
#endif
                break;
            }
            item.Status = DownloadStatus.Failed;
            item.StatusText = AppLocalization.Format("download.error", ex.Message);
#if DEBUG
            TrackEnd(item, $"FAILED exception={ex.Message}");
#endif
            break;
        }
        }
        ChallengeSolver.DeleteCookieFile(challengeCookieFile);
        ChallengeSolver.DeleteCookieFile(extensionCookieFile);
    }

    private static async Task DownloadDirectHttpAsync(DownloadItem item, string downloadsFolder, CancellationToken cancellationToken)
    {
        string partsSubdir = "";
        try
        {
            item.Status = DownloadStatus.Downloading;
            item.StatusText = AppLocalization.Get("download.connecting");

            var streamHeaders = item.StreamHeaders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string capturedCookie = streamHeaders.FirstOrDefault(h =>
                h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)).Value ?? "";
            string cookieHeader = !string.IsNullOrWhiteSpace(capturedCookie)
                ? capturedCookie
                : BuildCookieHeader(item.Cookies, item.Url);
            using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
            foreach (var header in streamHeaders)
            {
                if (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Range", StringComparison.OrdinalIgnoreCase)) continue;
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            if (!streamHeaders.Keys.Any(k => k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)))
            {
                string ua = !string.IsNullOrWhiteSpace(item.UserAgent)
                    ? item.UserAgent
                    : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";
                request.Headers.TryAddWithoutValidation("User-Agent", ua);
            }
            if (!streamHeaders.Keys.Any(k => k.Equals("Referer", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(item.Referrer))
                request.Headers.Referrer = Uri.TryCreate(item.Referrer, UriKind.Absolute, out var refUri) ? refUri : null;
            if (!string.IsNullOrWhiteSpace(cookieHeader))
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            if (!streamHeaders.Keys.Any(k => k.Equals("Accept", StringComparison.OrdinalIgnoreCase)))
                request.Headers.TryAddWithoutValidation("Accept", "*/*");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            string contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            string rawName = Path.GetFileName(new Uri(item.Url).AbsolutePath).ToLowerInvariant();

            if (IsManifestUrl(item.Url) ||
                contentType.Contains("mpegurl") || 
                contentType.Contains("m3u") || 
                contentType.Contains("dash+xml") ||
                rawName.EndsWith(".m3u8") || 
                rawName.EndsWith(".m3u") ||
                rawName.EndsWith(".mpd"))
            {
                // This is an HLS or DASH stream manifest, not a static file!
                // Re-route to DownloadStreamingSiteAsync to demux and mux the actual video into MP4
                await DownloadStreamingSiteAsync(item, downloadsFolder, cancellationToken);
                return;
            }

            long? totalBytes = response.Content.Headers.ContentLength;
            string totalStr = totalBytes.HasValue ? FormatBytes(totalBytes.Value) : AppLocalization.Get("download.unknownSize");
            item.SizeText = totalStr;

            // Determine filename and extension from Content-Disposition, URL, or item.Title
            string determinedFileName = "";
            if (response.Content.Headers.ContentDisposition != null)
            {
                determinedFileName = response.Content.Headers.ContentDisposition.FileNameStar ?? 
                                     response.Content.Headers.ContentDisposition.FileName ?? "";
                determinedFileName = determinedFileName.Trim('"', '\'', ' ');
            }

            if (string.IsNullOrWhiteSpace(determinedFileName))
            {
                try
                {
                    determinedFileName = Path.GetFileName(new Uri(item.Url).AbsolutePath);
                }
                catch { }
            }

            string rawExt = "";
            if (!string.IsNullOrEmpty(determinedFileName) && Path.HasExtension(determinedFileName))
            {
                rawExt = Path.GetExtension(determinedFileName);
            }
            else if (!string.IsNullOrEmpty(item.Title) && Path.HasExtension(item.Title))
            {
                rawExt = Path.GetExtension(item.Title);
            }
            else
            {
                rawExt = ".bin";
            }

            string safeTitle = SanitizeFileName(item.Title);
            if (string.IsNullOrWhiteSpace(safeTitle) || safeTitle.Equals("index", StringComparison.OrdinalIgnoreCase) || safeTitle.Equals("video", StringComparison.OrdinalIgnoreCase))
            {
                safeTitle = !string.IsNullOrWhiteSpace(determinedFileName) 
                    ? Path.GetFileNameWithoutExtension(determinedFileName) 
                    : "download";
            }
            if (safeTitle.EndsWith(rawExt, StringComparison.OrdinalIgnoreCase))
            {
                safeTitle = safeTitle[..^rawExt.Length].TrimEnd();
            }

            // Final home (prompt-chosen folder or downloads). The bytes land in
            // an isolated Parts subfolder first; only finished files move here.
            // A resumed SavePath points inside Parts - never treat scratch as home.
            string? saveDir = !string.IsNullOrEmpty(item.SavePath) ? Path.GetDirectoryName(item.SavePath) : null;
            string finalFolder = !string.IsNullOrEmpty(saveDir) && Directory.Exists(saveDir) && !IsUnderPartsDir(saveDir)
                ? saveDir
                : downloadsFolder;

            partsSubdir = ResolvePartsSubdir(item, safeTitle);
            string tempPath = Path.Combine(partsSubdir, SanitizeFileName(
                string.IsNullOrWhiteSpace(safeTitle) ? "download" : safeTitle) + rawExt + ".part");
            item.SavePath = tempPath;
            item.Title = Path.GetFileNameWithoutExtension(tempPath);

            // Keep the in-progress file out of media indexing and allow
            // readers such as antivirus scanners to inspect it without
            // preventing the downloader from writing.
            const int bufferSize = 524288;
            byte[] buffer = new byte[bufferSize];
            // Cross-session resume: the temp file may already hold bytes from
            // a paused/failed run in the reused scratch folder.
            long resumeFrom = 0;
            try { if (File.Exists(tempPath)) resumeFrom = new FileInfo(tempPath).Length; } catch { }
            long totalRead = resumeFrom;
            if (resumeFrom > 0)
            {
                LogDiag(item, $"resuming from {FormatBytes(resumeFrom)}");
                item.StatusText = AppLocalization.Format("download.transferred", FormatBytes(resumeFrom), totalStr);
            }
            var sw = Stopwatch.StartNew();
            var downloadTimer = Stopwatch.StartNew();
            long lastBytes = 0;
            bool completed = false;
            for (int attempt = 0; attempt < 4 && !completed; attempt++)
            {
                HttpResponseMessage? currentResponse = null;
                HttpRequestMessage? retryRequest = null;
                // Attempt 0 reuses the probing response, unless resuming (the
                // probe was plain - re-request with Range instead).
                bool needRangedRequest = attempt > 0 || (attempt == 0 && resumeFrom > 0);
                try
                {
                    if (needRangedRequest)
                    {
                        retryRequest = new HttpRequestMessage(HttpMethod.Get, item.Url);
                        foreach (var header in streamHeaders)
                        {
                            if (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                                header.Key.Equals("Range", StringComparison.OrdinalIgnoreCase)) continue;
                            retryRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        }
                        if (!streamHeaders.Keys.Any(k => k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) &&
                            !string.IsNullOrWhiteSpace(item.UserAgent))
                            retryRequest.Headers.TryAddWithoutValidation("User-Agent", item.UserAgent);
                        if (!streamHeaders.Keys.Any(k => k.Equals("Referer", StringComparison.OrdinalIgnoreCase)) &&
                            Uri.TryCreate(item.Referrer, UriKind.Absolute, out var retryReferrer))
                            retryRequest.Headers.Referrer = retryReferrer;
                        if (!string.IsNullOrWhiteSpace(cookieHeader))
                            retryRequest.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                        if (!streamHeaders.Keys.Any(k => k.Equals("Accept", StringComparison.OrdinalIgnoreCase)))
                            retryRequest.Headers.TryAddWithoutValidation("Accept", "*/*");
                        if (totalRead > 0)
                            retryRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(totalRead, null);
                        currentResponse = await _httpClient.SendAsync(retryRequest,
                            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    }
                    else
                    {
                        currentResponse = response;
                    }

                    if (totalRead > 0 && currentResponse.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        // This server ignored Range; restart from byte zero.
                        totalRead = 0;
                    }
                    else if (totalRead > 0 && currentResponse.StatusCode == System.Net.HttpStatusCode.PartialContent &&
                             currentResponse.Content.Headers.ContentRange?.From != totalRead)
                    {
                        throw new IOException("Server resumed the download at an unexpected byte offset");
                    }
                    else if (totalRead > 0 && currentResponse.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        // Already complete (or stale offset): finalize when the
                        // partial file holds real bytes, else restart whole.
                        FileInfo existing = new FileInfo(tempPath);
                        if (existing.Exists && existing.Length > 0)
                        {
                            totalRead = existing.Length;
                            completed = true;
                            continue;
                        }
                        totalRead = 0;
                    }

                    currentResponse.EnsureSuccessStatusCode();
                    long? expectedTotal = currentResponse.Content.Headers.ContentRange?.Length ??
                        (currentResponse.Content.Headers.ContentLength is long responseLength
                            ? totalRead + responseLength : totalBytes);
                    using var contentStream = await currentResponse.Content.ReadAsStreamAsync(cancellationToken);
                    using var fileStream = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete, bufferSize, true);
                    fileStream.SetLength(totalRead);
                    fileStream.Position = totalRead;
                    int bytesRead;
                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                        totalRead += bytesRead;

                        int maximumRateKBps = SettingsHelper.MaximumDownloadRateKBps;
                        if (maximumRateKBps > 0)
                        {
                            double requiredSeconds = (double)totalRead / (maximumRateKBps * 1024);
                            double delaySeconds = requiredSeconds - downloadTimer.Elapsed.TotalSeconds;
                            if (delaySeconds > 0)
                                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                        }

                        if (expectedTotal.HasValue && expectedTotal.Value > 0)
                            item.Progress = (double)totalRead / expectedTotal.Value * 100.0;

                        if (sw.ElapsedMilliseconds >= 500)
                        {
                            double speedBytesSec = (totalRead - lastBytes) / Math.Max(0.01, sw.Elapsed.TotalSeconds);
                            item.SpeedText = $"{FormatBytes((long)speedBytesSec)}/s";
                            item.StatusText = AppLocalization.Format("download.transferred", FormatBytes(totalRead), totalStr);
                            double elapsed = downloadTimer.Elapsed.TotalSeconds;
                            if (elapsed > 1 && totalRead > 0 && expectedTotal.HasValue && expectedTotal.Value > totalRead)
                            {
                                double remaining = (expectedTotal.Value - totalRead) / (totalRead / elapsed);
                                item.EtaText = AppLocalization.Format("download.timeLeft", FormatDuration(remaining));
                            }
                            else item.EtaText = "--";
                            lastBytes = totalRead;
                            sw.Restart();
                        }
                    }
                    await fileStream.FlushAsync(cancellationToken);
                    if (expectedTotal.HasValue && expectedTotal.Value > 0 && totalRead < expectedTotal.Value)
                        throw new IOException($"Connection ended early at {totalRead} of {expectedTotal.Value} bytes");
                    completed = true;
                }
                catch (Exception ex) when (attempt < 3 && (ex is IOException or HttpRequestException))
                {
                    LogDiag(item, $"direct transfer interrupted at {totalRead} bytes; retry {attempt + 1}/3: {ex.Message}");
                    item.StatusText = "Connection interrupted; retrying...";
                    await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken);
                }
                finally
                {
                    currentResponse?.Dispose();
                    retryRequest?.Dispose();
                }
            }

            if (!completed) throw new IOException("Direct transfer could not be completed after retries");

            item.Progress = 100;
            item.Status = DownloadStatus.Completed;
            item.SpeedText = AppLocalization.Get("download.finished");
            item.EtaText = "--";
            string dest = MoveFileRobust(tempPath, finalFolder, safeTitle + rawExt);
            try { if (Directory.Exists(partsSubdir)) Directory.Delete(partsSubdir, recursive: true); } catch { }
            item.SavePath = dest;
            item.Title = Path.GetFileName(dest);
            item.StatusText = AppLocalization.Format("download.saved", Path.GetFileName(item.SavePath));
#if DEBUG
            TrackEnd(item, $"OK file={dest}");
#endif
        }
        catch (OperationCanceledException)
        {
            item.Status = DownloadStatus.Paused;
            item.SpeedText = AppLocalization.Get("download.pausedSpeed");
            item.StatusText = AppLocalization.Get("download.paused");
            item.EtaText = "--";
#if DEBUG
            TrackEnd(item, "PAUSED (cancelled)");
#endif
            // Partial bytes are deliberately KEPT: resuming reuses this exact
            // scratch folder (see ResolvePartsSubdir). Week-old orphans are
            // reaped by the stale-parts cleaner, never active items.
            LogDiag(item, "paused, partial kept for resume");
        }
        catch (Exception ex)
        {
            item.Status = DownloadStatus.Failed;
            item.StatusText = AppLocalization.Format("download.error", ex.Message);
            item.EtaText = "--";
            LogDiag(item, $"direct failed: {ex}");
#if DEBUG
            TrackEnd(item, $"FAILED exception={ex.Message}");
#endif
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }

    /// <summary>
    /// True when the item points at an unfinished temp file under Parts/
    /// (paused, failed, or interrupted - never completed or already moved).
    /// </summary>
    public static bool IsUnfinishedTemp(DownloadItem? item)
    {
        try
        {
            if (item == null || item.Status == DownloadStatus.Completed) return false;
            if (string.IsNullOrWhiteSpace(item.SavePath)) return false;
            string full = Path.GetFullPath(item.SavePath);
            string root = Path.GetFullPath(PortablePaths.DownloadPartsDirectory);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>True when a path lives inside the Parts scratch root.</summary>
    public static bool IsUnderPartsDir(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(PortablePaths.DownloadPartsDirectory);
            return full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Resume-aware scratch folder: reuse the item's previous Parts subdir
    /// when it still exists, so pause/resume/retry continue the same bytes
    /// (direct Range resume, yt-dlp --continue). Otherwise mint an isolated
    /// one exactly as before.
    /// </summary>
    public static string ResolvePartsSubdir(DownloadItem item, string safeTitle)
    {
        try
        {
            if (IsUnfinishedTemp(item))
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(item.SavePath));
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    LogDiag(item, $"reusing scratch folder for resume: {dir}");
                    return dir;
                }
            }
        }
        catch { }
        return CreatePartsSubdir(safeTitle);
    }

    public static string GetUniqueFilePath(string folder, string baseTitle, string ext)
    {
        if (!ext.StartsWith('.')) ext = "." + ext;
        string safe = SanitizeFileName(baseTitle).Trim();
        if (string.IsNullOrWhiteSpace(safe)) safe = "download";

        string target = Path.Combine(folder, $"{safe}{ext}");
        if (!File.Exists(target)) return target;

        int num = 1;
        while (true)
        {
            target = Path.Combine(folder, $"{safe} ({num}){ext}");
            if (!File.Exists(target)) return target;
            num++;
        }
    }

    /// <summary>Transient download debris. Everything else must be preserved.</summary>
    public static bool IsTransientFile(string path)
    {
        string name = Path.GetFileName(path);
        return name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".temp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Isolated scratch folder for one download. EVERYTHING transient
    /// (.part, .ytdl, fragments, merges) lives here so the user's Downloads
    /// folder only ever receives finished files.
    /// </summary>
    public static string CreatePartsSubdir(string baseName)
    {
        string root = PortablePaths.DownloadPartsDirectory;
        Directory.CreateDirectory(root);
        string safe = SanitizeFileName(baseName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(safe)) safe = "download";
        if (safe.Length > 50) safe = safe[..50].Trim();
        string dir = Path.Combine(root, $"{safe}-{Guid.NewGuid():N}"[..Math.Min(70, safe.Length + 33)]);
        int n = 1;
        while (Directory.Exists(dir)) dir = Path.Combine(root, $"{safe}-{Guid.NewGuid():N}"[..Math.Min(70, safe.Length + 33)] + $"({n++})");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Move that also works across volumes (copy + delete fallback).</summary>
    public static string MoveFileRobust(string source, string destDir, string fileName)
    {
        Directory.CreateDirectory(destDir);
        string dest = GetUniqueFilePath(destDir,
            Path.GetFileNameWithoutExtension(fileName), Path.GetExtension(fileName));
        string? sourceRoot = Path.GetPathRoot(Path.GetFullPath(source));
        string? destRoot = Path.GetPathRoot(Path.GetFullPath(dest));
        bool sameVolume = string.Equals(sourceRoot, destRoot, StringComparison.OrdinalIgnoreCase);
        IOException? moveError = null;

        // On the same volume, an atomic rename is cheapest. Windows scanners
        // may briefly hold the completed file, so retry before falling back
        // to a copy. Never copy immediately after a sharing violation.
        int moveAttempts = sameVolume ? 5 : 1;
        for (int attempt = 0; attempt < moveAttempts; attempt++)
        {
            try
            {
                File.Move(source, dest);
                return dest;
            }
            catch (IOException ex)
            {
                moveError = ex;
                if (attempt + 1 < moveAttempts)
                    Thread.Sleep(150 * (attempt + 1));
            }
        }

        try
        {
            // Copy fallback also needs retries: AV/indexer can briefly lock
            // the just-finished source, which previously surfaced as
            // "being used by another process" FAILED even though bytes were OK.
            Exception? lastCopyError = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.Copy(source, dest, overwrite: false);
                    lastCopyError = null;
                    break;
                }
                catch (Exception ex) when (attempt + 1 < 5)
                {
                    lastCopyError = ex;
                    Thread.Sleep(200 * (attempt + 1));
                }
            }
            if (lastCopyError != null)
                throw lastCopyError;
            // Deleting a still-scanned source must never fail the download.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { File.Delete(source); break; }
                catch { Thread.Sleep(150 * (attempt + 1)); }
            }
        }
        catch (Exception copyError)
        {
            try
            {
                if (File.Exists(dest) && File.Exists(source) &&
                    new FileInfo(dest).Length != new FileInfo(source).Length)
                    File.Delete(dest);
            }
            catch { }
            // If another process still has the finished source open, keep
            // the complete file in Parts and report that usable path instead
            // of deleting a successful download during caller cleanup.
            if (File.Exists(source)) return source;
            throw new IOException(
                $"Could not move completed download to '{dest}'. Rename failed: {moveError?.Message}",
                copyError);
        }
        return dest;
    }

    /// <summary>
    /// Move every finished file from a Parts subfolder to the downloads
    /// folder (preserving playlist subfolders), with collision numbering.
    /// Moves ALL non-transient files: an unexpected file landing in Downloads
    /// is always better than silently deleting a finished download.
    /// Returns source-&gt;destination pairs.
    /// </summary>
    public static List<(string Source, string Dest)> MoveFinishedMediaToDownloads(
        string partsSubdir, string downloadsFolder, DownloadItem? item = null)
    {
        var moved = new List<(string Source, string Dest)>();
        if (!Directory.Exists(partsSubdir)) return moved;
        foreach (string source in Directory.EnumerateFiles(partsSubdir, "*", SearchOption.AllDirectories))
        {
            if (IsTransientFile(source))
                continue;
            string relative = Path.GetRelativePath(partsSubdir, Path.GetDirectoryName(source)!);
            string destDir = relative == "." ? downloadsFolder : Path.Combine(downloadsFolder, relative);
            try
            {
                string dest = MoveFileRobust(source, destDir, Path.GetFileName(source));
                moved.Add((source, dest));
            }
            catch (Exception ex)
            {
                if (item != null) LogDiag(item, $"move failed for {source}: {ex.Message}");
            }
        }
        return moved;
    }

    /// <summary>
    /// Delete a Parts subfolder ONLY when no preservable file remains inside.
    /// Returns true when the folder is gone.
    /// </summary>
    public static bool DeletePartsSubdirIfClean(string partsSubdir, DownloadItem? item = null)
    {
        try
        {
            if (!Directory.Exists(partsSubdir)) return true;
            bool remnants = Directory.EnumerateFiles(partsSubdir, "*", SearchOption.AllDirectories)
                .Any(f => !IsTransientFile(f));
            if (remnants)
            {
                if (item != null) LogDiag(item, $"parts folder kept, unmoved files remain: {partsSubdir}");
                return false;
            }
            Directory.Delete(partsSubdir, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            if (item != null) LogDiag(item, $"parts cleanup failed: {ex.Message}");
            return false;
        }
    }

    private static bool _partsCleanupDone;
    private static void CleanupStalePartsOnce()
    {
        if (_partsCleanupDone) return;
        _partsCleanupDone = true;
        try
        {
            Task.Run(() =>
            {
                try
                {
                    string root = PortablePaths.DownloadPartsDirectory;
                    if (!Directory.Exists(root)) return;
                    var cutoff = DateTime.Now.AddDays(-7);
                    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        string lower = file.ToLowerInvariant();
                        if (!(lower.EndsWith(".part") || lower.EndsWith(".ytdl") ||
                              lower.EndsWith(".tmp") || lower.EndsWith(".temp")))
                            continue;
                        try { if (File.GetLastWriteTime(file) < cutoff) File.Delete(file); } catch { }
                    }
                    foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                                 .OrderByDescending(d => d.Length))
                    {
                        try
                        {
                            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                                Directory.Delete(dir);
                        }
                        catch { }
                    }
                }
                catch { }
            });
        }
        catch { }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} MB";
        return $"{bytes / (1024.0 * 1024.0):F2} GB";
    }

    public static string FormatDuration(double totalSeconds)
    {
        if (double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds) || totalSeconds < 0)
            return "--";
        var span = TimeSpan.FromSeconds(totalSeconds);
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes:D2}m";
        if (span.TotalMinutes >= 1)
            return $"{(int)span.TotalMinutes}m {span.Seconds:D2}s";
        return $"{Math.Max(1, (int)Math.Ceiling(span.TotalSeconds))}s";
    }

    private static string FindFfmpegPath()
    {
        string choco = @"C:\ProgramData\chocolatey\bin\ffmpeg.exe";
        if (File.Exists(choco)) return choco;

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string pythonFfmpeg = Path.Combine(localAppData, @"Programs\Python\Python312\Scripts\ffmpeg.exe");
        if (File.Exists(pythonFfmpeg)) return pythonFfmpeg;

        return "ffmpeg";
    }

    /// <summary>ffmpeg for the bridge's MSE finish mux (same resolution).</summary>
    public static string FfmpegPath => FindFfmpegPath();

    private static readonly Lazy<string> _cachedJsRuntimeArgs = new Lazy<string>(() =>
    {
        // Returns e.g. "--js-runtimes node " when a supported runtime is on PATH, else "".
        foreach (string runtime in new[] { "node", "deno" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = runtime,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(3000);
                    if (proc.ExitCode == 0) return $"--js-runtimes {runtime} ";
                }
            }
            catch { }
        }
        return "";
    });

    private static string FindJsRuntimeArgs() => _cachedJsRuntimeArgs.Value;

    /// <summary>
    /// Gated-content signals from yt-dlp output (age checks, login walls,
    /// bot/PO-token walls, playability "reload" walls, private content).
    /// Matched against error text only - never against site names.
    /// </summary>
    private static bool IsGatedError(string lastError) =>
        !string.IsNullOrWhiteSpace(lastError) &&
        Regex.IsMatch(lastError, @"sign in|log ?in|login|confirm your age|age.restrict|private video|pass cookies|cookies required|account.*required|not a bot|bot check|po_?token|needs? to be reloaded|reload the page", RegexOptions.IgnoreCase);

    /// <summary>
    /// Next browser session to try for an auth-walled URL (chrome -&gt; edge
    /// -&gt; brave), or null when the chain is exhausted. Each url+browser
    /// pair is tried at most once.
    /// </summary>
    private static string? NextAuthBrowser(string? cookieArgs, string url, HashSet<string> tried)
    {
        string[] chain = ["chrome", "edge", "brave"];
        string current = "";
        if (!string.IsNullOrEmpty(cookieArgs))
        {
            var m = Regex.Match(cookieArgs, @"cookies-from-browser (\w+)");
            if (m.Success) current = m.Groups[1].Value.ToLowerInvariant();
        }
        int nextIdx = string.IsNullOrEmpty(current) ? 0 : Array.IndexOf(chain, current) + 1;
        for (int i = nextIdx; i < chain.Length; i++)
        {
            if (tried.Add($"{url}|{chain[i]}")) return chain[i];
        }
        return null;
    }

    /// <summary>
    /// Next unchallenged player client for a session-carrying attempt that
    /// still hit the wall (web_safari first - safe with sessions - then tv).
    /// Keeps the attempt's existing cookie args; each url+client tried once.
    /// </summary>
    private static string? NextClientVariant(string? cookieArgs, string url, HashSet<string> tried)
    {
        string[] clients = ["web_safari", "tv"];
        foreach (string client in clients)
        {
            if (tried.Add($"{url}|client:{client}"))
                return $"{(cookieArgs ?? "").Trim()} --extractor-args \"youtube:player_client={client}\" ".TrimStart();
        }
        return null;
    }

    private static string? _cachedYtDlpVersion;
    private static string YtDlpVersion(string ytdlpPath)
    {
        if (_cachedYtDlpVersion != null) return _cachedYtDlpVersion;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = ytdlpPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            string output = proc?.StandardOutput.ReadToEnd().Trim() ?? "";
            proc?.WaitForExit(15000);
            _cachedYtDlpVersion = string.IsNullOrEmpty(output) ? "unknown" : output;
        }
        catch
        {
            _cachedYtDlpVersion = "unknown";
        }
        return _cachedYtDlpVersion;
    }

    /// <summary>Append-only diagnostics for streaming downloads (attempts, yt-dlp errors).</summary>
    private static void LogDiag(DownloadItem item, string message)
    {
        try
        {
            File.AppendAllText(PortablePaths.StartupLogPath,
                $"[{DateTime.Now}] [dl:{item.Id[..8]}:{item.Title}] {message}\n");
        }
        catch { }
    }

    /// <summary>
    /// Debug-build tracking: who (site/page), what (url/quality/session) and
    /// how it ended. The user reports a broken download, we grep this id in
    /// startup.log. Calls are wrapped in #if DEBUG by callers; this method
    /// itself is always compiled so both engine and bridge can use it.
    /// </summary>
    public static void Track(DownloadItem item, string message)
    {
        try
        {
            File.AppendAllText(PortablePaths.StartupLogPath,
                $"[{DateTime.Now}] [track:{item.Id[..8]}:{item.Title}] {message}\n");
        }
        catch { }
    }

#if DEBUG
    private static string HostOf(string? url)
    {
        try { return new Uri(url ?? "").Host; } catch { return "?"; }
    }

    private static void TrackStart(DownloadItem item)
    {
        Track(item, $"START site={HostOf(item.PageUrl)} " +
            $"page={item.PageUrl} url={item.Url} quality={item.Quality} " +
            $"cookies={(item.Cookies?.Count ?? 0)} pot={(!string.IsNullOrEmpty(item.PoToken) ? "yes" : "no")} " +
            $"ref={HostOf(item.Referrer)} " +
            $"ua={(string.IsNullOrEmpty(item.UserAgent) ? "no" : "yes")} " +
            $"playlist={item.DownloadPlaylist}");
    }

    private static void TrackEnd(DownloadItem item, string outcome)
    {
        Track(item, $"END {outcome}");
    }
#endif

    private static async Task<string> EnsurePlayableMediaFileAsync(string searchDir, string safeTitle, string expectedExt, string knownPath, bool remuxManifest = false, CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            string exactPath = "";
            if (!string.IsNullOrEmpty(knownPath) && File.Exists(knownPath))
            {
                exactPath = knownPath;
            }
            else
            {
                exactPath = Path.Combine(searchDir, $"{safeTitle}.{expectedExt}");
                if (!File.Exists(exactPath))
                {
                    var candidates = Directory.GetFiles(searchDir, $"{safeTitle}.*");
                    if (candidates.Length > 0)
                    {
                        var nonPart = candidates.FirstOrDefault(c => !c.EndsWith(".part") && !c.EndsWith(".ytdl"));
                        exactPath = nonPart ?? candidates[0];
                    }
                }
            }

            if (!File.Exists(exactPath)) return "";

            // Check if file contains MPEG-TS stream (starts with 0x47 sync byte) but is named .mp4
            try
            {
                using (var fs = new FileStream(exactPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] buf = new byte[188 * 3];
                    int read = fs.Read(buf, 0, buf.Length);
                    bool isMpegTs = read >= 188 && buf[0] == 0x47 && (read < 376 || buf[188] == 0x47);

                    if (isMpegTs && exactPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        fs.Close();

                        // Try remuxing to true ISO MP4 container using ffmpeg
                        string ffmpegPath = FindFfmpegPath();
                        string tempMp4 = Path.Combine(searchDir, $"{safeTitle}_true.mp4");
                        try
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = ffmpegPath,
                                Arguments = $"-y -i \"{exactPath}\" -c copy \"{tempMp4}\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            using var p = Process.Start(psi);
                            if (p != null)
                            {
                                await p.WaitForExitAsync(cancellationToken);
                                if (p.ExitCode == 0 && File.Exists(tempMp4) && new FileInfo(tempMp4).Length > 1024)
                                {
                                    File.Delete(exactPath);
                                    File.Move(tempMp4, exactPath);
                                    return exactPath;
                                }
                            }
                        }
                        catch { }

                        // If remux failed or ffmpeg not present, rename to .ts
                        // MPEG-TS files play natively in all Windows media players and VLC without error!
                        string tsPath = Path.Combine(searchDir, $"{safeTitle}.ts");
                        if (File.Exists(tsPath)) File.Delete(tsPath);
                        File.Move(exactPath, tsPath);
                        return tsPath;
                    }
                }
            }
            catch { }

            // Manifest-sourced MP4s (parallel HLS/DASH segment fetches) often
            // carry timestamp gaps that show as VLC macroblocking/stutter.
            // Lossless copy-remux with regenerated PTS + faststart fixes
            // playback without re-encoding. Failure keeps the original file.
            if (remuxManifest && exactPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string remuxed = Path.Combine(searchDir, $"{safeTitle}_clean.mp4");
                    if (File.Exists(remuxed)) File.Delete(remuxed);
                    var rpsi = new ProcessStartInfo
                    {
                        FileName = FindFfmpegPath(),
                        Arguments = $"-y -fflags +genpts -i \"{exactPath}\" -c copy -movflags +faststart \"{remuxed}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var rp = Process.Start(rpsi);
                    if (rp != null)
                    {
                        await rp.WaitForExitAsync(cancellationToken);
                        var rfi = File.Exists(remuxed) ? new FileInfo(remuxed) : null;
                        var ofi = new FileInfo(exactPath);
                        if (rp.ExitCode == 0 && rfi != null && rfi.Length > 1024 * 1024 &&
                            rfi.Length >= ofi.Length / 2)
                        {
                            File.Delete(exactPath);
                            File.Move(remuxed, exactPath);
                        }
                        else if (File.Exists(remuxed))
                        {
                            try { File.Delete(remuxed); } catch { }
                        }
                    }
                }
                catch { }
            }

            return exactPath;
        }, cancellationToken);
    }
}
