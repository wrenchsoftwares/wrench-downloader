// Popup JS - displays streams captured for the active tab and desktop app status
const APP_HEALTH_URL = "http://127.0.0.1:45732/api/health";

document.addEventListener("DOMContentLoaded", async () => {
  const statusEl = document.getElementById("appStatus");
  const listEl = document.getElementById("mediaList");
  const interceptToggle = document.getElementById("interceptToggle");

  // Load intercept downloads preference
  if (interceptToggle) {
    chrome.storage.local.get({ interceptDownloads: true }, (items) => {
      interceptToggle.checked = items.interceptDownloads !== false;
    });
    interceptToggle.addEventListener("change", () => {
      chrome.storage.local.set({ interceptDownloads: interceptToggle.checked });
    });
  }

  // Check desktop app connectivity
  try {
    const res = await fetch(APP_HEALTH_URL);
    if (res.ok) {
      statusEl.textContent = "App Connected";
      statusEl.classList.remove("status-offline");
    } else {
      throw new Error();
    }
  } catch (e) {
    statusEl.textContent = "App Offline";
    statusEl.classList.add("status-offline");
  }

  // Get active tab
  const [activeTab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!activeTab) return;

  // Same generic flow on every page: ask the tab for its real items first,
  // fall back to background-captured streams, then to a plain page entry.
  chrome.tabs.sendMessage(activeTab.id, { action: "GET_FORMATS" }, (response) => {
    if (!chrome.runtime.lastError && response && response.items && response.items.length > 0) {
      renderItems(response.items.filter((item) => item.url && !item.isPromptToPlay));
      if (listEl.children.length > 0) return;
    }
    renderCapturedFallback();
  });
  return;

  function renderItems(items) {
    listEl.innerHTML = "";
    items.forEach((item) => {
      if (!item.url || item.isPromptToPlay) return;
      const div = document.createElement("div");
      div.className = "item";
      div.innerHTML = `
        <div class="item-title">${escapeHtml(item.displayName || item.title)}</div>
        <div class="item-url">${escapeHtml(item.sub || "")}</div>
      `;
      div.addEventListener("click", () => {
        chrome.runtime.sendMessage({
          action: "SEND_TO_APP",
          payload: {
            url: item.url,
            title: item.title,
            quality: item.quality || "best",
            format: item.format || "mp4",
            pageUrl: activeTab.url,
            referrer: item.referrer || activeTab.url,
            userAgent: item.userAgent || navigator.userAgent,
            prompt: false
          }
        }, (res) => {
          if (res && res.success) window.close();
          else alert("Could not reach Wrench Downloader desktop app.");
        });
      });
      listEl.appendChild(div);
    });
  }

  // Fallback: background-captured streams, then a plain page entry.
  function renderCapturedFallback() {
    chrome.runtime.sendMessage({ action: "GET_MEDIA", tabId: activeTab.id }, (response) => {
      if (!response || !response.media || response.media.length === 0) {
        renderPageFallback();
        return;
      }

      listEl.innerHTML = "";
      response.media.forEach((item) => {
        const div = document.createElement("div");
        div.className = "item";
        const cleanTitle = (item.title || activeTab.title || "Download").replace(/[\/\\:*?"<>|]/g, "").trim();
        let format = item.format || "";
        const mExt = item.url.split("?")[0].match(/\.([a-z0-9]+)($|\?)/i);
        if (mExt && !format) format = mExt[1].toLowerCase();
        if (item.type === "hls" || item.type === "dash") format = "mp4";

        const isStream = item.type === "hls" || item.type === "dash" || item.type === "video" || item.type === "audio";
        const qLabel = item.quality || (isStream ? (format === "mp3" ? "Audio" : (item.size ? item.size : "Media")) : "File");
        const resStr = item.size ? `(Size: ${item.size})` : "";
        const filename = format && !cleanTitle.toLowerCase().endsWith("." + format) ? `${cleanTitle}.${format}` : cleanTitle;

        div.innerHTML = `
          <div class="item-title">${escapeHtml(filename)}</div>
          <div class="item-url">${format ? `Type: ${format.toUpperCase()}` : ""}${resStr ? ` | ${resStr}` : ""}</div>
        `;

        div.addEventListener("click", () => {
          chrome.runtime.sendMessage({
            action: "SEND_TO_APP",
            payload: {
              url: item.url,
              title: filename,
              quality: qLabel,
              format: format,
              pageUrl: activeTab.url,
              referrer: item.referer || activeTab.url,
              userAgent: item.userAgent || navigator.userAgent,
              prompt: false
            }
          }, (res) => {
            if (res && res.success) {
              window.close();
            } else {
              alert("Could not reach Wrench Downloader desktop app. Ensure the app is open.");
            }
          });
        });

        listEl.appendChild(div);
      });
      if (!listEl.children.length) renderPageFallback();
    });
  }
});

function escapeHtml(str) {
  return String(str).replace(/[&<>'"]/g,
    tag => ({
      '&': '&amp;',
      '<': '&lt;',
      '>': '&gt;',
      "'": '&#39;',
      '"': '&quot;'
    }[tag] || tag)
  );
}

// Last-resort when no media or downloadable stream was detected on the page
function renderPageFallback() {
  const listEl = document.getElementById("mediaList");
  chrome.tabs.query({ active: true, currentWindow: true }, ([activeTab]) => {
    if (!activeTab) return;
    listEl.innerHTML = `
      <div class="empty">
        <div>No active downloads or media streams on this tab.</div>
        <div style="margin-top: 6px; font-size: 11px; color: #6ee7b7;">Browser downloads (ZIP, EXE, ISO, etc.) are automatically captured when you click download links!</div>
      </div>
    `;
  });
}

