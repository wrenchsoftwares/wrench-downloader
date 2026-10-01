using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WrenchDownloader;

/// <summary>
/// Makes Wrench Downloader the OS handler for magnet: links (magnet-button
/// clicks in Chrome) instead of other download managers. Registration is
/// per-user (HKCU, no admin) and re-applied on every launch so it sticks.
/// A second instance started by a protocol click forwards the magnet to the
/// running instance over the bridge and exits - no duplicate windows.
/// </summary>
public static class MagnetProtocol
{
    /// <summary>First command-line argument that is a magnet link, if any.</summary>
    public static string? TryGetLaunchMagnet()
    {
        try
        {
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (!string.IsNullOrWhiteSpace(arg) &&
                    arg.Trim().StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
                    return arg.Trim().Trim('"');
            }
        }
        catch { }
        return null;
    }

    /// <summary>Register this exe as the magnet: handler (best-effort).</summary>
    public static void Register()
    {
        try
        {
            string exe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrWhiteSpace(exe)) return;
            using var magnet = Registry.CurrentUser.CreateSubKey(@"Software\Classes\magnet");
            if (magnet == null) return;
            magnet.SetValue("", "URL:Magnet Protocol");
            magnet.SetValue("URL Protocol", "");
            using var command = magnet.CreateSubKey(@"shell\open\command");
            command?.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch { }
    }

    /// <summary>
    /// POST the magnet to the already-running instance. True when it
    /// answered (caller should exit); false when we are the first instance.
    /// </summary>
    public static async Task<bool> ForwardToRunningInstanceAsync(string magnet)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                using var health = await http.GetAsync("http://127.0.0.1:45732/api/health");
                if (!health.IsSuccessStatusCode) return false;
            }
            catch { return false; }
            var payload = new
            {
                url = magnet,
                title = magnet,
                quality = "torrent",
                format = "",
                pageUrl = "",
                referrer = "",
                userAgent = "",
                prompt = true
            };
            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync("http://127.0.0.1:45732/api/download", content);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
