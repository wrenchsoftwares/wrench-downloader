# Wrench Downloader — Agent Working Agreements

## 1. No porn / NSFW site mentions (standing rule)
- Never name, link, or describe porn or NSFW sites in code, comments,
  docs, scripts, logs, or commit messages. This includes hostnames, API
  routes, extractor names, CDN hostnames tied to such sites, and test
  fixtures pointing at them.
- Describe sites only by mechanism role: "gated direct-MP4 site",
  "login-walled page", "epoch-signed CDN", "open video site".
- Test scripts must take site-specific values (page/API URLs, IDs) via CLI
  flags or config — never hardcode them. See
  `.agents/skills/silent-download-test/scripts/silent_gated_mp4_test.py`.
- When scrubbing, check code, comments, markdown docs, skill files, scripts,
  and publisher-visible strings. Local user data (history, logs) is out of
  scope.

## 2. Generic detection only
- The app must work by catching HTML/page artifacts and video traffic
  structurally: `<video>`/`<source>` tags (incl. shadow DOM), inline player
  configs, JSON stream APIs (`qualities`/`sources`/`transcodings`,
  `src`/`url`/`file`), HLS/DASH manifests (incl. extension-less masters),
  rendition ids (`itag`), performance resource entries.
- No per-site branches, no site-name matching, no site lists. Quality labels
  come from the page/player (itag height, `qualityLabel`, `size` attrs),
  never invented.
- Challenge/gate handling in `ChallengeSolver.cs` is structural HTML
  detection for the same reason.

## 3. Freshness & label integrity (media pipeline)
- Advertised-quality rows are never gated on capture state; direct URLs
  outrank page URLs, never the reverse.
- Height/label gates may change a row's URL, never delete the row; a labeled
  via-page fallback beats a missing quality.
- Send-time repair uses a concrete-URL allow-list (`?itag=N`, masters,
  media extensions); new URL shapes default to pass-through.
- Signed links die: fail fast with a replay hint, never retry proven-dead
  URLs with another downloader.

## 4. Build & test discipline
- .NET 9 WinUI 3, `dotnet build WrenchDownloader.csproj -c Debug`.
  Stop the running app first (it locks the exe).
- After XAML `x:Name`/handler changes: delete `obj/` then rebuild.
- After extension changes: reload in `chrome://extensions` AND refresh open
  video tabs (reload invalidates tab contexts).
- Bridge: `http://127.0.0.1:45732`; dialog OFF for silent tests.
- Debug builds: no tray, close exits directly. Release: tray with
  `Open app / Settings / Exit`.
