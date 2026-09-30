# Investigation & Resolution of the "360p Only" Problem (v26.2.4)

Symptom: on YouTube (and similar players), the download menu lists only
`360p` (plus Audio/Record) even while the video plays at 1080p, and some
clicks download the wrong bytes or fail. Two sub-symptoms seen in the wild:
an `1080p` label attached to an `itag=18` (360p) URL, and direct-stream
`403 Forbidden` on fresh signed links.

This document records root causes, fixes, and rules for future work so the
same class of bug is not reintroduced.

---

## 1. Advertised qualities required a captured manifest

### Root cause
In `chrome extension/content.js` (`getGenericVideoItems`), the ladder block
that lists the player's own advertised qualities (1080p/720p/…) only runs
inside `if (streamItem)` — i.e. only when an HLS/DASH manifest or playlist
was captured. No captured manifest → advertised rows never built, even
though the player (and yt-dlp) knows the renditions exist. Verified against
`yt-dlp -j`: 43 formats incl. direct https itags 133→137 for a video whose
menu showed 360p only — the data exists, capture was blind to it.

### Fix (`content.js:1290`)
Ladder rows now use `url: freshMaster || postPageUrl`, labeled
`• via page` when page-resolved. The desktop app resolves the exact quality
with full decipher (proven path: public-video page fallback `END OK`). A
direct capture for the same quality replaces the via-page row via the
existing direct-upgrade rule, and `sendDownloadToApp` prefers concrete URLs —
so page-URL shadowing (page URL hiding a direct CDN URL) cannot return.

### Future rule
**Never gate advertised-quality rows on capture state.** A labeled
page-resolve fallback is always better than a missing quality. Direct URLs
outrank page URLs; never the reverse.

---

## 2. Playing-quality row suppressed by the height gate

### Root cause
`includeCurrentPlayingQuality` (`content.js:~1160`) was hardened to reject
height-mismatched URLs (correct: stops 360p bytes labeled 1080p) but returned
with **no row at all** when nothing concrete matched — deleting the quality
instead of offering it.

### Fix (`content.js:1181-1205`)
Concrete preference kept (`directSrc` trusted; other candidates must match
height or be a master), but with nothing concrete the row falls back to a
labeled via-page entry (`bestViaPage`). Same policy as §1.

### Future rule
**Height gates may change a row's URL, never delete the row.** Wrong-file
downloads and missing qualities are both failures; a labeled fallback is the
safe default.

---

## 3. Hook race: player fetch fires before the sniffer installs

### Root cause
`injected.js` is appended asynchronously (`document_start` + script-src
load). A `/youtubei/v1/player` call (or the initial page data) can complete
before the `fetch`/XHR hooks exist → no ladder, no manifests, no itag
qualities. First video after browser start is most affected; SPA navigations
are fine because hooks persist.

### Fix (`injected.js:260-273`, `content.js:308`, `content.js:1742`)
`harvestEmbeddedPlayerResponse()` reads the embedded player response
directly (same `{videoDetails, streamingData}` shape the fetch hook parses),
fired once 2.5 s after load and on demand via `__WRENCH_HARVEST_PLAYER__`,
which the dropdown and popup dispatch synchronously before building items.
Idempotent: same-videoId re-harvests change nothing; navigation clears and
rebuilds via the existing videoId guard.

### Future rule
**Every network-derived state must have an embedded-DOM fallback.**
Anything the hooks can miss (early fetches, HTML-embedded configs) needs a
direct read path, pulled at menu-build time.

---

## 4. Freshness ordering at send time (the MP3 swap)

### Root cause
`sendDownloadToApp` repaired extension-less URLs (all `videoplayback` itag
URLs have no file extension) by grabbing the newest capture **of any type** —
often an audio rendition. A 1080p click therefore sent an audio URL; the app
dialog showed an MP3.

### Fix (`content.js:sendDownloadToApp` + `isConcreteStreamUrl` /
`isAudioCapture` helpers)
`?itag=N` rendition URLs and extension-less masters count as concrete and go
through untouched. Repair only fires for real page fallbacks, matches quality
while excluding audio (unless the Audio row was clicked), prefers the newest
non-expired, non-fragment capture, and never swaps in a non-stream URL.

### Future rule
**Repair functions need an allow-list for "concrete", not a block-list for
"page".** Any new URL shape (new CDN, new param scheme) must default to
pass-through, not to replacement.

---

## 5. Direct-stream 403: missing SAPISIDHASH (IDM difference)

### Root cause
Direct `*.googlevideo.com` requests carried no session proof:
`youtube.com` cookies are domain-scoped away from the media host (correct per
spec), and nothing replaced them. The browser authorizes media with
`Authorization: SAPISIDHASH <ts>_<sha1(ts + SAPISID + origin)>` — exactly the
header a network-layer capture (IDM) replays. Fresh signed URLs + no auth =
`403 Forbidden`, forcing wasteful page re-resolution into age gates.

### Fix (`DownloadEngine.cs:195` `SapisidHashAuth`, applied at `:361`,
`:848`, `:1387`)
Mint SAPISIDHASH from the live extension session (`__Secure-3PAPISID`,
fallback `SAPISID`), googlevideo-only, on all direct paths (aria2c,
HttpClient video/audio incl. SABR segments) and yt-dlp session attempts via
repeatable `--add-headers`. Empty without a session → anonymous behavior
unchanged.

### Future rule
**googlevideo requests authenticate via header, never cookies.** Any new
direct-download path must carry SAPISIDHASH when a session exists.

---

## 6. Dead captures burned yt-dlp cycles

### Root cause
A capture proven dead by the direct attempt (DNS NXDOMAIN / `expire` past)
was re-tried through yt-dlp, producing cryptic `curl (6)` errors and wasting
attempts before the page fallback ran.

### Fix (`DownloadEngine.cs:1214`, `:1231`, `:1446`)
Dead hosts are recorded (`deadLinkHosts`) and skipped in the yt-dlp plan
with explicit log lines (`dead capture dropped from yt-dlp plan`,
`skipping dead captured URL in yt-dlp plan`). Page fallback unaffected
(different host).

### Future rule
**Never retry a proven-dead URL with a different downloader.** Fail fast
with a replay hint; spend attempts on live candidates only.

---

## 7. Retry-chain gaps closed along the way

- `--cookies-from-browser chrome → edge → brave` was unreachable whenever
  the extension supplied cookies; it now runs after client variants exhaust
  (`DownloadEngine.cs:1891`, `:1940`).
- DPAPI/app-bound vault failures skip profile-cookie attempts with a clear
  message instead of a confusing terminal error (`:1690`). Note: Chrome 127+
  app-bound encryption makes external profile reads impossible — the live
  extension jar is the only session channel on such machines.
- Tray: Debug builds skip the icon and exit on close directly; Release keeps
  `Open app / Settings / Exit` (`MainWindow.xaml.cs`, `TrayIconHelper.cs`).

---

## Operational discipline (user side, still required)

1. After **every** extension change: `chrome://extensions` → Reload, then
   **refresh all video tabs** (F5). Reloaded extensions invalidate open-tab
   contexts (`Extension context invalidated`) — stale tabs capture nothing.
2. Replay 2–3 s before clicking: signed links die (edge-host rotation,
   `expire`), and only a fresh capture carries a live link + token.
3. Age-gated downloads need the live logged-in tab session; no cookie
   export or flag bypasses a gate the browser itself cannot pass.

## Verification summary

| Check | Result |
| :--- | :--- |
| `node --check` content/injected/background/popup | Pass |
| Freshness sim (stale 360p + expired 1080p + fresh 1080p + audio pool) | Fresh 1080p picked; 360p URL blocked from 1080p label |
| `dotnet build` Debug + Release | 0 warnings, 0 errors; bridge `ok` |
| Live dead-link probe | `dead capture dropped`, `skipping dead captured URL`, straight to page fallback |
| Public YouTube end-to-end | `END OK`, file in Downloads |
| Age-gated end-to-end | Pending user fresh click (live session required) |
