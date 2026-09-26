using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jint;

namespace WrenchDownloader;

// DOM stubs exposed to the challenge script. Member names are intentionally
// lowercase to match the JavaScript property access (document.cookie, ...).
public sealed class ChallengeLocation
{
    public void reload(bool _) { }
}

public sealed class ChallengeDocument
{
    private string _cookie = "";
    public string cookie { get => _cookie; set => _cookie = value ?? ""; }
    public ChallengeLocation location { get; } = new ChallengeLocation();
}

/// <summary>
/// Solves Pornhub-style JS challenge pages natively (no PhantomJS, no browser,
/// no Node needed). The challenge is pure arithmetic that sets a session
/// cookie; it is executed in a sandboxed Jint interpreter and the resulting
/// cookie is written to a Netscape cookie jar for yt-dlp (--cookies).
/// Returns the temp jar path, or null when there is no challenge / on failure.
/// </summary>
public static class PornhubChallengeSolver
{
    private static readonly HttpClient _http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        return client;
    }

    public static async Task<string?> CreateCookieFileAsync(string? pageUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pageUrl))
            return null;
        try
        {
            string html = await _http.GetStringAsync(pageUrl, cancellationToken);
            if (!HasChallenge(html))
                return null;
            string? cookie = Solve(html);
            if (string.IsNullOrWhiteSpace(cookie))
                return null;
            return WriteCookieFile(pageUrl, cookie);
        }
        catch
        {
            return null;
        }
    }

    public static void DeleteCookieFile(string? path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static bool HasChallenge(string html) =>
        html.Contains("onload=\"go()\"", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("onload='go()'", StringComparison.OrdinalIgnoreCase) ||
        (html.Contains("document.cookie", StringComparison.Ordinal) &&
         html.Contains("location.reload", StringComparison.Ordinal));

    private static string? Solve(string html)
    {
        try
        {
            var scriptMatch = Regex.Match(html,
                @"<script[^>]*>(?:<!--)?(.*?)(?://-->)?</script>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!scriptMatch.Success)
                return null;
            // The challenge script is the one defining go().
            string script = scriptMatch.Value.Contains("go()") ? scriptMatch.Groups[1].Value : "";
            if (string.IsNullOrWhiteSpace(script))
            {
                foreach (Match m in Regex.Matches(html,
                    @"<script[^>]*>(?:<!--)?(.*?)(?://-->)?</script>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase))
                {
                    if (m.Groups[1].Value.Contains("function go(") || m.Groups[1].Value.Contains("go()"))
                    {
                        script = m.Groups[1].Value;
                        break;
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(script) || !script.Contains("document.cookie"))
                return null;

            var document = new ChallengeDocument();
            var engine = new Engine(options =>
                options.TimeoutInterval(TimeSpan.FromSeconds(10)));
            engine.SetValue("document", document);
            engine.SetValue("window", document);
            engine.Execute(script);
            engine.Execute("go();");

            string raw = document.cookie.Split(';')[0].Trim();
            int eq = raw.IndexOf('=');
            if (eq <= 0 || eq == raw.Length - 1)
                return null;
            return raw;
        }
        catch
        {
            return null;
        }
    }

    private static string? WriteCookieFile(string pageUrl, string cookie)
    {
        try
        {
            var uri = new Uri(pageUrl);
            int eq = cookie.IndexOf('=');
            string name = cookie[..eq].Trim();
            string value = cookie[(eq + 1)..].Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value))
                return null;
            long expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
            string path = Path.Combine(Path.GetTempPath(), $"wd_challenge_{Guid.NewGuid():N}.txt");
            File.WriteAllText(path,
                "# Netscape HTTP Cookie File\n" +
                $".{uri.Host}\tTRUE\t/\tFALSE\t{expiry}\t{name}\t{value}\n");
            return path;
        }
        catch
        {
            return null;
        }
    }
}
