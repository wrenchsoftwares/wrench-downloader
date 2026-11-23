pub mod classify;
pub mod stream;
pub mod torrent;
pub mod util;

pub use classify::UrlKind;

use crate::bridge::BridgeRequest;
use crate::model::{DownloadItem, DownloadStatus, Settings};
use parking_lot::{Mutex, RwLock};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, Instant};
use tokio::sync::mpsc::{UnboundedReceiver, UnboundedSender};

pub type SharedItem = Arc<Mutex<DownloadItem>>;

/// Engine → UI notifications. Some payloads are kept for the wire protocol
/// and some variants are only constructed by the release-only tray module.
#[derive(Clone, Debug)]
#[allow(dead_code)]
pub enum UiEvent {
    BridgeDownload { item: DownloadItem, prompt: bool },
    DownloadsChanged,
    ItemUpdated(String),
    Status(String),
    QueuePaused(bool),
    Completed(String),
    Failed(String),
    TorrentFiles {
        id: String,
        name: String,
        files: Vec<(String, u64)>,
    },
    TraySettings,
    TrayExit,
}

#[derive(Debug)]
pub enum EngineCommand {
    Start(String),
    Pause(String),
    Resume(String),
    Remove(String),
    SetQueuePaused(bool),
    TorrentFiles { id: String, files: Vec<String> },
}

#[derive(Clone)]
pub struct EngineHandle {
    pub tx: UnboundedSender<EngineCommand>,
}

impl EngineHandle {
    pub fn start(&self, id: &str) { let _ = self.tx.send(EngineCommand::Start(id.to_string())); }
    pub fn pause(&self, id: &str) { let _ = self.tx.send(EngineCommand::Pause(id.to_string())); }
    pub fn resume(&self, id: &str) { let _ = self.tx.send(EngineCommand::Resume(id.to_string())); }
    pub fn set_queue_paused(&self, paused: bool) { let _ = self.tx.send(EngineCommand::SetQueuePaused(paused)); }
}

#[derive(Clone, Default)]
pub struct DownloadStore {
    items: Arc<RwLock<Vec<SharedItem>>>,
}

impl DownloadStore {
    pub fn add(&self, item: DownloadItem, at_top: bool) -> SharedItem {
        let shared = Arc::new(Mutex::new(item));
        let mut list = self.items.write();
        if at_top { list.insert(0, shared.clone()); } else { list.push(shared.clone()); }
        shared
    }
    pub fn find(&self, id: &str) -> Option<SharedItem> {
        self.items.read().iter().find(|i| i.lock().id == id).cloned()
    }
    pub fn remove(&self, id: &str) {
        let mut list = self.items.write();
        if let Some(pos) = list.iter().position(|i| i.lock().id == id) { list.remove(pos); }
    }
    pub fn list(&self) -> Vec<SharedItem> { self.items.read().clone() }
}

fn update(store: &DownloadStore, ui: &UnboundedSender<UiEvent>, id: &str, f: impl FnOnce(&mut DownloadItem)) {
    if let Some(item) = store.find(id) {
        { let mut guard = item.lock(); f(&mut guard); }
        let _ = ui.send(UiEvent::ItemUpdated(id.to_string()));
    }
}

pub fn spawn_engine(
    runtime: &tokio::runtime::Runtime,
    store: DownloadStore,
    settings: Arc<Mutex<Settings>>,
    ui_tx: UnboundedSender<UiEvent>,
    bridge_rx: UnboundedReceiver<BridgeRequest>,
) -> EngineHandle {
    let (cmd_tx, mut cmd_rx) = tokio::sync::mpsc::unbounded_channel::<EngineCommand>();
    let handle = EngineHandle { tx: cmd_tx };
    let runtime = runtime.handle().clone();

    let ui_bridge = ui_tx.clone();
    let rt_bridge = runtime.clone();
    rt_bridge.spawn(async move {
        let mut rx = bridge_rx;
        while let Some(req) = rx.recv().await {
            let _ = ui_bridge.send(UiEvent::BridgeDownload { item: req.item, prompt: req.prompt });
        }
    });

    let queue_flag = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
    {
        let pump_store = store.clone();
        let pump_settings = settings.clone();
        let pump_ui = ui_tx.clone();
        let pump_rt = runtime.clone();
        let pump_rt_spawn = runtime.clone();
        let pump_flag = queue_flag.clone();
        pump_rt_spawn.spawn(async move {
            let mut ticker = tokio::time::interval(Duration::from_secs(2));
            loop {
                ticker.tick().await;
                if pump_flag.load(std::sync::atomic::Ordering::Relaxed) { continue; }
                let max = pump_settings.lock().maximum_concurrent_downloads.max(1) as usize;
                let list = pump_store.list();
                let active = list.iter().filter(|i| i.lock().status == DownloadStatus::Downloading).count();
                if active >= max { continue; }
                for item in list.iter().filter(|i| i.lock().status == DownloadStatus::Queued) {
                    let launch = {
                        let g = item.lock();
                        g.queue_name == "Main queue" && !g.wait_for_queue_start && !g.token.is_cancelled()
                    };
                    if launch {
                        let id = item.lock().id.clone();
                        let s = pump_store.clone(); let st = pump_settings.clone(); let u = pump_ui.clone();
                        let rt = pump_rt.clone();
                        rt.spawn(async move { run_download(s, st, u, id).await; });
                        if active + 1 >= max { break; }
                    }
                }
            }
        });
    }

    let rt_loop = runtime.clone();
    let rt_tasks = runtime.clone();
    let loop_flag = queue_flag.clone();
    rt_loop.spawn(async move {
        let mut queue_paused = false;
        while let Some(cmd) = cmd_rx.recv().await {
            match cmd {
                EngineCommand::Start(id) => {
                    let s = store.clone(); let st = settings.clone(); let ui = ui_tx.clone();
                    rt_tasks.spawn(async move { run_download(s, st, ui, id).await; });
                }
                EngineCommand::Pause(id) => {
                    if let Some(item) = store.find(&id) {
                        item.lock().token.cancel();
                        update(&store, &ui_tx, &id, |i| {
                            if i.status != DownloadStatus::Completed {
                                i.status = DownloadStatus::Paused;
                                i.status_text = "Paused by user".into();
                                i.speed_text = "Paused".into();
                                i.eta_text = "--".into();
                            }
                        });
                    }
                }
                EngineCommand::Resume(id) => {
                    update(&store, &ui_tx, &id, |i| {
                        i.reset_token();
                        i.status = DownloadStatus::Queued;
                        i.status_text = "Waiting in queue".into();
                        i.wait_for_queue_start = false;
                        i.retry_count = 0;
                    });
                    let _ = ui_tx.send(UiEvent::Status("Resuming download".into()));
                }
                EngineCommand::Remove(id) => {
                    if let Some(item) = store.find(&id) { item.lock().token.cancel(); }
                    store.remove(&id);
                    let _ = ui_tx.send(UiEvent::DownloadsChanged);
                }
                EngineCommand::SetQueuePaused(paused) => {
                    queue_paused = paused;
                    loop_flag.store(paused, std::sync::atomic::Ordering::Relaxed);
                    let _ = ui_tx.send(UiEvent::QueuePaused(paused));
                    let _ = ui_tx.send(UiEvent::Status(if paused { "Queue paused".into() } else { "Queue started".into() }));
                    if !paused {
                        for item in store.list() {
                            let g = item.lock();
                            if g.status == DownloadStatus::Paused && g.queue_name == "Main queue" && !g.wait_for_queue_start {
                                let id = g.id.clone();
                                drop(g);
                                update(&store, &ui_tx, &id, |i| {
                                    i.reset_token();
                                    i.status = DownloadStatus::Queued;
                                    i.status_text = "Waiting in queue".into();
                                });
                            }
                        }
                    }
                }
                EngineCommand::TorrentFiles { id, files } => {
                    torrent::confirm_files(&store, &settings, &ui_tx, &id, files);
                }
            }
            let _ = queue_paused;
        }
    });

    handle
}

async fn run_download(
    store: DownloadStore,
    settings: Arc<Mutex<Settings>>,
    ui: UnboundedSender<UiEvent>,
    id: String,
) {
    let Some(item) = store.find(&id) else { return };
    let snapshot = item.lock().clone();
    if snapshot.status == DownloadStatus::Completed { return; }

    let kind = classify::classify(&snapshot.url, &snapshot.quality);
    let token = snapshot.token.clone();
    if token.is_cancelled() { return; }

    update(&store, &ui, &id, |i| {
        i.status = DownloadStatus::Downloading;
        if i.status_text == "Waiting in queue" { i.status_text = "Connecting...".into(); }
    });

    let result = match kind {
        UrlKind::Torrent => torrent::download(&store, &settings, &ui, &id, snapshot).await,
        UrlKind::SabrRedirector | UrlKind::Playback => {
            let direct = if snapshot.is_sabr && snapshot.segments.as_ref().is_some_and(|s| !s.is_empty()) {
                stream::sabr_download(&store, &settings, &ui, &id, snapshot.clone()).await
            } else {
                stream::extension_resolved_download(&store, &settings, &ui, &id, snapshot.clone()).await
            };
            match direct {
                Ok(path) => Ok(path),
                Err(err) => {
                    let msg = err.to_string();
                    // A captured signed URL can die between capture and use.
                    // Fall back to the page URL with the same fresh cookies
                    // instead of failing immediately.
                    if !token.is_cancelled()
                        && !stream::requires_fresh_capture(&msg)
                        && !snapshot.page_url.is_empty()
                    {
                        update(&store, &ui, &id, |i| {
                            i.status_text = "Captured stream failed - retrying via page...".into();
                            i.progress = 0.0;
                        });
                        let mut fallback = snapshot.clone();
                        fallback.url = fallback.page_url.clone();
                        stream::ytdlp_download(&store, &settings, &ui, &id, fallback).await
                    } else {
                        Err(err)
                    }
                }
            }
        }
        UrlKind::Manifest | UrlKind::Page => {
            stream::ytdlp_download(&store, &settings, &ui, &id, snapshot).await
        }
        UrlKind::Direct => direct_download(&store, settings.clone(), &ui, &id, snapshot).await,
    };

    match result {
        Ok(path) if path.is_empty() => {
            // Still waiting for user input (e.g. torrent file picker).
        }
        Ok(path) => {
            update(&store, &ui, &id, |i| {
                i.status = DownloadStatus::Completed;
                i.progress = 100.0;
                i.status_text = format!("Saved to {}", path);
                i.speed_text = "Finished".into();
                i.eta_text = "--".into();
                i.save_path = path;
            });
            let _ = ui.send(UiEvent::Completed(id));
        }
        Err(err) => {
            if token.is_cancelled() {
                update(&store, &ui, &id, |i| {
                    i.status = DownloadStatus::Paused;
                    i.status_text = "Paused by user".into();
                    i.speed_text = "Paused".into();
                });
            } else {
                let msg = err.to_string();
                let replay = stream::requires_fresh_capture(&msg);
                update(&store, &ui, &id, |i| {
                    i.status = DownloadStatus::Failed;
                    i.status_text = if replay {
                        "This link expired. Replay the video in your browser to capture a fresh one.".into()
                    } else {
                        format!("Error: {}", {
                            let s = msg.replace('\n', " ");
                            if s.chars().count() > 120 { s.chars().take(120).collect::<String>() } else { s }
                        })
                    };
                });
                let _ = ui.send(UiEvent::Failed(id));
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Direct HTTP download with range resume, throttling and retries
// ---------------------------------------------------------------------------

fn http_client(item: &DownloadItem) -> anyhow::Result<reqwest::Client> {
    let mut headers = reqwest::header::HeaderMap::new();
    for (k, v) in &item.stream_headers {
        if k == "cookie" || k == "range" || k == "host" { continue; }
        if let Ok(name) = reqwest::header::HeaderName::from_bytes(k.as_bytes()) {
            if let Ok(value) = reqwest::header::HeaderValue::from_str(v) {
                headers.insert(name, value);
            }
        }
    }
    let cookie = util::build_cookie_header(item.cookies.as_ref(), &item.url);
    if !cookie.is_empty() {
        if let Ok(v) = reqwest::header::HeaderValue::from_str(&cookie) {
            headers.insert(reqwest::header::COOKIE, v);
        }
    }
    headers.insert(reqwest::header::ACCEPT, reqwest::header::HeaderValue::from_static("*/*"));
    Ok(reqwest::Client::builder()
        .default_headers(headers)
        .user_agent(if item.user_agent.is_empty() { util::CHROME_UA } else { &item.user_agent })
        .redirect(reqwest::redirect::Policy::limited(10))
        .build()?)
}

fn content_disposition_filename(value: &str) -> Option<String> {
    if let Some(pos) = value.to_ascii_lowercase().find("filename*=utf-8''") {
        let raw = &value[pos + "filename*=utf-8''".len()..];
        let raw = raw.split(';').next().unwrap_or(raw).trim().trim_matches('"');
        return Some(percent_decode(raw));
    }
    let lower = value.to_ascii_lowercase();
    let pos = lower.find("filename=")?;
    let raw = value[pos + "filename=".len()..].split(';').next()?.trim().trim_matches('"');
    Some(raw.to_string())
}

fn percent_decode(s: &str) -> String {
    let b = s.as_bytes(); let mut out = Vec::new(); let mut i = 0;
    while i < b.len() {
        if b[i] == b'%' && i + 2 < b.len() {
            let h = |x: u8| match x { b'0'..=b'9' => Some(x - b'0'), b'a'..=b'f' => Some(x - b'a' + 10), b'A'..=b'F' => Some(x - b'A' + 10), _ => None };
            if let (Some(a), Some(c)) = (h(b[i + 1]), h(b[i + 2])) { out.push(a * 16 + c); i += 3; continue; }
        }
        out.push(b[i]); i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

fn parts_subdir(item: &DownloadItem, safe_title: &str) -> PathBuf {
    let parts_root = crate::paths::Paths::get().parts();
    if !item.save_path.is_empty() {
        let p = PathBuf::from(&item.save_path);
        if p.starts_with(&parts_root) {
            if let Some(parent) = p.parent() { return parent.to_path_buf(); }
        }
    }
    let leaf = format!("{}-{}", safe_title.chars().take(50).collect::<String>(), uuid::Uuid::new_v4().simple());
    let leaf: String = leaf.chars().take(70).collect();
    let dir = parts_root.join(leaf);
    let _ = std::fs::create_dir_all(&dir);
    dir
}

pub fn move_file_robust(source: &Path, target_dir: &Path, name: &str) -> anyhow::Result<PathBuf> {
    let _ = std::fs::create_dir_all(target_dir);
    let dest = util::unique_path(target_dir, name);
    for n in 0..5 {
        match std::fs::rename(source, &dest) {
            Ok(()) => return Ok(dest),
            Err(_) => {
                if std::fs::copy(source, &dest).is_ok() {
                    for _ in 0..3 { if std::fs::remove_file(source).is_ok() { break; } std::thread::sleep(Duration::from_millis(100)); }
                    return Ok(dest);
                }
                std::thread::sleep(Duration::from_millis(150 * (n + 1)));
            }
        }
    }
    Ok(source.to_path_buf())
}

async fn direct_download(
    store: &DownloadStore,
    settings: Arc<Mutex<Settings>>,
    ui: &UnboundedSender<UiEvent>,
    id: &str,
    snapshot: DownloadItem,
) -> anyhow::Result<String> {
    let client = http_client(&snapshot)?;
    let token = snapshot.token.clone();
    let mut response = client.get(&snapshot.url).send().await?;

    let content_type = response.headers().get(reqwest::header::CONTENT_TYPE)
        .and_then(|v| v.to_str().ok()).unwrap_or("").to_ascii_lowercase();
    if content_type.contains("mpegurl") || content_type.contains("m3u") || content_type.contains("dash+xml")
        || snapshot.url.to_ascii_lowercase().ends_with(".m3u8")
    {
        return stream::ytdlp_download(store, &settings, ui, id, snapshot).await;
    }

    let status = response.status();
    if !status.is_success() && status != reqwest::StatusCode::PARTIAL_CONTENT {
        anyhow::bail!("HTTP {}", status.as_u16());
    }
    let total_header = response.content_length().unwrap_or(0) as i64;
    let disposition = response.headers().get(reqwest::header::CONTENT_DISPOSITION)
        .and_then(|v| v.to_str().ok()).and_then(content_disposition_filename);
    let url_name = util::filename_from_url(&snapshot.url);
    let mut filename = disposition
        .or(url_name)
        .unwrap_or_else(|| snapshot.title.clone());
    filename = util::sanitize_filename(&filename);
    if ["index", "video"].contains(&filename.to_ascii_lowercase().as_str()) {
        filename = util::sanitize_filename(&snapshot.title);
    }
    let ext = Path::new(&filename).extension().map(|e| format!(".{}", e.to_string_lossy()))
        .unwrap_or_default();
    let stem = Path::new(&filename).file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or(filename.clone());
    let parts = parts_subdir(&snapshot, &stem);
    let temp = parts.join(format!("{}{}.part", stem, ext));
    let final_folder = if snapshot.target_folder.is_empty() { settings.lock().download_folder.clone() } else { snapshot.target_folder.clone() };
    let final_folder = if settings.lock().organize_downloads_by_type {
        PathBuf::from(&final_folder).join(util::category_for(&snapshot))
    } else { PathBuf::from(&final_folder) };
    let _ = std::fs::create_dir_all(&final_folder);

    let mut resume_from = std::fs::metadata(&temp).map(|m| m.len() as i64).unwrap_or(0);
    if resume_from > 0 {
        let mut b = reqwest::header::HeaderMap::new();
        b.insert(reqwest::header::RANGE, reqwest::header::HeaderValue::from_str(&format!("bytes={}-", resume_from))?);
        response = client.get(&snapshot.url).headers(b).send().await?;
        if response.status() == reqwest::StatusCode::OK { resume_from = 0; }
    }
    let expected = total_header.max(snapshot.expected_bytes).max(0);
    let mut total_read = resume_from;
    let _ = ui.send(UiEvent::Status("Starting download".into()));

    use tokio::io::AsyncWriteExt;
    let mut file = tokio::fs::OpenOptions::new().create(true).write(true).open(&temp).await?;
    file.set_len(total_read as u64).await?;
    file.seek(std::io::SeekFrom::Start(total_read as u64)).await?;

    let started = Instant::now();
    let mut last_report = Instant::now();
    let mut interval = tokio::time::interval(Duration::from_millis(100));
    let mut attempt = 0u32;
    let mut next_response = Some(response);
    'attempts: while attempt < 4 {
        if token.is_cancelled() { anyhow::bail!("cancelled"); }
        let Some(resp) = next_response.take() else { break };
        let mut stream = Box::pin(resp.bytes_stream());
        file.seek(std::io::SeekFrom::Start(total_read as u64)).await?;
        let mut last_bytes = total_read;
        let mut last_report_time = Instant::now();
        loop {
            if token.is_cancelled() {
                file.flush().await?;
                anyhow::bail!("cancelled");
            }
            tokio::select! {
                maybe_chunk = stream.next() => {
                    let chunk = match maybe_chunk {
                        Some(Ok(c)) => c,
                        None => break,
                        Some(Err(_e)) => {
                            attempt += 1;
                            if attempt < 4 {
                                let mut h = reqwest::header::HeaderMap::new();
                                h.insert(reqwest::header::RANGE, reqwest::header::HeaderValue::from_str(&format!("bytes={}-", total_read))?);
                                next_response = Some(client.get(&snapshot.url).headers(h).send().await?);
                                tokio::time::sleep(Duration::from_secs(attempt as u64)).await;
                            }
                            continue 'attempts;
                        }
                    };
                    file.write_all(&chunk).await?;
                    total_read += chunk.len() as i64;

                    let rate = settings.lock().maximum_download_rate_kbps;
                    if rate > 0 {
                        let required = total_read as f64 / (rate as f64 * 1024.0);
                        let elapsed = started.elapsed().as_secs_f64();
                        if required > elapsed {
                            tokio::time::sleep(Duration::from_secs_f64(required - elapsed)).await;
                        }
                    }

                    if last_report.elapsed() >= Duration::from_millis(500) {
                        let dt = last_report_time.elapsed().as_secs_f64().max(0.001);
                        let speed = (total_read - last_bytes) as f64 / dt;
                        let progress = if expected > 0 { (total_read as f64 / expected as f64 * 100.0).min(99.9) } else { 0.0 };
                        let eta = if speed > 0.0 && expected > total_read { (expected - total_read) as f64 / speed } else { -1.0 };
                        update(store, ui, id, |i| {
                            i.progress = progress;
                            i.status_text = if expected > 0 {
                                format!("{:.1}% of {}", progress, util::human_size(expected as u64))
                            } else {
                                format!("{} downloaded", util::human_size(total_read as u64))
                            };
                            i.speed_text = util::human_speed(speed);
                            i.size_text = util::human_size(total_read as u64);
                            i.eta_text = util::human_duration(eta);
                        });
                        last_bytes = total_read;
                        last_report_time = Instant::now();
                        last_report = Instant::now();
                    }
                }
                _ = interval.tick() => {}
            }
        }
        if expected > 0 && total_read < expected && resume_from == 0 && attempt < 3 {
            attempt += 1;
            let mut h = reqwest::header::HeaderMap::new();
            h.insert(reqwest::header::RANGE, reqwest::header::HeaderValue::from_str(&format!("bytes={}-", total_read))?);
            next_response = Some(client.get(&snapshot.url).headers(h).send().await?);
            tokio::time::sleep(Duration::from_secs(attempt as u64)).await;
            continue 'attempts;
        }
        break;
    }
    file.flush().await?;
    drop(file);

    let final_name = format!("{}{}", stem, ext);
    let final_path = move_file_robust(&temp, &final_folder, &final_name)?;
    let _ = std::fs::remove_dir_all(&parts);
    Ok(final_path.to_string_lossy().into_owned())
}

use futures::StreamExt as _;
use tokio::io::AsyncSeekExt as _;
