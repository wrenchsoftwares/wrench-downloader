---
name: silent-download-test
description: >-
  Silently test a Wrench Downloader download end-to-end with no dialog, no
  browser, and no user clicks. Use whenever verifying a download-engine or
  extension-payload change: mint fresh stream URLs, POST to the bridge, poll
  the log to a terminal state, and verify the output file. Never pops UI.
---

# Silent Download Test

Proves a download works through the **real app code** (`ExtensionBridgeServer`
→ `DownloadEngine`) without touching the browser and without any modal
dialog. Public (non-gated) videos only — gated videos need a live tab session
that only the user holds.

## 0. One-time precondition: dialog must be OFF

`MainWindow.xaml.cs:OnExtensionDownloadRequested` computes
`shouldPrompt = promptFromCaller || SettingsHelper.ShowDownloadDialog`.
A bridge POST with `"prompt": false` still pops the modal confirmation when
the app setting is on, and a modal **blocks the test forever**.

- In the app: Settings → **uncheck "Show download dialog"** (one time).
- Verify silently: send the POST, then check `%LOCALAPPDATA%\WrenchDownloader\startup.log`
  for a `START` line with the returned id within ~15 s. No `START` = a dialog
  is open on the user's screen. **Stop and report it — never click it for them.**

## 1. Rule: rebuild needs the app stopped

The running exe locks `bin\...\WrenchDownloader.exe` and the build fails with
`MSB3027/MSB3021`. Always:

```powershell
Stop-Process -Name WrenchDownloader -Force 2>&1; Start-Sleep 3
dotnet build WrenchDownloader.csproj -c Debug
Start-Process -FilePath "bin\Debug\net9.0-windows10.0.26100.0\win-x64\WrenchDownloader.exe" -WorkingDirectory "."
```

Then wait for the bridge (fail fast if not up in 60 s):

```powershell
Invoke-RestMethod "http://127.0.0.1:45732/api/health" -TimeoutSec 5
```

## 2. Automated silent test (preferred)

```powershell
C:\Users\BOSS\AppData\Local\Programs\Python\Python312\python.exe `
  .agents/skills/silent-download-test/scripts/silent_yt_test.py `
  "https://www.youtube.com/watch?v=aqz-KE-bpKQ" "1080p" "SILENT-PROOF"
```

The script mints fresh URLs (`yt-dlp -g`, no browser), POSTs an
extension-shaped payload (`url` + `audioUrl` + `quality` + `prompt: false`),
polls `startup.log` for the track id until `END OK` / `END FAILED`, then
resolves `SavePath` from `history.json` and verifies with `ffprobe`
(resolution + audio stream present). Exit code 0 = pass. It prints the exact
log lines that decided the result.

## 3. What to check on failure (in order)

1. `BRIDGE-RECV` present? No → bridge/app not running.
2. `START` within 15 s? No → modal dialog is blocking (see §0).
3. `extension-resolved direct:` line — `progressive=` / `audio=` tells which
   path ran. `403/410` or DNS error right after POST = stale links (re-mint;
   captured links die in hours; `expire=` in the past = dead).
4. `video bytes=` / `audio bytes=` lines give exact downloaded sizes.
5. `moved to downloads:` / `END OK` = success. `terminal failure` / `END FAILED`
   = read the error text that follows (first 200 chars are the cause).
6. Confirm streams: `ffprobe -v error -show_entries stream=index,codec_name,width,height <file>`.

## 4. Hard rules (learned from real failures)

- **Never write URLs with PowerShell `Out-File`** (adds BOM → `﻿https://…`
  breaks `new Uri()`). The helper script writes files; if you must do it
  manually use `[System.IO.File]::WriteAllText(...)`, and always read back
  with `utf-8-sig`.
- **Mint URLs immediately before POSTing** — never reuse links across turns.
- **Expect throttling**: googlevideo single-connection downloads run
  ~25–400 KB/s. A 130 MB 1080p test takes ~10 min. Silence ≠ stuck: watch the
  `Parts\<title>-*` subdir file sizes grow. Only call it stuck after 15 min
  with zero growth.
- **Never open browser tabs, never copy the Chrome `Cookies` file** (locked
  while Chrome runs), never change user settings as a side effect.
- Leave the app running when done; report the output filename + duration.
