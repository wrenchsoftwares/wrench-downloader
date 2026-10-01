# 🔧 Wrench Downloader v26.3.1

> High-speed multi-threaded media and file downloader with integrated Chrome Companion stream sniffer. An ultra-fast, modern alternative to IDM built with .NET 9 and Windows App SDK (WinUI 3).

---

## ✨ Features

- **Blazing Fast Multi-Part Downloads**: Downloads files in concurrent parallel fragments with automatic resume and chunk assembly.
- **Smart Stream Sniffing**: Integrated Chrome extension sniffs HLS (`.m3u8`), DASH (`.mpd`), MP4, and WebM streams from video players in real-time.
- **Feed & Timeline Aware**: Accurately scopes videos on feeds (such as X/Twitter, Reddit) to download the exact clicked video rather than the feed.
- **Format & Quality Selection**: Extract real rendition playlists (up to 4K / 1080p / 720p) or download audio-only MP3s.
- **Download Manager Controls**: Queue limits, save-for-later items that wait for **Start Queue**, playlist downloads, speed caps, and optional clipboard URL capture.
- **Organized Downloads**: Optional folders by media/file type, with intermediate video parts kept outside the downloads folder.
- **Localized Interface**: English, French, and Arabic, including right-to-left layout for Arabic.
- **Modern Windows UI**: WinUI 3 interface with dark/light theme support, queue management, progress meters, and right-click actions.
- **yt-dlp Engine Integration**: Fallback extraction engine for encrypted, segmented, or complex video platforms.

Choose the interface language in **Settings → Language**. The selection is saved with portable settings when running the portable release. Clipboard monitoring is opt-in under **Settings → Downloads**; captured links always open the confirmation dialog.

---

## 🚀 Getting Started

### 1. Requirements
- Windows 10 (version 1809 / build 17763) or Windows 11
- Install with `WrenchDownloader-26.3.1-Setup.exe` (recommended: Start Menu entries, uninstaller, optional start-with-Windows). A portable ZIP is also available and needs no installation.
- Neither package requires a .NET installation. The .NET 9 SDK is only needed to build from source.

### 2. Build & Run Desktop App
```bash
# Clone the repository
git clone https://github.com/wrenchsoftwares/wrench-downloader.git
cd wrench-downloader

# Build and run
dotnet build WrenchDownloader.csproj -c Release
dotnet run --project WrenchDownloader.csproj
```

### 3. Create a Release Package
Primary distribution is the installer: run `./release-installer.ps1` from PowerShell (requires [Inno Setup 6](https://jrsoftware.org/isinfo.php)). It publishes the self-contained Windows x64 app and compiles `artifacts/releases/26.3.1/WrenchDownloader-26.3.1-Setup.exe`. Installed mode is non-portable: settings, history, and logs live in `%LOCALAPPDATA%\WrenchDownloader`.

Portable alternative: run `./release.ps1` from PowerShell. It publishes a self-contained Windows x64 portable package at `artifacts/releases/26.3.1/WrenchDownloader-26.3.1-win-x64.zip`. Extract it to a writable folder and run `WrenchDownloader.exe` in the master folder. The Chrome extension is in `chrome extension`, app dependencies are grouped under `Runtime`, and settings, history, and startup logs stay in `Data`.

The package includes the application resource index (`WrenchDownloader.pri`) beside the executable so WinUI can resolve its XAML resources at startup. Downloads are saved to the user's Downloads folder by default.

### 4. Install Chrome Companion Extension
Easiest: open Settings → **Browser integration** and click **Show extension folder** next to Google Chrome — it copies fresh files to `%LOCALAPPDATA%\WrenchDownloader\chrome-extension` and opens it. Then follow the steps at the top of the tab (Developer mode → Load unpacked). Manual alternative:
1. Open Google Chrome (or Edge/Brave/Chromium).
2. Navigate to `chrome://extensions/`.
3. Enable **Developer mode** in the top right corner.
4. Click **Load unpacked** and select `chrome extension` from the extracted release ZIP or repository (the in-app button copies it to `%LOCALAPPDATA%\WrenchDownloader\chrome-extension`).
5. The extension will automatically connect to Wrench Downloader's local bridge on `http://127.0.0.1:45732`.

---

## 🛠 Project Structure

```
wrench-downloader/
├── WrenchDownloader.csproj       # WinUI 3 desktop application (.NET 9)
├── AppLocalization.cs            # Localized strings and culture handling
├── Languages/                    # English, French, and Arabic catalogs
├── DownloadEngine.cs             # Multi-threaded download & assembly engine
├── ExtensionBridgeServer.cs      # Local HTTP bridge server for Chrome extension
├── MainWindow.xaml               # Main dashboard & download queue
├── SettingsDialog.cs             # App settings & configuration
├── Package.appxmanifest          # App package manifest (v26.3.1)
├── chrome extension/             # Chrome Manifest V3 Companion extension
│   ├── injected.js               # In-page fetch / XHR stream interceptor
│   ├── content.js                # Video detector & floating download overlay
│   ├── background.js             # Network request sniffer & desktop bridge
│   └── manifest.json             # Extension manifest (v26.3.1)
├── docs/
│   └── ARCHITECTURE.md           # In-depth architectural & download grabbing guide
├── tests/                        # Playwright verification and integration scripts
├── push.bat                      # Windows one-click script to stage, commit & push to main
├── push.ps1                      # PowerShell script to push to main
├── release.ps1                   # Builds and packages an organized release
├── artifacts/releases/           # Generated, versioned release packages (git-ignored)
└── WrenchDownloader.sln          # Solution file
```

---

## 📄 License

Developed by [Wrench Softwares](https://github.com/wrenchsoftwares). All rights reserved.
