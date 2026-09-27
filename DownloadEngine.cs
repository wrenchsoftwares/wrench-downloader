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

        string configuredDownloadFolder = SettingsHelper.DownloadFolder;
        string downloadsFolder = item.TargetFolder;
        if (string.IsNullOrWhiteSpace(downloadsFolder) || !Directory.Exists(downloadsFolder))
        {
            downloadsFolder = configuredDownloadFolder;
            if (!string.IsNullOrWhiteSpace(item.SavePath))
            {
                string? requestedFolder = Path.GetDirectoryName(item.SavePath);
                if (!string.IsNullOrWhiteSpace(requestedFolder) && Directory.Exists(requestedFolder))
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
        string partsSubdir = CreatePartsSubdir(safeTitle);

        // If title is generic or from a webpage script, let yt-dlp determine the real video title!
        // %(autonumber)s or yt-dlp template can be used, and after download completion we also ensure collision number.
        string outputTemplate = item.DownloadPlaylist
            ? Path.Combine(partsSubdir, "%(playlist_title)s", "%(playlist_index)03d - %(title)s.%(ext)s")
            : isGenericTitle
                ? Path.Combine(partsSubdir, "%(title)s.%(ext)s")
                : Path.Combine(partsSubdir, $"{safeTitle}.%(ext)s");
        string temporaryFolder = partsSubdir;

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

        var argsBuilder = new StringBuilder();
        // IDM-style parallel segment download flags with robust retry & timeout settings to ensure it completes
        argsBuilder.Append(item.DownloadPlaylist ? "--yes-playlist " : "--no-playlist ");
        argsBuilder.Append("--no-warnings --socket-timeout 20 --retries 10 --fragment-retries 20 ");
        argsBuilder.Append($"--concurrent-fragments {SettingsHelper.ConcurrentFragments} ");
        if (SettingsHelper.MaximumDownloadRateKBps > 0)
        {
            argsBuilder.Append($"--limit-rate {SettingsHelper.MaximumDownloadRateKBps}K ");
        }
        // Maximize network buffer & chunking to avoid server-side rate-limiting and maximize throughput
        argsBuilder.Append("--buffer-size 64K --http-chunk-size 10M --throttled-rate 100K ");

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

        // Live browser session from the extension: cookie-DB export fails
        // while the browser runs (locked profile), but the extension reads
        // its own tabs' cookies directly. Attaching them to every attempt
        // opens age/logged-in gates exactly like browser-attached downloaders.
        string? extensionCookieFile = null;
        if (item.Cookies != null && item.Cookies.Count > 0)
        {
            string jarHostUrl = !string.IsNullOrWhiteSpace(item.PageUrl) ? item.PageUrl : item.Url;
            try { extensionCookieFile = ChallengeSolver.WriteCookieJar(jarHostUrl, item.Cookies); }
            catch { extensionCookieFile = null; }
            if (extensionCookieFile != null)
            {
                commonArgs += $"--cookies \"{extensionCookieFile}\" ";
                LogDiag(item, $"browser session attached ({item.Cookies.Count} cookies)");
            }
        }

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

        // Captured stream URLs (signed HLS links) can expire (410 Gone) before
        // the download starts. Fall back to the source page, which yt-dlp re-resolves.
        var candidateUrls = new List<string>();
        bool isSubOrAudio = !isAudio && !string.IsNullOrWhiteSpace(item.Url) &&
                            (item.Url.Contains("/mp4a/", StringComparison.OrdinalIgnoreCase) ||
                             item.Url.Contains("/audio/", StringComparison.OrdinalIgnoreCase) ||
                             item.Url.Contains("/avc1/", StringComparison.OrdinalIgnoreCase) ||
                             item.Url.Contains("/aac/", StringComparison.OrdinalIgnoreCase) ||
                             item.Url.Contains("chunklist", StringComparison.OrdinalIgnoreCase));

        bool isManifestUrl = Regex.IsMatch(item.Url, @"\.(m3u8|mpd)(?:[?#]|$)", RegexOptions.IgnoreCase);
        if (!string.IsNullOrWhiteSpace(item.PageUrl) &&
            !item.PageUrl.Equals(item.Url, StringComparison.OrdinalIgnoreCase) &&
            (item.DownloadPlaylist || isSubOrAudio || isManifestUrl) &&
            !string.Equals(item.Quality, "audio", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(item.Quality, "file", StringComparison.OrdinalIgnoreCase))
        {
            // Resolve the page first so yt-dlp can select the requested quality instead of
            // being limited to the rendition that happened to be playing in the browser.
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
            foreach (string u in candidateUrls)
                plan.Add((u, null));
        }
        var pendingUrls = new Queue<(string Url, string? CookieArgs)>(plan);
        int sameUrlRetries = 0;
        var authBrowsersTried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int stuckFailures = 0;
        string? lastHead = null;
        bool firstTry = true;

        while (pendingUrls.Count > 0 && !succeeded)
        {
            var attempt = pendingUrls.Peek();
            string attemptUrl = attempt.Url;
            string attemptArgs = commonArgs + (attempt.CookieArgs ?? "") + (ariaActive ? ariaSuffix : "");
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

            string finalFile = await EnsurePlayableMediaFileAsync(partsSubdir, safeTitle, ext, item.SavePath, cancellationToken);

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
            }
            else
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    item.Status = DownloadStatus.Paused;
                    item.SpeedText = AppLocalization.Get("download.pausedSpeed");
                    item.StatusText = AppLocalization.Get("download.paused");
                    item.EtaText = "--";
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
                if (ariaActive && lastError.Contains("aria2c", StringComparison.OrdinalIgnoreCase))
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
                    break;
                }
                // Auth-walled content (age checks, login walls, private videos
                // the user can access): plain requests fail while the user's
                // browser already holds a verified session - exactly how
                // browser-attached downloaders get these files. Retry the same
                // URL through the browser's cookies (chrome -> edge -> brave).
                // Purely error-driven; no site lists. Skipped when cookie
                // export itself is broken (same guaranteed failure).
                if (!cookiesFailed && IsAuthError(lastError))
                {
                    string? nextBrowser = NextAuthBrowser(attempt.CookieArgs, attemptUrl, authBrowsersTried);
                    if (nextBrowser != null)
                    {
                        authBrowsersTried.Add($"{attemptUrl}|{nextBrowser}");
                        LogDiag(item, $"auth wall detected, retrying with {nextBrowser} session");
                        pendingUrls.Dequeue();
                        var rest = pendingUrls.ToList();
                        pendingUrls.Clear();
                        pendingUrls.Enqueue((attemptUrl, $"--cookies-from-browser {nextBrowser} "));
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
            break;
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                item.Status = DownloadStatus.Paused;
                item.SpeedText = AppLocalization.Get("download.pausedSpeed");
                item.StatusText = AppLocalization.Get("download.paused");
                break;
            }
            item.Status = DownloadStatus.Failed;
            item.StatusText = AppLocalization.Format("download.error", ex.Message);
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
            string finalFolder = !string.IsNullOrEmpty(item.SavePath) && Directory.Exists(Path.GetDirectoryName(item.SavePath))
                ? Path.GetDirectoryName(item.SavePath)!
                : downloadsFolder;

            partsSubdir = CreatePartsSubdir(safeTitle);
            string tempPath = Path.Combine(partsSubdir, SanitizeFileName(
                string.IsNullOrWhiteSpace(safeTitle) ? "download" : safeTitle) + rawExt);
            item.SavePath = tempPath;
            item.Title = Path.GetFileName(tempPath);

            // High-speed 512KB buffer for maximum I/O throughput
            const int bufferSize = 524288;
            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(item.SavePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, true);

            byte[] buffer = new byte[bufferSize];
            long totalRead = 0;
            int bytesRead;
            var sw = Stopwatch.StartNew();
            var downloadTimer = Stopwatch.StartNew();
            long lastBytes = 0;

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

                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    item.Progress = (double)totalRead / totalBytes.Value * 100.0;
                }

                if (sw.ElapsedMilliseconds >= 500)
                {
                    double speedBytesSec = (totalRead - lastBytes) / (sw.Elapsed.TotalSeconds);
                    item.SpeedText = $"{FormatBytes((long)speedBytesSec)}/s";
                    item.StatusText = AppLocalization.Format("download.transferred", FormatBytes(totalRead), totalStr);
                    double elapsed = downloadTimer.Elapsed.TotalSeconds;
                    if (elapsed > 1 && totalRead > 0 && totalBytes.HasValue &&
                        totalBytes.Value > totalRead)
                    {
                        double remaining = (totalBytes.Value - totalRead) / (totalRead / elapsed);
                        item.EtaText = AppLocalization.Format("download.timeLeft", FormatDuration(remaining));
                    }
                    else
                    {
                        item.EtaText = "--";
                    }
                    lastBytes = totalRead;
                    sw.Restart();
                }
            }

            item.Progress = 100;
            item.Status = DownloadStatus.Completed;
            item.SpeedText = AppLocalization.Get("download.finished");
            item.EtaText = "--";
            string dest = MoveFileRobust(tempPath, finalFolder, Path.GetFileName(tempPath));
            try { if (Directory.Exists(partsSubdir)) Directory.Delete(partsSubdir, recursive: true); } catch { }
            item.SavePath = dest;
            item.Title = Path.GetFileName(dest);
            item.StatusText = AppLocalization.Format("download.saved", Path.GetFileName(item.SavePath));
        }
        catch (OperationCanceledException)
        {
            item.Status = DownloadStatus.Paused;
            item.SpeedText = AppLocalization.Get("download.pausedSpeed");
            item.StatusText = AppLocalization.Get("download.paused");
            item.EtaText = "--";
            try { if (Directory.Exists(partsSubdir)) Directory.Delete(partsSubdir, recursive: true); } catch { }
        }
        catch (Exception ex)
        {
            item.Status = DownloadStatus.Failed;
            item.StatusText = AppLocalization.Format("download.error", ex.Message);
            item.EtaText = "--";
            LogDiag(item, $"direct failed: {ex}");
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
        try
        {
            File.Move(source, dest);
        }
        catch (IOException)
        {
            File.Copy(source, dest, overwrite: false);
            try { File.Delete(source); } catch { }
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
    /// Auth-wall signals from yt-dlp output (age checks, login walls, private
    /// content). Matched against error text only - never against site names.
    /// </summary>
    private static bool IsAuthError(string lastError) =>
        !string.IsNullOrWhiteSpace(lastError) &&
        Regex.IsMatch(lastError, @"sign in|log ?in|login|confirm your age|age.restrict|private video|pass cookies|cookies required|account.*required", RegexOptions.IgnoreCase);

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

    private static async Task<string> EnsurePlayableMediaFileAsync(string searchDir, string safeTitle, string expectedExt, string knownPath, CancellationToken cancellationToken = default)
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

            return exactPath;
        }, cancellationToken);
    }
}
