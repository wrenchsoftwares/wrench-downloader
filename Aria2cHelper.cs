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
/// Multi-connection downloads via aria2c (the IDM-style speed lever for
/// plain HTTP files, where yt-dlp alone uses a single connection).
/// If aria2c is not on the machine it is fetched once (pinned release) into
/// the app Data folder. HLS/DASH are unaffected: yt-dlp keeps those on its
/// native downloader driven by --concurrent-fragments.
/// </summary>
public static class Aria2cHelper
{
    private const string PinnedZipUrl =
        "https://github.com/aria2/aria2/releases/download/release-1.37.0/aria2-1.37.0-win-64bit-build1.zip";

    public static string ProvisionedPath =>
        Path.Combine(PortablePaths.DataDirectory, "aria2c.exe");

    /// <summary>Fast synchronous lookup, no network. Null when absent.</summary>
    public static string? FindAria2c()
    {
        try
        {
            if (File.Exists(ProvisionedPath)) return ProvisionedPath;
            string local = Path.Combine(AppContext.BaseDirectory, "aria2c.exe");
            if (File.Exists(local)) return local;

            foreach (string dir in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\WinGet\Links"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "aria2"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "aria2"),
            })
            {
                string candidate = Path.Combine(dir, "aria2c.exe");
                if (File.Exists(candidate)) return candidate;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "aria2c.exe",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            string output = proc?.StandardOutput.ReadToEnd() ?? "";
            proc?.WaitForExit(8000);
            string? found = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().Trim('"'))
                .FirstOrDefault(File.Exists);
            if (found != null) return found;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Ensure an aria2c binary exists (fetch pinned build on first use).
    /// Returns the path or null. Never throws.
    /// </summary>
    public static async Task<string?> EnsureAria2cAsync(CancellationToken cancellationToken = default)
    {
        string? found = FindAria2c();
        if (found != null) return found;
        try
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"wd_aria2_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                string zipPath = Path.Combine(tempDir, "aria2.zip");
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WrenchDownloader");
                using var response = await http.GetAsync(PinnedZipUrl,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var netStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await netStream.CopyToAsync(fileStream, cancellationToken);

                using var archive = ZipFile.OpenRead(zipPath);
                var entry = archive.Entries.FirstOrDefault(e =>
                    e.Name.Equals("aria2c.exe", StringComparison.OrdinalIgnoreCase));
                if (entry == null) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(ProvisionedPath)!);
                entry.ExtractToFile(ProvisionedPath, overwrite: true);
                if (await VerifyAsync(ProvisionedPath, cancellationToken))
                    return ProvisionedPath;
                try { File.Delete(ProvisionedPath); } catch { }
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
        catch { }
        return null;
    }

    private static async Task<bool> VerifyAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc == null) return false;
            string output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return proc.ExitCode == 0 && output.Contains("aria2 version", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// yt-dlp args enabling the aria2c external downloader, or "" when absent.
    /// Connection count follows the user's parallel-fragments setting but never
    /// exceeds 8: strict CDNs answer bigger bursts with HTTP 503 for every
    /// connection (total failure), while 8 sustains IDM-class ~2MB/s.
    /// Honors the user's speed cap via aria2c (yt-dlp --limit-rate is ignored
    /// by external downloaders).
    /// </summary>
    public static string BuildDownloaderArgs(string? aria2cPath, int fragments, int rateCapKBps)
    {
        if (string.IsNullOrEmpty(aria2cPath) || !File.Exists(aria2cPath))
            return "";
        int connections = Math.Clamp(fragments, 1, 8);
        string rateArg = rateCapKBps > 0 ? $" --max-download-limit={rateCapKBps}K" : "";
        return $"--downloader aria2c --downloader-args \"aria2c:-x {connections} -s {connections} -k 1M --file-allocation=none --retry-wait=2 --max-tries=5{rateArg}\" ";
    }

    /// <summary>
    /// yt-dlp locates external downloaders on PATH; make sure OUR binary is
    /// visible to the child process regardless of machine configuration.
    /// </summary>
    public static void ConfigureEnvironment(ProcessStartInfo psi, string? aria2cPath)
    {
        try
        {
            if (string.IsNullOrEmpty(aria2cPath)) return;
            string? dir = Path.GetDirectoryName(aria2cPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            string current = psi.Environment.TryGetValue("PATH", out string? existing) && !string.IsNullOrEmpty(existing)
                ? existing
                : Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!current.Split(';').Any(p => p.Trim().Equals(dir, StringComparison.OrdinalIgnoreCase)))
                psi.Environment["PATH"] = dir + ";" + current;
        }
        catch { }
    }
}
