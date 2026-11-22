# Wrench Downloader — Rust / GPUI Kit rewrite

Wrench Downloader is a Rust desktop app built with
**[GPUI Kit](https://gpui-kit.com/)** (`gpui-kit 0.10`). This repository is
Rust-only; the former WinUI 3 app is preserved on the `archive-winui` branch.

## Build & run

```powershell
cargo build                 # debug
cargo build --release       # release
cargo run
```

The binary is `target\debug\wrench-downloader.exe` (or `target\release\...`).

## Package layout

For portable use, ship the binary with a `portable.mode` marker at the package
root, plus these sibling folders:

```
WrenchDownloader/
  portable.mode
  wrench-downloader.exe
  Languages/            # en/fr/ar JSON
  chrome extension/     # Manifest V3 companion
  Data/                 # created at runtime: history.json, settings.json, Parts/
```

Installed (non-portable) mode stores data in `%LOCALAPPDATA%\WrenchDownloader`.
The JSON schemas remain compatible with the archived WinUI app's history and
settings files.

## Architecture

| Module | Role |
| --- | --- |
| `bridge.rs` | axum HTTP bridge on `127.0.0.1:45732` — identical endpoints, payload fields and CORS/PNA headers as the C# `ExtensionBridgeServer`, so the existing Chrome extension works unchanged. |
| `engine/` | Tokio download engine: structural URL classification, direct HTTP with `Range` resume, token-bucket throttling, yt-dlp/aria2c/ffmpeg orchestration, SABR segment replay, extension-resolved stream download, aria2c torrent flow. |
| `model.rs` | `DownloadItem`, settings and history schemas with credential scrubbing. JSON field names match the C# app. |
| `localization.rs` | Loads `Languages/*.json` with English fallback. |
| `platform.rs` | Magnet protocol registration, clipboard link monitoring. |
| `ui.rs` | GPUI Kit views: header, toolbar, download cards, prompt/settings/about/torrent-picker overlays, theming. |

Engine events reach the UI through a `tokio::sync::mpsc` channel pumped into
GPUI's App context; UI commands go back through an unbounded command channel.

## Implemented behaviour

- Bridge `/api/health`, `/api/download`, `OPTIONS` with exact headers; payload
  allow-lists and caps (segments, headers, cookies).
- Structural detection (manifests, playback URLs, redirectors, page URLs,
  direct files) with no per-site branches.
- Direct download: `Content-Disposition`/URL naming, 4 retry attempts, resume
  from `.part`, speed/ETA/progress, cancellation keeps partial bytes, robust
  move to the final folder.
- Streaming: yt-dlp argument construction, format selectors, cookie jars,
  session-held-back retry plan, gated-error/browser/client fallback, circuit
  breaker, rate-limit retries, aria2c for non-manifest URLs.
- SABR: range replay with overlap trimming, init range, ffmpeg mux.
- Extension-resolved playback: signed-expiry checks, live browser headers,
  video+audio mux with size floors.
- Torrents: aria2c metadata resolution, bencode file list, file picker,
  selective download.
- Queue pump, pause/resume, history persistence, portable/installed paths,
  clipboard monitoring, magnet registration, Arabic/French/English locale files.

## Known simplifications vs the C# app

- The system-tray icon and `ITaskbarList3` progress overlay are not ported yet.
- Settings/About/Prompt/Torrent picker render as modal overlays in the main
  window rather than separate top-level OS windows.
- Most visible strings are still English literals; the locale files load and
  `Localization::format` is available but not every label is routed through it.
- DNS pinning for expired signed-link hosts, the POT attestation server helper,
  and Chrome extension auto-staging are not ported.
- Native folder-picker: folder paths are edited directly in the settings and
  prompt fields instead of opening a WinRT `FolderPicker`.

These are incremental follow-ups; the bridge protocol, download engine and UI
are already functional end-to-end (verified with a local HTTP server).
