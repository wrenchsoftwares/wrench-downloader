use crate::paths::Paths;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::Path;
use tokio_util::sync::CancellationToken;

// ---------------------------------------------------------------------------
// Download item
// ---------------------------------------------------------------------------

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum DownloadStatus {
    #[default]
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
}

impl DownloadStatus {
    pub fn is_active(self) -> bool { matches!(self, Self::Downloading | Self::Queued) }
    pub fn can_toggle_pause(self) -> bool { self != Self::Completed }
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct StreamSegment {
    pub url: String,
    pub range: String,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct BrowserCookie {
    pub name: String,
    pub value: String,
    pub domain: String,
    pub path: String,
    pub secure: bool,
    pub expiry: i64,
}

fn new_id() -> String { uuid::Uuid::new_v4().to_string() }
fn default_queue_name() -> String { "Main queue".to_string() }
fn default_title() -> String { "Download".to_string() }
fn default_status_text() -> String { "Waiting in queue".to_string() }
fn default_speed() -> String { "0 KB/s".to_string() }
fn default_size() -> String { "--".to_string() }
fn default_token() -> std::sync::Arc<CancellationToken> { std::sync::Arc::new(CancellationToken::new()) }

/// Faithful Rust mirror of C# `DownloadItem` plus UI-only fields.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct DownloadItem {
    pub id: String,
    pub title: String,
    pub url: String,
    pub quality: String,
    pub page_url: String,
    pub referrer: String,
    pub user_agent: String,
    pub cookies: Option<Vec<BrowserCookie>>,
    pub po_token: String,
    pub audio_url: String,
    pub audio_referrer: String,
    pub is_sabr: bool,
    pub itag: i32,
    pub expected_bytes: i64,
    pub init_range: String,
    pub audio_init_range: String,
    pub segments: Option<Vec<StreamSegment>>,
    pub audio_segments: Option<Vec<StreamSegment>>,
    pub is_torrent: bool,
    pub torrent_magnet: String,
    pub torrent_files: Vec<String>,
    pub stream_headers: HashMap<String, String>,
    pub audio_stream_headers: HashMap<String, String>,
    pub requires_fresh_browser_capture: bool,
    pub target_folder: String,
    pub queue_name: String,
    pub download_playlist: bool,
    pub wait_for_queue_start: bool,
    pub scheduled_at: Option<String>,
    pub save_path: String,
    pub created_at: String,
    pub progress: f64,
    pub status: DownloadStatus,
    pub status_text: String,
    pub speed_text: String,
    pub size_text: String,

    // Runtime-only state (never persisted).
    #[serde(skip)]
    pub token: std::sync::Arc<CancellationToken>,
    #[serde(skip)]
    pub selected: bool,
    #[serde(skip)]
    pub eta_text: String,
    #[serde(skip)]
    pub retry_count: u32,
}

impl Default for DownloadItem {
    fn default() -> Self {
        Self {
            id: new_id(),
            title: default_title(),
            url: String::new(),
            quality: String::new(),
            page_url: String::new(),
            referrer: String::new(),
            user_agent: String::new(),
            cookies: None,
            po_token: String::new(),
            audio_url: String::new(),
            audio_referrer: String::new(),
            is_sabr: false,
            itag: 0,
            expected_bytes: 0,
            init_range: String::new(),
            audio_init_range: String::new(),
            segments: None,
            audio_segments: None,
            is_torrent: false,
            torrent_magnet: String::new(),
            torrent_files: Vec::new(),
            stream_headers: HashMap::new(),
            audio_stream_headers: HashMap::new(),
            requires_fresh_browser_capture: false,
            target_folder: String::new(),
            queue_name: default_queue_name(),
            download_playlist: false,
            wait_for_queue_start: false,
            scheduled_at: None,
            save_path: String::new(),
            created_at: chrono::Local::now().to_rfc3339(),
            progress: 0.0,
            status: DownloadStatus::Queued,
            status_text: default_status_text(),
            speed_text: default_speed(),
            size_text: default_size(),
            token: default_token(),
            selected: false,
            eta_text: "--".to_string(),
            retry_count: 0,
        }
    }
}

impl DownloadItem {
    pub fn new(url: impl Into<String>, title: impl Into<String>) -> Self {
        Self { url: url.into(), title: title.into(), ..Default::default() }
    }

    pub fn reset_token(&mut self) -> CancellationToken {
        self.token = std::sync::Arc::new(CancellationToken::new());
        (*self.token).clone()
    }

    pub fn action_glyph(&self) -> &'static str {
        if self.status.is_active() { "⏸" } else { "▶" }
    }

    pub fn added_text(&self) -> String {
        chrono::DateTime::parse_from_rfc3339(&self.created_at)
            .map(|d| format!("Added {}", d.format("%Y-%m-%d %H:%M")))
            .unwrap_or_else(|_| "Added".to_string())
    }
}

// ---------------------------------------------------------------------------
// Settings
// ---------------------------------------------------------------------------

fn default_download_folder() -> String {
    std::env::var_os("USERPROFILE")
        .or_else(|| std::env::var_os("HOME"))
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."))
        .join("Downloads")
        .to_string_lossy().into_owned()
}

use std::path::PathBuf;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct Settings {
    pub download_folder: String,
    pub concurrent_fragments: i32,
    pub default_quality: String,
    pub show_download_dialog: bool,
    pub close_to_tray: bool,
    pub maximum_concurrent_downloads: i32,
    pub language: String,
    pub maximum_download_rate_kbps: i32,
    pub clipboard_monitor_enabled: bool,
    pub organize_downloads_by_type: bool,
    pub app_theme: String,
    pub show_complete_dialog: bool,
    pub play_sound_on_complete: bool,
    pub confirm_on_delete_file: bool,
    pub auto_resume_interrupted: bool,
    pub auto_retry_count: i32,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            download_folder: default_download_folder(),
            concurrent_fragments: 8,
            default_quality: "best".into(),
            show_download_dialog: true,
            close_to_tray: true,
            maximum_concurrent_downloads: 1,
            language: "en".into(),
            maximum_download_rate_kbps: 0,
            clipboard_monitor_enabled: false,
            organize_downloads_by_type: false,
            app_theme: "system".into(),
            show_complete_dialog: false,
            play_sound_on_complete: true,
            confirm_on_delete_file: true,
            auto_resume_interrupted: false,
            auto_retry_count: 2,
        }
    }
}

impl Settings {
    pub fn load() -> Self {
        let path = Paths::get().settings();
        let mut s = std::fs::read_to_string(&path)
            .ok()
            .and_then(|t| serde_json::from_str::<Settings>(&t).ok())
            .unwrap_or_default();
        s.clamp();
        s
    }

    pub fn save(&self) {
        let path = Paths::get().settings();
        if let Some(p) = path.parent() { let _ = std::fs::create_dir_all(p); }
        if let Ok(text) = serde_json::to_string_pretty(self) {
            let _ = std::fs::write(path, text);
        }
    }

    fn clamp(&mut self) {
        self.concurrent_fragments = self.concurrent_fragments.clamp(1, 32);
        self.maximum_concurrent_downloads = self.maximum_concurrent_downloads.clamp(1, 8);
        self.maximum_download_rate_kbps = self.maximum_download_rate_kbps.clamp(0, 1_000_000);
        self.auto_retry_count = self.auto_retry_count.clamp(0, 10);
        if !["en", "fr", "ar"].contains(&self.language.as_str()) { self.language = "en".into(); }
        if !["system", "light", "dark"].contains(&self.app_theme.as_str()) { self.app_theme = "system".into(); }
        if !Path::new(&self.download_folder).is_dir() {
            self.download_folder = default_download_folder();
        }
    }
}

// ---------------------------------------------------------------------------
// History with credential scrubbing
// ---------------------------------------------------------------------------

const SENSITIVE_KEYS: &[&str] = &[
    "sig", "auth", "authorization", "credential", "cookie", "session",
    "policy", "key", "expires", "expire", "pot", "sparams", "lsig",
];

fn is_sensitive_param(key: &str) -> bool {
    let k = key.to_lowercase();
    k.contains("token") || k.contains("signature") || SENSITIVE_KEYS.contains(&k.as_str())
}

fn safe_page_url(url: &str) -> String {
    let Ok(mut parsed) = url::Url::parse(url) else { return String::new() };
    let pairs: Vec<(String, String)> = parsed.query_pairs()
        .filter(|(k, _)| !is_sensitive_param(k.as_ref()))
        .map(|(k, v)| (k.into_owned(), v.into_owned()))
        .collect();
    parsed.query_pairs().for_each(drop);
    parsed.set_query(None);
    if !pairs.is_empty() {
        let mut q = parsed.query_pairs_mut();
        for (k, v) in pairs { q.append_pair(&k, &v); }
    }
    parsed.to_string()
}

fn url_has_sensitive_query(url: &str) -> bool {
    url::Url::parse(url)
        .map(|u| u.query_pairs().any(|(k, _)| is_sensitive_param(k.as_ref())))
        .unwrap_or(false)
}

impl DownloadItem {
    pub fn has_transient_credentials(&self) -> bool {
        let sensitive_header = |m: &HashMap<String, String>| {
            m.keys().any(|k| {
                let k = k.to_ascii_lowercase();
                k == "cookie" || k == "authorization" || k == "proxy-authorization"
            })
        };
        self.cookies.as_ref().is_some_and(|c| !c.is_empty())
            || !self.po_token.trim().is_empty()
            || self.is_sabr
            || sensitive_header(&self.stream_headers)
            || sensitive_header(&self.audio_stream_headers)
    }

    fn any_sensitive_url(&self) -> bool {
        [&self.url, &self.audio_url, &self.page_url, &self.referrer, &self.audio_referrer]
            .iter().any(|u| url_has_sensitive_query(u))
            || self.segments.iter().flatten().any(|s| url_has_sensitive_query(&s.url))
            || self.audio_segments.iter().flatten().any(|s| url_has_sensitive_query(&s.url))
    }

    /// Produce a credential-free snapshot for history persistence.
    pub fn history_snapshot(&self) -> Self {
        let mut out = self.clone();
        let must_recapture = self.requires_fresh_browser_capture
            || self.has_transient_credentials()
            || self.any_sensitive_url();

        out.cookies = None;
        out.po_token.clear();
        out.stream_headers.clear();
        out.audio_stream_headers.clear();

        if must_recapture {
            out.url.clear();
            out.audio_url.clear();
            out.referrer.clear();
            out.audio_referrer.clear();
            out.user_agent.clear();
            out.segments = None;
            out.audio_segments = None;
            out.page_url = safe_page_url(&self.page_url);
            out.is_sabr = false;
            out.requires_fresh_browser_capture = self.status != DownloadStatus::Completed && !self.is_torrent;
            if out.requires_fresh_browser_capture {
                out.status = DownloadStatus::Paused;
                out.wait_for_queue_start = false;
                out.status_text = "Please replay the video in your browser to refresh this link.".into();
            }
        }
        out
    }
}

pub fn load_history() -> Vec<DownloadItem> {
    let path = Paths::get().history();
    let Ok(text) = std::fs::read_to_string(&path) else { return Vec::new() };
    let mut items: Vec<DownloadItem> = serde_json::from_str(&text).unwrap_or_default();
    for item in &mut items {
        let scrubbed = item.history_snapshot();
        *item = scrubbed;
        if item.scheduled_at.take().is_some() { item.wait_for_queue_start = true; }
        if matches!(item.status, DownloadStatus::Downloading | DownloadStatus::Queued) {
            item.status = DownloadStatus::Paused;
            item.status_text = "Interrupted".into();
        }
    }
    items.truncate(100);
    save_history(&items);
    items
}

pub fn save_history(items: &[DownloadItem]) {
    let path = Paths::get().history();
    if let Some(p) = path.parent() { let _ = std::fs::create_dir_all(p); }
    let snapshots: Vec<_> = items.iter().take(100).map(|i| i.history_snapshot()).collect();
    if let Ok(text) = serde_json::to_string_pretty(&snapshots) {
        let _ = std::fs::write(path, text);
    }
}
