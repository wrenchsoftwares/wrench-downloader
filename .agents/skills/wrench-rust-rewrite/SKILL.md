---
name: wrench-rust-rewrite
description: Work on the Rust + GPUI Kit Wrench Downloader at the project root. Use when building, running, debugging, or extending the app, the 127.0.0.1:45732 bridge, its Tokio download engine, or the GPUI Kit UI. Covers exact API gotchas, the end-to-end verification recipe and remaining parity gaps.
---

# Wrench Downloader — Rust / GPUI Kit rewrite

The Rust app is at the project root (`Cargo.toml`, `src/`, `target/`). It is a
ground-up rewrite of the former WinUI 3 app, **not** a wrapper. The old C# app
is preserved on the `archive-winui` branch; the main branch is Rust-only.

## Map

| File | Role |
| --- | --- |
| `src/main.rs` | Tokio runtime, settings/history load, bridge + engine + clipboard startup, GPUI Kit `application().run`. |
| `src/bridge.rs` | axum bridge on `127.0.0.1:45732`; wire payload fields and CORS/PNA headers must stay byte-compatible with the Chrome extension. |
| `src/engine/mod.rs` | command loop, queue pump (2 s tick), download store, `run_download` dispatch, direct HTTP with resume/throttle/retry. |
| `src/engine/classify.rs` | structural URL detection (manifest / playback / redirector / page / direct / torrent). No per-site lists. |
| `src/engine/stream.rs` | yt-dlp plan, aria2c/ffmpeg invocation, SABR segment replay, extension-resolved playback. |
| `src/engine/torrent.rs` | aria2c metadata, minimal bencode reader, file picker flow. |
| `src/model.rs` | `DownloadItem`, `Settings`, history with credential scrubbing; serde field names are PascalCase to match C#. |
| `src/paths.rs` | portable (`portable.mode` next to package root) vs installed (`%LOCALAPPDATA%\WrenchDownloader`) data dirs. |
| `src/ui.rs` | GPUI Kit views: header, toolbar, cards, modal overlays. |
| `src/platform.rs` | magnet protocol registration, clipboard polling via `arboard`. |

## Build / run

```powershell
cargo build
 Start-Process -FilePath .\target\debug\wrench-downloader.exe -WorkingDirectory (Get-Location) -PassThru
```

**Stop the running app before rebuilding** — the exe is locked otherwise.
`Get-Process wrench-downloader | Stop-Process -Force`.

Dev smoke test with portable data (keeps `%LOCALAPPDATA%` untouched):

1. The repo-root `portable.mode` makes the app use `Data/` in the repo. For an
   isolated test, point `DownloadFolder` in `Data/settings.json` at a temp dir
   (`{"DownloadFolder":"<temp dir>","ShowDownloadDialog":false,
   "MaximumConcurrentDownloads":4}` — PascalCase keys).
2. Start a local file server: `python -m http.server 45799 --bind 127.0.0.1 --directory <serve dir>`.
3. Start the app, then:
   `Invoke-RestMethod http://127.0.0.1:45732/api/health` → `ok / Wrench Downloader`.
4. POST `/api/download` with `{url, title, quality:"file", prompt:false}` and
   confirm the file lands in `DownloadFolder`.
5. Stop the app and server; restore `DownloadFolder` afterwards.

## GPUI Kit API facts learned the hard way (gpui-kit 0.10 / gpui-pre 0.3.12)

- `gpui_kit::open_window(WindowOptions, cx, |window, cx| ...)` takes **two**
  closure args. `WindowOptions` fields are `window_bounds`, `titlebar`,
  `focus`, `show`, `kind`, ... — there is **no** `title` or `bounds` field.
  For maximized startup use:
  ```rust
  window_bounds: Some(WindowBounds::Maximized(Bounds {
      origin: Point { x: px(80.), y: px(80.) },
      size: Size { width: px(1280.), height: px(800.) },
  }))
  ```
- `InputState::new(window, cx)` needs a `&mut Window`, but `cx.new` only passes
  `&mut Context<T>` — create the input entities in the `open_window` build
  closure (which has `window`) and pass them into the view constructor.
- `InputState::value()` takes **no** cx argument; `set_value(value, window, cx)` takes both.
- `h_flex()` / `v_flex()` are free functions, **not** methods on `Div`. Use
  `.flex().flex_row()` / `.flex().flex_col()` on an existing element.
- `Progress` lives in `gpui_kit::component::progress::Progress` (not `ProgressBar`).
- `Button::disabled`, `cx.theme()` need the `Disableable` / `ActiveTheme` traits
  from `gpui_kit::component` in scope.
- `ElementId` from `format!("id-{}", key)` is safest; tuple ids with mixed types
  are not universally supported.
- **Stack overflow in debug builds**: one giant `render` with many chained
  `.child(...)` calls creates a huge stack frame. Split sections into helper
  methods returning `AnyElement` (e.g. `render_header`, `render_toolbar`,
  `render_list`, `render_card`, `render_footer`) and return
  `root.into_any_element()`.
- Engine → UI events: `tokio::sync::mpsc::UnboundedReceiver` awaited inside
  `cx.spawn(async move |app| ...)`; call
  `app.update(|cx| view.update(cx, |v, cx| v.handle_event(ev, cx)))`.
  `App::spawn` futures take exactly **one** argument (`&mut AsyncApp`).
- `App::handle_event` must call `cx.notify()` for GPUI to re-render.

## Bridge invariants (do not "fix" these)

- Health: `{"status":"ok","app":"Wrench Downloader"}`.
- Download success: `{"success":true,"id":"<guid>"}`; 400 on parse error, 404 on unknown route.
- Every response gets the CORS + Private Network Access headers.
- `prompt` defaults to `true`; only explicit JSON `false` suppresses it.
- Header allow-list, 20-header cap, 16 KB value cap; segments capped 1500;
  cookies capped 100; `format` is intentionally ignored.

## Engine invariants

- Direct URLs outrank page URLs; signed/expired links fail fast with a replay hint.
- Session/cookie args are held back as retry ammo; first attempt must look anonymous.
- Circuit breaker drops an identical `(url, cookieArgs)` head after 3 failures.
- aria2c is never used for HLS/DASH manifests or when credentials would appear on the command line.
- One dead SABR segment is tolerated; init range is fetched first.
- Progress caps at 99 until finalize; partial `.part` bytes are kept on cancellation.
- Credentials are scrubbed from `history.json` snapshots on save and on load.

## Browser → app handoff

- For YouTube, the extension must send the **video page URL**
  (`https://www.youtube.com/watch?v=...`) plus fresh youtube.com/google.com
  cookies. Signed `googlevideo` playback URLs die between capture and use;
  if one is used anyway, fall back to yt-dlp on `page_url` with the same cookies.
- `--cookies-from-browser brave/chrome/edge` is only a last resort when the
  extension supplied no cookies. Never probe other browsers when a fresh jar
  exists — it produces misleading "could not find browser cookies database"
  errors.
- IDM/ODM research: their dropdowns offer a standard 144p–4K ladder plus audio;
  the resolutions are **targets resolved from the page**, not claimed as
  captured renditions. Our extension now does the same for any video page:
  it appends missing ladder rows pointing at `pageUrl` with `viaPage: true`
  and subtext "Resolve via app", and sends them with fresh cookies. The app
  prompt also has its own quality selector as a second path.
- The prompt is a separate OS window (`src/prompt.rs`, `gpui_kit::open_window`).
  Call `cx.activate(true)` and `window.activate_window()` so Windows brings it
  forward instead of leaving it behind the browser/taskbar.

## Prompt flow

- The `prompt: true` bridge path must keep the **whole `DownloadItem`** in the
  prompt state and only add it to the store when Start is clicked. Adding it
  early makes the queue pump start it before confirmation; not adding it at all
  makes Start silently fail because `store.find(id)` returns `None`.
- Honour "Download later": add the item with `wait_for_queue_start = true` but
  do not send `EngineCommand::Start`.
- `paths::log_line("prompt shown ...")` / `"prompt start clicked ..."` are
  useful for verifying this flow when the UI cannot be inspected directly.

## yt-dlp path — verified fixes

- Mirror the user's working `D:\ytdl.py` invocation: pass
  `--remote-components ejs:github`, `--merge-output-format mp4`, `--no-playlist`,
  `--retries 10`, `--fragment-retries 10`, `--progress`, and let yt-dlp
  **discover Deno on PATH** — do NOT pass `--js-runtimes`, which restricts the
  runtime and breaks the EJS solver.
- Numeric quality selector from that script:
  `bv*[height<=H][ext=mp4]+ba[ext=m4a]/b[height<=H][ext=mp4]/bv*[height<=H]+ba/b[height<=H]`.
- **Measured on this machine:** plain yt-dlp defaults finish a small video in
  ~4s; aria2c took ~30s and `-N 16` triggered HTTP 403. Default to plain
  downloads; aria2c is opt-in only. Pass `--ffmpeg-location` when ffmpeg is not
  just `ffmpeg` on PATH.
- Stream yt-dlp stdout with a **chunk reader split on both `\\r` and `\\n`**
  (`run_ytdlp_streaming`) instead of `wait_with_output`. yt-dlp progress uses
  carriage returns; `BufRead::lines()` only splits on `\\n`, so all progress
  arrives as one line at the end and the UI appears stuck then jumps to 100%.
  Parse `[download] N%` in the streaming loop and update `item.progress` /
  `status_text` / `speed_text` immediately (cap progress at 99 until finalize).
- `paths::log_line` records `yt-dlp start ...` / `yt-dlp attempt N finished in
  Xs` for timing diagnosis.
- Do **not** pass `--impersonate chrome` unless the installed yt-dlp supports
  impersonation targets — the current install aborts with
  `Impersonate target "chrome" is not available`.
- Classify extensionless page URLs (e.g. `/watch?v=...`) as `UrlKind::Page`.
  Treating them as `Direct` downloads the HTML page instead of invoking yt-dlp.
- Validate finished media by extension **and** magic bytes; an HTML error page
  over 1 MiB must never be reported as Completed.
- The extension gathers fresh cookies for youtube.com **and** google.com at
  send time (`collectFreshSessionCookies` in `background.js`). Reload the
  extension and refresh video tabs after changing it.
- Cookie jars must be strictly 7 tab-separated fields per line. Skip cookies
  whose name/domain/path/value contain tabs or newlines, and omit `--cookies`
  entirely if none survive — otherwise yt-dlp rejects the whole file with
  "invalid Netscape format cookies file". Log cookie counts only, never values.
- Canonical Netscape rows use `.youtube.com / TRUE` for domain cookies and
  `www.youtube.com / FALSE` for host-only cookies. Do not rewrite every domain
  to a leading-dot form, and keep the leading dot from `chrome.cookies` in the
  bridge payload. Expiry `0` is valid for session cookies.
- Debug builds panic with `Duplicate a11y node id` when repeated labels share
  an implicit id. Give labels inside repeated download cards explicit per-item
  `ElementId`s.

## Release branches

- A version branch (e.g. `26.4.1`) is a **released snapshot**. Once created,
  never commit, push, merge into, or force-update it.
- Do all post-release work on `main`. Ship by creating a **new** version branch
  from `main` and a matching release/tag.
- Pushing a later commit to an old version branch or force-moving its tag is a
  mistake — ask before doing either.

## Tray / startup (release builds)

- `platform::start_tray` is compiled only in release (`#[cfg(not(debug_assertions))]`).
  Debug builds have no tray and close exits directly.
- Tray uses raw Win32 FFI in a background thread: Shell_NotifyIconW, a hidden
  helper window, a WM_CLOSE subclass on the main window (close hides it instead
  of quitting), and a menu with Open app / Settings / Exit.
- `UiEvent::TraySettings` and `UiEvent::TrayExit` bridge tray actions to the UI;
  the main event loop handles `TrayExit` with `cx.quit()`.
- `--background` / `/background` starts with `WindowOptions.show = false`,
  `focus = false`; the installer adds the HKCU Run entry with that flag.
- Single instance: a named mutex (`WrenchDownloader_SingleInstance`) makes a
  second launch call `focus_existing()` (FindWindowW by title `Wrench Downloader`
  + ShowWindow(SW_RESTORE)) and exit. Set the window title with
  `window.set_window_title("Wrench Downloader")` so FindWindowW can find it.
- The companion extension checks `/api/health`; when offline it offers a
  "Start Wrench Downloader" button and tries `wrench://launch`. The installer
  registers the `wrench:` protocol to the app exe.
- Icons: `Assets/AppIcon.ico` is generated from `Assets/logo-source.png` (16–256 px). It is
  staged as `app.ico`, used for the Start/desktop shortcuts (`IconFilename`),
  the tray icon, and the window/taskbar icons via `WM_SETICON` on the main HWND.
- Verified: sending WM_CLOSE leaves the process alive with the window hidden;
  `--background` starts with zero visible windows and the bridge still up.

## Known parity gaps (safe to work on next)

Taskbar progress, native folder picker, full locale routing for visible
strings, DNS pinning, POT attestation helper, extension auto-staging.
