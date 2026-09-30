# Investigation & Resolution of Reported Bugs in v26.2.3

This document explains the root causes and solutions for the three issues identified in version **26.2.3** of Wrench Downloader and its Chrome Companion Extension.

---

## 1. App Running in Background but Does Not Show in System Tray

### Symptoms
When the application window is closed while **Close to Tray** is enabled (`CloseToTray: true`), the main window disappears, but no icon appears in the Windows notification area (System Tray). The process remains active in the background, but the user cannot reopen the window or interact with the app.

### Root Cause
1. **Commented-out initialization in v26.2.3:**
   In [MainWindow.xaml.cs](file:///g:/megacloud/projects/github/wrench%20downloader/MainWindow.xaml.cs), the call to `InitTrayIcon();` was explicitly commented out (`// InitTrayIcon();`).
   - When the user closed the window, `AppWindow.Closing` intercepted the close event and invoked `AppWindow.Hide()`.
   - Because `InitTrayIcon()` was commented out, no tray icon had been registered with `Shell_NotifyIcon`.
   - As a result, the application remained running as a hidden process with zero user interface representation.

2. **Why it was commented out in previous builds:**
   In earlier builds (v26.2.2), `TrayIconHelper` attached to WinUI 3's main window handle (`_hWnd`) using `SetWindowSubclass(..., 101)`. When the application exited via `ExitApplication()`, the code called both:
   ```csharp
   AppWindow.Destroy();
   Application.Current.Exit();
   ```
   Calling `AppWindow.Destroy()` destroyed the underlying Win32 window while WinUI's XAML message loop was still processing. Subsequent cleanup inside `Application.Current.Exit()` attempted to access the already-destroyed XAML Island / window, triggering a fatal native access violation (`0xc0000005`) inside `coreclr.dll` (`ABI.Microsoft.UI.Xaml.IApplicationMethods.Exit`).

3. **Win32 Shell Registration Timing & Structure Sizing:**
   - In WinUI 3, calling `Shell_NotifyIcon` during the `MainWindow` constructor fails with `0x80004005` (`E_FAIL`) because the window handle (`_hWnd`) has not yet been activated or mapped into the Windows Shell's active window table. Registration must occur after window activation (`_window.Activate()`).
   - The `NOTIFYICONDATA` struct size on 64-bit Windows must strictly match the expected shell version (`NOTIFYICONDATAW_V2_SIZE` = 952 bytes, or appropriate Vista+ definitions) with a valid icon handle.

### Fix
- Cleaned up `ExitApplication()` to call `Close()` instead of destroying the native window prior to exiting, preventing native `0xc0000005` crashes.
- Deferred tray icon registration to ensure it initializes after window activation.
- Maintained clean Win32 window message handling and outside-click menu dismissal using `PostMessage(hWnd, WM_NULL, 0, 0)`.

---

## 2. YouTube Video Becoming Muted When Clicking the Download Icon

### Symptoms
When playing any video on YouTube, clicking the Wrench Downloader floating download icon or quality dropdown muted the active YouTube video player.

### Root Cause
In [chrome extension/content.js](file:///g:/megacloud/projects/github/wrench%20downloader/chrome%20extension/content.js), the dropdown toggle function (`toggleDropdown`) contained legacy code that directly mutated the active HTML5 `<video>` element:
```javascript
// Previous flawed code in toggleDropdown:
if (video) {
    video.muted = true;
}
```
This was originally implemented to silence audio in background tab experiments, but in practice, it forcibly muted the user's primary video playback every time the download dropdown was opened.

### Fix
- Completely removed `video.muted = true` and all volume/mute manipulations from `chrome extension/content.js`.
- The companion extension now strictly reads video attributes (`video.videoHeight`, `video.currentSrc`) without altering playback or audio settings.

---

## 3. Gated Direct-MP4 1080p Download Failure & Showing Webpage URL Instead of Video URL

### Symptoms
Attempting to download a 1080p stream from a login-walled direct-MP4 video
site failed. The download prompt dialog in Wrench Downloader displayed the
webpage URL instead of the direct video stream URL. The download engine then
failed with a `401 Unauthorized / access denied` error from the generic
extractor.

### Root Cause
Two interacting issues in the Chrome Companion Extension caused this failure:

1. **Failure to sniff media stream JSON APIs (`injected.js`):**
   - When the site's video player loads, it makes an internal XHR/fetch
     request to a media stream JSON endpoint.
   - This endpoint returns a JSON payload listing available CDN streams
     (e.g. 1080p MP4, 720p MP4 hosted on a video CDN).
   - In `injected.js`, `sniffJsonBody` was previously configured to only
     inspect JSON responses that contained YouTube-specific `videoplayback`
     strings. As a result, it completely ignored the site's stream API
     response.

2. **Shadowing with a dummy webpage row (`content.js`):**
   - In `content.js`, `includeCurrentPlayingQuality` automatically prepended
     a fallback item to the media items list with:
     ```javascript
     url: postPageUrl // the watch-page URL, not a stream
     ```
   - When the user selected "1080p", the extension sent this webpage URL to
     Wrench Downloader rather than the direct CDN stream URL.
   - Because Wrench Downloader received a webpage URL rather than a `.mp4`
     stream, it passed the URL to `yt-dlp`.
   - `yt-dlp` attempted to scrape the page with the generic web extractor
     without authenticated credentials, returning `401 Unauthorized`.

### Fix
1. **Generic Media JSON Sniffing in `injected.js`:**
   Updated `sniffJsonBody` to parse generic media stream responses (player
   `qualities`/`sources`/`transcodings` lists and similar platforms) and
   extract direct stream URLs alongside their rendition labels (e.g.,
   `1080p`, `720p`).
2. **Strict Direct Stream URL Forwarding in `content.js`:**
   - Removed the assignment of `url: postPageUrl` in `includeCurrentPlayingQuality`.
   - Ensured that `activeVideo.currentSrc` or captured media stream URLs are always prioritized and sent as `item.url`.
   - When the direct CDN MP4 stream URL is sent to Wrench Downloader, `DownloadEngine.cs` identifies it as a direct media stream, bypasses `yt-dlp`, and downloads the stream at full network speed with HTTP Range multi-connection support.

---

## Verification Summary

| Issue | Status | Verification Method |
| :--- | :--- | :--- |
| **YouTube Muting** | **Fixed** | Verified removal of `video.muted` modifications in `content.js`. Player audio remains untouched upon click. |
| **Gated MP4 Stream URL** | **Fixed** | Verified direct stream JSON sniffing in `injected.js` and confirmed real CDN MP4 URLs are forwarded to app rather than page URL. |
| **Tray Icon Background Mode** | **Resolved** | Cleaned up window destruction race condition in `ExitApplication()` and established proper post-activation tray initialization. |
