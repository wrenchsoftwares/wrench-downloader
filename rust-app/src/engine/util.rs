use crate::model::{BrowserCookie, DownloadItem};
use std::path::{Path, PathBuf};
use url::Url;

pub const CHROME_UA: &str = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

pub fn sanitize_filename(name: &str) -> String {
    let invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    let mut s: String = name.chars().map(|c| if invalid.contains(&c) { '_' } else { c }).collect();
    s = s.trim().trim_end_matches('.').to_string();
    if s.is_empty() { "download".into() } else { s }
}

pub fn extension_from_url(url: &str) -> String {
    Url::parse(url).ok()
        .and_then(|u| {
            let path = u.path().to_ascii_lowercase();
            let name = path.rsplit('/').next().unwrap_or("");
            let ext = name.rsplit_once('.').map(|(_, e)| e.to_string())?;
            if ext.len() <= 6 && ext.chars().all(|c| c.is_ascii_alphanumeric()) { Some(ext) } else { None }
        })
        .unwrap_or_default()
}

pub fn filename_from_url(url: &str) -> Option<String> {
    let u = Url::parse(url).ok()?;
    let name = u.path().rsplit('/').next().unwrap_or("");
    if name.is_empty() { return None; }
    let decoded = percent_decode(name);
    if decoded.contains('.') { Some(decoded) } else { None }
}

fn percent_decode(s: &str) -> String {
    let bytes = s.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' && i + 2 < bytes.len() {
            if let (Some(a), Some(b)) = (hex_val(bytes[i + 1]), hex_val(bytes[i + 2])) {
                out.push(a * 16 + b);
                i += 3;
                continue;
            }
        }
        out.push(bytes[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

fn hex_val(b: u8) -> Option<u8> {
    match b {
        b'0'..=b'9' => Some(b - b'0'),
        b'a'..=b'f' => Some(b - b'a' + 10),
        b'A'..=b'F' => Some(b - b'A' + 10),
        _ => None,
    }
}

pub fn unique_path(dir: &Path, filename: &str) -> PathBuf {
    let path = dir.join(filename);
    if !path.exists() { return path; }
    let (stem, ext) = match filename.rsplit_once('.') {
        Some((s, e)) => (s.to_string(), format!(".{}", e)),
        None => (filename.to_string(), String::new()),
    };
    for n in 1..1000 {
        let candidate = dir.join(format!("{} ({}){}", stem, n, ext));
        if !candidate.exists() { return candidate; }
    }
    dir.join(format!("{}-{}", uuid::Uuid::new_v4(), filename))
}

pub fn build_cookie_header(cookies: Option<&Vec<BrowserCookie>>, url: &str) -> String {
    let Some(cookies) = cookies else { return String::new() };
    let Ok(parsed) = Url::parse(url) else { return String::new() };
    let host = parsed.host_str().unwrap_or("");
    let path = parsed.path();
    let secure = parsed.scheme() == "https";
    let now = chrono::Utc::now().timestamp();
    let mut parts = Vec::new();
    for c in cookies {
        if c.expiry > 0 && c.expiry < now { continue; }
        if c.secure && !secure { continue; }
        if !c.domain.is_empty() {
            let d = c.domain.trim_start_matches('.').to_ascii_lowercase();
            let h = host.to_ascii_lowercase();
            if h != d && !h.ends_with(&format!(".{}", d)) { continue; }
        }
        if !c.path.is_empty() && !path.starts_with(&c.path) { continue; }
        parts.push(format!("{}={}", c.name, c.value));
    }
    parts.join("; ")
}

pub fn human_speed(bytes_per_sec: f64) -> String {
    if bytes_per_sec <= 0.0 { return "0 KB/s".into(); }
    if bytes_per_sec >= 1024.0 * 1024.0 {
        format!("{:.1} MB/s", bytes_per_sec / 1024.0 / 1024.0)
    } else {
        format!("{:.0} KB/s", bytes_per_sec / 1024.0)
    }
}

pub fn human_size(bytes: u64) -> String {
    if bytes >= 1024u64.pow(3) { format!("{:.2} GB", bytes as f64 / 1024f64.powi(3)) }
    else if bytes >= 1024u64.pow(2) { format!("{:.1} MB", bytes as f64 / 1024f64.powi(2)) }
    else if bytes >= 1024 { format!("{:.0} KB", bytes as f64 / 1024.0) }
    else { format!("{} B", bytes) }
}

pub fn human_duration(secs: f64) -> String {
    if !secs.is_finite() || secs < 0.0 { return "--".into(); }
    let s = secs as u64;
    if s < 60 { format!("{}s", s) }
    else if s < 3600 { format!("{}m {}s", s / 60, s % 60) }
    else { format!("{}h {}m", s / 3600, (s % 3600) / 60) }
}

pub fn is_transient_file(path: &Path) -> bool {
    let name = path.file_name().and_then(|n| n.to_str()).unwrap_or("").to_ascii_lowercase();
    [".part", ".ytdl", ".tmp", ".temp"].iter().any(|ext| name.ends_with(ext))
}

pub fn tool_path(name: &str, extra: &[&str]) -> String {
    for candidate in extra {
        let p = Path::new(candidate);
        if p.is_file() { return p.to_string_lossy().into_owned(); }
    }
    let localappdata = std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_default();
    let python_scripts = localappdata.join("Programs/Python/Python312/Scripts").join(name);
    if python_scripts.is_file() { return python_scripts.to_string_lossy().into_owned(); }
    if name == "ffmpeg" {
        let choco = Path::new(r"C:\ProgramData\chocolatey\bin\ffmpeg.exe");
        if choco.is_file() { return choco.to_string_lossy().into_owned(); }
    }
    name.to_string()
}

/// Download category folder, mirroring C# `GetDownloadCategory`.
pub fn category_for(item: &DownloadItem) -> &'static str {
    let source = if !item.url.is_empty() { &item.url } else { &item.title };
    let lower = source.to_ascii_lowercase();
    let ext = extension_from_url(&lower);
    if matches!(ext.as_str(), "m3u8" | "mpd" | "pls" | "m3u") { return "Playlists"; }
    if matches!(ext.as_str(), "mp3" | "m4a" | "flac" | "wav" | "aac" | "ogg" | "opus") || item.quality == "audio" { return "Audio"; }
    if matches!(ext.as_str(), "mp4" | "mkv" | "webm" | "avi" | "mov" | "ts" | "m4v" | "flv") { return "Videos"; }
    if matches!(ext.as_str(), "jpg" | "jpeg" | "png" | "gif" | "webp" | "bmp" | "svg") { return "Images"; }
    if matches!(ext.as_str(), "zip" | "rar" | "7z" | "tar" | "gz" | "bz2" | "xz" | "iso") { return "Archives"; }
    if matches!(ext.as_str(), "pdf" | "doc" | "docx" | "xls" | "xlsx" | "ppt" | "pptx" | "txt" | "rtf" | "csv") { return "Documents"; }
    if ext.is_empty() || lower.contains("/watch") || lower.contains("/video") { return "Videos"; }
    "Other"
}
