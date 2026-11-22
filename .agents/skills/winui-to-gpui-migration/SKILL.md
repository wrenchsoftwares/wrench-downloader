---
name: winui-to-gpui-migration
description: Playbook for porting a C#/.NET WinUI 3 desktop app to Rust with GPUI Kit. Use when asked to rewrite or migrate a desktop app to Rust/GPUI, assess feasibility, plan the port layer by layer, preserve wire protocols, or avoid common migration and GPUI API pitfalls.
---

# WinUI 3 → Rust + GPUI Kit migration

This is a **rewrite, not a translation**. C#/XAML cannot be compiled into Rust.
Plan around what survives unchanged, what must be reimplemented, and what can
be verified independently.

## 1. Classify before writing code

Split the existing app into three buckets:

**Survives unchanged (language-agnostic):**
- Browser extensions / JS payloads / wire protocols.
- External tools invoked as processes (yt-dlp, ffmpeg, aria2c, etc.).
- JSON data formats, locale dictionaries, folder layout conventions.
- Loopback HTTP endpoints and their exact field names / headers.

**Must be rewritten:**
- XAML views → `Render` views / `RenderOnce` components.
- Owners of mutable state → `Entity<T>` / `Arc<Mutex<T>>` with an explicit event channel.
- Platform integrations (tray, taskbar, protocol handlers, clipboard, pickers).
- Async engine code → Tokio tasks + channels.

**Can be dropped or deferred:** framework-specific plumbing (dispatchers,
`INotifyPropertyChanged`, XAML binding, WinRT storage APIs).

## 2. Preserve the contract first

Implement and test the loopback bridge / wire protocol before the UI. It is the
seam between the existing ecosystem and the new app, and it is cheap to verify
with `curl` / `Invoke-RestMethod`. Keep:
- endpoint paths, HTTP methods, status codes and response bodies;
- JSON field names (including camelCase vs PascalCase differences between the
  wire payload and persisted files);
- CORS / Private Network Access headers;
- defaults (e.g. `prompt` defaults true — only explicit false suppresses it);
- caps and allow-lists for untrusted fields.

## 3. Pick the stack deliberately

- Prefer **one UI crate**: `gpui-kit = "0.10"` re-exports GPUI, base, components
  and assets. Do not add raw `gpui`/`gpui-component` dependencies just to copy
  an older example.
- GPUI Kit is cross-platform (Windows/macOS/Linux) and includes 75+ widgets,
  virtual lists, data tables, dialogs and theming — it removes most of the risk
  that raw GPUI has for a conventional desktop app.
- Use Tokio for networking/process I/O; GPUI's executor is separate. Bridge the
  two with an unbounded channel: engine sends `UiEvent`, a `cx.spawn` future
  receives it and calls `app.update(|cx| view.update(cx, ...))` followed by
  `cx.notify()`.

## 4. Port order that works

1. **Paths + models + persistence** — portable vs installed dirs, exact JSON
   field names, credential scrubbing, settings defaults and clamps.
2. **Bridge server** — health + download endpoints, byte-compatible responses.
3. **Engine** — classification first, then direct HTTP, then external-tool
   pipelines, then exotic paths (segment replay, torrents).
4. **UI shell** — header/toolbar/list/footer with helper methods per section.
5. **Overlays** — prompt/settings/about/picker. Modal overlays in the main
   window are a pragmatic substitute for multiple OS windows.
6. **Platform edges** — tray, taskbar, protocol registration, clipboard.

## 5. GPUI Kit pitfalls (verified on gpui-kit 0.10 / gpui-pre 0.3.12)

- `WindowOptions` has `window_bounds`, not `bounds`, and no `title` field.
  Maximize with `Some(WindowBounds::Maximized(restore_bounds))`.
- Entity creation (`cx.new`) passes only `&mut Context<T>`, so entities whose
  constructors need `&mut Window` (e.g. `InputState::new`) must be created in
  the `open_window` build closure where `window` is available.
- `h_flex()` / `v_flex()` are free functions; on an element use
  `.flex().flex_row()` / `.flex().flex_col()`.
- Component names matter: it is `progress::Progress`, not `ProgressBar`.
- Trait imports (`ActiveTheme`, `Disableable`) are required for `cx.theme()`
  and `.disabled(...)`.
- **Debug stack overflow**: a single deeply chained `render` can overflow the
  main thread stack. Split sections into methods returning `AnyElement` and
  return `root.into_any_element()`.
- `ElementId`s must be stable and domain-derived; avoid raw list indexes.

## 6. Verification recipe

- `cargo check` after each module; `cargo build` before smoke tests.
- Stop the running GUI app before rebuilding (Windows locks the exe).
- Local end-to-end test: start a Python `http.server` serving a known-size file,
  POST it to the bridge with `prompt:false`, wait, assert the file appears in
  the configured download folder with the expected length.
- Keep the loopback bridge response contract under test — it protects the
  browser extension integration.

## 7. Communication with the user

Be explicit about what is and is not ported. A functional rewrite with a short,
honest parity-gap list is more useful than claiming full parity. Suggest a
concrete next slice (tray/taskbar, localization, native pickers) rather than
an open-ended "more work needed".
