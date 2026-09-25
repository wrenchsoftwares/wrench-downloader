# 🔧 Wrench Downloader v26.1

> High-speed multi-threaded media and file downloader with integrated Chrome Companion stream sniffer. An ultra-fast, modern alternative to IDM built with .NET 9 and Windows App SDK (WinUI 3).

---

## ✨ Features

- **Blazing Fast Multi-Part Downloads**: Downloads files in concurrent parallel fragments with automatic resume and chunk assembly.
- **Smart Stream Sniffing**: Integrated Chrome extension sniffs HLS (`.m3u8`), DASH (`.mpd`), MP4, and WebM streams from video players in real-time.
- **Feed & Timeline Aware**: Accurately scopes videos on feeds (such as X/Twitter, Reddit) to download the exact clicked video rather than the feed.
- **Format & Quality Selection**: Extract real rendition playlists (up to 4K / 1080p / 720p) or download audio-only MP3s.
- **Modern Windows 11 UI**: Fluent WinUI 3 interface with dark/light theme support, queue management, progress meters, and right-click actions.
- **yt-dlp Engine Integration**: Fallback extraction engine for encrypted, segmented, or complex video platforms.

---

## 🚀 Getting Started

### 1. Requirements
- Windows 10 (version 1809 / build 17763) or Windows 11
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

### 2. Build & Run Desktop App
```bash
# Clone the repository
git clone https://github.com/wrenchsoftwares/wrench-downloader.git
cd wrench-downloader

# Build and run
dotnet build WrenchDownloader/WrenchDownloader.csproj -c Release
dotnet run --project WrenchDownloader/WrenchDownloader.csproj
```

### 3. Install Chrome Companion Extension
1. Open Google Chrome (or Edge/Brave/Chromium).
2. Navigate to `chrome://extensions/`.
3. Enable **Developer mode** in the top right corner.
4. Click **Load unpacked** and select the `chrome extension` directory from this repository.
5. The extension will automatically connect to Wrench Downloader's local bridge on `http://127.0.0.1:45732`.

---

## 🛠 Project Structure

```
wrench-downloader/
├── WrenchDownloader/             # WinUI 3 desktop application (.NET 9)
│   ├── DownloadEngine.cs         # Multi-threaded download & assembly engine
│   ├── ExtensionBridgeServer.cs  # Local HTTP bridge server for Chrome extension
│   ├── MainWindow.xaml           # Main dashboard & download queue
│   ├── SettingsDialog.cs         # App settings & configuration
│   └── Package.appxmanifest      # App package manifest (v26.1)
├── chrome extension/             # Chrome Manifest V3 Companion extension
│   ├── injected.js               # In-page fetch / XHR stream interceptor
│   ├── content.js                # Video detector & floating download overlay
│   ├── background.js             # Network request sniffer & desktop bridge
│   └── manifest.json             # Extension manifest (v26.1)
└── WrenchDownloader.sln          # Solution file
```

---

## 📄 License

Developed by [Wrench Softwares](https://github.com/wrenchsoftwares). All rights reserved.
