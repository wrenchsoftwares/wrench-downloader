using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WrenchDownloader;

/// <summary>
/// YouTube PO-token provider (bgutil plugin): mints the proof-of-origin
/// tokens YouTube's player API demands alongside a logged-in session
/// (age/bot gates). yt-dlp picks the plugin up automatically once it is
/// installed in yt-dlp's own Python environment - the app just makes sure
/// it is there, like the aria2c provisioning. Never throws.
/// </summary>
public static class PotProviderHelper
{
    private const string PackageName = "bgutil-ytdlp-pot-provider";
    private static int _ensured;

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
            if (await HasProviderAsync(pipOwner, cancellationToken)) return;
            await InstallAsync(pipOwner, cancellationToken);
            await HasProviderAsync(pipOwner, cancellationToken);
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
}
