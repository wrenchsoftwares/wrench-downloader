// Wrench Downloader - Content Script
// Detects <video> tags, injects stream sniffer, tracks m3u8/mpd/mp4 network requests,
// and renders a floating button in the top-right corner outside the video container.
// Clicking shows available streams and sends the direct stream URL straight to the desktop app.

(function () {
  const trackedVideos = new WeakSet();
  const pageCapturedStreams = [];
  let currentOpenMenu = null;

  // Inject in-page sniffer into the main world to intercept fetch/XHR
  function injectSniffer() {
    try {
      const script = document.createElement("script");
      script.src = chrome.runtime.getURL("injected.js");
      (document.head || document.documentElement).appendChild(script);
      script.onload = () => script.remove();
    } catch (e) {}
  }
  injectSniffer();

  let activeVideo = null;

  // Listen for streams captured by injected.js
  window.addEventListener("__WRENCH_STREAM_CAPTURED__", (e) => {
    if (e.detail && e.detail.url) {
      const targetVideo = activeVideo || pickMainVideo();
      const context = getVideoContext(targetVideo);
      const item = {
        url: e.detail.url,
        title: (context && context.title) || getCleanVideoTitle(),
        type: e.detail.type || "hls",
        referer: (context && context.pageUrl) || window.location.href,
        userAgent: navigator.userAgent,
        time: Date.now()
      };

      if (targetVideo) {
        if (!targetVideo._wrenchCapturedStreams) targetVideo._wrenchCapturedStreams = [];
        if (!targetVideo._wrenchCapturedStreams.some(s => s.url === item.url)) {
          targetVideo._wrenchCapturedStreams.unshift(item);
        }
      }

      if (!pageCapturedStreams.some((s) => s.url === item.url)) {
        pageCapturedStreams.unshift(item);
      }

      if (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.sendMessage) {
        chrome.runtime.sendMessage({
          action: "REPORT_MEDIA",
          media: item
        }).catch(() => {});
      }

  // If dropdown is currently open, refresh it live (debounced: HLS players
  // emit network events constantly, and each one used to rebuild the menu)
  if (currentOpenMenu && currentOpenMenu._video) {
        scheduleDropdownRefresh();
      }
    }
  });

  // Listen for media detected from background service worker,
  // and serve real format lists to the popup (no fake qualities).
  function pickMainVideo() {
    try {
      const vids = Array.from(document.querySelectorAll("video"));
      if (vids.length === 0) return null;
      vids.sort((a, b) => {
        const ra = a.getBoundingClientRect();
        const rb = b.getBoundingClientRect();
        return (rb.width * rb.height) - (ra.width * ra.height);
      });
      return vids[0];
    } catch (e) { return null; }
  }

  async function collectItemsForPopup() {
    const video = pickMainVideo();
    if (!video) return [];
    let bgMedia = [];
    try {
      const response = await chrome.runtime.sendMessage({ action: "GET_MEDIA" }).catch(() => null);
      if (response && response.media) bgMedia = response.media;
    } catch (e) {}
    return getGenericVideoItems(video, bgMedia);
  }

  if (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.onMessage) {
    chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
      if (msg.action === "MEDIA_DETECTED" && msg.media) {
        const targetVideo = activeVideo || pickMainVideo();
        if (targetVideo) {
          if (!targetVideo._wrenchCapturedStreams) targetVideo._wrenchCapturedStreams = [];
          if (!targetVideo._wrenchCapturedStreams.some(s => s.url === msg.media.url)) {
            targetVideo._wrenchCapturedStreams.unshift(msg.media);
          }
        }
        if (!pageCapturedStreams.some((s) => s.url === msg.media.url)) {
          pageCapturedStreams.unshift(msg.media);
        }
        if (currentOpenMenu && currentOpenMenu._video) {
          scheduleDropdownRefresh();
        }
        return;
      }
      if (msg.action === "GET_FORMATS") {
        collectItemsForPopup()
          .then(items => {
            // Multi-frame race: tabs.sendMessage is answered by EVERY frame
            // running this script, first response wins. Ad/tracking iframes
            // (no video) answer [] instantly and would shadow the real video.
            // Frames WITH items answer immediately; empty frames hold back so
            // the video's answer wins. Top frame waits less (embeds live in
            // subframes and need a head start the other way).
            if (items && items.length > 0) {
              sendResponse({ items });
            } else {
              const isTop = (() => { try { return window === window.top; } catch (e) { return false; } })();
              setTimeout(() => sendResponse({ items: [] }), isTop ? 1500 : 4000);
            }
          })
          .catch(e => sendResponse({ items: [], error: String(e).slice(0, 200) }));
        return true;
      }
    });
  }

  // Wait for the video's real dimensions (labels come from these, never faked).
  function ensureVideoMetadata(video) {
    return new Promise((resolve) => {
      try {
        if (!video || (video.videoWidth > 0 && video.videoHeight > 0)) { resolve(); return; }
        let done = false;
        const finish = () => { if (!done) { done = true; resolve(); } };
        video.addEventListener("loadedmetadata", finish, { once: true });
        setTimeout(finish, 2500);
      } catch (e) { resolve(); }
    });
  }

  // Concrete { label: "720p", res: "(1280x720)" } from the playing rendition, or null.
  function getResolutionFromVideo(video) {
    try {
      const h = video && video.videoHeight > 0 ? video.videoHeight : 0;
      const w = video && video.videoWidth > 0 ? video.videoWidth : 0;
      if (h > 0) {
        return { label: `${h}p`, res: `(${w > 0 ? w : Math.round(h * 16 / 9)}x${h})` };
      }
    } catch (e) {}
    return null;
  }

  // Download icon SVG (built via DOM APIs to stay Trusted-Types safe on strict pages)
  const DOWNLOAD_ICON_PATH =
    "M19 9h-4V3H9v6H5l7 7 7-7zM5 18v2h14v-2H5z";

  function createWrenchIcon() {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("class", "wrench-icon");
    svg.setAttribute("viewBox", "0 0 24 24");
    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    path.setAttribute("d", DOWNLOAD_ICON_PATH);
    svg.appendChild(path);
    return svg;
  }

  // Scan for videos on the page
  function scanForVideos() {
    if (!document.body) return;
    const videoElements = document.querySelectorAll("video");
    videoElements.forEach((video) => {
      if (!trackedVideos.has(video)) {
        try {
          setupVideoOverlay(video);
          trackedVideos.add(video);
        } catch (e) {}
      }
    });
  }

  function setupVideoOverlay(video) {
    const btn = document.createElement("div");
    btn.className = "wrench-video-overlay-btn";
    btn.title = "Download Video";
    btn.setAttribute("aria-label", "Download Video");
    btn.appendChild(createWrenchIcon());

    document.body.appendChild(btn);

    function updatePosition() {
      if (!video.isConnected) {
        btn.remove();
        if (currentOpenMenu) currentOpenMenu.remove();
        return;
      }
      
      const rect = video.getBoundingClientRect();
      if (rect.width === 0 && rect.height === 0) {
        btn.style.display = "none";
        return;
      }

      // Position small icon box OUTSIDE video border (above top-right corner)
      const BTN_SIZE = 30;
      const GAP = 6;
      const videoTop = window.scrollY + rect.top;
      const videoRight = window.scrollX + rect.right;

      let top = videoTop - BTN_SIZE - GAP;
      let left = videoRight - BTN_SIZE;

      // Fallback: if no room above viewport, dock to right-outside instead
      if (top < window.scrollY + 2) {
        const rightOutside = videoRight + GAP;
        if (rightOutside + BTN_SIZE < window.scrollX + window.innerWidth - 2) {
          top = videoTop;
          left = rightOutside;
        } else {
          // Last resort: inside top-right so it stays visible
          top = videoTop + GAP;
          left = videoRight - BTN_SIZE - GAP;
        }
      }

      btn.style.top = `${Math.max(0, top)}px`;
      btn.style.left = `${Math.max(0, left)}px`;
      btn.style.display = "flex";
    }

    window.addEventListener("scroll", updatePosition, { passive: true });
    window.addEventListener("resize", updatePosition, { passive: true });
    const ro = new ResizeObserver(updatePosition);
    ro.observe(video);
    updatePosition();
    setTimeout(updatePosition, 300);
    setTimeout(updatePosition, 1000);

    btn.addEventListener("click", (e) => {
      e.stopPropagation();
      e.preventDefault();
      activeVideo = video;
      toggleDropdown(video, btn);
    });

    btn.addEventListener("mouseenter", () => {
      activeVideo = video;
    });

    video.addEventListener("play", () => {
      activeVideo = video;
      sniffVideoSources(video);
    });
    video.addEventListener("pointerdown", () => {
      activeVideo = video;
    });
    video.addEventListener("loadedmetadata", () => {
      sniffVideoSources(video);
      // Metadata may have arrived after the menu opened: refresh so
      // resolution labels upgrade from temporary to concrete.
      if (currentOpenMenu && currentOpenMenu._video === video) {
        scheduleDropdownRefresh();
      }
    });
    sniffVideoSources(video);
  }

  // Extract post context (permalink and post text) for any feed-based site (X/Twitter, Reddit, FB, IG, etc.)
  function getVideoContext(video) {
    if (!video) return { pageUrl: window.location.href, title: getCleanVideoTitle() };

    try {
      // Find enclosing feed container / article / tweet
      const container = video.closest("article, [data-testid='tweet'], [role='article'], .tweet, li, [data-testid='cellInnerDiv']");
      if (container) {
        // Look for permalink to this specific post
        const link = container.querySelector("a[href*='/status/'], a[href*='/p/'], a[href*='/reel/'], a[href*='/comments/'], a[href*='/watch/']");
        let postUrl = "";
        if (link && link.href) {
          postUrl = link.href.split("?")[0]; // Clean query parameters
        }

        // Look for text content of the post
        let postTitle = "";
        const tweetTextEl = container.querySelector("[data-testid='tweetText'], [lang], .entry-title, .post-title, p");
        if (tweetTextEl && tweetTextEl.textContent && tweetTextEl.textContent.trim()) {
          postTitle = tweetTextEl.textContent.trim().replace(/\s+/g, " ").substring(0, 80);
        }

        if (postTitle) {
          postTitle = postTitle.replace(/[\/\\:*?"<>|]/g, "").trim();
        }

        return {
          pageUrl: postUrl || window.location.href,
          title: postTitle || getCleanVideoTitle()
        };
      }
    } catch (e) {}

    return {
      pageUrl: window.location.href,
      title: getCleanVideoTitle()
    };
  }

  // Generic title: embedded metadata first (usually the clean content
  // title without any site suffix), page title otherwise.
  function getCleanVideoTitle(video) {
    if (video) {
      const ctx = getVideoContext(video);
      if (ctx && ctx.title && ctx.title !== "Video") return ctx.title;
    }
    let title = "";
    try {
      const og = document.querySelector('meta[property="og:title"]');
      if (og && og.content && og.content.trim()) title = og.content.trim();
    } catch (e) {}
    if (!title) title = (document.title || "").trim();

    // Specific YouTube selector checks if available on page
    try {
      const ytTitleEl = document.querySelector("#title h1 yt-formatted-string, ytd-watch-metadata #title h1, h1.ytd-watch-metadata");
      if (ytTitleEl && ytTitleEl.textContent && ytTitleEl.textContent.trim()) {
        title = ytTitleEl.textContent.trim();
      }
    } catch (e) {}

    // Strip common site brand suffixes (e.g. " - YouTube", " | Twitter", " / X", etc.)
    title = title
      .replace(/\s*[-–—|•/]\s*(YouTube|YouTube Music|X|Twitter|Vimeo|Dailymotion|Facebook|Instagram|TikTok|Reddit)\s*$/i, "")
      .replace(/^[\(\[\{]\d+[\)\]\}]\s*/, "") // Strip notification counters e.g. "(1) Video Title"
      .trim();

    const lower = title.toLowerCase();
    if (!title || lower === "video" || lower === "watch" || lower === "reels" || lower === "youtube" || lower === "x") {
      try {
        const h1 = document.querySelector("#title h1, h1");
        if (h1 && h1.textContent.trim()) {
          const h1Text = h1.textContent.trim().substring(0, 80);
          if (h1Text.toLowerCase() !== "youtube" && h1Text.toLowerCase() !== "video") {
            title = h1Text;
          }
        }
      } catch (e) {}
    }
    title = title.replace(/[\/\\:*?"<>|]/g, "").trim();
    if (!title || title.toLowerCase() === "youtube") {
      title = "Video";
    }
    return title;
  }

  function getCodecName(mimeType, format) {
    if (!mimeType) return null;
    const l = mimeType.toLowerCase();
    if (l.includes("av01") || l.includes("av1")) return "AV1";
    if (l.includes("vp9")) return "VP9";
    if (l.includes("vp8")) return "VP8";
    if (l.includes("avc1") || l.includes("h264")) return "H.264";
    if (l.includes("hevc") || l.includes("h265") || l.includes("hvc1") || l.includes("bytevc1")) return "H.265 (HEVC)";
    if (l.includes("opus")) return "Opus";
    if (l.includes("mp4a") || l.includes("aac")) return "AAC";
    return null;
  }

  // NOTE: no per-site code in this file. Every page goes through the same
  // generic pipeline: captured streams -> page-embedded URLs -> page URL
  // fallback labeled from the actual playing rendition.

  // (generic pipeline continues below in getGenericVideoItems)

  // Media URLs seen by THIS page's network activity (behind blob: players).
  // Includes extension-less playlist endpoints (manifest.googlevideo.com
  // HLS masters etc.) that never match a file-extension filter.
  function collectPerfMediaUrls() {
    try {
      return (performance.getEntriesByType("resource") || [])
        .map(r => r.name)
        .filter(u => typeof u === "string" && (
          /\.m3u8($|\?)/i.test(u) || /\.mpd($|\?)/i.test(u) ||
          /hls_playlist|hls_variant|manifest\.googlevideo\.com|\/manifest\//i.test(u) ||
          /\.(mp4|webm|mov)($|\?)/i.test(u)));
    } catch (e) { return []; }
  }

  // Rendition-id (itag) helpers: mirror of the background table. Height 0 =
  // unknown id (not listed); audio ids are offered as the audio download.
  const ITAG_HEIGHTS = { 17: 144, 36: 240, 18: 360, 43: 360, 22: 720, 160: 144, 133: 240, 242: 240, 394: 144, 395: 240, 134: 360, 243: 360, 396: 360, 234: 480, 235: 480, 135: 480, 244: 480, 397: 480, 136: 720, 247: 720, 298: 720, 302: 720, 398: 720, 137: 1080, 248: 1080, 299: 1080, 303: 1080, 399: 1080, 264: 1440, 271: 1440, 400: 1440, 266: 2160, 313: 2160, 401: 2160 };
  const ITAG_AUDIO = new Set([139, 140, 141, 256, 258, 249, 250, 251]);
  const ITAG_WEBM = new Set([43, 242, 243, 244, 247, 248, 249, 250, 251, 271, 272, 302, 303, 313, 394, 395, 396, 397, 398, 399, 400, 401]);
  function getItagHeight(u) {
    const m = String(u || "").match(/[?&]itag=(\d+)\b/i);
    return m ? (ITAG_HEIGHTS[parseInt(m[1], 10)] || 0) : 0;
  }
  function getItagContainer(u) {
    const m = String(u || "").match(/[?&]itag=(\d+)\b/i);
    if (m && ITAG_WEBM.has(parseInt(m[1], 10))) return "webm";
    if (m && ITAG_AUDIO.has(parseInt(m[1], 10))) return "m4a";
    return "mp4";
  }

  function isPlayableStreamUrl(u) {
    if (!u || typeof u !== "string") return false;
    if (!/^https?:\/\//i.test(u)) return false;
    if (/bytestart|byteend|range=|\.m4s($|\?)|\.ts($|\?)|init\.mp4|init\.m4s|\/segment|\/frag|beacon|analytics|preview|thumb|poster|sprite|storyboard/i.test(u)) return false;
    return true;
  }

  function isAudioTrack(u) {
    if (!u || typeof u !== "string") return false;
    return /\/(mp4a|audio|aac)\//i.test(u) || /[-_]audio(\.|\/|$)/i.test(u);
  }

  function isVideoTrackOnly(u) {
    if (!u || typeof u !== "string") return false;
    return /\/(avc1|hevc|h264|vp9|av01)\//i.test(u);
  }

  function isMasterLike(u) {
    if (!u || typeof u !== "string") return false;
    if (isAudioTrack(u) || isVideoTrackOnly(u)) return false;
    // Media playlists are never masters, even on manifest hosts.
    if (/hls_variant/i.test(u)) return false;
    return /master|playlist|manifest|hls_playlist|\/pl\/[^\/]+\.m3u8/i.test(u) || (u.includes(".m3u8") && u.includes("tag="));
  }

  function isRenditionLike(u) {
    if (!u || typeof u !== "string") return false;
    return isAudioTrack(u) || isVideoTrackOnly(u) || /chunklist|index-v\d|rendition|media_|hls_variant|-a\d+(\.m3u8|$)/i.test(u);
  }

  function scoreStreamUrl(u) {
    if (isAudioTrack(u)) return 9; // Never prioritize audio-only for main video
    if (isMasterLike(u)) return 0;
    if (/\.(mp4|webm|mov)($|\?)/i.test(u)) return 1;
    if (isRenditionLike(u)) return 3;
    if (/\.m3u8($|\?)/i.test(u)) return 2;
    if (/\.mpd($|\?)/i.test(u)) return 4;
    return 5;
  }

  // Resolve the concrete downloadable stream behind the player (incl. blob:
  // MediaSource players): background captures + in-page hooks + page network
  // log + page-embedded URLs, master playlists first. "" when nothing yet.
  async function resolveConcreteStreamUrl() {
    const pool = [];
    try {
      if (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.sendMessage) {
        const r = await chrome.runtime.sendMessage({ action: "GET_MEDIA" }).catch(() => null);
        ((r && r.media) || []).forEach(m => { if (m && m.url) pool.push(m.url); });
      }
    } catch (e) {}
    try {
      pageCapturedStreams.forEach(s => { if (s && s.url) pool.push(s.url); });
      collectPerfMediaUrls().forEach(u => pool.push(u));
      collectPageEmbeddedMedia().forEach(e => pool.push(e.url));
    } catch (e) {}
    const uniq = [...new Set(pool)].filter(isPlayableStreamUrl);
    // Never hand back per-fragment junk as "the video".
    uniq.sort((a, b) => scoreStreamUrl(a) - scoreStreamUrl(b));
    return uniq.length ? uniq[0] : "";
  }
  // ([{ height, width, bandwidth, url }], best first). Returns [] when the
  // playlist can't be fetched/parsed (CORS, media playlist, etc.).
  // Parsed variant lists are cached briefly: every dropdown refresh used to
  // re-fetch the (often signed, soon-expiring) master playlist, which both
  // flashed the menu and raced expiry. Only non-empty results are cached.
  const playlistVariantsCache = new Map(); // url -> { time, variants }
  const VARIANTS_TTL_MS = 60000;
  function getCachedVariants(url) {
    try {
      const entry = playlistVariantsCache.get(url);
      if (entry && (Date.now() - entry.time < VARIANTS_TTL_MS)) return entry.variants;
    } catch (e) {}
    return null;
  }
  function setCachedVariants(url, variants) {
    try {
      if (url && variants && variants.length > 0) {
        if (playlistVariantsCache.size > 50) playlistVariantsCache.clear();
        playlistVariantsCache.set(url, { time: Date.now(), variants });
      }
    } catch (e) {}
  }
  async function getHlsVariants(masterUrl) {
    const cached = getCachedVariants(masterUrl);
    if (cached) return cached;
    try {
      const res = await fetch(masterUrl, { credentials: "omit" });
      if (!res.ok) return [];
      const text = await res.text();
      if (!text.includes("#EXT-X-STREAM-INF")) return [];
      const lines = text.split("\n");
      const out = [];
      for (let i = 0; i < lines.length; i++) {
        const line = lines[i].trim();
        if (!line.startsWith("#EXT-X-STREAM-INF")) continue;
        const bwMatch = line.match(/BANDWIDTH=(\d+)/);
        const resMatch = line.match(/RESOLUTION=(\d+)x(\d+)/);
        let uri = "";
        for (let j = i + 1; j < lines.length; j++) {
          const nl = lines[j].trim();
          if (!nl || nl.startsWith("#EXT-X-SESSION") || nl.startsWith("#EXT-X-MEDIA")) continue;
          if (nl.startsWith("#")) continue;
          uri = nl;
          break;
        }
        if (!uri || !resMatch) continue;
        try {
          out.push({
            width: parseInt(resMatch[1], 10),
            height: parseInt(resMatch[2], 10),
            bandwidth: bwMatch ? parseInt(bwMatch[1], 10) : 0,
            url: new URL(uri, masterUrl).href
          });
        } catch (e) {}
      }
      // Dedupe by height (keep highest bandwidth), best first, max 6
      const byHeight = new Map();
      out.forEach(v => {
        if (!byHeight.has(v.height) || byHeight.get(v.height).bandwidth < v.bandwidth) {
          byHeight.set(v.height, v);
        }
      });
      const deduped = Array.from(byHeight.values())
        .sort((a, b) => b.height - a.height)
        .slice(0, 6);
      setCachedVariants(masterUrl, deduped);
      return deduped;
    } catch (e) {
      return [];
    }
  }

  async function getDashVariants(manifestUrl) {
    const cachedDash = getCachedVariants(manifestUrl);
    if (cachedDash) return cachedDash;
    try {
      const response = await fetch(manifestUrl, { credentials: "omit" });
      if (!response.ok) return [];
      const xml = new DOMParser().parseFromString(await response.text(), "application/xml");
      if (xml.querySelector("parsererror")) return [];

      const representations = Array.from(xml.getElementsByTagNameNS("*", "Representation"));
      const variants = representations.map(representation => {
        const height = Number(representation.getAttribute("height")) || 0;
        const width = Number(representation.getAttribute("width")) || 0;
        const bandwidth = Number(representation.getAttribute("bandwidth")) || 0;
        const adaptation = representation.parentElement;
        const contentType = `${representation.getAttribute("mimeType") || ""} ${adaptation?.getAttribute("mimeType") || ""} ${adaptation?.getAttribute("contentType") || ""}`;
        return { height, width, bandwidth, contentType };
      }).filter(variant => variant.height > 0 && /video/i.test(variant.contentType));

      const byHeight = new Map();
      variants.forEach(variant => {
        const previous = byHeight.get(variant.height);
        if (!previous || previous.bandwidth < variant.bandwidth) byHeight.set(variant.height, variant);
      });
      const dedupedDash = Array.from(byHeight.values())
        .sort((a, b) => b.height - a.height)
        .slice(0, 6);
      setCachedVariants(manifestUrl, dedupedDash);
      return dedupedDash;
    } catch (e) {
      return [];
    }
  }

  // Guess video height from URL hints (/1080/, _720p, quality_480p, 1080P_2000K, 1920x1080…).
  function guessHeightFromUrl(url) {
    try {
      const wh = url.match(/(\d{3,4})x(\d{3,4})/i);
      if (wh) {
        const h = parseInt(wh[2], 10);
        if (h >= 200 && h <= 2300) return h;
      }
      const parsedUrl = new URL(url);
      for (const key of ["quality", "video_quality", "resolution", "height", "res"]) {
        const value = parsedUrl.searchParams.get(key) || "";
        const match = value.match(/^(\d{3,4})p?$/i);
        if (match) {
          const height = parseInt(match[1], 10);
          if (height >= 200 && height <= 4320) return height;
        }
      }
      const m = url.match(/(?:\/|_|-)(\d{3,4})p(?:\/|\.|_|-|\?|#|&|$)/i);
      if (m) {
        const h = parseInt(m[1], 10);
        if (h >= 200 && h <= 4320) return h;
      }
    } catch (e) {}
    return 0;
  }

  // Harvest media URLs embedded IN THE PAGE (player configs, mediaDefinitions,
  // JWPlayer sources, JSON-LD, og:video tags). These carry fresh page-issued
  // tokens, unlike re-resolved or stale captured links.
  function collectPageEmbeddedMedia() {
    const found = [];
    const isManifest = (u) => /hls_playlist|hls_variant|manifest\.googlevideo\.com|\/manifest\//i.test(u || "");
    // Page-issued rendition URLs: the player config carries the exact signed
    // stream URLs (e.g. googlevideo videoplayback + itag) with fresh auth
    // params. This is the IDM-equivalent capture: no page re-resolve needed.
    const isPageRendition = (u) => /googlevideo\.com\/videoplayback[^"'\\\s]*[?&]itag=\d+/i.test(u || "");
    const push = (url, weight) => {
      if (found.length > 40 || !url || typeof url !== "string") return;
      url = url.trim().replace(/\\u0026/g, "&").replace(/\\\//g, "/").replace(/&amp;/g, "&");
      if (!/^https?:\/\//i.test(url) || url.length > 1500) return;
      // Extension-less playlist endpoints (player configs carry complete
      // HLS/DASH masters with auth params, e.g. hlsManifestUrl).
      const manifest = isManifest(url);
      if (!manifest && !isPageRendition(url) && !/\.(mp4|webm|mkv|m3u8|mpd|mov)(\?|#|$)/i.test(url)) return;
      if (/preview|thumb|poster|sprite|storyboard|\/ads?\//i.test(url)) return;
      if (!found.some(f => f.url === url)) found.push({ url, weight: weight || 0, manifest });
    };

    // High-signal tags first
    try {
      document.querySelectorAll('meta[property="og:video"], meta[property="og:video:url"], meta[property="og:video:secure_url"]')
        .forEach(m => { if (m.content) push(m.content, 10); });
      document.querySelectorAll('script[type="application/ld+json"]').forEach(s => {
        try {
          const arr = JSON.parse(s.textContent);
          (Array.isArray(arr) ? arr : [arr]).forEach(o => { if (o && o.contentUrl) push(o.contentUrl, 9); });
        } catch (e) {}
      });
    } catch (e) {}

    // Quoted media URLs inside inline scripts (player configs / mediaDefinitions).
    // Player responses can be megabytes (full streamingData), so the cap is
    // generous here; patterns are linear scans.
    try {
      const re = /"(https?:\/\/[^"\\\s]+?\.(?:mp4|webm|mkv|m3u8|mpd|mov)(?:\?[^"\\\s]*)?)"|'(https?:\/\/[^'\\\s]+?\.(?:mp4|webm|mkv|m3u8|mpd|mov)(?:\?[^'\\\s]*)?)'/gi;
      // Extension-less HLS/DASH masters (hlsManifestUrl and friends).
      const reManifest = /"(https?:\/\/(?:manifest\.googlevideo\.com[^"\\\s]*|[^"\\\s]*hls_playlist[^"\\\s]*|[^"\\\s]*\/manifest\/[^"\\\s]*))"/gi;
      // Page-issued rendition URLs (signed videoplayback + itag pairs).
      const reRendition = /"(https?:\/\/[^"\\\s]*googlevideo\.com\/videoplayback[^"\\\s]*?[?&]itag=\d+[^"\\\s]*)"/gi;
      document.querySelectorAll("script:not([src])").forEach(s => {
        const t = s.textContent || "";
        if (!t || t.length > 5000000) return;
        let m;
        while ((m = re.exec(t)) !== null) push(m[1] || m[2], 5);
        let mm;
        while ((mm = reManifest.exec(t)) !== null) push(mm[1], 9);
        let mr;
        while ((mr = reRendition.exec(t)) !== null) push(mr[1], 8);
      });
    } catch (e) {}

    // Direct video/source tags
    try {
      document.querySelectorAll("video, source").forEach(v => {
        if (v.currentSrc) push(v.currentSrc, 8);
        else if (v.src) push(v.src, 8);
      });
    } catch (e) {}

    return found;
  }

  async function getGenericVideoItems(video, bgMediaList) {
    const context = getVideoContext(video);
    const postPageUrl = (context && context.pageUrl) || window.location.href;
    const title = (context && context.title) || getCleanVideoTitle(video);
    const items = [];
    // Real dimensions first: every label below derives from something concrete.
    await ensureVideoMetadata(video);
    const videoHeight = video.videoHeight || 0;
    const videoWidth = video.videoWidth || (videoHeight > 0 ? Math.round(videoHeight * 16 / 9) : 0);
    const directSrc = video.currentSrc || video.src || "";

    // Streams captured specifically while this video was active / playing
    const videoStreams = (video && video._wrenchCapturedStreams) || [];

    // Merge streams specifically for this video first, then background media, then global page captured streams
    const combined = [...videoStreams, ...(bgMediaList || []), ...pageCapturedStreams];
    const uniqueMap = new Map();
    combined.forEach((m) => {
      if (m && m.url && !uniqueMap.has(m.url)) {
        uniqueMap.set(m.url, m);
      }
    });
    // Plus URLs embedded in the page itself (freshest tokens, page context)
    const embedded = collectPageEmbeddedMedia();
    embedded.forEach(e => {
      if (!uniqueMap.has(e.url)) {
        const embType = /\.(mpd)(\?|#|$)/i.test(e.url) || /\/manifest\/dash/i.test(e.url) ? "dash"
          : (/\.(m3u8)(\?|#|$)/i.test(e.url) || e.manifest ? "hls" : "video");
        uniqueMap.set(e.url, {
          url: e.url,
          title: title,
          type: embType,
          referer: postPageUrl,
          userAgent: navigator.userAgent,
          embeddedWeight: e.weight,
          time: Date.now()
        });
      }
    });
    const isManifestEntry = (m) =>
      m.type === "hls" || m.type === "dash" ||
      /hls_playlist|hls_variant|manifest\.googlevideo\.com|\/manifest\/|\.m3u8(\?|#|$)|\.mpd(\?|#|$)/i.test(m.url || "") ||
      (m.contentType && (m.contentType.includes("mpegurl") || m.contentType.includes("dash")));
    const validMedia = Array.from(uniqueMap.values()).filter(m =>
      m.url &&
      !m.url.startsWith("blob:") &&
      !m.url.startsWith("data:") &&
      !m.url.includes("bytestart=") &&
      !m.url.includes("byteend=") &&
      !m.url.includes(".m4s") &&
      !m.url.includes("init.mp4") &&
      !m.url.includes("init.m4s") &&
      !m.url.includes("range=") &&
      !m.url.includes("/segment") &&
      !m.url.includes("/frag") &&
      // SABR/challenge URLs only play inside the page's own session handshake.
      !/[?&]sabr=\d/i.test(m.url) &&
      // HLS fragments are never standalone videos (a 10s .ts chunk looks
      // like a "download" but plays only a fragment - the #1 junk entry).
      !/\.(m4s|ts)(\?|#|$)/i.test(m.url.split("?")[0]) &&
      // Hover previews / thumbnails / ad clips captured as video/*.
      !/preview|thumb|poster|sprite|storyboard|\/ads?\//i.test(m.url) &&
      // Tiny-file rule applies to DIRECT files only: masters/playlists are
      // legitimately a few KB (this rule used to eat IDM-style manifests).
      !(!isManifestEntry(m) &&
        Number(m.contentLengthBytes) > 0 &&
        Number(m.contentLengthBytes) < 256 * 1024)
    );

    // Rendition-id streams (e.g. ?itag=22&): the exact bytes the browser is
    // already playing, authorized session included. They download directly
    // with no page re-resolve, so they outrank everything else - this is what
    // makes gated videos downloadable while merely watched in the tab.
    const streamHeights = new Set();
    const itagVideo = new Map();
    validMedia.forEach(m => {
      if (m.type === "audio" || /[?&]sabr=\d/i.test(m.url)) return;
      const h = getItagHeight(m.url);
      if (h <= 0 || itagVideo.has(h)) return;
      itagVideo.set(h, m);
    });
    Array.from(itagVideo.entries())
      .sort((a, b) => b[0] - a[0])
      .slice(0, 6)
      .forEach(([h, m]) => {
        const label = `${h}p`;
        const ext = getItagContainer(m.url);
        const w = Math.round(h * 16 / 9);
        streamHeights.add(label.toLowerCase());
        items.push({
          displayName: `${title} - ${label}.${ext}`,
          title: `${title} - ${label}`,
          quality: label,
          format: ext,
          url: m.url,
          pageUrl: postPageUrl,
          referrer: m.referer || postPageUrl,
          userAgent: m.userAgent || navigator.userAgent,
          badge: label.toUpperCase(),
          sub: `Direct stream • Res: (${w}x${h})`
        });
      });
    const audioItag = validMedia.find(m => {
      if (!/[?&]itag=\d+/i.test(m.url) || /[?&]sabr=\d/i.test(m.url)) return false;
      if (m.type === "audio") return true;
      // Embedded player URLs carry no audio markers in the URL itself;
      // the rendition id is the signal (e.g. itag 140 = m4a audio).
      const num = parseInt((m.url.match(/[?&]itag=(\d+)/i) || [])[1] || "0", 10);
      return ITAG_AUDIO.has(num);
    });

    // 1. Check for HLS (.m3u8) or DASH (.mpd) stream.
    // Filter out audio-only tracks so video downloads NEVER receive audio-only URLs!
    const streamCandidates = validMedia.filter(m =>
      !isAudioTrack(m.url) &&
      m.type !== "audio" &&
      (m.url.includes(".m3u8") ||
       m.url.includes(".mpd") ||
       m.type === "hls" ||
       m.type === "dash" ||
       (m.contentType && (m.contentType.includes("mpegurl") || m.contentType.includes("dash"))))
    );
    streamCandidates.sort((a, b) => {
      // Prioritize streams captured specifically for this video
      const isVideoA = videoStreams.some(s => s.url === a.url) ? 1 : 0;
      const isVideoB = videoStreams.some(s => s.url === b.url) ? 1 : 0;
      if (isVideoA !== isVideoB) return isVideoB - isVideoA;

      const score = (m) => (isMasterLike(m.url) ? 0 : isRenditionLike(m.url) ? 2 : 1);
      return score(a) - score(b);
    });
    const streamItem = streamCandidates[0];

    const directMediaItems = validMedia.filter(m => m !== streamItem && (m.type === "video" || m.url.match(/\.(mp4|webm|mkv|mov)($|\?|#)/i)));

    if (streamItem) {
      const isHls = streamItem.url.includes(".m3u8") || streamItem.type === "hls" ||
        (streamItem.contentType && streamItem.contentType.includes("mpegurl"));
      const isDash = streamItem.url.includes(".mpd") || streamItem.type === "dash" ||
        (streamItem.contentType && streamItem.contentType.includes("dash"));
      const referrer = streamItem.referer || postPageUrl;
      const userAgent = streamItem.userAgent || navigator.userAgent;

      // List REAL renditions from the master playlist when possible.
      // Otherwise list advertised qualities, else ONE honest entry.
      let variants = [];
      let coveredByStream = false;
      if (!isRenditionLike(streamItem.url)) {
        if (isHls) variants = await getHlsVariants(streamItem.url);
        else if (isDash) variants = await getDashVariants(streamItem.url);
      }

      if (variants.length > 0) {
        // Show ALL real renditions, but send the MASTER url + quality label
        // so yt-dlp resolves fresh with both audio and video tracks merged.
        // Direct-file sections below are skipped: they are duplicates of
        // these renditions or stray fragments, not additional videos.
        variants.forEach(v => {
          const label = `${v.height}p`;
          items.push({
            displayName: `${title} - ${label}.mp4`,
            title: `${title} - ${label}`,
            quality: label,
            format: "mp4",
            url: streamItem.url,
            pageUrl: postPageUrl,
            referrer: referrer,
            userAgent: userAgent,
            badge: label.toUpperCase(),
            sub: `Available quality • Res: (${v.width}x${v.height})`
          });
        });
      } else {
        // Opaque player (no inspectable master): list every quality the page
        // itself advertises for this video, each resolved by the app from the
        // page URL. Labels come from embedded player configs, never invented.
        const ladder = collectEmbeddedQualityLabels().filter(h =>
          !items.some(i => (i.quality || "").toLowerCase() === `${h}p`));
        if (ladder.length > 0) {
          ladder.forEach(h => {
            const label = `${h}p`;
            const w = Math.round(h * 16 / 9);
            items.push({
              displayName: `${title} - ${label}.mp4`,
              title: `${title} - ${label}`,
              quality: label,
              format: "mp4",
              url: postPageUrl,
              pageUrl: postPageUrl,
              referrer: referrer,
              userAgent: userAgent,
              badge: label.toUpperCase(),
              sub: `Available quality • Res: (${w}x${h})`
            });
          });
          coveredByStream = true;
        } else {
          // No advertised qualities: offer only the rendition confirmed by
          // playback metadata. Masters carry their own URL (exact manifest,
          // IDM-style dialog); track/media playlists resolve via the page so
          // the app merges full audio+video instead of a silent track.
          const res = getResolutionFromVideo(video);
          if (res) {
            const downloadUrl = (isMasterLike(streamItem.url) || !postPageUrl)
              ? streamItem.url
              : (isRenditionLike(streamItem.url) ? postPageUrl : streamItem.url);
            items.push({
              displayName: `${title} - ${res.label}.mp4`,
              title: `${title} - ${res.label}`,
              quality: res.label,
              format: "mp4",
              url: downloadUrl,
              pageUrl: postPageUrl,
              referrer: referrer,
              userAgent: userAgent,
              badge: res.label.toUpperCase(),
              sub: `Available quality • Res: ${res.res}`
            });
          }
        }
      }

      const audioUrl = (audioItag && audioItag.url) || streamItem.url;
      items.push({
        displayName: `${title} - Audio.mp3`,
        title: `${title} - Audio`,
        quality: "audio",
        format: "mp3",
        url: audioUrl,
        pageUrl: postPageUrl,
        referrer: (audioItag && audioItag.referer) || streamItem.referer || postPageUrl,
        userAgent: (audioItag && audioItag.userAgent) || streamItem.userAgent || navigator.userAgent,
        badge: "MP3",
        sub: `Format: MP3 | Res: (Audio Only)`
      });

      // Stream fully describes the video: the rows above (real renditions or
      // advertised qualities + audio) are the complete working set. Anything
      // else captured on the page is a duplicate or a fragment - don't list it.
      // (Single unconfirmed fallback rows don't cover: direct files still apply.)
      if (variants.length > 0 || coveredByStream) return items;
    }

    // (streamHeights seeded above with exact-stream heights, so page-resolve
    // and direct rows never duplicate them.)
    items.forEach(i => { const q = (i.quality || "").toLowerCase(); if (q) streamHeights.add(q); });

    // 2. Page-embedded direct files (freshest page-issued URLs first).
    // Group by detected quality so each entry is a distinct real stream.
    const embeddedUrls = new Set();
    const embeddedMp4s = validMedia.filter(m =>
      (m.embeddedWeight || 0) > 0 &&
      m !== streamItem &&
      /\.(mp4|webm|mkv|mov)(\?|#|$)/i.test(m.url)
    );
    const embeddedByQuality = new Map();
    embeddedMp4s.forEach(m => {
      const hintH = guessHeightFromUrl(m.url);
      // URL hint first, actual playing rendition second - never fabricated.
      const h = hintH > 0 ? hintH : (videoHeight > 0 ? videoHeight : 0);
      const key = h > 0 ? `h${h}` : `u${m.url}`;
      const prev = embeddedByQuality.get(key);
      if (!prev || (m.embeddedWeight || 0) > (prev.embeddedWeight || 0)) {
        embeddedByQuality.set(key, { entry: m, height: h });
      }
    });
    Array.from(embeddedByQuality.values())
      .sort((a, b) => (b.height || 9999) - (a.height || 9999))
      // Skip heights already listed from the stream; keep at most one extra
      // direct file so the menu stays short and every row is distinct.
      .filter(({ height }) => height <= 0 || !streamHeights.has(`${height}p`))
      .slice(0, 1)
      .forEach(({ entry: m, height: h }) => {
        embeddedUrls.add(m.url);
        streamHeights.add(h > 0 ? `${h}p` : "file");
        let ext = "mp4";
        const mExt = m.url.split("?")[0].match(/\.(mp4|webm|mkv|mov)($|\?)/i);
        if (mExt) ext = mExt[1].toLowerCase();
        const w = (h === videoHeight && videoWidth > 0) ? videoWidth : (h > 0 ? Math.round(h * 16 / 9) : 0);
        const label = h > 0 ? `${h}p` : "Video";
        const resStr = h > 0 ? `(${w}x${h})` : "";
        items.push({
          displayName: `${title} - ${label}.${ext}`,
          title: `${title} - ${label}`,
          quality: h > 0 ? label : "file",
          format: ext,
          url: m.url,
          referrer: m.referer || window.location.href,
          userAgent: m.userAgent || navigator.userAgent,
          badge: h > 0 ? label.toUpperCase() : ext.toUpperCase(),
          sub: h > 0 ? `Available quality • Res: ${resStr}` : `Direct ${ext.toUpperCase()} file`
        });
      });

    // 3. Other captured direct video files - only distinct heights not already
    // listed, max 2 rows. Height-unknown leftovers ("Part N") are skipped
    // whenever a height-known entry exists: they are fragments/previews that
    // download a broken file, the exact "entries that don't work" complaint.
    // Resolution: playing rendition first, URL hint second - never fabricated.
    const remainingDirects = directMediaItems.filter(m => !embeddedUrls.has(m.url));
    const getDirectHeight = media => guessHeightFromUrl(media.url) || (media.url === directSrc ? videoHeight : 0);
    remainingDirects.sort((a, b) => getDirectHeight(b) - getDirectHeight(a));
    const seenDirectHeights = new Set(streamHeights);
    const hasKnownHeight = remainingDirects.some(media => getDirectHeight(media) > 0) || seenDirectHeights.size > 0;
    const uniqueDirects = remainingDirects.filter(media => {
      const height = getDirectHeight(media);
      if (height === 0) return !hasKnownHeight;
      const key = `${height}p`;
      if (seenDirectHeights.has(key)) return false;
      seenDirectHeights.add(key);
      return true;
    });
    uniqueDirects.slice(0, 2).forEach((m, idx) => {
      let ext = "mp4";
      const mExt = m.url.split("?")[0].match(/\.(mp4|webm|mkv|mov)($|\?)/i);
      if (mExt) ext = mExt[1].toLowerCase();

      const urlH = guessHeightFromUrl(m.url);
      const h = urlH > 0 ? urlH : (m.url === directSrc ? videoHeight : 0);
      const w = h === videoHeight && videoWidth > 0 ? videoWidth : (h > 0 ? Math.round(h * 16 / 9) : 0);
      const resStr = h > 0 ? `(${w}x${h})` : "";
      const codec = getCodecName(m.contentType || m.mimeType, ext);
      const codecPart = codec ? ` | Codec: ${codec}` : "";
      const qLabel = h > 0 ? `${h}p` : "Video";
      const sizeInfo = m.size ? ` • ${m.size}` : "";

      items.push({
        displayName: `${title} - ${qLabel}.${ext}`,
        title: `${title} - ${qLabel}`,
        quality: h > 0 ? `${h}p` : "file",
        format: ext,
        url: m.url,
        referrer: m.referer || window.location.href,
        userAgent: m.userAgent || navigator.userAgent,
        badge: h > 0 ? qLabel.toUpperCase() : ext.toUpperCase(),
        sub: `${h > 0 ? `Available quality • Res: ${resStr}` : `Direct ${ext.toUpperCase()} file`}${codecPart}${sizeInfo}`
      });
    });

    // 4. Check direct src attribute on video if not a blob
    if (items.length === 0 && directSrc && !directSrc.startsWith("blob:") && !directSrc.startsWith("data:") && !directSrc.startsWith("chrome-extension://")) {
      let ext = "mp4";
      const mExt = directSrc.split("?")[0].match(/\.(mp4|webm|mkv|mov)($|\?)/i);
      if (mExt) ext = mExt[1].toLowerCase();

      const srcH = videoHeight > 0 ? videoHeight : guessHeightFromUrl(directSrc);
      const h = srcH;
      const w = videoWidth > 0 && videoHeight > 0 ? videoWidth : (h > 0 ? Math.round(h * 16 / 9) : 0);
      const label = h > 0 ? `${h}p` : "Video";

      items.push({
        displayName: `${title} - ${label}.${ext}`,
        title: `${title} - ${label}`,
        quality: h > 0 ? label : "file",
        format: ext,
        url: directSrc,
        pageUrl: postPageUrl,
        referrer: postPageUrl,
        userAgent: navigator.userAgent,
        badge: h > 0 ? label.toUpperCase() : ext.toUpperCase(),
        sub: h > 0 ? `Available quality • Res: (${w}x${h})` : `Direct ${ext.toUpperCase()} file`
      });
    }

    // 5. Nothing captured (opaque player, uncaptured stream): fall back to the
    // specific post / page URL itself and let the app resolve it. Labeled from the ACTUAL playing
    // rendition, so the entry is always actionable on any page.
    if (items.length === 0) {
      const pageUrl = postPageUrl;
      const res = getResolutionFromVideo(video);
      if (res) {
        items.push({
          displayName: `${title} - ${res.label}.mp4`,
          title: `${title} - ${res.label}`,
          quality: res.label,
          format: "mp4",
          url: pageUrl,
          pageUrl: pageUrl,
          referrer: pageUrl,
          userAgent: navigator.userAgent,
          badge: res.label.toUpperCase(),
          sub: `Available quality • Res: ${res.res}`
        });
      }
      items.push({
        displayName: `${title} - Audio.mp3`,
        title: `${title} - Audio`,
        quality: "audio",
        format: "mp3",
        url: pageUrl,
        pageUrl: pageUrl,
        referrer: pageUrl,
        userAgent: navigator.userAgent,
        badge: "MP3",
        sub: `Format: MP3 | Res: (Audio Only)`
      });
    }

    // Hard cap: the menu stays short. Every row above is a distinct,
    // working entry (real rendition, advertised quality, direct file,
    // or page fallback).
    return items.slice(0, 10);
  }

  // Every quality the page advertises for its video, best first. Harvested
  // generically from embedded player configs (per-rendition labels such as
  // "qualityLabel":"720p"), never invented - the app resolves each label to
  // the real stream from the page URL.
  function collectEmbeddedQualityLabels() {
    const heights = new Set();
    try {
      document.querySelectorAll("script:not([src])").forEach(s => {
        const t = s.textContent || "";
        if (!t || t.length > 2000000) return;
        const re = /"(?:qualityLabel|quality_label|label)"\s*:\s*"(\d{3,4})p"/gi;
        let m;
        while ((m = re.exec(t)) !== null) {
          const h = parseInt(m[1], 10);
          if (h >= 144 && h <= 4320) heights.add(h);
        }
      });
    } catch (e) {}
    return [...heights].sort((a, b) => b - a);
  }

  function sniffVideoSources(video) {
    const sources = [];
    if (video.src) sources.push(video.src);
    if (video.currentSrc) sources.push(video.currentSrc);

    video.querySelectorAll("source").forEach((s) => {
      if (s.src) sources.push(s.src);
    });

    const context = getVideoContext(video);
    const postPageUrl = (context && context.pageUrl) || window.location.href;
    const postTitle = (context && context.title) || getCleanVideoTitle(video);

    sources.forEach((url) => {
      if (url && !url.startsWith("chrome-extension://") && !url.startsWith("blob:") && !url.startsWith("data:")) {
        const item = {
          url: url,
          title: postTitle,
          type: "video",
          referer: postPageUrl,
          userAgent: navigator.userAgent
        };

        if (!video._wrenchCapturedStreams) video._wrenchCapturedStreams = [];
        if (!video._wrenchCapturedStreams.some(s => s.url === url)) {
          video._wrenchCapturedStreams.unshift(item);
        }

        if (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.sendMessage) {
          chrome.runtime.sendMessage({
            action: "REPORT_MEDIA",
            media: item
          }).catch(() => {});
        }
      }
    });
  }

  async function toggleDropdown(video, btn) {
    if (currentOpenMenu) {
      currentOpenMenu.remove();
      const wasSame = currentOpenMenu._btn === btn;
      currentOpenMenu = null;
      if (wasSame) return;
    }

    const menu = document.createElement("div");
    menu.className = "wrench-dropdown-menu";
    menu._btn = btn;
    menu._video = video;

    // Blob: (MediaSource) players only fetch the real playlist once playing.
    // Start it so the concrete stream gets captured; the open menu refreshes
    // live when it arrives.
    try {
      const src = video.currentSrc || video.src || "";
      if (src.startsWith("blob:") && video.paused) {
        video.muted = true;
        const pr = video.play();
        if (pr && pr.catch) pr.catch(() => {});
      }
    } catch (e) {}

    const header = document.createElement("div");
    header.className = "wrench-dropdown-header";
    header.textContent = "Available Downloads";
    menu.appendChild(header);

    await populateDropdownItems(menu, video, btn);

    document.body.appendChild(menu);
    currentOpenMenu = menu;

    const btnRect = btn.getBoundingClientRect();
    const menuTop = window.scrollY + btnRect.bottom + 6;
    const menuLeft = window.scrollX + Math.min(window.innerWidth - 360, Math.max(10, btnRect.right - 350));
    menu.style.top = `${menuTop}px`;
    menu.style.left = `${Math.max(10, menuLeft)}px`;
  }

  async function refreshOpenDropdown(video, btn) {
    if (!currentOpenMenu) return;
    await populateDropdownItems(currentOpenMenu, video, btn);
  }

  // Coalesce bursts of capture/metadata events into one refresh. Without
  // this, every HLS segment request rebuilds the open menu (visible flash).
  let refreshTimer = null;
  function scheduleDropdownRefresh() {
    if (!currentOpenMenu) return;
    if (refreshTimer) return; // one pending refresh is enough
    refreshTimer = setTimeout(() => {
      refreshTimer = null;
      if (currentOpenMenu && currentOpenMenu._video) {
        refreshOpenDropdown(currentOpenMenu._video, currentOpenMenu._btn);
      }
    }, 700);
  }

  function dropdownItemsSignature(items) {
    return (items || []).map((i) =>
      `${i.displayName || i.title || ""}|${i.badge || ""}|${i.url || ""}|${i.sub || ""}`
    ).join("\n");
  }

  async function populateDropdownItems(menu, video, btn) {
    const generation = (menu._populateGeneration || 0) + 1;
    menu._populateGeneration = generation;
    try {
      // One pipeline for every page: no per-site branches.
      let items = [];
      let bgMedia = [];
      if (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.sendMessage) {
        const response = await chrome.runtime.sendMessage({ action: "GET_MEDIA" }).catch(() => null);
        if (response && response.media) bgMedia = response.media;
      }
      items = await getGenericVideoItems(video, bgMedia);
      if (menu._populateGeneration !== generation) return;

      // Diff-render: identical list => don't touch the DOM at all (no flash).
      const sig = dropdownItemsSignature(items);
      if (menu._itemsSignature === sig) return;
      // Debug tracking: what the browser offered for this video (labels only,
      // no URLs). Reported via console; matched with app logs by page + time.
      try {
        console.debug("[wrench-track] dropdown for " + window.location.hostname + ": " +
          items.map(i => `${i.badge || "?"}|${(i.displayName || i.title || "").slice(0, 60)}`).join(" ;; ").slice(0, 600));
      } catch (e) {}
      menu.querySelectorAll(".wrench-dropdown-item, .wrench-dropdown-empty").forEach((e) => e.remove());

      if (items.length === 0) {
        const empty = document.createElement("div");
        empty.className = "wrench-dropdown-empty";
        const emptyTitle = document.createElement("span");
        emptyTitle.textContent = "No video stream detected yet.";
        empty.appendChild(emptyTitle);
        empty.appendChild(document.createElement("br"));
        const emptyHint = document.createElement("small");
        emptyHint.setAttribute("style", "color:#aaa;");
        emptyHint.textContent = "Start playing the video to capture stream directly.";
        empty.appendChild(emptyHint);
        menu.appendChild(empty);
      } else {
        items.forEach((item) => {
          const itemEl = document.createElement("div");
          itemEl.className = "wrench-dropdown-item";

          const infoEl = document.createElement("div");
          infoEl.className = "wrench-item-info";

          const titleEl = document.createElement("div");
          titleEl.className = "wrench-item-title";
          titleEl.textContent = item.displayName || item.title;

          const subEl = document.createElement("div");
          subEl.className = "wrench-item-sub";
          subEl.textContent = item.sub || "Ready to download";

          infoEl.appendChild(titleEl);
          infoEl.appendChild(subEl);

          const badge = document.createElement("span");
          badge.className = "wrench-item-badge";
          badge.textContent = item.badge || "MP4";

          itemEl.appendChild(infoEl);
          itemEl.appendChild(badge);

          itemEl.addEventListener("click", () => {
            if (item.isPromptToPlay) {
              video.play().catch(() => {});
              item.sub = "Starting playback... sniffing stream...";
              subEl.textContent = item.sub;
              return;
            }
            sendDownloadToApp(item);
            menu.remove();
            currentOpenMenu = null;
          });

          menu.appendChild(itemEl);
        });
      }
      menu._itemsSignature = sig;
    } catch (err) {
      if (menu._populateGeneration !== generation) return;
      menu.querySelectorAll(".wrench-dropdown-item, .wrench-dropdown-empty").forEach((e) => e.remove());
      const empty = document.createElement("div");
      empty.className = "wrench-dropdown-empty";
      empty.textContent = "Error loading streams: " + err.message;
      menu.appendChild(empty);
    }
  }

  function sendDownloadToApp(item) {
    if (!item.url || item.url.startsWith("blob:")) {
      alert("Stream URL not ready yet. Please play the video for 1 second first!");
      return;
    }

    const payload = {
      url: item.url,
      title: item.title || document.title,
      quality: item.quality || "best",
      format: item.format || "mp4",
      pageUrl: item.pageUrl || window.location.href,
      referrer: item.referrer || document.referrer || window.location.href,
      userAgent: item.userAgent || navigator.userAgent,
      prompt: true
    };

    if (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.sendMessage) {
      chrome.runtime.sendMessage({
        action: "SEND_TO_APP",
        payload: payload
      }, (res) => {
        if (chrome.runtime.lastError || !res || !res.success) {
          alert("Wrench Downloader app is not running or bridge failed.\nPlease start Wrench Downloader!");
        } else {
          console.log("Download sent to Wrench Downloader:", res);
        }
      });
    } else {
      fetch("http://127.0.0.1:45732/api/download", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload)
      }).catch((e) => {
        alert("Failed to communicate with Wrench Downloader: " + e.message);
      });
    }
  }

  // Periodic scan for video elements
  setInterval(scanForVideos, 1000);
  scanForVideos();
})();
