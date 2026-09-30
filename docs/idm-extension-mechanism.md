# IDM Integration Module — Video Capture Mechanism (Reverse-Engineered)

Source analyzed: `IDM extension/6.43.1_0` and `IDM extension/IDMGCExt`
(IDM Integration Module 6.43.1, all `.js` byte-identical between the two copies).
All code is minified/obfuscated; findings below were decoded from the actual
source plus binary string analysis of the installed IDM app.

Companion doc: `idm-app-reception.md` (how the desktop app receives the data).

## TL;DR

IDM grabs videos with **zero site-specific code for YouTube**. Four generic layers:

1. **Network sniffer** (service worker) — classifies every 200/206/304 response
   by `Content-Type` via a MIME→extension table.
2. **Full session forwarding** — sends the native app the exact URL **plus the
   original request headers and cookies**, so the app replays bytes instead of
   re-resolving pages (this is what defeats login/age gates).
3. **In-page body hooks** (MAIN world) — scans fetch/XHR *response bodies* for
   media URLs the browser never directly requests (e.g. HLS masters).
4. **DOM layer** (isolated world) — tracks `<video>` elements, renders the
   download panel; per-site code exists only for Vimeo + Facebook.

## 1. Manifest & permissions (`manifest.json`)

- Service worker: `background.js`. Content script: `content.js`
  (`matches: *://*/*`, `all_frames: true`, `run_at: document_start`).
- Web-accessible MAIN-world scripts: `document.js`, `debug.js`, `captured.js`.
- Key permissions: `webRequest`, `cookies`, `tabs`, `webNavigation`,
  `downloads`, `storage`, `scripting`, `proxy`, `management`.
- Notably **no `nativeMessaging` in the manifest's `permissions`** in this
  build (it lives under optional/host usage at runtime via
  `connectNative("com.tonec.idm")` — see reception doc).

## 2. Network capture (`background.js`, gate function `Hc`, ~line 94)

```text
accept only status ∈ {200, 206, 304}
skip text/html pages (unless Content-Disposition: attachment)
classify by Content-Type through a static table, e.g.:
  video/mpegurl        → M3U|M3U8
  audio/mpegurl        → M3U|M3U8
  application/dash+xml → MPD (handled in the F4M/MPD branch, ~line 97)
  video/*, audio/*     → MP4/M4V/AVI/MP3/M4A/WEBM/…
```

No URL regexes, no domain lists: a stream is recognized purely by what the
server declares. Range-continuation (206) responses are accepted, not
filtered — the app de-duplicates by URL.

## 3. Session forwarding (function `X`, ~lines 99–100)

For every candidate the extension builds one packet for the app:

| Field | Content | How obtained |
|---|---|---|
| `[6]` | exact stream URL | `webRequest` details |
| `[12]` | status line | response |
| `[2]` | remote server IP | socket info |
| `[11]` | **request headers verbatim** | `onBeforeSendHeaders` |
| `[13]` | response headers | `onHeadersReceived` |
| `[51]` / cookie | **Cookie header** | request headers, else **explicitly fetched via cookies API** (`U(f)` async fetch, then re-send) |
| `[50]` | page/referer context | tab data (`Lc`) |
| `[54]` | User-Agent | `navigator.userAgent` fallback |
| `[14]`/`[19]` | POST body (+headers) | request body capture |
| `[8]` | flags (POST? proxy? frame? …) | bitmask |

Decoded logic: if the intercepted request carries no `Cookie` header and the
scheme is http(s), the extension **actively fetches the cookies itself** and
attaches them before forwarding. The app therefore always downloads with the
browser's live session — the precise reason gated videos work without any
page re-resolution.

## 4. In-page body sniffing (`document.js`, MAIN world, ~5 KB, obfuscated)

Injected into page context; hooks ( restored on demand via message
`1229212978` config):

- `fetch` + `Response.prototype.{text,json,arrayBuffer,blob}` and `.clone()`
- `XMLHttpRequest` response readers
- `MediaSource` / readable-stream readers (`getReader().read()` chains)

Response bodies are scanned for media URLs and reported via
`window.postMessage([1229212980, body, extractedUrl])`. The match patterns
(`M`, `ka` regexes) are **pushed from the background at runtime**, so they
don't appear statically in the file. Effect: URLs that only ever appear
*inside* API payloads (player responses, HLS master URLs like
`manifest.googlevideo.com/...hls_playlist...`) are captured even when the
browser never issues a request for them.

## 5. DOM / panel layer (`content.js`, isolated world, ~29 KB)

- Enumerates visible `<video>`/`<embed>` elements, observes DOM mutations
  (SPA navigation), positions the floating download button outside the player.
- Playback dimensions come from the live element — labels are never invented.
- Exactly **two** site-specific handlers exist in the whole extension:
  - Vimeo: `RegExp("^(?:[^/]+|player\.vimeo\.com/video)/(\d{2,})")` + player
    CSS selectors (`div.vp-video`, …) — extracts the numeric video ID.
  - Facebook: `*.return.return.memoizedProps.coreVideoPlayerMetaData.videoFBID`
    (React-props scraping).
- Everything else, including YouTube, flows through the generic layers above.

## 6. Transport to the app (summary — details in `idm-app-reception.md`)

Primary: localhost WebSocket `ws://127.0.0.1:1001/?cid=…&rnd=…`, subprotocol
`plugin.v3.internetdownloadmanager.com`, custom `;`-joined framing sent as
`Blob`, 15 s heartbeat, rotating retry with backoff. Fallback: Chrome native
messaging host `com.tonec.idm` (`IDMMsgHost.exe` via registry +
`IDMMsgHost.json`). The WS server lives in `IDMNetMon.dll`/`IDMNetMon64.dll`.

## 7. What this means for Wrench Downloader

Already matched: header/cookie forwarding, manifest + rendition capture,
response-body sniffing (our `injected.js` JSON scan), no-fake-labels policy.

Genuinely IDM-only (closed-source app side): multi-connection segmented
download of HLS/DASH with the forwarded session, in-app muxing, and the
quality panel built from the manifest's own variants. Our equivalents:
`TryDownloadExtensionResolvedAsync` (direct byte replay + ffmpeg mux) and
yt-dlp-on-manifest fallback.

Standing lesson applied from this analysis: never offer handshake-only or
expired URLs (SABR `aitags=`, past `expire=`) — IDM only ever surfaces
live-sniffed, directly replayable streams.
