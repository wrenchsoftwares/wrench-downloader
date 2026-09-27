using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WrenchDownloader;

/// <summary>
/// YouTube PO-token capability: the bgutil plugin (mints tokens inside
/// yt-dlp) plus its local attestation server (does the BotGuard math).
/// yt-dlp picks the plugin up automatically once installed in yt-dlp's own
/// Python environment; the server is spawned on 127.0.0.1:4416 on demand.
/// Everything here is best-effort and never throws - without it, downloads
/// simply behave as before. The app just makes sure it is there, like the
/// aria2c provisioning.
/// </summary>
public static class PotProviderHelper
{
    private const string PackageName = "bgutil-ytdlp-pot-provider";
    private const string ServerVersion = "2.0.0";
    private const string ServerRepoZip =
        "https://github.com/Brainicism/bgutil-ytdlp-pot-provider/archive/refs/tags/2.0.0.zip";
    private const string DenoZipUrl =
        "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";
    private const int ServerPort = 4416;
    private static int _ensured;
    private static Process? _serverProcess;

    public static bool IsYouTubeUrl(string? url)
    {
        try
        {
            string host = new Uri(url ?? "").Host.ToLowerInvariant();
            return host.Contains("youtube.", StringComparison.Ordinal) ||
                   host.Equals("youtu.be", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>Ensure the provider exists (install once per session when missing).</summary>
    public static async Task EnsureAsync(string? ytdlpPath, CancellationToken cancellationToken = default)
    {
        if (System.Threading.Interlocked.Exchange(ref _ensured, 1) == 1) return;
        try
        {
            string? pipOwner = OwnerPython(ytdlpPath);
            if (pipOwner == null) return;
            if (!await HasProviderAsync(pipOwner, cancellationToken))
                await InstallAsync(pipOwner, cancellationToken);
            // The plugin alone is inert without its attestation server.
            await EnsureServerRunningAsync(cancellationToken);
        }
        catch { }
    }

    private static string? OwnerPython(string? ytdlpPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(ytdlpPath) && ytdlpPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                string? scriptsDir = Path.GetDirectoryName(ytdlpPath);
                string? baseDir = !string.IsNullOrEmpty(scriptsDir)
                    ? Directory.GetParent(scriptsDir)?.FullName
                    : null;
                if (!string.IsNullOrEmpty(baseDir))
                {
                    string candidate = Path.Combine(baseDir, "python.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        catch { }
        return "python";
    }

    private static async Task<bool> HasProviderAsync(string python, CancellationToken cancellationToken)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = python,
                Arguments = "-m pip show " + PackageName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc == null) return false;
            using var reg = cancellationToken.Register(() => { try { proc.Kill(); } catch { } });
            await proc.WaitForExitAsync(cancellationToken);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task InstallAsync(string python, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = python,
                Arguments = "-m pip install " + PackageName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc == null) return;
            using var reg = timeoutCts.Token.Register(() => { try { proc.Kill(); } catch { } });
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch { }
    }

    private static string DenoDir => Path.Combine(PortablePaths.DataDirectory, "deno");
    private static string DenoExe => Path.Combine(DenoDir, "bin", "deno.exe");
    private static string ServerRoot => Path.Combine(PortablePaths.DataDirectory, "pot-server");
    private static string ServerHome => Path.Combine(ServerRoot, "server");
    private static string ServerMarker => Path.Combine(ServerRoot, ".ready");

    /// <summary>
    /// Ensure the local attestation server answers on 127.0.0.1:4416,
    /// provisioning deno + server files on first use. A running server is
    /// intentionally left alive (loopback-only, shared across app restarts).
    /// </summary>
    public static async Task EnsureServerRunningAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (await PingAsync()) return;
            if (!await EnsureServerFilesAsync(cancellationToken)) return;
            StartServer();
            for (int i = 0; i < 15; i++)
            {
                try { await Task.Delay(2000, cancellationToken); } catch { return; }
                if (await PingAsync()) return;
            }
        }
        catch { }
    }

    private static async Task<bool> PingAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            string body = await http.GetStringAsync($"http://127.0.0.1:{ServerPort}/ping");
            return body.Contains("server_uptime", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static async Task<bool> EnsureServerFilesAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(ServerMarker) &&
                File.ReadAllText(ServerMarker).Trim() == ServerVersion &&
                Directory.Exists(Path.Combine(ServerHome, "node_modules")))
                return true;

            if (!await EnsureDenoAsync(cancellationToken)) return false;

            string tempDir = Path.Combine(Path.GetTempPath(), $"wd_pot_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                string zipPath = Path.Combine(tempDir, "pot.zip");
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WrenchDownloader");
                using var response = await http.GetAsync(ServerRepoZip,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var netStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await netStream.CopyToAsync(fileStream, cancellationToken);

                string extractDir = Path.Combine(tempDir, "x");
                ZipFile.ExtractToDirectory(zipPath, extractDir);
                string? serverSrc = Directory.GetDirectories(extractDir)
                    .Select(d => Path.Combine(d, "server"))
                    .FirstOrDefault(Directory.Exists);
                if (serverSrc == null) return false;

                if (Directory.Exists(ServerRoot))
                    Directory.Delete(ServerRoot, recursive: true);
                Directory.CreateDirectory(Path.GetDirectoryName(ServerHome)!);
                Directory.Move(serverSrc, ServerHome);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = DenoExe,
                    Arguments = "install --allow-scripts=npm:canvas --frozen",
                    WorkingDirectory = ServerHome,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (proc == null) return false;
                using var reg = timeoutCts.Token.Register(() => { try { proc.Kill(); } catch { } });
                await proc.WaitForExitAsync(timeoutCts.Token);
                if (proc.ExitCode != 0) return false;
                if (!Directory.Exists(Path.Combine(ServerHome, "node_modules"))) return false;

                File.WriteAllText(ServerMarker, ServerVersion);
                return true;
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
        catch { return false; }
    }

    private static async Task<bool> EnsureDenoAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(DenoExe)) return true;

            string tempDir = Path.Combine(Path.GetTempPath(), $"wd_deno_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                string zipPath = Path.Combine(tempDir, "deno.zip");
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WrenchDownloader");
                using var response = await http.GetAsync(DenoZipUrl,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var netStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await netStream.CopyToAsync(fileStream, cancellationToken);

                Directory.CreateDirectory(Path.Combine(DenoDir, "bin"));
                ZipFile.ExtractToDirectory(zipPath, Path.Combine(DenoDir, "bin"), overwriteFiles: true);
                return File.Exists(DenoExe);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
        catch { return false; }
    }

    private static void StartServer()
    {
        try
        {
            if (_serverProcess != null && !_serverProcess.HasExited) return;
            var psi = new ProcessStartInfo
            {
                FileName = DenoExe,
                Arguments = "run --allow-env --allow-net --allow-ffi=. --allow-read=. ../src/main.ts --port 4416 --host 127.0.0.1",
                WorkingDirectory = Path.Combine(ServerHome, "node_modules"),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            if (proc == null) return;
            proc.OutputDataReceived += (_, __) => { };
            proc.ErrorDataReceived += (_, __) => { };
            try { proc.BeginOutputReadLine(); } catch { }
            try { proc.BeginErrorReadLine(); } catch { }
            _serverProcess = proc;
        }
        catch { }
    }
}
