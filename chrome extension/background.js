// Wrench Downloader - Background Service Worker
// IDM-Style Network Sniffing: Intercepts media requests, manifests (m3u8, mpd),
// and direct video streams (mp4, webm, etc.) across all frames and tabs,
// capturing the exact stream URL, request headers (Referer, User-Agent), and content lengths.

const mediaByTab = new Map();
const requestHeadersMap = new Map(); // url -> { referer, userAgent }
const APP_SERVER_URL = "http://127.0.0.1:45732/api/download";

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

    // Ignore chunked video fragments / ping requests to avoid cluttering dropdown.
    // Generic URL patterns only - no per-site rules.
    if (
      url.includes("/videoplayback") ||
      url.includes("/segment") ||
      url.includes("/frag") ||
      url.includes("range=") ||
      url.includes("bytestart=") ||
      url.includes("byteend=") ||
      url.includes(".m4s") ||
      url.includes("init.mp4") ||
      url.includes("init.m4s") ||
      url.endsWith(".key") ||
      url.includes("beacon") ||
      url.includes("analytics")
    ) {
      return;
    }

    let contentType = "";
    let contentLength = 0;

    if (details.responseHeaders) {
      for (const h of details.responseHeaders) {
        const name = h.name.toLowerCase();
        if (name === "content-type") contentType = (h.value || "").toLowerCase();
        if (name === "content-length") contentLength = parseInt(h.value || "0", 10);
      }
    }

    const isAudioTrack = contentType.startsWith("audio/") ||
      url.includes("/mp4a/") || url.includes("/audio/") || url.includes("/aac/") ||
      url.match(/\.(mp3|aac|m4a|ogg|opus)($|\?)/i) || url.match(/[-_]audio(\.|\/|$)/i);
    const isHls = url.includes(".m3u8") || contentType.includes("mpegurl") || contentType.includes("application/x-mpegurl");
    const isDash = url.includes(".mpd") || contentType.includes("dash+xml");
    const isVideo = contentType.startsWith("video/") || url.match(/\.(mp4|webm|mkv|flv|m4v|mov|avi|ts)($|\?)/i);

    if (isHls || isDash || isVideo || isAudioTrack) {
      const headers = requestHeadersMap.get(url) || {};
      const type = isAudioTrack ? "audio" : isHls ? "hls" : isDash ? "dash" : "video";

      addDetectedMedia(details.tabId, {
        url: url,
        title: guessTitle(url),
        type: type,
        size: formatBytes(contentLength),
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
    const cleanUrl = url.split("?")[0].split("#")[0];
    const parts = cleanUrl.split("/");
    let last = parts[parts.length - 1] || "video";
    return decodeURIComponent(last);
  } catch (e) {
    return "Video Stream";
  }
}

function formatBytes(bytes) {
  if (!bytes || bytes <= 0) return "";
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
    if (item.referer && !existing.referer) existing.referer = item.referer;
    if (item.userAgent && !existing.userAgent) existing.userAgent = item.userAgent;
  }
}

// Clean up when tabs close
chrome.tabs.onRemoved.addListener((tabId) => {
  mediaByTab.delete(tabId);
});

// Message listener from content script or popup
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  const tabId = sender.tab ? sender.tab.id : message.tabId;

  if (message.action === "GET_MEDIA") {
    let items = (tabId && mediaByTab.get(tabId)) || [];
    // If empty for this tab, check if there's any recent stream across tabs
    if (items.length === 0) {
      for (const [tId, tItems] of mediaByTab.entries()) {
        if (tItems && tItems.length > 0) {
          items = tItems;
          break;
        }
      }
    }
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
    sendToDesktopApp(message.payload)
      .then((res) => sendResponse({ success: true, result: res }))
      .catch((err) => sendResponse({ success: false, error: err.message }));
    return true;
  }
});

async function sendToDesktopApp(payload) {
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
