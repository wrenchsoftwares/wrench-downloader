// Wrench Downloader - In-Page Sniffer (runs in MAIN world)
// Hooks fetch, XMLHttpRequest, and MediaSource to catch HLS (.m3u8), DASH (.mpd),
// and direct media URLs the millisecond they are requested by web players (e.g. JWPlayer, VideoJS, Hls.js)
(function () {
  if (window.__WRENCH_INJECTED__) return;
  window.__WRENCH_INJECTED__ = true;

  const capturedUrls = new Set();

  function notifyStream(url, type, extra) {
    if (!url || typeof url !== "string") return;
    if (
      url.includes("/videoplayback") ||
      url.includes("/segment") ||
      url.includes("/frag") ||
      url.includes("range=") ||
      url.includes("bytestart=") ||
      url.includes("byteend=") ||
      url.includes(".m4s") ||
      url.includes("init.mp4") ||
      url.includes("init.m4s")
    ) {
      return;
    }

    // Filter for streaming playlists or media files
    const isAudioTrack = url.includes("/mp4a/") || url.includes("/audio/") || url.includes("/aac/") ||
      url.match(/\.(mp3|aac|m4a|ogg|opus)($|\?)/i) || url.match(/[-_]audio(\.|\/|$)/i);
    const resolvedType = isAudioTrack ? "audio" : (type || (url.includes(".mpd") ? "dash" : url.includes(".m3u8") ? "hls" : "video"));

    const isStream = 
      url.includes(".m3u8") || 
      url.includes(".mpd") || 
      url.includes("master.") ||
      url.includes("playlist.") ||
      url.match(/\.(mp4|webm|mkv|flv|m4v|mov|avi|ts)($|\?)/i) ||
      resolvedType === "hls" || resolvedType === "dash" || resolvedType === "video" || resolvedType === "audio";

    if (isStream && !capturedUrls.has(url)) {
      capturedUrls.add(url);
      window.dispatchEvent(new CustomEvent("__WRENCH_STREAM_CAPTURED__", {
        detail: {
          url: url,
          type: resolvedType,
          extra: extra || {}
        }
      }));
    }
  }

  // 1. Hook window.fetch
  if (window.fetch) {
    const originalFetch = window.fetch;
    window.fetch = async function (...args) {
      const input = args[0];
      const url = typeof input === "string" ? input : (input && input.url ? input.url : "");
      
      if (url && (url.includes(".m3u8") || url.includes(".mpd") || url.includes("playlist") || url.includes("master"))) {
        notifyStream(url, url.includes(".mpd") ? "dash" : "hls");
      }

      try {
        const response = await originalFetch.apply(this, args);
        try {
          const contentType = response.headers.get("content-type") || "";
          if (contentType.includes("mpegurl") || contentType.includes("application/x-mpegurl")) {
            notifyStream(response.url || url, "hls");
          } else if (contentType.includes("dash+xml")) {
            notifyStream(response.url || url, "dash");
          } else if (contentType.startsWith("video/")) {
            notifyStream(response.url || url, "video");
          }
        } catch (e) {}
        return response;
      } catch (err) {
        return originalFetch.apply(this, args);
      }
    };
  }

  // 2. Hook XMLHttpRequest
  if (window.XMLHttpRequest) {
    const origOpen = XMLHttpRequest.prototype.open;
    const origSend = XMLHttpRequest.prototype.send;

    XMLHttpRequest.prototype.open = function (method, url, ...rest) {
      this._wrenchUrl = url;
      if (typeof url === "string" && (url.includes(".m3u8") || url.includes(".mpd") || url.includes("master") || url.includes("playlist"))) {
        notifyStream(url, url.includes(".mpd") ? "dash" : "hls");
      }
      return origOpen.apply(this, [method, url, ...rest]);
    };

    XMLHttpRequest.prototype.send = function (...args) {
      this.addEventListener("readystatechange", function () {
        if (this.readyState === 2 || this.readyState === 4) {
          try {
            const ct = this.getResponseHeader("content-type") || "";
            if (ct.includes("mpegurl") || ct.includes("application/x-mpegurl")) {
              notifyStream(this.responseURL || this._wrenchUrl, "hls");
            } else if (ct.includes("dash+xml")) {
              notifyStream(this.responseURL || this._wrenchUrl, "dash");
            } else if (ct.startsWith("video/")) {
              notifyStream(this.responseURL || this._wrenchUrl, "video");
            }
          } catch (e) {}
        }
      });
      return origSend.apply(this, args);
    };
  }

  // 3. Hook HTMLMediaElement src
  try {
    const origSrcDesc = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, "src");
    if (origSrcDesc && origSrcDesc.set) {
      Object.defineProperty(HTMLMediaElement.prototype, "src", {
        set: function (val) {
          if (typeof val === "string" && !val.startsWith("blob:") && !val.startsWith("data:")) {
            notifyStream(val, "video");
          }
          return origSrcDesc.set.call(this, val);
        },
        get: origSrcDesc.get,
        configurable: true
      });
    }
  } catch (e) {}
})();
