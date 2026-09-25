---
name: wrench-workflows
description: >-
  Workflows, procedures, and diagnostic tools for developing, testing, building,
  and debugging Wrench Downloader and its Chrome Companion extension. Use whenever
  building the WinUI 3 desktop app, diagnosing download or bridge issues, testing
  stream interception, or inspecting download history.
---

# Wrench Downloader Engineering & Diagnostic Workflows

This skill packages standard procedures, build runbooks, and token-optimized CLI helpers for developing and diagnosing **Wrench Downloader** (.NET 9 WinUI 3) and its **Chrome Companion Extension** (Manifest V3).

---

## 1. Quick Diagnostics & Health Checks

Use local helper scripts instead of loading large logs or raw history into the LLM context:

```powershell
# Check bridge server health, history stats, and recent error logs
python .agents/skills/wrench-workflows/scripts/check_health.py

# Send an end-to-end test download into the desktop app via bridge
python .agents/skills/wrench-workflows/scripts/test_bridge.py "https://speed.hetzner.de/100MB.bin" "TestFile.bin"
```

---

## 2. Building & Running the Desktop App

The application is located at the workspace root:

```powershell
# Build Debug (fast check)
dotnet build WrenchDownloader.csproj -c Debug

# Build Release
dotnet build WrenchDownloader.csproj -c Release

# Run Desktop Application
dotnet run --project WrenchDownloader.csproj
```

### Architecture Key Notes
- **UI Framework**: Windows App SDK (WinUI 3) on .NET 9.
- **Single Process / Native AOT ready**: Self-contained WinUI packaging (`WindowsPackageType=None`).
- **Bridge Port**: `127.0.0.1:45732` (HTTP server for Chrome extension).
- **History File**: `%LOCALAPPDATA%\WrenchDownloader\history.json`.

---

## 3. Chrome Extension Development Runbook

The companion extension lives in `chrome extension/`:
- `manifest.json`: Manifest V3 configuration with `webRequest`, `downloads`, `storage`.
- `background.js`: Intercepts HLS/DASH/MP4 streams & browser downloads (`chrome.downloads.onCreated`), sends payloads to `http://127.0.0.1:45732/api/download`.
- `injected.js`: Intercepts in-page `fetch()` and `XMLHttpRequest` calls for live stream tokens.
- `content.js`: Detects video elements, resolves feeds/timelines (X.com, Reddit), renders floating download button.
- `popup.html` / `popup.js`: Shows current tab streams and toggles download interception.

### Reloading After Changes
After modifying any extension file, navigate to `chrome://extensions/` in Chrome and click **Reload (🔄)** on **Wrench Downloader Companion**.

---

## 4. Git Push & Release Procedures

Use the root push scripts to commit and sync changes directly to `origin main`:

```powershell
# Via batch script
.\push.bat "Commit message"

# Or via PowerShell
.\push.ps1 "Commit message"
```
