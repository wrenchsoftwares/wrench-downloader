# YouTube Stream Interception, Chunks, and Browser Merging Documentation

> **Status note (2026-09-28):** background reading only (§1–§2, §4 token
> expiry). The Stage A–D pipeline below (chunk capture + `FFmpeg.wasm`
> in-browser muxing) is **NOT** the architecture this project uses and will
> **NOT** defeat age/login gates. The working design is documented in
> `docs/idm-extension-mechanism.md` + `docs/idm-app-reception.md`: the
> extension resolves fresh, directly replayable stream URLs (prefer the HLS
> master `hls_playlist` embedding video+audio; never SABR `aitags=`
> redirectors or past-`expire=` links) and forwards the live browser session,
> while the desktop app downloads the bytes and muxes with native ffmpeg.
> Do not build Stage B/C in the extension.

This document provides a technical overview of how YouTube delivers media streams via `videoplayback` chunks, how adaptive streaming works, and the architectural steps required to build a Chrome extension that captures and merges these streams using WebAssembly (`FFmpeg.wasm`).

---

## 1. Understanding YouTube Stream Architecture

Modern video platforms like YouTube do not deliver content as a single monolithic file. Instead, they leverage adaptive bitrate streaming protocols (such as DASH) which split media into discrete segments.

### Key Characteristics of `videoplayback` Chunks
* **Fragmentation:** Video and audio are sliced into small sequential segments (typically 2 to 10 seconds each).
* **Separation of Tracks:** Video and audio streams are requested independently. 
  * Video streams contain visual frame data (`mime=video/mp4` or `mime=video/webm`) with **no audio track**.
  * Audio streams contain sound data (`mime=audio/mp4` or `mime=audio/webm`) with **no video track**.
* **Dynamic Range Requests:** URLs include HTTP range parameters (e.g., `range=0-50000`) allowing the player to buffer content on-demand and pause downloading if the user pauses playback.

---

## 2. Browser Playback via Media Source Extensions (MSE)

The native HTML5 `<video>` element relies on JavaScript APIs called **Media Source Extensions** to manage these streams:
1. The browser's media engine fetches raw binary chunks of video and audio via asynchronous network operations.
2. These chunks are pushed into separate source buffers.
3. The MSE decoder synchronizes and stitches the tracks together in real-time during user playback.

---

## 3. Implementing a Chrome Extension Workflow

To replicate or automate stream capturing and file compilation within a browser extension, developers follow a four-stage pipeline:

### Stage A: Interception & Detection
* The extension utilizes background scripts or Web Request APIs to listen for network signatures containing keywords like `videoplayback`.
* Incoming URLs are filtered and categorized into isolated tracking arrays for video versus audio streams.

### Stage B: Data Buffering & Storage
* Instead of letting the browser player handle tiny fragments transiently, background workers fetch the binary data directly.
* Because full-length videos exceed standard extension memory limitations, raw data `Blobs` are stored locally using **IndexedDB**.

### Stage C: WebAssembly Muxing (`FFmpeg.wasm`)
To merge separate streams without native software like desktop FFmpeg, extensions rely on **FFmpeg.wasm** running inside a dedicated Chrome **Offscreen Document**:
1. Stored video and audio blobs are transferred to the WebAssembly worker environment.
2. The internal FFmpeg binary executes a copy-muxing command:
   $$\text{ffmpeg} -i \text{video.mp4} -i \text{audio.m4a} -c:v \text{copy} -c:a \text{copy output.mp4}$$
3. The compiled media stream is wrapped into a local blob URL.

### Stage D: Exporting the File
* The extension triggers `chrome.downloads.download()` using the generated blob URL to save the finished, synchronized media container straight to the user's disk.

---

## 4. Technical Caveats and Limitations

* **Token Expiration:** YouTube security tokens embedded within `videoplayback` query strings have a short lifetime. Chunks must be fetched quickly after identification.
* **Store Policies:** Chrome Web Store policies strictly monitor extensions targeting Google-owned video services, meaning utility implementations often require local sideloading or external helper runtimes.