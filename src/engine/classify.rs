//! Structural URL classification — no per-site branches or site lists.
use crate::model::DownloadItem;
use url::Url;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum UrlKind {
    Direct,
    Manifest,
    Page,
    Playback,
    SabrRedirector,
    Torrent,
}

fn lower(url: &str) -> String { url.to_ascii_lowercase() }

fn query_keys(u: &Url) -> std::collections::HashSet<String> {
    u.query_pairs().map(|(k, _)| k.to_ascii_lowercase()).collect()
}

fn has_manifest_marker(l: &str) -> bool {
    [".m3u8", ".mpd", ".m3u", "hls_playlist", "hls_variant", "/manifest/", "/master", "/playlist"]
        .iter().any(|m| l.contains(m))
}

pub fn classify(url: &str, quality: &str) -> UrlKind {
    let l = lower(url);
    if l.starts_with("magnet:") { return UrlKind::Torrent; }
    if l.starts_with("blob:") { return UrlKind::Manifest; }
    if is_sabr_redirector(url) { return UrlKind::SabrRedirector; }
    if is_captured_playback(url) { return UrlKind::Playback; }
    if has_manifest_marker(&l) || l.contains("videoplayback") {
        return UrlKind::Manifest;
    }
    if quality == "file" { return UrlKind::Direct; }

    if let Ok(u) = Url::parse(url) {
        let keys = query_keys(&u);
        for k in ["slug", "file", "filename"] {
            if keys.contains(k) {
                if let Some((_, v)) = u.query_pairs().find(|(key, _)| key.eq_ignore_ascii_case(k)) {
                    let v = v.to_ascii_lowercase();
                    if v.contains('.') && !v.ends_with(".html") && !v.ends_with(".php") {
                        return UrlKind::Direct;
                    }
                }
            }
        }
    }

    let Ok(u) = Url::parse(url) else { return UrlKind::Direct };
    let path = u.path().to_ascii_lowercase();
    let name = path.rsplit('/').next().unwrap_or("");
    // No filename extension (e.g. /watch?v=...) or an HTML-like page means
    // this is a video page, not a direct file. Route it to yt-dlp.
    if name.is_empty()
        || !name.contains('.')
        || [".html", ".htm", ".php", ".asp", ".aspx"].iter().any(|e| name.ends_with(e))
    {
        return UrlKind::Page;
    }
    UrlKind::Direct
}

pub fn is_manifest_url(url: &str) -> bool {
    let l = lower(url);
    has_manifest_marker(&l) || l.contains("videoplayback")
}

pub fn is_captured_playback(url: &str) -> bool {
    let Ok(u) = Url::parse(url) else { return false };
    let path = u.path();
    if !path.ends_with("/videoplayback") { return false; }
    let keys = query_keys(&u);
    keys.contains("itag") && !keys.contains("aitags")
}

pub fn is_sabr_redirector(url: &str) -> bool {
    let Ok(u) = Url::parse(url) else { return false };
    u.path().ends_with("/videoplayback") && query_keys(&u).contains("aitags")
}

pub fn is_extension_resolved(item: &DownloadItem) -> bool {
    is_captured_playback(&item.url) && !matches!(item.quality.as_str(), "file" | "audio")
}

pub fn is_progressive_itag(itag: i32) -> bool {
    matches!(itag, 17 | 18 | 36 | 43 | 22)
}

/// Signed URLs die: detect expiry windows with a 60-second grace period.
pub fn signed_link_expired(url: &str) -> bool {
    let Ok(u) = Url::parse(url) else { return false };
    let now = chrono::Utc::now().timestamp();
    for (k, v) in u.query_pairs() {
        let k = k.to_ascii_lowercase();
        if k == "expire" || k == "expires" || k == "e" {
            if let Ok(ts) = v.parse::<i64>() {
                let candidate = if ts > 10_000_000_000 { ts / 1000 } else { ts };
                if candidate > 0 && candidate + 60 < now { return true; }
            }
        }
    }
    false
}

pub fn quality_selector(quality: &str) -> String {
    match quality.trim().to_ascii_lowercase().as_str() {
        "" | "best" | "auto" => "bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/bv*+ba/b".into(),
        "audio" => "bestaudio/best".into(),
        other => {
            let h: i32 = other.chars().take_while(|c| c.is_ascii_digit()).collect::<String>().parse().unwrap_or(1080);
            let w = (h * 16) / 9;
            format!("bv*[height<={h}]+ba/b[height<={h}]/bv*[height<={w}][width<={h}]+ba/b[height<={w}][width<={h}]/bv*+ba/b")
        }
    }
}
