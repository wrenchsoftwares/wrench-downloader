//! Local HTTP bridge — protocol compatible with the existing Chrome extension.
//! Port 45732, loopback only, CORS + Private Network Access headers preserved.

use crate::model::{BrowserCookie, DownloadItem, StreamSegment};
use axum::extract::State;
use axum::http::{HeaderValue, Method, StatusCode};
use axum::middleware::Next;
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use axum::{Json, Router, extract::Request};
use serde::Deserialize;
use std::collections::HashMap;
use tokio::sync::mpsc::UnboundedSender;

pub const DEFAULT_PORT: u16 = 45732;
const CHROME_UA: &str = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

#[derive(Debug)]
pub struct BridgeRequest {
    pub item: DownloadItem,
    pub prompt: bool,
}

#[derive(Clone)]
pub struct BridgeState {
    pub tx: UnboundedSender<BridgeRequest>,
}

// ---------------------------------------------------------------------------
// Wire payload (exact field names the extension posts)
// ---------------------------------------------------------------------------

#[derive(Debug, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct WireSegment {
    u: String,
    range: String,
}

#[derive(Debug, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct WireCookie {
    name: String,
    value: String,
    domain: String,
    path: String,
    secure: bool,
    expiry: i64,
}

#[derive(Debug, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct BridgePayload {
    url: String,
    #[serde(alias = "title")]
    name: String,
    quality: String,
    page_url: String,
    referrer: String,
    user_agent: String,
    po_token: String,
    audio_url: String,
    audio_referrer: String,
    #[serde(default)]
    sabr: Option<bool>,
    itag: i32,
    expected_bytes: i64,
    init_range: String,
    audio_init_range: String,
    segments: Option<Vec<WireSegment>>,
    audio_segments: Option<Vec<WireSegment>>,
    stream_headers: HashMap<String, String>,
    audio_stream_headers: HashMap<String, String>,
    cookies: Option<Vec<WireCookie>>,
    prompt: Option<bool>,
}

impl Default for BridgePayload {
    fn default() -> Self {
        Self {
            url: String::new(), name: "Video Download".into(), quality: String::new(),
            page_url: String::new(), referrer: String::new(), user_agent: String::new(),
            po_token: String::new(), audio_url: String::new(), audio_referrer: String::new(),
            sabr: None, itag: 0, expected_bytes: 0, init_range: String::new(),
            audio_init_range: String::new(), segments: None, audio_segments: None,
            stream_headers: HashMap::new(), audio_stream_headers: HashMap::new(),
            cookies: None, prompt: None,
        }
    }
}

const HEADER_ALLOW_LIST: &[&str] = &[
    "accept", "accept-language", "authorization", "cookie", "origin", "referer",
    "user-agent", "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform",
    "sec-fetch-dest", "sec-fetch-mode", "sec-fetch-site", "sec-fetch-user",
];

fn filtered_headers(input: HashMap<String, String>) -> HashMap<String, String> {
    input.into_iter()
        .filter(|(k, v)| HEADER_ALLOW_LIST.contains(&k.to_ascii_lowercase().as_str())
            && v.len() <= 16384)
        .take(20)
        .map(|(k, v)| (k.to_ascii_lowercase(), v))
        .collect()
}

fn segments(input: Option<Vec<WireSegment>>) -> Option<Vec<StreamSegment>> {
    input.map(|v| {
        v.into_iter()
            .filter(|s| !s.u.is_empty())
            .take(1500)
            .map(|s| StreamSegment { url: s.u, range: s.range })
            .collect()
    })
}

fn build_item(p: BridgePayload) -> DownloadItem {
    let ua = if p.user_agent.trim().is_empty() { CHROME_UA } else { &p.user_agent };
    let title = if p.name.trim().is_empty() { "Video Download" } else { &p.name };
    let cookies: Option<Vec<BrowserCookie>> = p.cookies.map(|v| {
        v.into_iter()
            .filter(|c| !c.name.is_empty())
            .take(100)
            .map(|c| BrowserCookie {
                name: c.name, value: c.value,
                domain: c.domain.trim_start_matches('.').to_string(),
                path: if c.path.is_empty() { "/".into() } else { c.path },
                secure: c.secure, expiry: c.expiry,
            })
            .collect::<Vec<BrowserCookie>>()
    }).filter(|v: &Vec<BrowserCookie>| !v.is_empty());

    DownloadItem {
        url: p.url,
        title: title.to_string(),
        quality: p.quality,
        page_url: p.page_url,
        referrer: p.referrer,
        user_agent: ua.to_string(),
        po_token: p.po_token,
        audio_url: p.audio_url,
        audio_referrer: p.audio_referrer,
        is_sabr: p.sabr.unwrap_or(false),
        itag: p.itag,
        expected_bytes: p.expected_bytes,
        init_range: p.init_range,
        audio_init_range: p.audio_init_range,
        segments: segments(p.segments),
        audio_segments: segments(p.audio_segments),
        stream_headers: filtered_headers(p.stream_headers),
        audio_stream_headers: filtered_headers(p.audio_stream_headers),
        cookies,
        status_text: "Added from browser extension".into(),
        ..Default::default()
    }
}

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

async fn health() -> impl IntoResponse {
    Json(serde_json::json!({"status": "ok", "app": "Wrench Downloader"}))
}

async fn download(
    State(state): State<BridgeState>,
    Json(payload): Json<BridgePayload>,
) -> Response {
    let prompt = payload.prompt.unwrap_or(true);
    let item = build_item(payload);
    if item.url.is_empty() {
        return (StatusCode::BAD_REQUEST, Json(serde_json::json!({"success": false, "error": "Missing url"}))).into_response();
    }
    let id = item.id.clone();
    let _ = state.tx.send(BridgeRequest { item, prompt });
    (StatusCode::OK, Json(serde_json::json!({"success": true, "id": id}))).into_response()
}

async fn cors_layer(req: Request, next: Next) -> Response {
    let is_options = req.method() == Method::OPTIONS;
    let mut response = if is_options {
        StatusCode::NO_CONTENT.into_response()
    } else {
        next.run(req).await
    };
    let headers = response.headers_mut();
    headers.insert("access-control-allow-origin", HeaderValue::from_static("*"));
    headers.insert("access-control-allow-methods", HeaderValue::from_static("POST, GET, OPTIONS"));
    headers.insert("access-control-allow-headers", HeaderValue::from_static("Content-Type, Access-Control-Request-Private-Network"));
    headers.insert("access-control-allow-private-network", HeaderValue::from_static("true"));
    response
}

async fn fallback() -> StatusCode { StatusCode::NOT_FOUND }

pub async fn serve(port: u16, tx: UnboundedSender<BridgeRequest>) -> anyhow::Result<()> {
    let state = BridgeState { tx };
    let app = Router::new()
        .route("/api/health", get(health))
        .route("/api/download", post(download).options(|| async { StatusCode::NO_CONTENT }))
        .fallback(fallback)
        .with_state(state)
        .layer(axum::middleware::from_fn(cors_layer));
    let listener = tokio::net::TcpListener::bind(("127.0.0.1", port)).await?;
    tracing::info!("bridge listening on http://127.0.0.1:{}", port);
    axum::serve(listener, app).await?;
    Ok(())
}
