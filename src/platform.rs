//! Windows/native integration: protocol registration and clipboard monitoring.

use crate::engine::UiEvent;
use crate::model::{DownloadItem, Settings};
use parking_lot::Mutex;
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::mpsc::UnboundedSender;

pub fn register_magnet_protocol() {
    #[cfg(target_os = "windows")]
    {
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
