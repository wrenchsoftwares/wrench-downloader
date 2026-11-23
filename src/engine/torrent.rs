//! Torrent support via the bundled/system aria2c: metadata resolution,
//! file-selection picker and selective download.

use super::util;
use super::{update, DownloadStore, UiEvent};
use super::stream::run_process;
use crate::model::{DownloadItem, DownloadStatus, Settings};
use parking_lot::Mutex;
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, OnceLock};
use std::time::Duration;
use tokio::sync::mpsc::UnboundedSender;

#[derive(Clone, Debug)]
pub struct TorrentFile {
    pub path: String,
    pub length: u64,
}

#[derive(Clone)]
struct Session {
    magnet: String,
    files: Vec<TorrentFile>,
}

static SESSIONS: OnceLock<Mutex<HashMap<String, Session>>> = OnceLock::new();
fn sessions() -> &'static Mutex<HashMap<String, Session>> {
    SESSIONS.get_or_init(|| Mutex::new(HashMap::new()))
}

// ---------------------------------------------------------------------------
// Minimal bencode reader for torrent metadata
// ---------------------------------------------------------------------------

#[derive(Clone, Debug)]
enum BValue {
    Int(i64),
    Bytes(Vec<u8>),
    List(Vec<BValue>),
    Dict(Vec<(Vec<u8>, BValue)>),
}

struct BReader<'a> { data: &'a [u8], pos: usize }
impl<'a> BReader<'a> {
    fn new(data: &'a [u8]) -> Self { Self { data, pos: 0 } }
    fn peek(&self) -> Option<u8> { self.data.get(self.pos).copied() }
    fn value(&mut self) -> anyhow::Result<BValue> {
        match self.peek() {
            Some(b'i') => {
                self.pos += 1;
                let end = self.data[self.pos..].iter().position(|&b| b == b'e').ok_or_else(|| anyhow::anyhow!("bad bencode int"))? + self.pos;
                let n = std::str::from_utf8(&self.data[self.pos..end])?.parse()?;
                self.pos = end + 1;
                Ok(BValue::Int(n))
            }
            Some(b'l') => {
                self.pos += 1;
                let mut items = Vec::new();
                while self.peek() != Some(b'e') { items.push(self.value()?); }
                self.pos += 1;
                Ok(BValue::List(items))
            }
            Some(b'd') => {
                self.pos += 1;
                let mut pairs = Vec::new();
                while self.peek() != Some(b'e') {
                    let key = self.bytes()?;
                    let value = self.value()?;
                    pairs.push((key, value));
                }
                self.pos += 1;
                Ok(BValue::Dict(pairs))
            }
            Some(b'0'..=b'9') => Ok(BValue::Bytes(self.bytes()?)),
            _ => anyhow::bail!("bad bencode value at {}", self.pos),
        }
    }
    fn bytes(&mut self) -> anyhow::Result<Vec<u8>> {
        let colon = self.data[self.pos..].iter().position(|&b| b == b':').ok_or_else(|| anyhow::anyhow!("bad bencode string"))? + self.pos;
        let len: usize = std::str::from_utf8(&self.data[self.pos..colon])?.parse()?;
        self.pos = colon + 1;
        let end = self.pos + len;
        if end > self.data.len() { anyhow::bail!("bencode string out of bounds"); }
        let out = self.data[self.pos..end].to_vec();
        self.pos = end;
        Ok(out)
    }
}

fn dict_get<'a>(v: &'a BValue, key: &str) -> Option<&'a BValue> {
    match v {
        BValue::Dict(pairs) => pairs.iter().find(|(k, _)| k.as_slice() == key.as_bytes()).map(|(_, v)| v),
        _ => None,
    }
}

fn text(v: Option<&BValue>) -> String {
    match v { Some(BValue::Bytes(b)) => String::from_utf8_lossy(b).into_owned(), _ => String::new() }
}

fn parse_torrent(path: &Path) -> anyhow::Result<(String, Vec<TorrentFile>)> {
    let data = std::fs::read(path)?;
    let root = BReader::new(&data).value()?;
    let info = dict_get(&root, "info").ok_or_else(|| anyhow::anyhow!("torrent has no info dictionary"))?;
    let name = text(dict_get(info, "name"));
    let mut files = Vec::new();
    if let Some(BValue::List(list)) = dict_get(info, "files") {
        for entry in list {
            let length = match dict_get(entry, "length") { Some(BValue::Int(n)) => *n as u64, _ => 0 };
            let parts = match dict_get(entry, "path") {
                Some(BValue::List(items)) => items.iter().map(|v| text(Some(v))).collect::<Vec<_>>(),
                _ => Vec::new(),
            };
            files.push(TorrentFile { path: parts.join("/"), length });
        }
    } else if let Some(BValue::Int(n)) = dict_get(info, "length") {
        files.push(TorrentFile { path: name.clone(), length: *n as u64 });
    }
    Ok((name, files))
}

// ---------------------------------------------------------------------------
// Download flow
// ---------------------------------------------------------------------------

fn aria_bin() -> String { util::tool_path("aria2c.exe", &[]) }

fn parts_dir(_item: &DownloadItem) -> PathBuf {
    let root = crate::paths::Paths::get().parts().join("torrents");
    let dir = root.join(format!("{}", uuid::Uuid::new_v4().simple()));
    let _ = std::fs::create_dir_all(&dir);
    dir
}

/// Returns `Ok(String::new())` when the download is waiting for the file picker.
pub async fn download(
    store: &DownloadStore,
    _settings: &Arc<Mutex<Settings>>,
    ui: &UnboundedSender<UiEvent>,
    id: &str,
    item: DownloadItem,
) -> anyhow::Result<String> {
    let dir = parts_dir(&item);
    let magnet = if item.torrent_magnet.is_empty() { item.url.clone() } else { item.torrent_magnet.clone() };
    let magnet = magnet.trim().to_string();
    let token = item.token.clone();
    update(store, ui, id, |i| i.status_text = "Resolving torrent metadata...".into());

    let torrent_file = dir.join("metadata.torrent");
    let args = vec![
        "--dir".into(), dir.to_string_lossy().into_owned(),
        "--out".into(), "metadata.torrent".into(),
        "--bt-metadata-only=true".into(), "--bt-save-metadata=true".into(),
        "--console-log-level=error".into(), "--summary-interval=0".into(),
        "--quiet=true".into(), magnet.clone(),
    ];
    let _ = run_process(&aria_bin(), &args, Duration::from_secs(120), Some(&token), None).await;
    if !torrent_file.exists() {
        anyhow::bail!("could not resolve torrent metadata");
    }
    let (name, files) = parse_torrent(&torrent_file)?;
    sessions().lock().insert(id.to_string(), Session { magnet, files: files.clone() });
    update(store, ui, id, |i| { i.title = name.clone(); i.status_text = "Waiting for file selection".into(); });
    let _ = ui.send(UiEvent::TorrentFiles {
        id: id.to_string(),
        name,
        files: files.iter().map(|f| (f.path.clone(), f.length)).collect(),
    });
    Ok(String::new())
}

pub fn confirm_files(
    store: &DownloadStore,
    settings_ref: &Arc<Mutex<Settings>>,
    ui: &UnboundedSender<UiEvent>,
    id: &str,
    selected: Vec<String>,
) {
    let Some(session) = sessions().lock().get(id).cloned() else { return };
    let Some(item) = store.find(id) else { return };
    let item_snapshot = item.lock().clone();
    if selected.is_empty() {
        update(store, ui, id, |i| { i.status = DownloadStatus::Paused; i.status_text = "No files selected".into(); });
        return;
    }
    let mut indices = Vec::new();
    for (i, f) in session.files.iter().enumerate() {
        if selected.contains(&f.path) { indices.push((i + 1).to_string()); }
    }
    let settings = settings_ref.lock().clone();
    let folder = if item_snapshot.target_folder.is_empty() { settings.download_folder.clone() } else { item_snapshot.target_folder.clone() };
    let _ = std::fs::create_dir_all(&folder);
    let mut args = vec![
        "--dir".into(), folder.clone(),
        "--select-file".into(), indices.join(","),
        "--continue=true".into(), "--console-log-level=error".into(),
        "--summary-interval=0".into(), "--seed-time=0".into(),
    ];
    if settings.maximum_download_rate_kbps > 0 {
        args.push("--max-download-limit".into());
        args.push(format!("{}K", settings.maximum_download_rate_kbps));
    }
    args.push(session.magnet.clone());

    let token = item_snapshot.token.clone();
    let store2 = store.clone();
    let ui2 = ui.clone();
    let id2 = id.to_string();
    let dir2 = folder.clone();
    tokio::spawn(async move {
        update(&store2, &ui2, &id2, |i| {
            i.status = DownloadStatus::Downloading;
            i.status_text = "Downloading torrent...".into();
            i.save_path = dir2.clone();
        });
        let result = run_process(&aria_bin(), &args, Duration::from_secs(86400), Some(&token), None).await;
        match result {
            Ok(output) if output.status.success() => {
                update(&store2, &ui2, &id2, |i| {
                    i.status = DownloadStatus::Completed;
                    i.progress = 100.0;
                    i.status_text = format!("Saved to {}", dir2);
                    i.save_path = dir2;
                });
                let _ = ui2.send(UiEvent::Completed(id2));
            }
            Ok(output) => {
                let msg = String::from_utf8_lossy(&output.stderr).into_owned();
                update(&store2, &ui2, &id2, |i| {
                    i.status = DownloadStatus::Failed;
                    i.status_text = format!("Error: {}", msg.trim());
                });
            }
            Err(e) => {
                update(&store2, &ui2, &id2, |i| { i.status = DownloadStatus::Failed; i.status_text = format!("Error: {}", e); });
            }
        }
    });
}
