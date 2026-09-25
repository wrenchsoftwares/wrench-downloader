using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
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

        string downloadsFolder = SettingsHelper.DownloadFolder;
        if (!Directory.Exists(downloadsFolder)) Directory.CreateDirectory(downloadsFolder);

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

    // No per-site lists anywhere: a URL needs resolving when it structurally
    // looks like a stream (manifest/playlist/chunk) or a page (no static
    // file extension). Anything else downloads directly; if the server
    // answers with a stream content-type we re-route (see DownloadDirectHttpAsync).
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
        if (lower.Contains(".m3u8") || 
            lower.Contains(".mpd") || 
            lower.Contains(".m3u") || 
            lower.Contains("/manifest") ||
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
        bool isDirectManifest = item.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                                item.Url.Contains(".mpd", StringComparison.OrdinalIgnoreCase);

        item.Status = DownloadStatus.Downloading;
        item.StatusText = isDirectManifest ? "Connecting to video stream..." : "Extracting video stream...";
        item.SpeedText = "Resolving...";

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

        if (!isGenericTitle)
        {
            // If file already exists with same name, increment number: "Title (1)", "Title (2)", etc.
            string baseSafeTitle = safeTitle;
            int count = 1;
            while (File.Exists(Path.Combine(downloadsFolder, $"{safeTitle}.{ext}")) ||
                   File.Exists(Path.Combine(downloadsFolder, $"{safeTitle}.mp4")) ||
                   File.Exists(Path.Combine(downloadsFolder, $"{safeTitle}.mkv")) ||
                   File.Exists(Path.Combine(downloadsFolder, $"{safeTitle}.webm")) ||
                   File.Exists(Path.Combine(downloadsFolder, $"{safeTitle}.mp3")))
            {
                safeTitle = $"{baseSafeTitle} ({count++})";
            }
        }

        // If title is generic or from a webpage script, let yt-dlp determine the real video title!
        // %(autonumber)s or yt-dlp template can be used, and after download completion we also ensure collision number.
        string outputTemplate = isGenericTitle
            ? Path.Combine(downloadsFolder, "%(title)s.%(ext)s")
            : Path.Combine(downloadsFolder, $"{safeTitle}.%(ext)s");

        // Locate yt-dlp.exe
        string ytdlpPath = "yt-dlp";
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string pythonYtDlp = Path.Combine(localAppData, @"Programs\Python\Python312\Scripts\yt-dlp.exe");
        if (File.Exists(pythonYtDlp)) ytdlpPath = pythonYtDlp;

        var argsBuilder = new StringBuilder();
        // IDM-style parallel segment download flags with robust retry & timeout settings to ensure it completes
        argsBuilder.Append("--no-playlist --no-warnings --socket-timeout 20 --retries 10 --fragment-retries 20 ");
        argsBuilder.Append($"--concurrent-fragments {SettingsHelper.ConcurrentFragments} ");
        // Maximize network buffer & chunking to avoid server-side rate-limiting and maximize throughput
        argsBuilder.Append("--buffer-size 64K --http-chunk-size 10M --throttled-rate 100K ");

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

        // Captured stream URLs (signed HLS links) can expire (410 Gone) before
        // the download starts. Fall back to the source page, which yt-dlp re-resolves.
        var candidateUrls = new List<string>();
        bool isSubOrAudio = !isAudio && !string.IsNullOrWhiteSpace(item.Url) &&
                            (item.Url.Contains("/mp4a/", StringComparison.OrdinalIgnoreCase) ||
                             item.Url.Contains("/audio/", StringComparison.OrdinalIgnoreCase) ||
                             item.Url.Contains("/avc1/", StringComparison.OrdinalIgnoreCase) ||
                             item.Url.Contains("/aac/", StringComparison.OrdinalIgnoreCase) ||
                             item.Url.Contains("chunklist", StringComparison.OrdinalIgnoreCase));

        if (isSubOrAudio && !string.IsNullOrWhiteSpace(item.PageUrl))
        {
            // If the captured URL was an isolated audio track or single-track rendition,
            // prioritize the page URL which resolves the complete presentation with both audio and video!
            candidateUrls.Add(item.PageUrl);
            if (!string.IsNullOrWhiteSpace(item.Url)) candidateUrls.Add(item.Url);
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
        var pendingUrls = new Queue<string>(candidateUrls);
        int sameUrlRetries = 0;
        bool firstTry = true;

        while (pendingUrls.Count > 0 && !succeeded)
        {
            string attemptUrl = pendingUrls.Peek();
            if (!firstTry)
            {
                item.Progress = 0;
                item.SavePath = "";
                item.SpeedText = "Resolving...";
            }
            firstTry = false;

            var psi = new ProcessStartInfo
            {
                FileName = ytdlpPath,
                Arguments = $"{commonArgs}-o \"{outputTemplate}\" \"{attemptUrl}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

        try
        {
            using var proc = new Process { StartInfo = psi };
            string lastError = "";
            
            // Regexes for parsing yt-dlp progress (handling ~ in HLS estimates and ETA)
            var progressRegex = new Regex(@"\[download\]\s+([\d\.]+)%\s+of\s+(?:~?\s*)([^\s]+)\s+at\s+([^\s]+)(?:\s+ETA\s+([^\s]+))?", RegexOptions.Compiled);
            var destRegex = new Regex(@"\[Merger\] Merging formats into ""([^""]+)""", RegexOptions.Compiled);
            var destDirectRegex = new Regex(@"\[download\] Destination: (.+)", RegexOptions.Compiled);
            var remuxRegex = new Regex(@"\[VideoRemuxer\] Remuxing video from [^ ]+ to ""?([^""]+)""?", RegexOptions.Compiled);

            proc.OutputDataReceived += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                string line = e.Data.Trim();

                var m = progressRegex.Match(line);
                if (m.Success)
                {
                    if (double.TryParse(m.Groups[1].Value, out double pct))
                    {
                        item.Progress = pct;
                    }
                    string size = m.Groups[2].Value;
                    string speed = m.Groups[3].Value;
                    string eta = m.Groups[4].Success ? m.Groups[4].Value : "";

                    item.SizeText = size;
                    item.SpeedText = speed;
                    item.StatusText = string.IsNullOrEmpty(eta) ? $"{pct:F1}% of {size} ({speed})" : $"{pct:F1}% of {size} (ETA {eta})";
                }
                else if (line.Contains("[Merger]"))
                {
                    item.StatusText = "Muxing video and audio tracks...";
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
                    item.StatusText = "Remuxing into valid MP4...";
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
                    }
                }
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await proc.WaitForExitAsync(cancellationToken);

            string finalFile = await EnsurePlayableMediaFileAsync(downloadsFolder, safeTitle, ext, item.SavePath, cancellationToken);

            if (proc.ExitCode == 0 || (!string.IsNullOrEmpty(finalFile) && File.Exists(finalFile) && new FileInfo(finalFile).Length > 1024 * 1024))
            {
                item.Progress = 100;
                item.Status = DownloadStatus.Completed;
                item.SpeedText = "Finished";
                if (!string.IsNullOrEmpty(finalFile))
                {
                    item.SavePath = finalFile;
                    string currentName = Path.GetFileNameWithoutExtension(finalFile);
                    if (isGenericTitle)
                    {
                        item.Title = currentName;
                    }
                }
                item.StatusText = $"Completed! Saved to {Path.GetFileName(item.SavePath)}";
                succeeded = true;
            }
            else
            {
                // Flaky endpoints (rate limits, Cloudflare walls, empty JSON):
                // retry the same URL briefly before giving up on it.
                bool rateLimited = Regex.IsMatch(lastError, @"\b429\b|rate.?limit|too many requests|timed out|timeout|failed to parse json|unable to download.*json|\b50[234]\b|bad gateway|service unavailable|gateway timeout|temporary failure", RegexOptions.IgnoreCase);
                if (rateLimited && sameUrlRetries < 2)
                {
                    sameUrlRetries++;
                    item.StatusText = $"Rate limited, retrying ({sameUrlRetries}/2)...";
                    item.SpeedText = "Waiting...";
                    try { await Task.Delay(5000, cancellationToken); } catch { }
                    continue;
                }
                sameUrlRetries = 0;
                pendingUrls.Dequeue();
                if (pendingUrls.Count > 0)
                {
                    // Next candidate source (e.g. the page re-resolves expired links).
                    item.Progress = 0;
                    item.SavePath = "";
                    item.StatusText = "Stream link failed, retrying via page...";
                    item.SpeedText = "Resolving...";
                    continue;
                }
                item.Status = DownloadStatus.Failed;
                if (!string.IsNullOrWhiteSpace(lastError))
                {
                    item.StatusText = lastError.Length > 90 ? lastError[..90] + "..." : lastError;
                }
                else
                {
                    item.StatusText = $"Download failed (code {proc.ExitCode})";
                }
                break;
            }
        }
        catch (Exception ex)
        {
            item.Status = DownloadStatus.Failed;
            item.StatusText = $"Error: {ex.Message}";
            break;
        }
        }
    }

    private static async Task DownloadDirectHttpAsync(DownloadItem item, string downloadsFolder, CancellationToken cancellationToken)
    {
        try
        {
            item.Status = DownloadStatus.Downloading;
            item.StatusText = "Connecting...";

            using var response = await _httpClient.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            string contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            string rawName = Path.GetFileName(new Uri(item.Url).AbsolutePath).ToLowerInvariant();

            if (contentType.Contains("mpegurl") || 
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
            string totalStr = totalBytes.HasValue ? FormatBytes(totalBytes.Value) : "Unknown size";
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

            // If a specific custom SavePath folder was already chosen in prompt dialog
            string targetFolder = !string.IsNullOrEmpty(item.SavePath) && Directory.Exists(Path.GetDirectoryName(item.SavePath))
                ? Path.GetDirectoryName(item.SavePath)!
                : downloadsFolder;

            // Avoid collision: file (1).ext, file (2).ext
            item.SavePath = GetUniqueFilePath(targetFolder, safeTitle, rawExt);
            item.Title = Path.GetFileName(item.SavePath);

            // High-speed 512KB buffer for maximum I/O throughput
            const int bufferSize = 524288;
            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(item.SavePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, true);

            byte[] buffer = new byte[bufferSize];
            long totalRead = 0;
            int bytesRead;
            var sw = Stopwatch.StartNew();
            long lastBytes = 0;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalRead += bytesRead;

                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    item.Progress = (double)totalRead / totalBytes.Value * 100.0;
                }

                if (sw.ElapsedMilliseconds >= 500)
                {
                    double speedBytesSec = (totalRead - lastBytes) / (sw.Elapsed.TotalSeconds);
                    item.SpeedText = $"{FormatBytes((long)speedBytesSec)}/s";
                    item.StatusText = $"{FormatBytes(totalRead)} / {totalStr}";
                    lastBytes = totalRead;
                    sw.Restart();
                }
            }

            item.Progress = 100;
            item.Status = DownloadStatus.Completed;
            item.SpeedText = "Finished";
            item.StatusText = $"Saved to {Path.GetFileName(item.SavePath)}";
        }
        catch (OperationCanceledException)
        {
            item.Status = DownloadStatus.Paused;
            item.StatusText = "Cancelled";
        }
        catch (Exception ex)
        {
            item.Status = DownloadStatus.Failed;
            item.StatusText = $"Error: {ex.Message}";
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

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
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

    private static async Task<string> EnsurePlayableMediaFileAsync(string downloadsFolder, string safeTitle, string expectedExt, string knownPath, CancellationToken cancellationToken = default)
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
                exactPath = Path.Combine(downloadsFolder, $"{safeTitle}.{expectedExt}");
                if (!File.Exists(exactPath))
                {
                    var candidates = Directory.GetFiles(downloadsFolder, $"{safeTitle}.*");
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
                        string tempMp4 = Path.Combine(downloadsFolder, $"{safeTitle}_true.mp4");
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
                        string tsPath = Path.Combine(downloadsFolder, $"{safeTitle}.ts");
                        if (File.Exists(tsPath)) File.Delete(tsPath);
                        File.Move(exactPath, tsPath);
                        return tsPath;
                    }
                }
            }
            catch { }

            return exactPath;
        }, cancellationToken);
    }
}
