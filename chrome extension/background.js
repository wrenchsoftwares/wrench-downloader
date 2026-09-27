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

    if (details.requestHeaders) {
      for (const h of details.requestHeaders) {
        const name = h.name.toLowerCase();
        if (name === "referer") referer = h.value || "";
        if (name === "user-agent") userAgent = h.value || "";
      }
    }

    if (referer || userAgent) {
      requestHeadersMap.set(details.url, { referer, userAgent, time: Date.now() });
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
    // (.ts = HLS fragments: a 10s chunk that plays only a fragment, never the video.)
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
    const isHls = url.includes(".m3u8") || contentType.includes("mpegurl") || contentType.includes("application/x-mpegurl");
    const isDash = url.includes(".mpd") || contentType.includes("dash+xml");
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

    if (
      url.includes("/videoplayback") ||
      url.includes("/segment") ||
      url.includes("/frag") ||
      url.includes("range=")
    ) {
      return;
    }

    if (url.includes(".m3u8") || url.includes(".mpd")) {
      addDetectedMedia(details.tabId, {
        url: url,
        title: guessTitle(url),
        type: url.includes(".mpd") ? "dash" : "hls",
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
    // Rendition-id stream URLs carry no filename; the content script replaces
    // this with the real video title once it associates the stream.
    if (/videoplayback/i.test(urlObj.pathname)) return "Video";
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

function addDetectedMedia(tabId, item) {
  if (!item || !item.url) return;
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
  }
}

// Clean up when tabs close
chrome.tabs.onRemoved.addListener((tabId) => {
  mediaByTab.delete(tabId);
  potByTab.delete(tabId);
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
    sendResponse({ media: items });
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
});

async function sendToDesktopApp(payload, tabId) {
  // Hand the app the live browser session: cookie-DB export (yt-dlp
  // --cookies-from-browser) fails while Chrome runs with a locked profile,
  // but the extension can read its own tabs' cookies directly - including
  // HttpOnly ones - so age/logged-in gates open exactly like they do for
  // browser-attached downloaders. Best-effort: older installs without the
  // "cookies" permission simply send none.
  try {
    const cookieUrl = payload.pageUrl || payload.referrer || payload.url || "";
    if (cookieUrl && /^https?:\/\//i.test(cookieUrl) &&
        chrome.cookies && chrome.cookies.getAll) {
      const raw = await chrome.cookies.getAll({ url: cookieUrl });
      if (raw && raw.length > 0) {
        payload.cookies = raw.slice(0, 100).map(c => ({
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
