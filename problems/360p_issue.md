# 360p-Only Menu Problem

## Symptoms
- Download menu lists only `360p` (plus Audio / Record) while the video
  plays at 1080p.
- Variant A: an `1080p` label attached to an `itag=18` (360p) URL — clicking
  downloads the wrong file.
- Variant B: clicking a quality sends an audio URL — the app dialog shows an
  MP3 (the "MP3 swap").
- Affects some videos, not all; gated/SABR content most often.

Proven during investigation: `yt-dlp -j` returned 43 formats incl. direct
https itags 133→137 (144p→1080p) for a video whose menu showed 360p only —
the data exists, our capture path was blind to it.

## Root causes & fixes

### 1. Advertised qualities required a captured manifest
`getGenericVideoItems` (`chrome extension/content.js`) builds the player's
own quality ladder only inside `if (streamItem)` — no captured HLS/DASH
manifest/playlist, no 1080p/720p rows, even when the player advertises them.
**Fix** (`content.js:1290`): ladder rows use `url: freshMaster || postPageUrl`,
labeled `• via page` when page-resolved. The app resolves the exact quality
with full decipher; a direct capture for the same quality replaces the
via-page row via the existing upgrade rule, so direct URLs can never be
shadowed again.
**Rule:** never gate advertised-quality rows on capture state.

### 2. Playing-quality row suppressed by the height gate
`includeCurrentPlayingQuality` (`content.js:~1160`) rejected
height-mismatched URLs by returning with no row at all — deleting the quality
instead of offering it.
**Fix** (`content.js:1181-1205`): concrete preference kept (`directSrc`
trusted; other candidates must match height or be a master), but with nothing
concrete the row falls back to a labeled via-page entry.
**Rule:** height gates may change a row's URL, never delete the row.

### 3. Hook race: player fetch fires before the sniffer installs
`injected.js` loads asynchronously; a `/youtubei/v1/player` call can finish
before the `fetch`/XHR hooks exist → no ladder, no manifests. First video
after browser start is most affected.
**Fix** (`injected.js:260-273`): `harvestEmbeddedPlayerResponse()` reads the
embedded player response directly (same shape the hook parses), fired 2.5 s
after load and on demand via `__WRENCH_HARVEST_PLAYER__`, which the dropdown
(`content.js:1742`) and popup (`content.js:308`) dispatch synchronously
before building items. Idempotent via the existing videoId guard.
**Rule:** every network-derived state needs an embedded-DOM fallback, pulled
at menu-build time.

### 4. Freshness ordering at send time (MP3 swap + mislabeling)
Captures are tagged with itag height at sniff time; the send-time repair pool
is newest-first, excludes expired links and fragment junk
(range/sabr/aitags), excludes audio unless the Audio row was clicked, and has
no blind "newest of any type" fallback. Extension-less itag URLs and masters
count as concrete and pass through untouched.
**Rule:** repair functions use a concrete-URL allow-list; new URL shapes
default to pass-through, never replacement.

## Gated-site constraint (do not regress)
Page-URL fallback is correct where the app can resolve the page (open video
sites) and wrong where it can't (login-walled direct-MP4 sites). The restores
above are safe because: ladder fallback needs a captured HLS/DASH item
(direct-MP4 sites serve plain files, unaffected), and the playing-row
fallback only fires when `directSrc` is a blob (direct-MP4 players serve a
concrete CDN `src`). Keep it that way.

## Operational discipline (user side)
1. After every extension change: `chrome://extensions` → Reload, then F5 all
   video tabs (reload invalidates open-tab contexts).
2. Replay 2–3 s before clicking — signed links die fast.
3. Gated downloads need the live logged-in tab session.

## Verification
- `node --check` on all extension scripts: pass.
- Freshness sim (stale 360p + expired 1080p + fresh 1080p + audio): fresh
  1080p picked; 360p URL blocked from the 1080p label.
- Public YouTube end-to-end after fixes: `END OK`, file in Downloads.
- Age-gated end-to-end: pending a fresh user click (live session required).
