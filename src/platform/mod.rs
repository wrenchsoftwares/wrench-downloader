//! Native integration: protocol registration, clipboard monitoring,
//! single-instance handoff, and (release builds only) the system tray.

#[cfg(target_os = "windows")]
mod win32;
#[cfg(all(target_os = "windows", not(debug_assertions)))]
mod tray;

use crate::engine::UiEvent;
use crate::model::{DownloadItem, Settings};
use parking_lot::Mutex;
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::mpsc::UnboundedSender;

pub fn register_magnet_protocol() {
    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt as _;
        const CREATE_NO_WINDOW: u32 = 0x08000000;
        let exe = std::env::current_exe().unwrap_or_default();
        let exe = exe.to_string_lossy();
        let command = format!("\"{}\" \"%1\"", exe);
        let calls = [
            vec!["add", r"HKCU\Software\Classes\magnet", "/ve", "/d", "URL:Magnet Protocol", "/f"],
            vec!["add", r"HKCU\Software\Classes\magnet", "/v", "URL Protocol", "/d", "", "/f"],
            vec!["add", r"HKCU\Software\Classes\magnet\shell\open\command", "/ve", "/d", &command, "/f"],
        ];
        for args in calls {
            let _ = std::process::Command::new("reg.exe").args(args)
                .creation_flags(CREATE_NO_WINDOW)
                .stdout(std::process::Stdio::null())
                .stderr(std::process::Stdio::null())
                .status();
        }
    }
}

pub fn start_clipboard_monitor(ui: UnboundedSender<UiEvent>, settings: Arc<Mutex<Settings>>) {
    std::thread::spawn(move || {
        let mut clipboard = match arboard::Clipboard::new() {
            Ok(c) => c,
            Err(_) => return,
        };
        let mut last = String::new();
        loop {
            std::thread::sleep(Duration::from_millis(1200));
            if !settings.lock().clipboard_monitor_enabled {
                continue;
            }
            let text = clipboard.get_text().unwrap_or_default();
            let text = text.trim().to_string();
            if text.is_empty() || text == last { continue; }
            last = text.clone();
            if !text.to_ascii_lowercase().starts_with("http://")
                && !text.to_ascii_lowercase().starts_with("https://")
                && !text.to_ascii_lowercase().starts_with("magnet:") {
                continue;
            }
            let quality = settings.lock().default_quality.clone();
            let mut item = DownloadItem::new(&text, title_from_url(&text));
            item.quality = quality;
            item.status_text = "Waiting in queue".into();
            if text.to_ascii_lowercase().starts_with("magnet:") { item.is_torrent = true; }
            let _ = ui.send(UiEvent::BridgeDownload { item, prompt: true });
        }
    });
}

fn title_from_url(url: &str) -> String {
    url::Url::parse(url).ok()
        .and_then(|u| u.path().rsplit('/').find(|s| !s.is_empty()).map(|s| s.to_string()))
        .filter(|s| s.contains('.'))
        .unwrap_or_else(|| "Video_Download".into())
}

/// Single-instance gate. A second launch focuses the existing window instead.
pub fn is_first_instance() -> bool {
    #[cfg(target_os = "windows")]
    { win32::is_first_instance() }
    #[cfg(not(target_os = "windows"))]
    { true }
}

pub fn focus_existing() -> bool {
    #[cfg(target_os = "windows")]
    { win32::focus_existing() }
    #[cfg(not(target_os = "windows"))]
    { false }
}

/// Start the release-only system tray. Debug builds intentionally have no tray.
#[cfg_attr(debug_assertions, allow(dead_code))]
pub fn start_tray(ui: UnboundedSender<UiEvent>, settings: Arc<Mutex<Settings>>) {
    #[cfg(all(target_os = "windows", not(debug_assertions)))]
    {
        let _ = tray::TRAY_TX.set(ui);
        std::thread::spawn(move || unsafe { tray::run_tray(settings) });
    }
    #[cfg(not(all(target_os = "windows", not(debug_assertions))))]
    { let _ = (ui, settings); }
}
