//! Streaming pipeline: external-tool orchestration, SABR segment replay and
//! extension-resolved direct playback. Detection stays structural.

use super::classify;
use super::util;
use super::{update, DownloadStore, UiEvent};
use crate::model::{DownloadItem, Settings};
use parking_lot::Mutex;
use regex::Regex;
use std::path::{Path, PathBuf};
use std::process::Stdio;
use std::sync::Arc;
use std::time::{Duration, Instant};
use tokio::sync::mpsc::UnboundedSender;

const FRESH_CAPTURE_MARKERS: &[&str] = &[
    "does not resolve", "link expired", "session expired", "unexpected byte offset",
    "not available", "no sabr segments", "range not satisfiable", "private video",
    "reload the page", "needs to be reloaded",
];

pub fn requires_fresh_capture(msg: &str) -> bool {
    let m = msg.to_ascii_lowercase();
    FRESH_CAPTURE_MARKERS.iter().any(|marker| m.contains(marker))
        || classify::signed_link_expired(msg)
}

fn is_gated_error(msg: &str) -> bool {
    let m = msg.to_ascii_lowercase();
    [
        "sign in", "log in", "login", "confirm your age", "age-restrict", "age restrict",
        "private video", "pass cookies", "cookies required", "not a bot", "bot check",
        "po_token", "needs to be reloaded", "reload the page",
    ].iter().any(|k| m.contains(k))
}

fn is_rate_limit(msg: &str) -> bool {
    let m = msg.to_ascii_lowercase();
    let re = Regex::new(r"\b429\b|rate.?limit|too many requests|timed out|timeout|failed to parse json|unable to download.*json|\b50[234]\b|bad gateway|service unavailable|gateway timeout|temporary failure").unwrap();
    re.is_match(&m)
}

fn is_expired(msg: &str) -> bool {
    let m = msg.to_ascii_lowercase();
    m.contains("410") && (m.contains("gone") || m.contains("unable to download"))
}

fn tools() -> (String, String, String) {
    let parts = crate::paths::Paths::get().parts();
    let aria = util::tool_path("aria2c.exe", &[
        &parts.join("aria2c.exe").to_string_lossy(),
    ]);
    let ytdlp = util::tool_path("yt-dlp", &[]);
    let ffmpeg = util::tool_path("ffmpeg", &[]);
    (ytdlp, ffmpeg, aria)
}

pub(crate) async fn run_process(
    program: &str,
    args: &[String],
    timeout: Duration,
    token: Option<&tokio_util::sync::CancellationToken>,
    env_path: Option<&str>,
) -> anyhow::Result<std::process::Output> {
    let mut cmd = tokio::process::Command::new(program);
    cmd.args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true)
        .creation_flags(0x08000000);
    if let Some(p) = env_path { cmd.env("PATH", p); }
    let child = cmd.spawn()?;
    let future = child.wait_with_output();
    // `kill_on_drop(true)` plus dropping the future on cancellation/timeout
    // terminates the whole child process.
    let output = match token {
        Some(t) => tokio::select! {
            _ = t.cancelled() => anyhow::bail!("cancelled"),
            r = future => r?,
        },
        None => match tokio::time::timeout(timeout, future).await {
            Ok(r) => r?,
            Err(_) => anyhow::bail!("process timed out"),
        },
    };
    Ok(output)
}

/// Like `run_process`, but streams stdout line-by-line so the UI shows the
/// current yt-dlp phase instead of freezing on "Extracting stream information"
/// until the process exits.
#[allow(clippy::too_many_arguments)]
pub(crate) async fn run_ytdlp_streaming(
    program: &str,
    args: &[String],
    timeout: Duration,
    token: Option<&tokio_util::sync::CancellationToken>,
    store: &DownloadStore,
    ui: &UnboundedSender<UiEvent>,
    id: &str,
) -> anyhow::Result<std::process::Output> {
    let mut cmd = tokio::process::Command::new(program);
    cmd.args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true)
        .creation_flags(0x08000000);
    let mut child = cmd.spawn()?;
    let stdout = child.stdout.take().unwrap();
    let stderr = child.stderr.take().unwrap();

    let collected = Arc::new(parking_lot::Mutex::new(Vec::<String>::new()));
    let lines = collected.clone();
    let ui_tx = ui.clone();
    let store = store.clone();
    let id = id.to_string();
    let stdout_task = tokio::spawn(async move {
        use tokio::io::AsyncBufReadExt as _;
        let mut reader = tokio::io::BufReader::new(stdout).lines();
        while let Ok(Some(line)) = reader.next_line().await {
            lines.lock().push(line.clone());
            let lower = line.to_ascii_lowercase();
            let phase = if lower.contains("solving js challenges") {
                Some("Solving JavaScript challenge...")
            } else if lower.contains("extracting url") {
                Some("Resolving page URL...")
            } else if lower.contains("downloading webpage") || lower.contains("player api") {
                Some("Reading player formats...")
            } else if lower.contains("downloading manifest") || lower.contains("metadata") {
                Some("Reading stream metadata...")
            } else {
                None
            };
            if let Some(phase) = phase {
                update(&store, &ui_tx, &id, |i| i.status_text = phase.into());
            }
        }
    });

    let stderr_task = tokio::spawn(async move {
        use tokio::io::AsyncReadExt as _;
        let mut text = String::new();
        let _ = tokio::io::BufReader::new(stderr).read_to_string(&mut text).await;
        text
    });

    let status = match token {
        Some(t) => tokio::select! {
            _ = t.cancelled() => anyhow::bail!("cancelled"),
            r = child.wait() => r?,
        },
        None => match tokio::time::timeout(timeout, child.wait()).await {
            Ok(r) => r?,
            Err(_) => anyhow::bail!("process timed out"),
        },
    };
    let _ = stdout_task.await;
    let stderr_text = stderr_task.await.unwrap_or_default();
    let stdout_text = collected.lock().join("\n");
    Ok(std::process::Output {
        status,
        stdout: stdout_text.into_bytes(),
        stderr: stderr_text.into_bytes(),
    })
}

fn safe_cookie_field(s: &str, trim: bool) -> Option<String> {
    // Netscape files are strictly tab-separated, one cookie per line. A single
    // embedded tab/newline makes yt-dlp reject the whole file.
    if s.contains(['\t', '\n', '\r']) { return None; }
    let value = if trim { s.trim().to_string() } else { s.to_string() };
    if value.is_empty() { None } else { Some(value) }
}

fn write_cookie_jar(item: &DownloadItem, prefix: &str) -> Option<PathBuf> {
    let cookies = item.cookies.as_ref()?;
    if cookies.is_empty() { return None; }
    let path = crate::paths::Paths::get().parts().join(format!("{}_{}.txt", prefix, uuid::Uuid::new_v4().simple()));
    let _ = std::fs::create_dir_all(path.parent()?);
    let mut text = String::from("# Netscape HTTP Cookie File\n");
    let mut written = 0usize;
    for c in cookies {
        let (Some(name), Some(domain), Some(cookie_path), Some(value)) = (
            safe_cookie_field(&c.name, true),
            safe_cookie_field(&c.domain, true),
            safe_cookie_field(&c.path, true),
            safe_cookie_field(&c.value, false),
        ) else { continue };
        // Canonical Netscape format: a leading dot + TRUE means "include
        // subdomains"; a bare host + FALSE means host-only. Keep the domain
        // exactly as the browser reported it.
        let flag = if domain.starts_with('.') { "TRUE" } else { "FALSE" };
        text.push_str(&format!("{}\t{}\t{}\t{}\t{}\t{}\t{}\n",
            domain, flag, cookie_path, if c.secure { "TRUE" } else { "FALSE" },
            if c.expiry > 0 { c.expiry } else { 0 }, name, value));
        written += 1;
    }
    if written == 0 {
        let _ = std::fs::remove_file(&path);
        tracing::warn!("no usable browser cookies; continuing without a cookie jar");
        return None;
    }
    std::fs::write(&path, text).ok()?;
    crate::paths::log_line(&format!("cookie jar {} with {} entries", path.display(), written));
    Some(path)
}

fn parts_subdir(item: &DownloadItem, title: &str) -> PathBuf {
    let root = crate::paths::Paths::get().parts();
    if !item.save_path.is_empty() {
        let p = PathBuf::from(&item.save_path);
        if p.starts_with(&root) { if let Some(parent) = p.parent() { return parent.to_path_buf(); } }
    }
    let leaf: String = format!("{}-{}", title.chars().take(50).collect::<String>(), uuid::Uuid::new_v4().simple())
        .chars().take(70).collect();
    let dir = root.join(leaf);
    let _ = std::fs::create_dir_all(&dir);
    dir
}

fn output_template(item: &DownloadItem, parts: &Path, title: &str) -> String {
    if item.download_playlist {
        format!("{}/%(playlist_title)s/%(playlist_index)03d - %(title)s.%(ext)s", parts.display())
    } else {
        format!("{}/{}.%(ext)s", parts.display(), title)
    }
}

/// Mirrors the working ytdl.py invocation: let yt-dlp discover Deno on PATH
/// (no --js-runtimes), use its canonical format selectors, and keep the
/// EJS solver flag that makes age/login gates work.
fn common_args(
    item: &DownloadItem,
    settings: &Settings,
    ffmpeg: &str,
    aria_active: bool,
) -> Vec<String> {
    let mut a = vec![
        if item.download_playlist { "--yes-playlist".into() } else { "--no-playlist".into() },
        "--no-warnings".into(),
        "--retries".into(), "10".into(),
        "--fragment-retries".into(), "10".into(),
        "--progress".into(),
    ];
    // Required for YouTube's JS challenge solver.
    a.extend(["--remote-components".into(), "ejs:github".into()]);

    if item.quality == "audio" {
        a.extend(["-f".into(), "bestaudio/best".into(),
            "-x".into(), "--audio-format".into(), "mp3".into(), "--audio-quality".into(), "0".into()]);
    } else {
        a.extend(["-f".into(), classify::quality_selector(&item.quality),
            "--merge-output-format".into(), "mp4".into()]);
    }

    if settings.maximum_download_rate_kbps > 0 {
        a.push("--limit-rate".into());
        a.push(format!("{}K", settings.maximum_download_rate_kbps));
    }
    // Measured on this machine: aria2c and -N 16 are much slower here
    // (aria2c ~30s, -N 16 gets HTTP 403). Plain yt-dlp defaults finish in ~4s.
    // Keep the switch for the fallback path, but default to plain downloads.
    if aria_active {
        a.extend(["--downloader".into(), "aria2c".into(), "--downloader-args".into(),
            "aria2c:-x 16 -s 16 -k 1M --file-allocation=none".into()]);
    }
    if !ffmpeg.is_empty() && ffmpeg != "ffmpeg" {
        a.extend(["--ffmpeg-location".into(), ffmpeg.to_string()]);
    }
    let referer = if !item.referrer.is_empty() { &item.referrer } else { &item.page_url };
    if !referer.is_empty() { a.extend(["--referer".into(), referer.clone()]); }
    if !item.user_agent.is_empty() { a.extend(["--user-agent".into(), item.user_agent.clone()]); }
    a
}

fn parse_ytdlp_progress(line: &str) -> Option<(f64, String)> {
    let re = Regex::new(r"\[download\]\s+(\d+(?:\.\d+)?)%").ok()?;
    let p = re.captures(line)?.get(1)?.as_str().parse::<f64>().ok()?;
    let speed = Regex::new(r"at\s+([\d.]+\s*[KMG]?i?B/s)")
        .ok().and_then(|r| r.captures(line).map(|c| c[1].to_string()))
        .unwrap_or_default();
    Some((p, speed))
}

fn is_real_media(path: &Path) -> bool {
    let ext = path.extension().and_then(|e| e.to_str()).unwrap_or("").to_ascii_lowercase();
    if !matches!(ext.as_str(), "mp4" | "mkv" | "webm" | "mov" | "m4v" | "mp3" | "m4a" | "aac" | "flac" | "wav" | "opus") {
        return false;
    }
    match std::fs::File::open(path) {
        Ok(mut f) => {
            use std::io::Read;
            let mut head = [0u8; 16];
            if f.read(&mut head).unwrap_or(0) < 4 { return false; }
            let text = String::from_utf8_lossy(&head).to_ascii_lowercase();
            !text.contains("<!doctype") && !text.contains("<html")
        }
        Err(_) => false,
    }
}

fn find_finished_file(parts: &Path, title: &str) -> Option<PathBuf> {
    let candidates: Vec<PathBuf> = std::fs::read_dir(parts).ok()?
        .flatten().map(|e| e.path())
        .filter(|p| p.is_file() && !util::is_transient_file(p) && is_real_media(p))
        .collect();
    let wanted = title.to_ascii_lowercase();
    candidates.iter().find(|p| {
        p.file_stem().and_then(|s| s.to_str()).unwrap_or("").to_ascii_lowercase() == wanted
    }).cloned().or_else(|| candidates.into_iter().next())
}

fn move_finished_media(parts: &Path, settings: &Settings, item: &DownloadItem) -> anyhow::Result<String> {
    let folder = if !item.target_folder.is_empty() { PathBuf::from(&item.target_folder) }
        else { PathBuf::from(&settings.download_folder) };
    let folder = if settings.organize_downloads_by_type { folder.join(util::category_for(item)) } else { folder };
    std::fs::create_dir_all(&folder)?;
    let mut last = String::new();
    for entry in walk_files(parts) {
        if util::is_transient_file(&entry) || !is_real_media(&entry) { continue; }
        let name = entry.file_name().and_then(|n| n.to_str()).unwrap_or("download");
        let dest = super::move_file_robust(&entry, &folder, name)?;
        last = dest.to_string_lossy().into_owned();
    }
    let _ = std::fs::remove_dir_all(parts);
    if last.is_empty() { anyhow::bail!("no finished media file found"); }
    Ok(last)
}

fn walk_files(dir: &Path) -> Vec<PathBuf> {
    let mut out = Vec::new();
    let Ok(entries) = std::fs::read_dir(dir) else { return out };
    for e in entries.flatten() {
        let p = e.path();
        if p.is_dir() { out.extend(walk_files(&p)); } else { out.push(p); }
    }
    out
}

// ---------------------------------------------------------------------------
// yt-dlp pipeline
// ---------------------------------------------------------------------------

pub async fn ytdlp_download(
    store: &DownloadStore,
    settings_ref: &Arc<Mutex<Settings>>,
    ui: &UnboundedSender<UiEvent>,
    id: &str,
    item: DownloadItem,
) -> anyhow::Result<String> {
    let (ytdlp, ffmpeg, aria) = tools();
    let token = item.token.clone();
    let settings = settings_ref.lock().clone();
    let title = {
        let mut t = util::sanitize_filename(&item.title);
        for suffix in [" - 1080p", " - 720p", " - best", " - audio", " - video"] {
            if t.to_ascii_lowercase().ends_with(suffix) { t.truncate(t.len() - suffix.len()); }
        }
        let generic = ["master", "index", "video", "direct download", "video_download", "video download", "youtube", "watch", "view_video"];
        let low = t.to_ascii_lowercase();
        if t.is_empty() || generic.iter().any(|g| low == *g || low.starts_with(&format!("{}_", g))) {
            t = "Video_Download".into();
        }
        for ext in [".mp4", ".mp3", ".m3u8", ".mpd"] {
            if t.to_ascii_lowercase().ends_with(ext) { t.truncate(t.len() - ext.len()); }
        }
        t
    };
    let parts = parts_subdir(&item, &title);
    let template = output_template(&item, &parts, &title);
    // Plain yt-dlp is measurably fastest/reliable here; aria2c is opt-in only.
    let mut aria_active = false;
    let mut common = common_args(&item, &settings, &ffmpeg, aria_active);
    let jar = write_cookie_jar(&item, "wd_session");
    let challenge_jar = if item.page_url.to_ascii_lowercase().contains("challenge") { write_cookie_jar(&item, "wd_challenge") } else { None };

    let mut candidates: Vec<String> = Vec::new();
    if classify::is_sabr_redirector(&item.url) {
        if !item.page_url.is_empty() { candidates.push(item.page_url.clone()); }
    } else if item.download_playlist {
        candidates.push(item.page_url.clone());
        if item.url != item.page_url && !item.url.is_empty() { candidates.push(item.url.clone()); }
    } else {
        candidates.push(item.url.clone());
        if !item.page_url.is_empty() && item.page_url != item.url { candidates.push(item.page_url.clone()); }
    }
    candidates.retain(|u| !u.is_empty());

    let cookie_arg = |jar: &Option<PathBuf>| -> Vec<String> {
        jar.as_ref().map(|p| vec!["--cookies".into(), p.to_string_lossy().into_owned()]).unwrap_or_default()
    };
    // Fresh cookies captured by the browser extension are the primary session
    // source. Attach them to every attempt; only fall back to
    // --cookies-from-browser when the extension supplied none.
    let has_jar = jar.is_some();
    let mut plan: Vec<(String, Vec<String>)> = Vec::new();
    if challenge_jar.is_some() {
        plan.push((item.page_url.clone(), cookie_arg(&challenge_jar)));
        if !has_jar {
            plan.push((item.page_url.clone(), vec!["--cookies-from-browser".into(), "chrome".into()]));
        }
    }
    for url in candidates.iter() {
        let cargs = if has_jar { cookie_arg(&jar) } else { Vec::new() };
        plan.push((url.clone(), cargs));
    }
    if plan.is_empty() { anyhow::bail!("no candidate URLs"); }

    let mut circuit: std::collections::HashMap<String, u32> = std::collections::HashMap::new();
    let mut last_error = String::new();
    let mut idx = 0usize;

    while idx < plan.len() {
        let (url, cookie_args) = plan[idx].clone();
        if token.is_cancelled() { anyhow::bail!("cancelled"); }
        let key = format!("{}|{:?}", url, cookie_args);
        let fails = circuit.entry(key.clone()).or_insert(0);
        if *fails > 3 { idx += 1; continue; }
        *fails += 1;

        let mut args = common.clone();
        args.extend(cookie_args.clone());
        args.extend(["-P".into(), format!("temp:{}", parts.display()), "-o".into(), template.clone(), url.clone()]);

        update(store, ui, id, |i| {
            i.status_text = if idx == 0 { "Extracting stream information...".into() } else { "Retrying via page...".into() };
            i.speed_text = "Resolving".into();
            if idx > 0 { i.progress = 0.0; }
        });

        let started = Instant::now();
        crate::paths::log_line(&format!("yt-dlp start attempt {} cookies={}", idx + 1, has_jar));
        let output = run_ytdlp_streaming(&ytdlp, &args, Duration::from_secs(3600), Some(&token), store, ui, id).await;
        crate::paths::log_line(&format!("yt-dlp attempt {} finished in {:.1}s", idx + 1, started.elapsed().as_secs_f64()));
        let output = match output {
            Ok(o) => o,
            Err(e) => { last_error = e.to_string(); idx += 1; continue; }
        };
        let stdout = String::from_utf8_lossy(&output.stdout).into_owned();
        let stderr = String::from_utf8_lossy(&output.stderr).into_owned();
        for line in stdout.lines() {
            if let Some((pct, speed)) = parse_ytdlp_progress(line) {
                update(store, ui, id, |i| {
                    i.progress = pct.min(99.0);
                    if !speed.is_empty() { i.speed_text = speed.clone(); }
                    i.status_text = format!("{:.1}% downloaded", pct);
                });
            } else if line.contains("[Merger]") || line.contains("[VideoRemuxer]") || line.contains("Destination:") {
                if let Some(p) = line.split("Destination:").nth(1).map(|s| s.trim().to_string()) {
                    update(store, ui, id, |i| i.save_path = p);
                }
            }
        }
        for line in stderr.lines() {
            if line.contains("ERROR:") { last_error = line.trim().to_string(); }
        }

        let finished = find_finished_file(&parts, &title);
        let success = output.status.success() || finished.as_ref().map(|p| std::fs::metadata(p).map(|m| m.len() > 1024 * 1024).unwrap_or(false)).unwrap_or(false);
        if success {
            let saved = move_finished_media(&parts, &settings, &item)?;
            return Ok(saved);
        }

        let error_text = format!("{} {}", stdout, stderr);
        if error_text.to_ascii_lowercase().contains("aria2c") && aria_active {
            aria_active = false;
            common = common_args(&item, &settings, &ffmpeg, false);
            continue; // retry same URL natively
        }
        if is_expired(&error_text) {
            anyhow::bail!("link expired");
        }
        if is_gated_error(&error_text) {
            if has_jar {
                // The extension already gave us fresh session cookies. Do not
                // probe other browsers (chrome/edge/brave) - that is both slow
                // and wrong when the click happened in Chrome.
                anyhow::bail!("login or age gate with fresh browser cookies - verify the account in Chrome and retry");
            }
            if cookie_args.is_empty() {
                for browser in ["chrome", "edge"] {
                    plan.push((item.page_url.clone(), vec!["--cookies-from-browser".into(), browser.into()]));
                }
                idx += 1;
                continue;
            }
        }
        if is_rate_limit(&error_text) && idx < plan.len() - 1 {
            update(store, ui, id, |i| i.status_text = "Rate limited, retrying...".into());
            tokio::time::sleep(Duration::from_secs(5)).await;
            continue;
        }
        idx += 1;
    }

    if let Some(j) = &jar { let _ = std::fs::remove_file(j); }
    if let Some(j) = &challenge_jar { let _ = std::fs::remove_file(j); }
    if last_error.is_empty() { last_error = format!("yt-dlp failed with code {}", output_code_placeholder()); }
    anyhow::bail!(last_error)
}

fn output_code_placeholder() -> i32 { 1 }

// ---------------------------------------------------------------------------
// SABR segment replay
// ---------------------------------------------------------------------------

fn parse_range(range: &str) -> Option<(u64, Option<u64>)> {
    let s = range.strip_prefix("bytes=").unwrap_or(range);
    let (a, b) = s.split_once('-')?;
    let start = a.parse().ok()?;
    let end = if b.is_empty() { None } else { b.parse().ok() };
    Some((start, end))
}

async fn fetch_range(
    client: &reqwest::Client,
    url: &str,
    range: &str,
    item: &DownloadItem,
) -> anyhow::Result<bytes::Bytes> {
    let mut headers = reqwest::header::HeaderMap::new();
    for (k, v) in &item.stream_headers {
        if matches!(k.as_str(), "cookie" | "range" | "host") { continue; }
        if let (Ok(n), Ok(v)) = (reqwest::header::HeaderName::from_bytes(k.as_bytes()), reqwest::header::HeaderValue::from_str(v)) {
            headers.insert(n, v);
        }
    }
    let cookie = util::build_cookie_header(item.cookies.as_ref(), url);
    if !cookie.is_empty() { headers.insert(reqwest::header::COOKIE, reqwest::header::HeaderValue::from_str(&cookie)?); }
    headers.insert(reqwest::header::RANGE, reqwest::header::HeaderValue::from_str(range)?);
    let response = client.get(url).headers(headers).timeout(Duration::from_secs(180)).send().await?;
    let status = response.status().as_u16();
    if status == 401 || status == 403 { anyhow::bail!("session expired (HTTP {})", status); }
    if status == 410 { anyhow::bail!("link expired"); }
    if status == 416 { anyhow::bail!("range not satisfiable"); }
    if !response.status().is_success() { anyhow::bail!("segment HTTP {}", status); }
    Ok(response.bytes().await?)
}

async fn build_track(
    client: &reqwest::Client,
    segments: &[super::super::model::StreamSegment],
    init_range: &str,
    expected: i64,
    item: &DownloadItem,
    parts: &Path,
    name: &str,
) -> anyhow::Result<PathBuf> {
    if segments.is_empty() { anyhow::bail!("no {} segments captured", name); }
    let path = parts.join(format!("{}.{}", name, if name == "audio" { "m4a" } else { "mp4" }));
    let mut file = tokio::fs::File::create(&path).await?;
    use tokio::io::AsyncWriteExt;
    let mut written = 0u64;

    // Init range first for playable fMP4.
    if !init_range.is_empty() {
        if let Ok(bytes) = fetch_range(client, &segments[0].url, init_range, item).await {
            file.write_all(&bytes).await?;
            written += bytes.len() as u64;
        }
    }

    let mut ordered: Vec<_> = segments.iter().collect();
    ordered.sort_by_key(|s| parse_range(&s.range).map(|r| r.0).unwrap_or(u64::MAX));
    let mut covered_until = 0u64;
    for seg in ordered {
        let Some((start, end)) = parse_range(&seg.range) else { continue };
        if end.is_some_and(|e| e <= covered_until) { continue; }
        let request_range = if start < covered_until && end.is_some() {
            format!("bytes={}-{}", covered_until, end.unwrap())
        } else if seg.range.is_empty() { "bytes=0-".into() } else { seg.range.clone() };
        match fetch_range(client, &seg.url, &request_range, item).await {
            Ok(bytes) => {
                file.write_all(&bytes).await?;
                written += bytes.len() as u64;
                covered_until = end.unwrap_or(start + bytes.len() as u64).max(covered_until);
            }
            Err(e) if requires_fresh_capture(&e.to_string()) => return Err(e),
            Err(_) => continue, // one dead segment is tolerated
        }
    }
    file.flush().await?;
    let minimum = if name == "audio" { 16 * 1024 } else { 256 * 1024 };
    if written < minimum || (expected > 0 && written < expected as u64 / 2) {
        anyhow::bail!("{} track too small ({} bytes)", name, written);
    }
    Ok(path)
}

pub async fn sabr_download(
    store: &DownloadStore,
    settings_ref: &Arc<Mutex<Settings>>,
    ui: &UnboundedSender<UiEvent>,
    id: &str,
    item: DownloadItem,
) -> anyhow::Result<String> {
    let title = util::sanitize_filename(&item.title);
    let parts = parts_subdir(&item, &title);
    let token = item.token.clone();
    let client = reqwest::Client::builder().user_agent(if item.user_agent.is_empty() { util::CHROME_UA } else { &item.user_agent }).build()?;
    update(store, ui, id, |i| i.status_text = "Reassembling captured segments...".into());

    let video = build_track(&client, item.segments.as_deref().unwrap_or(&[]), &item.init_range, item.expected_bytes, &item, &parts, "video").await?;
    let mut final_source = video.clone();
    if let Some(audio_segments) = item.audio_segments.as_ref().filter(|s| !s.is_empty()) {
        if let Ok(audio) = build_track(&client, audio_segments, &item.audio_init_range, 0, &item, &parts, "audio").await {
            let (_, ffmpeg, _) = tools();
            let output = parts.join("muxed.mp4");
            let args = vec!["-y".into(), "-i".into(), video.to_string_lossy().into_owned(),
                "-i".into(), audio.to_string_lossy().into_owned(), "-c".into(), "copy".into(),
                "-movflags".into(), "+faststart".into(), output.to_string_lossy().into_owned()];
            if run_process(&ffmpeg, &args, Duration::from_secs(900), Some(&token), None).await.map(|o| o.status.success()).unwrap_or(false) {
                final_source = output;
            }
        }
    }
    let settings = settings_ref.lock().clone();
    let saved = move_finished_media(&parts, &settings, &item)?;
    let _ = final_source;
    Ok(saved)
}

// ---------------------------------------------------------------------------
// Extension-resolved playback (direct signed URL with live browser headers)
// ---------------------------------------------------------------------------

fn client_with_item_headers(item: &DownloadItem) -> anyhow::Result<reqwest::Client> {
    let mut headers = reqwest::header::HeaderMap::new();
    for (k, v) in &item.stream_headers {
        if matches!(k.as_str(), "range" | "host") { continue; }
        if let (Ok(n), Ok(v)) = (reqwest::header::HeaderName::from_bytes(k.as_bytes()), reqwest::header::HeaderValue::from_str(v)) {
            headers.insert(n, v);
        }
    }
    if !headers.contains_key(reqwest::header::REFERER) && !item.referrer.is_empty() {
        headers.insert(reqwest::header::REFERER, reqwest::header::HeaderValue::from_str(&item.referrer)?);
    }
    Ok(reqwest::Client::builder().default_headers(headers)
        .user_agent(if item.user_agent.is_empty() { util::CHROME_UA } else { &item.user_agent }).build()?)
}

async fn fetch_track(
    client: &reqwest::Client,
    url: &str,
    item: &DownloadItem,
    path: &Path,
    token: &tokio_util::sync::CancellationToken,
) -> anyhow::Result<u64> {
    if classify::signed_link_expired(url) { anyhow::bail!("link expired"); }
    let mut request = client.get(url).timeout(Duration::from_secs(3600));
    let cookie = util::build_cookie_header(item.cookies.as_ref(), url);
    if !cookie.is_empty() {
        request = request.header(reqwest::header::COOKIE, cookie);
    }
    let response = request.send().await?;
    if response.status().as_u16() == 410 { anyhow::bail!("link expired"); }
    if !response.status().is_success() { anyhow::bail!("HTTP {}", response.status().as_u16()); }
    let mut stream = Box::pin(response.bytes_stream());
    let mut file = tokio::fs::File::create(path).await?;
    use futures::StreamExt as _;
    use tokio::io::AsyncWriteExt as _;
    let mut written = 0u64;
    while let Some(chunk) = stream.next().await {
        if token.is_cancelled() { anyhow::bail!("cancelled"); }
        let chunk = chunk?;
        file.write_all(&chunk).await?;
        written += chunk.len() as u64;
    }
    file.flush().await?;
    Ok(written)
}

pub async fn extension_resolved_download(
    store: &DownloadStore,
    settings_ref: &Arc<Mutex<Settings>>,
    ui: &UnboundedSender<UiEvent>,
    id: &str,
    item: DownloadItem,
) -> anyhow::Result<String> {
    if classify::is_sabr_redirector(&item.url) { anyhow::bail!("captured redirector cannot be downloaded directly"); }
    if classify::signed_link_expired(&item.url) { anyhow::bail!("link expired"); }
    let title = util::sanitize_filename(&item.title);
    let parts = parts_subdir(&item, &title);
    let token = item.token.clone();
    let client = client_with_item_headers(&item)?;
    update(store, ui, id, |i| i.status_text = "Downloading captured stream...".into());

    let video_path = parts.join("video.mp4");
    let written = fetch_track(&client, &item.url, &item, &video_path, &token).await?;
    if written < 256 * 1024 { anyhow::bail!("stream too small ({} bytes)", written); }

    let mut final_source = video_path.clone();
    if !item.audio_url.is_empty() {
        let audio_path = parts.join("audio.m4a");
        match fetch_track(&client, &item.audio_url, &item, &audio_path, &token).await {
            Ok(n) if n >= 16 * 1024 => {
                let (_, ffmpeg, _) = tools();
                let output = parts.join("muxed.mp4");
                let args = vec!["-y".into(), "-i".into(), video_path.to_string_lossy().into_owned(),
                    "-i".into(), audio_path.to_string_lossy().into_owned(), "-c".into(), "copy".into(),
                    "-movflags".into(), "+faststart".into(), output.to_string_lossy().into_owned()];
                if run_process(&ffmpeg, &args, Duration::from_secs(900), Some(&token), None).await
                    .map(|o| o.status.success()).unwrap_or(false) { final_source = output; }
            }
            _ => {}
        }
    }
    let _ = final_source;
    let settings = settings_ref.lock().clone();
    move_finished_media(&parts, &settings, &item)
}
