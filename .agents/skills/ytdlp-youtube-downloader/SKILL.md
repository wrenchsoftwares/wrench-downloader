---
name: ytdlp-youtube-downloader
description: Use when building or debugging an app that downloads YouTube (or other site) videos with yt-dlp, especially age-restricted or login-required videos on Windows with Chrome. Covers cookie handling, the Chrome DPAPI problem, the JS challenge solver (Deno/EJS), error-to-fix mapping, and IDM-style browser extension integration.
---

# yt-dlp + YouTube: working knowledge

## Why IDM works but yt-dlp fails by default
IDM runs inside the browser and captures streams the browser already played with the user's logged-in session. yt-dlp runs outside the browser, so YouTube sees an anonymous client and blocks login-required and age-restricted videos. The fix is to give yt-dlp the browser's session cookies.

## Requirements checklist (check all before debugging anything else)
1. **yt-dlp is up to date.** YouTube breaks it often; a version older than ~90 days prints a warning and often fails.
   - pip install: `pip install -U yt-dlp`
   - standalone binary: `yt-dlp -U`
2. **A JavaScript runtime is installed (Deno).** Windows: `winget install DenoLand.Deno`. Open a new terminal afterwards so PATH updates.
3. **The EJS challenge solver is allowed to download.** Pass `--remote-components ejs:github` (alternative: `ejs:npm`). Without it, Deno runs but the solver script is skipped, "n challenge solving failed" appears, and formats go missing or age-restricted videos fail. This was the decisive fix in testing.
4. **Valid cookies from a logged-in, age-verified account** (see below).

## Working command
```
yt-dlp --cookies cookies.txt --remote-components ejs:github "VIDEO_URL"
```

Permanent config (`%APPDATA%\yt-dlp\config.txt` on Windows):
```
--remote-components ejs:github
--cookies C:\Users\<USER>\cookies.txt
--merge-output-format mp4
```
Without `--merge-output-format`, split video+audio streams (e.g. format 137+251) merge into `.mkv`.

## Age-restricted videos: what is required
Verified working: age-restricted video downloaded with cookies.txt + Deno + `--remote-components ejs:github`. All four must be true: logged-in cookies from an age-verified account, JS runtime, EJS solver allowed, current yt-dlp. If any is missing, expect "age-restricted" errors or missing formats.

## MP4 output
- **ffmpeg must be installed and on PATH** (YouTube serves video and audio separately; yt-dlp merges them with ffmpeg).
- Default merge of 137+251 (H.264 video + Opus audio) gives `.mkv`. Add `--merge-output-format mp4` to force mp4.
- For maximum player compatibility (phones, TVs, editors), prefer H.264 video + AAC audio: `-f "bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/bv*+ba/b"` together with `--merge-output-format mp4`. This avoids Opus-in-mp4, which some players reject.
- Optional fallback: `--recode-video mp4` re-encodes anything incompatible (slow, lossy, use only if needed).
- Note: the best quality (VP9/AV1, 4K) often exists only as webm. Choosing mp4-only formats can cap quality (commonly 1080p H.264). The app should expose a "best quality (mkv/webm)" vs "compatible (mp4)" choice.

## Getting cookies

### Option A: `--cookies-from-browser <browser>`
- **Firefox**: most reliable. `yt-dlp --cookies-from-browser firefox "URL"`.
- **Chrome/Edge on Windows**: usually fails (see errors below). Try it, but have a fallback.
- Chrome must be fully closed first (`taskkill /F /IM chrome.exe`), otherwise the cookie DB is locked.

### Option B: export `cookies.txt` (most reliable for Chrome)
1. Install **"Get cookies.txt LOCALLY"** (not the older "Get cookies.txt", which was flagged as malicious). Enable **Allow in Incognito** in `chrome://extensions` > Details.
2. Open an **incognito window**, log in to YouTube.
3. In the same tab, open `https://www.youtube.com/robots.txt`. It is a static page that does not rotate the session.
4. Export cookies for youtube.com, save as `cookies.txt`.
5. **Close the incognito window immediately.** YouTube rotates cookies on open tabs and invalidates the export.
6. Use with `--cookies cookies.txt`.

Netscape format is required for `--cookies`.

## Error to fix mapping
| Error / warning | Cause | Fix |
|---|---|---|
| `Could not copy Chrome cookie database` | Chrome running, DB locked | Close Chrome fully (`taskkill /F /IM chrome.exe`) and retry |
| `Failed to decrypt with DPAPI` | Chrome app-bound cookie encryption | Not fixable by a flag. Export `cookies.txt` via extension, or use Firefox |
| `Your yt-dlp version is older than 90 days` | Outdated install | `pip install -U yt-dlp` (or `yt-dlp -U`) |
| `Remote components challenge solver script ... were skipped` | EJS solver not allowed to download | Add `--remote-components ejs:github` |
| `n challenge solving failed: Some formats may be missing` | No JS runtime or solver | Install Deno and use `--remote-components ejs:github` |
| `age-restricted and YouTube is requiring account age-verification` | Cookies not from a logged-in, age-verified account | Verify age in a normal browser session first, log in inside the incognito window before exporting, re-export |
| `web_creator client https formats require a GVS PO Token` | That client needs a PO token | Usually safe to ignore if another client provides formats. Advanced: `--extractor-args "youtube:po_token=web_creator.gvs+XXX"` |
| Works once, then fails later | Cookies rotated or expired | Re-export fresh cookies; automate export at download time |

## Architecture options for an IDM-style app

1. **Extension writes `cookies.txt`** (simplest). A Manifest V3 extension uses `chrome.cookies.getAll({domain: ".youtube.com"})`, converts to Netscape format, saves via `chrome.downloads`. The user runs yt-dlp with that file. Chrome decrypts cookies for extensions, so DPAPI is not an issue.
2. **Extension posts to a local server.** A small Python/Node server on `127.0.0.1:<port>` receives cookies + video URL and runs yt-dlp. Bind to localhost only and require a secret token so other sites/programs cannot call it.
3. **Native messaging** (cleanest, most IDM-like). The extension sends `{url, cookies}` to a registered native host (JSON manifest plus a registry key on Windows). The host launches yt-dlp. No open port.

Best UX: export cookies fresh at download time so rotation does not matter.

## Custom browser extension that sends cookies to the app (logic only)

### Components
1. **Browser extension (Manifest V3)**: lives in the user's real Chrome, where cookies are already decrypted for it.
2. **Local receiver in the app**: either a native messaging host or a localhost HTTP endpoint.
3. **Download engine**: the app's yt-dlp runner.

### Extension permissions (minimum)
- `cookies` permission with host permissions limited to youtube.com and google.com domains only.
- Permission to read the active tab URL.
- Native messaging permission (if using native messaging) or host permission for the localhost address.
- No access to all sites.

### Flow, step by step
1. User opens a YouTube video page and clicks the extension button (or a context-menu item "Download with App").
2. Extension reads the current tab URL and validates it is a supported video URL; otherwise it shows a message and stops.
3. Extension fetches all cookies for the YouTube and Google domains from the browser's cookie store, at this moment, so they are fresh.
4. Extension converts the cookies into Netscape cookies.txt text format (fields: domain, include-subdomains flag, path, secure flag, expiry, name, value). Session cookies with no expiry get a far-future or zero expiry per the format rules.
5. Extension sends one message to the app containing: video URL, cookies text, and a request id.
6. App receives the message and validates it:
   - Native messaging: Chrome only allows the registered extension ID to talk to the host.
   - Localhost HTTP: accept only from 127.0.0.1, require a shared secret token, check the Origin matches the extension, reject everything else.
7. App writes the cookies to a temporary cookies.txt in a private per-user folder.
8. App starts yt-dlp with that cookies file, the EJS flag, the chosen format, and mp4 merge settings.
9. App streams progress (percent, speed, ETA) back to the extension, which shows it on the badge or popup.
10. When finished, the app deletes the temporary cookies file and reports the saved file path.
11. On failure, the app maps the error to the error table and sends a plain message back (for example "cookies expired, log in again" or "update yt-dlp").

### Design rules
- Export cookies at download time, every time. Never reuse an old export; YouTube rotates cookies.
- Never persist cookies longer than the download. Delete the temp file in a finally/cleanup step, including on crash.
- Never log cookie values. Log only counts or domain names.
- Never send cookies over the network to anything except the local app.
- Native messaging is preferred (no open port). If using localhost HTTP, use a random port or fixed port plus secret token generated at install time and shared with the extension during pairing.
- Handle the "not logged in" case: if no login cookies (such as the session cookies) are present, tell the user to log in rather than starting a doomed download.
- Handle age-restricted failures by telling the user to verify age in the browser, then retry.
- Support batch: the extension can send a list of URLs (playlist or multiple tabs) with one cookie payload; the app queues them.
- Extension and app should each version their messages so updates do not break the pairing.

### Why this beats cookies-from-browser
The extension runs inside Chrome, so it receives cookies already decrypted. This sidesteps the DPAPI / app-bound encryption failure and the locked cookie database, with no need to close Chrome.

## Invoking yt-dlp from code
- Prefer the CLI via subprocess, or the Python API (`yt_dlp.YoutubeDL`) with equivalent options: `cookiefile`, `remote_components` (check the current option name for your version), `merge_output_format`.
- Always include the EJS flag and ensure Deno is on PATH for the process environment.
- Capture stderr and map messages to the error table above to give users actionable fixes.
- Offer an "Update yt-dlp" action in the app; most sudden breakage is fixed by updating.
- Optional speed-up: pair with aria2 as an external downloader.

## Security rules (non-negotiable)
- `cookies.txt` equals account access. Never log it, upload it, commit it, or send it to remote servers.
- Request the extension `cookies` permission only for `*.youtube.com` and `*.google.com`, not all sites.
- Store cookie files in a user-only location and delete or refresh them after use where possible.
- Using the user's main account carries some risk of YouTube flagging it; consider letting users choose a secondary account.
- Only download content the user has the right to download.

## Debugging order of operations
1. Update yt-dlp.
2. Confirm Deno is installed (new terminal).
3. Add `--remote-components ejs:github`.
4. Test with a fresh `cookies.txt` from incognito (logged in, then robots.txt, export, close).
5. Confirm the account can watch the video in a normal browser (age-verified).
6. If Chrome cookies keep failing, switch to Firefox with `--cookies-from-browser firefox`.
7. Only then consider PO tokens or other advanced options.
