// Wrench Downloader - Background Service Worker
// IDM-Style Network Sniffing: Intercepts media requests, manifests (m3u8, mpd),
// and direct video streams (mp4, webm, etc.) across all frames and tabs,
// capturing the exact stream URL, request headers (Referer, User-Agent), and content lengths.

const mediaByTab = new Map();
const requestHeadersMap = new Map(); // url -> { referer, userAgent }
const contentDispositionMap = new Map(); // url -> server filename
const APP_SERVER_URL = "http://127.0.0.1:45732/api/download";
// Latest browser-minted PO token per tab, harvested from videoplayback URLs
// (their pot= parameter). The playing browser already passed every gate, so
// its token + session is exactly what the desktop app needs and cannot mint.
const potByTab = new Map(); // tabId -> { token, time }
const POT_FRESH_MS = 30 * 60 * 1000;

function harvestPot(tabId, url) {
  try {
    if (tabId < 0 || !url) return;
    const m = String(url).match(/[?&]pot=([^&#]+)/i);
    if (!m || !m[1] || m[1].length < 8) return;
    potByTab.set(tabId, { token: decodeURIComponent(m[1]), time: Date.now() });
    if (potByTab.size > 50) {
      const firstKey = potByTab.keys().next().value;
      potByTab.delete(firstKey);
    }
  } catch (e) {}
}

function freshPot(tabId) {
  try {
    const e = potByTab.get(tabId);
    if (e && (Date.now() - e.time < POT_FRESH_MS)) return e.token;
  } catch (err) {}
  return "";
}

// SABR segment sets: tabId -> itag -> [{ u, range, len, time }].
// u = exact segment URL the browser fetched (signed, session-authorized).
// range = "bytes=a-b" (Range request header or range= query) or "".
// len = response Content-Length (segment bytes; full length when un-ranged).
// The app replays these in byte order and concatenates = the rendition file.
const sabrByTab = new Map();
const SABR_MAX_SEGMENTS = 1500;

function recordSabrSegment(tabId, url, details) {
  try {
    if (tabId < 0 || !url) return;
    const itagMatch = String(url).match(/[?&]itag=(\d+)/i);
    if (!itagMatch) return;
    const itag = parseInt(itagMatch[1], 10);
    let range = "";
    const rq = String(url).match(/[?&]range=([\d]+-[\d]*)/i);
    if (rq) range = "bytes=" + rq[1];
    if (!range) {
      try {
        const rec = requestHeadersMap.get(url);
        if (rec && rec.range) range = rec.range;
      } catch (e) {}
    }
    let len = 0;
    try {
      if (details && details.responseHeaders) {
        for (const h of details.responseHeaders) {
          if (h.name && h.name.toLowerCase() === "content-length") {
            len = parseInt(h.value || "0", 10) || 0;
            break;
          }
        }
      }
    } catch (e) {}
    // Skip non-media responses (empty pings).
    if (!len && !range) {
      // Still record range-less full-file responses; length unknown.
      if (!/\/videoplayback/i.test(url)) return;
    }
    let perTab = sabrByTab.get(tabId);
    if (!perTab) { perTab = new Map(); sabrByTab.set(tabId, perTab); }
    let list = perTab.get(itag);
    if (!list) { list = []; perTab.set(itag, list); }
    const key = url + "|" + range;
    for (const s of list) { if (s.key === key) return; } // seen
    list.push({ u: url, range: range, len: len, time: Date.now(), key: key });
    if (list.length > SABR_MAX_SEGMENTS) list.splice(0, list.length - SABR_MAX_SEGMENTS);
    if (sabrByTab.size > 20) {
      const firstKey = sabrByTab.keys().next().value;
      sabrByTab.delete(firstKey);
    }
  } catch (e) {}
}

function getSabrSets(tabId) {
  const out = [];
  try {
    const perTab = sabrByTab.get(tabId);
    if (!perTab) return out;
    for (const [itag, list] of perTab) {
      if (!list || list.length === 0) continue;
      out.push({
        itag: itag,
        segments: list.slice(-500).map(s => ({ u: s.u, range: s.range, len: s.len }))
      });
    }
  } catch (e) {}
  return out;
}

// Debug tracking: last sends to the desktop app (what site, what quality,
// how many session cookies, when). Pulled via GET_RECENT_SENDS when the user
// reports a broken download; matched against the app's startup.log by time.
const recentSends = [];
function trackSend(payload) {
  try {
    const page = payload.pageUrl || payload.referrer || "";
    let host = "";
    try { host = new URL(page || payload.url || "").hostname; } catch (e) {}
    recentSends.unshift({
      time: new Date().toISOString(),
      site: host,
      quality: payload.quality || "",
      format: payload.format || "",
      cookies: (payload.cookies || []).length,
      pot: payload.poToken ? 1 : 0,
      urlHost: (() => { try { return new URL(payload.url || "").hostname; } catch (e) { return ""; } })()
    });
    if (recentSends.length > 20) recentSends.pop();
  } catch (e) {}
}

function parseContentDispositionFilename(headerVal) {
  if (!headerVal) return "";
  try {
    // 1. Check filename*=UTF-8''... (RFC 5987 / RFC 6266)
    const starMatch = headerVal.match(/filename\*=([^;']+''|[^;']*;)?([^;]+)/i);
    if (starMatch && starMatch[2]) {
      let raw = starMatch[2].trim().replace(/^["']|["']$/g, "");
      return decodeURIComponent(raw);
    }
    // 2. Standard filename="..." or filename=...
    const stdMatch = headerVal.match(/filename\s*=\s*(?:"([^"]+)"|'([^']+)'|([^;\s]+))/i);
    if (stdMatch) {
      let raw = (stdMatch[1] || stdMatch[2] || stdMatch[3] || "").trim();
      return raw.replace(/^.*[\\\/]/, "");
    }
  } catch (e) {}
  return "";
}

// 1. Capture request headers (Referer, User-Agent) before sending
chrome.webRequest.onBeforeSendHeaders.addListener(
  (details) => {
    if (!details.url || details.tabId < 0) return;
    let referer = "";
    let userAgent = "";
    const streamHeaders = {};
    const forwardedHeaderNames = new Set([
      "accept", "accept-language", "cookie", "origin", "referer", "user-agent",
      "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform",
      "sec-fetch-dest", "sec-fetch-mode", "sec-fetch-site", "sec-fetch-user"
    ]);

    let rangeHeader = "";
    if (details.requestHeaders) {
      for (const h of details.requestHeaders) {
        const name = h.name.toLowerCase();
        if (name === "referer") referer = h.value || "";
        if (name === "user-agent") userAgent = h.value || "";
        if (name === "range") rangeHeader = h.value || "";
        // Reproduce the browser's media request in the desktop downloader.
        // Range/Host/transport headers are intentionally omitted: the app
        // requests the complete signed rendition URL itself.
        if (forwardedHeaderNames.has(name) && h.value) streamHeaders[h.name] = h.value;
      }
    }

    if (referer || userAgent || rangeHeader || Object.keys(streamHeaders).length > 0) {
      requestHeadersMap.set(details.url, { referer, userAgent, streamHeaders, range: rangeHeader, time: Date.now() });
      // Keep map small
      if (requestHeadersMap.size > 200) {
        const firstKey = requestHeadersMap.keys().next().value;
        requestHeadersMap.delete(firstKey);
      }
    }
  },
  { urls: ["<all_urls>"] },
  ["requestHeaders", "extraHeaders"]
);

// 2. Inspect response headers (Content-Type, Content-Length) and URL patterns
chrome.webRequest.onHeadersReceived.addListener(
  (details) => {
    if (!details.url || details.tabId < 0) return;
    const url = details.url;

    // Harvest attestation tokens from EVERY playing-stream request (even
    // chunk continuations and SABR handshakes that are otherwise ignored).
    harvestPot(details.tabId, url);

    // Ignore chunked video fragments / ping requests to avoid cluttering dropdown.
    // Generic URL patterns only - no per-site rules.
    // NOTE: /videoplayback is deliberately NOT ignored: range-less requests
    // are the exact bytes the browser is already playing (authorized session
    // included), which is precisely what the desktop app cannot re-resolve on
    // gated pages. Chunk continuations (range/bytestart) are still skipped.
    // SABR redirectors (?aitags= list) are also skipped: only playable
    // through the page's live handshake, never directly downloadable - the
    // HLS master (hls_playlist, embeds all renditions + audio) is the prize.
    // BUT their per-segment traffic IS the content: record every SABR
    // segment (itag + byte range) per tab so the app can replay the exact
    // bytes the browser fetched and reassemble the file. Pure-SABR gated
    // videos have no other downloadable form.
    // (.ts = HLS fragments: a 10s chunk that plays only a fragment, never the video.)
    if (/\/videoplayback/i.test(url) && /[?&]itag=\d+/i.test(url)) {
      recordSabrSegment(details.tabId, url, details);
    }
    if (/[?&]aitags=/i.test(url)) return;
    if (
      url.includes("/segment") ||
      url.includes("/frag") ||
      url.includes("range=") ||
      url.includes("bytestart=") ||
      url.includes("byteend=") ||
      url.includes(".m4s") ||
      url.includes("init.mp4") ||
      url.includes("init.m4s") ||
      /\.ts($|\?|#)/i.test(url) ||
      /preview|thumb|poster|sprite|storyboard|\/ads?\//i.test(url) ||
      url.endsWith(".key") ||
      url.includes("beacon") ||
      url.includes("analytics")
    ) {
      return;
    }

    let contentType = "";
    let contentLength = 0;
    let contentDisposition = "";

    if (details.responseHeaders) {
      for (const h of details.responseHeaders) {
        const name = h.name.toLowerCase();
        if (name === "content-type") contentType = (h.value || "").toLowerCase();
        if (name === "content-length") contentLength = parseInt(h.value || "0", 10);
        if (name === "content-disposition") contentDisposition = h.value || "";
      }
    }

    if (contentDisposition) {
      const serverFileName = parseContentDispositionFilename(contentDisposition);
      if (serverFileName) {
        contentDispositionMap.set(url, serverFileName);
        if (contentDispositionMap.size > 200) {
          const firstKey = contentDispositionMap.keys().next().value;
          contentDispositionMap.delete(firstKey);
        }
      }
    }

    const itag = itagOf(url);
    const itagHeight = ITAG_HEIGHTS[itag] || 0;
    const itagAudio = ITAG_AUDIO.has(itag);
    const isAudioTrack = itagAudio || contentType.startsWith("audio/") ||
      url.includes("/mp4a/") || url.includes("/audio/") || url.includes("/aac/") ||
      url.match(/\.(mp3|aac|m4a|ogg|opus)($|\?)/i) || url.match(/[-_]audio(\.|\/|$)/i);
    // Extension-less manifests classify by path markers (see isManifestUrl).
    const manifestKind = isManifestUrl(url) ? manifestType(url) : "";
    const isHls = manifestKind === "hls" || url.includes(".m3u8") || contentType.includes("mpegurl") || contentType.includes("application/x-mpegurl");
    const isDash = manifestKind === "dash" || url.includes(".mpd") || contentType.includes("dash+xml");
    const isVideo = contentType.startsWith("video/") || url.match(/\.(mp4|webm|mkv|m4v|mov|avi)($|\?)/i) ||
      (itagHeight > 0 && !itagAudio);

    if (isHls || isDash || isVideo || isAudioTrack) {
      const headers = requestHeadersMap.get(url) || {};
      const type = isAudioTrack ? "audio" : isHls ? "hls" : isDash ? "dash" : "video";

      addDetectedMedia(details.tabId, {
        url: url,
        title: guessTitle(url),
        type: type,
        quality: itagHeight > 0 ? `${itagHeight}p` : "",
        size: formatBytes(contentLength),
        contentLengthBytes: contentLength,
        contentType: contentType,
        referer: headers.referer || details.initiator || "",
        userAgent: headers.userAgent || "",
        streamHeaders: headers.streamHeaders || {},
        time: Date.now()
      });
    }
  },
  { urls: ["<all_urls>"] },
  ["responseHeaders", "extraHeaders"]
);

// Fallback: onBeforeRequest for direct m3u8 or mp4 matches if headers haven't fired
chrome.webRequest.onBeforeRequest.addListener(
  (details) => {
    if (!details.url || details.tabId < 0) return;
    const url = details.url;

    harvestPot(details.tabId, url);

    // Extension-resolved architecture: range-less videoplayback URLs with a
    // rendition id are the authorized stream itself - capture them here too
    // (onHeadersReceived is primary, this is the fallback).
    const isResolvablePlayback = /\/videoplayback/i.test(url) &&
      /[?&]itag=\d+/i.test(url) &&
      !/[?&](range|bytestart|byteend|aitags|sabr)=/i.test(url);
    if (
      (url.includes("/videoplayback") && !isResolvablePlayback) ||
      url.includes("/segment") ||
      url.includes("/frag") ||
      url.includes("range=")
    ) {
      return;
    }

    if (isResolvablePlayback) {
      addDetectedMedia(details.tabId, {
        url: url,
        title: guessTitle(url),
        type: "video",
        quality: (() => { const m = url.match(/[?&]itag=(\d+)/i); const h = m ? (ITAG_HEIGHTS[parseInt(m[1], 10)] || 0) : 0; return h > 0 ? `${h}p` : ""; })(),
        size: "",
        time: Date.now()
      });
      return;
    }

    if (isManifestUrl(url)) {
      const kind = manifestType(url);
      addDetectedMedia(details.tabId, {
        url: url,
        title: guessTitle(url),
        type: kind,
        quality: "",
        size: "",
        time: Date.now()
      });
    }
  },
  { urls: ["<all_urls>"] }
);

function guessTitle(url) {
  try {
    const urlObj = new URL(url);
    // Rendition-id stream URLs and extension-less manifests carry no
    // filename; the content script replaces this with the real video title
    // once it associates the stream.
    if (/videoplayback|hls_playlist|hls_variant/i.test(urlObj.pathname) ||
        /manifest/i.test(urlObj.pathname + urlObj.hostname)) return "Video";
    // 1. Check common query parameter names used by file download mirrors (e.g. slug=win64.exe.zip, file=..., filename=...)
    for (const param of ["slug", "file", "filename", "name", "title"]) {
      const val = urlObj.searchParams.get(param);
      if (val && val.includes(".")) {
        return decodeURIComponent(val.replace(/^.*[\\\/]/, ""));
      }
    }

    // 2. Extract from URL path
    const pathname = urlObj.pathname;
    const parts = pathname.split("/").filter(Boolean);
    const last = parts.length > 0 ? parts[parts.length - 1] : "";
    if (last && last.includes(".")) {
      return decodeURIComponent(last);
    }

    if (last && last.toLowerCase() !== "download" && last.toLowerCase() !== "video") {
      return decodeURIComponent(last);
    }
  } catch (e) {}

  return "Download";
}

// Extension-less manifests (IDM's bread and butter): master/media playlist
// URLs with no .m3u8/.mpd in them, e.g. manifest.googlevideo.com HLS masters.
// Matched structurally by playlist path markers, never by site name.
function isManifestUrl(u) {
  const s = String(u || "").toLowerCase();
  return s.includes(".m3u8") || s.includes(".mpd") ||
    s.includes("hls_playlist") || s.includes("hls_variant") ||
    s.includes("manifest.googlevideo.com") || /\/manifest\//.test(s);
}

function manifestType(u) {
  const s = String(u || "").toLowerCase();
  return (s.includes(".mpd") || /\/manifest\/dash/.test(s)) ? "dash" : "hls";
}

// Rendition-id table: stream URLs carrying ?itag=N& identify the exact bytes
// the browser is playing (progressive or DASH renditions). Height 0 =
// unknown id; audio ids double as the audio-only download.
const ITAG_HEIGHTS = {
  17: 144, 36: 240, 18: 360, 43: 360, 22: 720,
  160: 144, 133: 240, 242: 240, 394: 144, 395: 240,
  134: 360, 243: 360, 396: 360, 234: 480, 235: 480,
  135: 480, 244: 480, 397: 480, 136: 720, 247: 720,
  298: 720, 302: 720, 398: 720, 137: 1080, 248: 1080,
  299: 1080, 303: 1080, 399: 1080, 264: 1440, 271: 1440,
  400: 1440, 266: 2160, 313: 2160, 401: 2160
};
const ITAG_AUDIO = new Set([139, 140, 141, 256, 258, 249, 250, 251]);
const ITAG_WEBM = new Set([43, 242, 243, 244, 247, 248, 249, 250, 251, 271, 272, 302, 303, 313, 394, 395, 396, 397, 398, 399, 400, 401]);

function itagOf(url) {
  const m = String(url || "").match(/[?&]itag=(\d+)\b/i);
  return m ? parseInt(m[1], 10) : 0;
}

function formatBytes(bytes) {  if (!bytes || bytes <= 0) return "";
  const units = ["B", "KB", "MB", "GB"];
  let i = 0;
  let val = bytes;
  while (val >= 1024 && i < units.length - 1) {
    val /= 1024;
    i++;
  }
  return `${val.toFixed(1)} ${units[i]}`;
}

// Signed stream URLs carry their own death date (?expire=unix). A dead
// edge hostname (NXDOMAIN) or 410 is guaranteed past expiry - never store
// or offer those; the tab must replay to mint fresh links (IDM behaves the
// same: it only ever offers live-sniffed streams).
function isExpiredUrl(u) {
  try {
    const s = String(u || "");
    let m = s.match(/[?&]expire=(\d+)/i);
    if (!m) {
      // Some CDNs sign with e=<epoch> instead (e.g. phncdn masters).
      const e2 = s.match(/[?&]e=(\d{10})/);
      if (!e2) return false;
      const v = parseInt(e2[1], 10);
      if (v < 1000000000 || v > 4000000000) return false;
      m = e2;
    }
    return parseInt(m[1], 10) < (Date.now() / 1000 - 60);
  } catch (e) { return false; }
}

function addDetectedMedia(tabId, item) {
  if (!item || !item.url) return;
  if (isExpiredUrl(item.url)) return;
  if (!mediaByTab.has(tabId)) {
    mediaByTab.set(tabId, []);
  }
  const list = mediaByTab.get(tabId);
  
  const existing = list.find((m) => m.url === item.url);
  if (!existing) {
    list.unshift(item); // Newest on top
    if (list.length > 50) list.pop();

    // Broadcast to content scripts in the tab
    chrome.tabs.sendMessage(tabId, { action: "MEDIA_DETECTED", media: item }).catch(() => {});
  } else {
    // Update size or headers if newly resolved
    if (item.size && !existing.size) existing.size = item.size;
    if (item.contentLengthBytes && !existing.contentLengthBytes) existing.contentLengthBytes = item.contentLengthBytes;
    if (item.referer && !existing.referer) existing.referer = item.referer;
    if (item.userAgent && !existing.userAgent) existing.userAgent = item.userAgent;
    if (item.streamHeaders && Object.keys(item.streamHeaders).length > 0) {
      existing.streamHeaders = { ...(existing.streamHeaders || {}), ...item.streamHeaders };
      chrome.tabs.sendMessage(tabId, { action: "MEDIA_DETECTED", media: existing }).catch(() => {});
    }
  }
}

// Clean up when tabs close
chrome.tabs.onRemoved.addListener((tabId) => {
  mediaByTab.delete(tabId);
  potByTab.delete(tabId);
  sabrByTab.delete(tabId);
});

// A top-level navigation starts a new page context. Drop the old page's
// streams and attestation so a new video cannot inherit expired links.
chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (changeInfo.status === "loading") {
    mediaByTab.delete(tabId);
    potByTab.delete(tabId);
  }
});

// Intercept general browser downloads (MediaFire, direct downloads, zip, exe, rar, pdf, iso, etc.)
// and hand them over to Wrench Downloader app.
const interceptedDownloadIds = new Set();

async function handleInterceptedDownload(downloadItem) {
  try {
    if (!downloadItem || !downloadItem.url) return;
    const url = downloadItem.finalUrl || downloadItem.url;

    // Do not intercept data URIs or blob URIs created internally by pages
    if (url.startsWith("data:") || url.startsWith("blob:") || url.startsWith("chrome-extension://")) {
      return;
    }

    if (interceptedDownloadIds.has(downloadItem.id)) return;
    interceptedDownloadIds.add(downloadItem.id);

    // Cancel the browser's native download so Wrench Downloader takes over
    try {
      await chrome.downloads.cancel(downloadItem.id);
      await chrome.downloads.erase({ id: downloadItem.id });
    } catch (e) {}

    // Priority for filename sent by the server / page:
    // 1. downloadItem.filename (available in onDeterminingFilename after server headers/redirects)
    // 2. Server Content-Disposition header recorded from webRequest
    // 3. Fallback to URL query slug / path parameters
    let rawFilename = downloadItem.filename ? downloadItem.filename.replace(/^.*[\\\/]/, "").trim() : "";
    if (!rawFilename) {
      rawFilename = contentDispositionMap.get(url) || contentDispositionMap.get(downloadItem.url) || "";
    }
    let title = rawFilename || guessTitle(url) || guessTitle(downloadItem.url);
    if (!title || title.toLowerCase() === "download") {
      title = guessTitle(url);
    }

    // Retrieve captured headers (Referer, User-Agent)
    const headers = requestHeadersMap.get(url) || requestHeadersMap.get(downloadItem.url) || {};

    const payload = {
      url: url,
      title: title,
      quality: "file",
      format: title.includes(".") ? title.split(".").pop().toLowerCase() : "",
      pageUrl: downloadItem.referrer || "",
      referrer: headers.referer || downloadItem.referrer || "",
      userAgent: headers.userAgent || navigator.userAgent,
      streamHeaders: headers.streamHeaders || {},
      prompt: true
    };

    await sendToDesktopApp(payload);
  } catch (err) {
    console.warn("Could not forward browser download to Wrench Downloader:", err);
  }
}

// 1. chrome.downloads.onDeterminingFilename fires AFTER server headers (Content-Disposition, redirects) have arrived
if (chrome.downloads && chrome.downloads.onDeterminingFilename) {
  chrome.downloads.onDeterminingFilename.addListener((downloadItem, suggest) => {
    chrome.storage.local.get({ interceptDownloads: true }).then((settings) => {
      if (settings.interceptDownloads !== false) {
        handleInterceptedDownload(downloadItem);
      } else {
        suggest(); // Let Chrome proceed normally
      }
    }).catch(() => suggest());

    return true; // Keep suggest callback valid asynchronously
  });
}

// 2. Fallback onCreated in case onDeterminingFilename is not triggered
if (chrome.downloads && chrome.downloads.onCreated) {
  chrome.downloads.onCreated.addListener(async (downloadItem) => {
    // If onDeterminingFilename is supported, give it a moment to fire first
    if (chrome.downloads.onDeterminingFilename) {
      setTimeout(async () => {
        if (!interceptedDownloadIds.has(downloadItem.id)) {
          const settings = await chrome.storage.local.get({ interceptDownloads: true });
          if (settings.interceptDownloads !== false) {
            handleInterceptedDownload(downloadItem);
          }
        }
      }, 700);
      return;
    }

    const settings = await chrome.storage.local.get({ interceptDownloads: true });
    if (settings.interceptDownloads !== false) {
      handleInterceptedDownload(downloadItem);
    }
  });
}

// Message listener from content script or popup
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  const tabId = sender.tab ? sender.tab.id : message.tabId;

  if (message.action === "GET_RECENT_SENDS") {
    sendResponse({ sends: recentSends });
    return true;
  }

  if (message.action === "GET_MEDIA") {
    // Only this tab's streams: leaking another tab's media here produced
    // dropdown rows whose URLs don't match the current video (rows that fail).
    const items = (tabId && mediaByTab.get(tabId)) || [];
    sendResponse({ media: items, sabr: getSabrSets(tabId || -1) });
    return true;
  }

  if (message.action === "CLEAR_MEDIA") {
    if (tabId !== undefined && tabId !== null && tabId >= 0) mediaByTab.delete(tabId);
    sendResponse({ status: "ok" });
    return true;
  }

  if (message.action === "REPORT_MEDIA") {
    if (message.media) {
      const targetTabId = tabId || -1;
      addDetectedMedia(targetTabId, message.media);
      sendResponse({ status: "ok" });
    }
    return true;
  }

  if (message.action === "SEND_TO_APP") {
    const sendTabId = sender.tab ? sender.tab.id : message.tabId;
    try {
      if (sendTabId !== undefined && sendTabId !== null && sendTabId >= 0 && message.payload) {
        const captured = (mediaByTab.get(sendTabId) || []).find(m => m.url === message.payload.url);
        const headers = captured && captured.streamHeaders;
        if (headers && Object.keys(headers).length > 0) message.payload.streamHeaders = headers;
      }
      if (sendTabId !== undefined && sendTabId !== null && sendTabId >= 0 &&
          message.payload && !message.payload.poToken) {
        const pot = freshPot(sendTabId);
        if (pot) message.payload.poToken = pot;
      }
    } catch (e) {}
    trackSend(message.payload);
    sendToDesktopApp(message.payload, sendTabId)
      .then((res) => sendResponse({ success: true, result: res }))
      .catch((err) => sendResponse({ success: false, error: err.message }));
    return true;
  }

  if (message.action === "MSE_BEGIN") {
    const sendTabId = sender.tab ? sender.tab.id : message.tabId;
    const payload = {
      title: message.title || "Recording",
      quality: "record",
      format: "mp4",
      pageUrl: message.pageUrl || "",
      referrer: message.referrer || "",
      userAgent: message.userAgent || navigator.userAgent,
      mse: true,
      uploadId: message.uploadId || "",
      prompt: false
    };
    try {
      if (sendTabId !== undefined && sendTabId !== null && sendTabId >= 0 && !payload.poToken) {
        const pot = freshPot(sendTabId);
        if (pot) payload.poToken = pot;
      }
    } catch (e) {}
    sendToDesktopApp(payload, sendTabId)
      .then((res) => sendResponse({ success: true, result: res }))
      .catch((err) => sendResponse({ success: false, error: err.message }));
    return true;
  }

  if (message.action === "MSE_CHUNK") {
    // Ordered per track: localhost POSTs for one track chain sequentially so
    // the app appends bytes in playback order even under concurrency.
    const key = (message.uploadId || "") + "|" + (message.track || "video");
    const prev = msePostChains.get(key) || Promise.resolve();
    const next = prev.then(() => postMseChunk(message)).catch(() => {});
    msePostChains.set(key, next);
    if (msePostChains.size > 20) {
      const firstKey = msePostChains.keys().next().value;
      msePostChains.delete(firstKey);
    }
    sendResponse({ status: "queued" });
    return true;
  }

  if (message.action === "MSE_END") {
    postMseFinish(message)
      .then((res) => sendResponse({ success: true, result: res }))
      .catch((err) => sendResponse({ success: false, error: err.message }));
    return true;
  }
});

// In-flight POST chains per upload+track (see MSE_CHUNK above).
const msePostChains = new Map();

async function postMseChunk(message) {
  const params = new URLSearchParams({
    uploadId: message.uploadId || "",
    track: message.track === "audio" ? "audio" : "video",
    seq: String(message.seq || 0)
  });
  const response = await fetch(`${APP_SERVER_URL.replace("/api/download", "/api/segment")}?${params}`, {
    method: "POST",
    body: message.data
  });
  if (!response.ok) {
    throw new Error(`segment rejected: ${response.status}`);
  }
}

async function postMseFinish(message) {
  const params = new URLSearchParams({
    uploadId: message.uploadId || "",
    reason: message.reason || "stop"
  });
  const response = await fetch(`${APP_SERVER_URL.replace("/api/download", "/api/finish")}?${params}`, {
    method: "POST"
  });
  if (!response.ok) {
    throw new Error(`finish rejected: ${response.status}`);
  }
  return await response.json();
}

async function sendToDesktopApp(payload, tabId) {
  // Hand the app the live browser session: cookie-DB export (yt-dlp
  // --cookies-from-browser) fails while Chrome runs with a locked profile,
  // but the extension can read its own tabs' cookies directly - including
  // HttpOnly ones - so age/logged-in gates open exactly like they do for
  // browser-attached downloaders. Best-effort: older installs without the
  // "cookies" permission simply send none.
  try {
    const cookieUrls = [...new Set([payload.url, payload.pageUrl || payload.referrer].filter(u =>
      typeof u === "string" && /^https?:\/\//i.test(u)))];
    if (cookieUrls.length && chrome.cookies && chrome.cookies.getAll) {
      const allCookies = [];
      for (const cookieUrl of cookieUrls) {
        const raw = await chrome.cookies.getAll({ url: cookieUrl });
        if (raw) allCookies.push(...raw);
      }
      const uniqueCookies = new Map();
      for (const c of allCookies) {
        const key = `${c.domain || ""}|${c.path || "/"}|${c.name || ""}`;
        uniqueCookies.set(key, c);
      }
      if (uniqueCookies.size > 0) {
        payload.cookies = Array.from(uniqueCookies.values()).slice(0, 100).map(c => ({
          name: c.name || "",
          value: c.value || "",
          domain: (c.domain || "").replace(/^\./, ""),
          path: c.path || "/",
          secure: !!c.secure,
          expiry: c.expirationDate ? Math.floor(c.expirationDate) : 0
        }));
      }
    }
  } catch (e) {}

  // Browser-minted attestation for the playing tab (see harvestPot).
  try {
    if (!payload.poToken && tabId !== undefined && tabId !== null && tabId >= 0) {
      const pot = freshPot(tabId);
      if (pot) payload.poToken = pot;
    }
  } catch (e) {}

  try {
    const response = await fetch(APP_SERVER_URL, {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify(payload)
    });
    if (!response.ok) {
      throw new Error(`App returned error: ${response.status} ${response.statusText}`);
    }
    return await response.json();
  } catch (error) {
    console.error("Failed to connect to Wrench Downloader Desktop App:", error);
    throw error;
  }
}
