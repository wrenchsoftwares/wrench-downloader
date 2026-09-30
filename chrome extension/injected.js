// Wrench Downloader - In-Page Sniffer (runs in MAIN world)
// Hooks fetch, XMLHttpRequest, and MediaSource to catch HLS (.m3u8), DASH (.mpd),
// and direct media URLs the millisecond they are requested by web players (e.g. JWPlayer, VideoJS, Hls.js)
(function () {
  if (window.__WRENCH_INJECTED__) return;
  window.__WRENCH_INJECTED__ = true;

  const capturedUrls = new Set();

  // Playlist/manifest URL markers incl. extension-less masters
  // (manifest.googlevideo.com HLS/DASH endpoints carry no .m3u8/.mpd).
  function looksLikeManifest(u) {
    if (!u || typeof u !== "string") return false;
    return u.includes(".m3u8") || u.includes(".mpd") || u.includes("playlist") ||
      u.includes("master") || u.includes("hls_variant") ||
      u.includes("manifest.googlevideo.com") || /\/manifest\//i.test(u) ||
      isExtensionResolvablePlayback(u);
  }

  function manifestKind(u) {
    return (/\.mpd|\/manifest\/dash/i.test(u || "")) ? "dash" : "hls";
  }

  // Extension-resolved architecture: range-less videoplayback URLs carrying
  // a rendition id (?itag=N) are the exact authorized bytes the browser is
  // playing. They must be captured (not filtered) so gated pages download
  // with zero page re-resolve. Chunk continuations still excluded.
  function isExtensionResolvablePlayback(u) {
    return /\/videoplayback/i.test(u || "") &&
      /[?&]itag=\d+/i.test(u || "") &&
      !/[?&](range|bytestart|byteend)=/i.test(u || "");
  }

  // SABR redirectors (videoplayback with an aitags= list) are NOT media:
  // the browser only plays them through its live SABR handshake. A direct
  // download returns garbage. The downloadable form is the HLS master
  // (hls_playlist, itag 96) which embeds every rendition + audio.
  function isSabrRedirector(u) {
    return /\/videoplayback/i.test(u || "") && /[?&]aitags=/i.test(u || "");
  }

  function notifyStream(url, type, extra) {
    if (!url || typeof url !== "string") return;
    if (isSabrRedirector(url)) return;
    if (
      (url.includes("/videoplayback") && !isExtensionResolvablePlayback(url)) ||
      url.includes("/segment") ||
      url.includes("/frag") ||
      url.includes("range=") ||
      url.includes("bytestart=") ||
      url.includes("byteend=") ||
      url.includes(".m4s") ||
      /\.ts($|\?|#)/i.test(url) ||
      /preview|thumb|poster|sprite|storyboard|\/ads?\//i.test(url) ||
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
      url.match(/\.(mp4|webm|mkv|m4v|mov|avi)($|\?)/i) ||
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

      if (looksLikeManifest(url)) {
        notifyStream(url, isExtensionResolvablePlayback(url) ? "video" : manifestKind(url));
      }

      // IDM-style response-body sniffing: ANY JSON body delivered to the
      // page can carry fresh stream URLs (player, next, browse endpoints -
      // not just /player). Bodies are pre-filtered by substring so busy
      // pages pay ~zero cost; only matching bodies are parsed/scanned.
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
        if (contentType.includes("json")) {
          let bodyText = "";
          try { bodyText = await response.clone().text(); } catch (e) {}
          if (bodyText) sniffJsonBody(bodyText, isPlayerApiUrl(url));
        }
      } catch (e) {}
      return response;
    };
  }

  // 2. Hook XMLHttpRequest
  if (window.XMLHttpRequest) {
    const origOpen = XMLHttpRequest.prototype.open;
    const origSend = XMLHttpRequest.prototype.send;

    XMLHttpRequest.prototype.open = function (method, url, ...rest) {
      this._wrenchUrl = url;
      if (looksLikeManifest(url)) {
        notifyStream(url, isExtensionResolvablePlayback(url) ? "video" : manifestKind(url));
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
            if (this.readyState === 4 && ct.includes("json")) {
              try {
                const txt = this.responseText || "";
                if (txt) sniffJsonBody(txt, isPlayerApiUrl(this._wrenchUrl));
              } catch (e) {}
            }
          } catch (e) {}
        }
      });
      return origSend.apply(this, args);
    };

  // Player-API endpoint marker (innertube /player calls). The response JSON
  // holds videoDetails + streamingData regardless of site branding.
  function isPlayerApiUrl(u) {
    return typeof u === "string" && /\/youtubei\/v\d+\/player(\?|#|$)/i.test(u);
  }

  // Generic JSON-body scan: structured player data when present, else
  // raw fresh stream URLs (deciphered, page-issued) from any endpoint.
  function sniffJsonBody(bodyText, isPlayerApi) {
    try {
      if (!bodyText || bodyText.length > 5000000) return;
      const hasPlayback = bodyText.includes("videoplayback");
      const hasManifest = bodyText.includes("hlsManifestUrl") || bodyText.includes("dashManifestUrl") ||
        bodyText.includes("manifest.googlevideo.com") || bodyText.includes("hls_playlist");
      const hasStreaming = bodyText.includes("streamingData");
      const hasGenericStreams = (bodyText.includes('"qualities"') || bodyText.includes('"sources"') || bodyText.includes('"transcodings"')) ||
        ((bodyText.includes('.mp4') || bodyText.includes('.m3u8') || bodyText.includes('.mpd')) &&
         (bodyText.includes('"src"') || bodyText.includes('"url"') || bodyText.includes('"file"')));

      if (!hasPlayback && !hasManifest && !hasStreaming && !hasGenericStreams) return;

      if (hasGenericStreams) {
        try {
          const gdata = JSON.parse(bodyText);
          const glist = gdata.qualities || (gdata.result && gdata.result.qualities) || gdata.sources || gdata.transcodings;
          if (Array.isArray(glist)) {
            glist.forEach(item => {
              const u = item.src || item.url || item.file;
              if (u && typeof u === "string" && /^https?:\/\//i.test(u)) {
                let q = item.quality || item.size || item.video_mode || item.label || "";
                const m = String(q).match(/(\d{3,4})/);
                const qualityStr = m ? `${parseInt(m[1], 10)}p` : String(q);
                notifyStream(u, (u.includes(".m3u8") ? "hls" : u.includes(".mpd") ? "dash" : "video"), { quality: qualityStr });
              }
            });
          }
        } catch (e) {}
      }

      if (hasStreaming || isPlayerApi) {
        try {
          const data = JSON.parse(bodyText);
          if (data && data.streamingData) {
            notifyPlayerResponse(bodyText);
            return;
          }
        } catch (e) { /* fall through to regex scan */ }
      }
      // Raw URL scan: fresh signed rendition + manifest URLs.
      const rePlayback = /"(https?:\/\/[^"\\\s]*googlevideo\.com\/videoplayback[^"\\\s]*?[?&]itag=\d+[^"\\\s]*)"/gi;
      const reManifest = /"(https?:\/\/(?:manifest\.googlevideo\.com[^"\\\s]*|[^"\\\s]*hls_playlist[^"\\\s]*))"/gi;
      let m, n = 0;
      while ((m = rePlayback.exec(bodyText)) !== null && n < 30) {
        n++;
        let u = m[1].replace(/\\u0026/g, "&").replace(/\\\//g, "/").replace(/&amp;/g, "&");
        if (/[?&](range|bytestart|byteend|sabr|aitags)=/i.test(u)) continue;
        notifyStream(u, "video");
      }
      n = 0;
      while ((m = reManifest.exec(bodyText)) !== null && n < 10) {
        n++;
        let u = m[1].replace(/\\u0026/g, "&").replace(/\\\//g, "/").replace(/&amp;/g, "&");
        notifyStream(u, manifestKind(u));
      }
    } catch (e) {}
  }

  function notifyPlayerResponse(bodyText) {
    try {
      if (!bodyText || bodyText.length > 5000000) return;
      const data = JSON.parse(bodyText);
      const sd = data && data.streamingData;
      if (!sd) return;
      const out = { videoId: (data.videoDetails && data.videoDetails.videoId) || "", formats: [], manifest: {} };
      const push = (f) => {
        if (!f || typeof f !== "object") return;
        if (out.formats.length > 60) return;
        // Ciphered entries need JS deciphering the extension cannot do;
        // flag them so the UI prefers directly playable URLs.
        out.formats.push({
          itag: f.itag || 0,
          url: typeof f.url === "string" ? f.url : "",
          ciphered: !!f.signatureCipher,
          qualityLabel: f.qualityLabel || "",
          mimeType: f.mimeType || "",
          contentLength: f.contentLength || "",
          audioTrack: !!(f.audioTrack && (f.audioTrack.displayName || f.audioTrack.id))
        });
      };
      (sd.formats || []).forEach(push);
      (sd.adaptiveFormats || []).forEach(push);
      if (typeof sd.hlsManifestUrl === "string") out.manifest.hls = sd.hlsManifestUrl;
      if (typeof sd.dashManifestUrl === "string") out.manifest.dash = sd.dashManifestUrl;
      if (out.formats.length === 0 && !out.manifest.hls && !out.manifest.dash) return;
      window.dispatchEvent(new CustomEvent("__WRENCH_PLAYER_RESPONSE__", { detail: out }));
    } catch (e) {}
  }
  }

  // Embedded player data (initial HTML / SPA state): the /player fetch may
  // have fired before this hook installed. Harvest it directly so the
  // quality ladder is complete even with zero observed network calls.
  function harvestEmbeddedPlayerResponse() {
    try {
      const pr = window.ytInitialPlayerResponse;
      if (!pr || !pr.streamingData) return;
      notifyPlayerResponse(JSON.stringify(pr));
    } catch (e) {}
  }
  try { setTimeout(harvestEmbeddedPlayerResponse, 2500); } catch (e) {}
  try {
    window.addEventListener("__WRENCH_HARVEST_PLAYER__", () => {
      try { harvestEmbeddedPlayerResponse(); } catch (e) {}
    });
  } catch (e) {}

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
