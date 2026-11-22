use crate::engine::{DownloadStore, EngineHandle, EngineCommand, UiEvent};
use crate::localization::Localization;
use crate::model::{self, DownloadItem, DownloadStatus, Settings};
use gpui::prelude::FluentBuilder;
use gpui_kit::component::button::{Button, ButtonVariants};
use gpui_kit::component::checkbox::Checkbox;
use gpui_kit::component::input::{Input, InputState};
use gpui_kit::component::label::Label;
use gpui_kit::component::progress::Progress;
use gpui_kit::component::{ActiveTheme, Disableable};
use gpui_kit::*;
use parking_lot::Mutex;
use std::sync::Arc;

#[derive(Clone)]
pub struct AppRuntime {
    pub store: DownloadStore,
    pub engine: EngineHandle,
    pub settings: Arc<Mutex<Settings>>,
    pub localization: Arc<Localization>,
}

struct PromptState {
    item: DownloadItem,
    id: String,
    title: String,
    url: String,
    folder: String,
    playlist: bool,
    later: bool,
    dirty: bool,
}

struct TorrentPickerState {
    id: String,
    name: String,
    files: Vec<(String, u64, bool)>,
}

pub struct AppView {
    runtime: AppRuntime,
    url_input: Entity<InputState>,
    prompt_title: Entity<InputState>,
    prompt_folder: Entity<InputState>,
    settings_folder: Entity<InputState>,
    settings_dirty: bool,
    status: String,
    queue_paused: bool,
    settings_open: bool,
    settings_draft: Settings,
    about_open: bool,
    prompt: Option<PromptState>,
    torrent_picker: Option<TorrentPickerState>,
    confirm_delete: Option<String>,
    settings_tab: usize,
}

impl AppView {
    pub fn new(
        runtime: AppRuntime,
        url_input: Entity<InputState>,
        prompt_title: Entity<InputState>,
        prompt_folder: Entity<InputState>,
        settings_folder: Entity<InputState>,
    ) -> Self {
        Self {
            runtime,
            url_input,
            prompt_title,
            prompt_folder,
            settings_folder,
            settings_dirty: false,
            status: "Ready. Files and streams captured from your browser will prompt for download.".into(),
            queue_paused: false,
            settings_open: false,
            settings_draft: Settings::default(),
            about_open: false,
            prompt: None,
            torrent_picker: None,
            confirm_delete: None,
            settings_tab: 0,
        }
    }

    fn tr(&self, key: &str) -> String { self.runtime.localization.get(key) }

    pub fn handle_event(&mut self, event: UiEvent, cx: &mut Context<Self>) {
        match event {
            UiEvent::ItemUpdated(_) => cx.notify(),
            UiEvent::DownloadsChanged => {
                model::save_history(&self.snapshot_items());
                cx.notify();
            }
            UiEvent::Status(text) => { self.status = text; cx.notify(); }
            UiEvent::QueuePaused(paused) => { self.queue_paused = paused; cx.notify(); }
            UiEvent::Completed(id) => {
                if let Some(item) = self.runtime.store.find(&id) {
                    let name = item.lock().title.clone();
                    self.status = format!("Completed: {}", name);
                }
                model::save_history(&self.snapshot_items());
                cx.notify();
            }
            UiEvent::Failed(_) => {
                self.status = "A download failed. See the item for details.".into();
                model::save_history(&self.snapshot_items());
                cx.notify();
            }
            UiEvent::BridgeHealth(ok) => {
                self.status = if ok { "Chrome Companion connected".into() } else { "Chrome Companion not detected".into() };
                cx.notify();
            }
            UiEvent::BridgeDownload { item, prompt } => {
                let should_prompt = prompt && self.runtime.settings.lock().show_download_dialog;
                if should_prompt {
                    crate::paths::log_line(&format!("prompt shown for {}", item.id));
                    self.prompt = Some(PromptState {
                        item: item.clone(),
                        id: item.id.clone(),
                        title: item.title.clone(),
                        url: if item.page_url == item.url && !item.page_url.is_empty() { "Waiting for fresh stream capture...".into() } else { item.url.clone() },
                        folder: if item.target_folder.is_empty() { self.runtime.settings.lock().download_folder.clone() } else { item.target_folder.clone() },
                        playlist: item.download_playlist,
                        later: item.wait_for_queue_start,
                        dirty: true,
                    });
                } else {
                    self.runtime.store.add(item.clone(), true);
                    self.runtime.engine.start(&item.id);
                    model::save_history(&self.snapshot_items());
                }
                cx.notify();
            }
            UiEvent::TorrentFiles { id, name, files } => {
                self.torrent_picker = Some(TorrentPickerState {
                    id, name,
                    files: files.into_iter().map(|(p, l)| (p, l, true)).collect(),
                });
                cx.notify();
            }
        }
    }

    fn snapshot_items(&self) -> Vec<DownloadItem> {
        self.runtime.store.list().iter().map(|i| i.lock().clone()).collect()
    }

    fn add_url(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let url = self.url_input.read(cx).value().trim().to_string();
        if url.is_empty() {
            self.status = self.tr("main.invalidUrl");
            cx.notify();
            return;
        }
        self.url_input.update(cx, |state, cx| state.set_value("", window, cx));
        let quality = self.runtime.settings.lock().default_quality.clone();
        let mut item = DownloadItem::new(&url, title_from_url(&url));
        item.quality = quality;
        item.status_text = "Waiting in queue".into();
        if url.to_ascii_lowercase().starts_with("magnet:") { item.is_torrent = true; }
        self.runtime.store.add(item.clone(), true);
        self.runtime.engine.start(&item.id);
        self.status = self.tr("main.started");
        model::save_history(&self.snapshot_items());
        cx.notify();
    }

    fn start_prompt(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some(prompt) = self.prompt.take() else { return };
        let title = self.prompt_title.read(cx).value().trim().to_string();
        let folder = self.prompt_folder.read(cx).value().trim().to_string();
        let id = prompt.id.clone();
        crate::paths::log_line(&format!("prompt start clicked for {}", id));
        let later = prompt.later;
        let mut item = prompt.item;
        if !title.is_empty() { item.title = title; }
        if std::path::Path::new(&folder).is_dir() { item.target_folder = folder; }
        item.download_playlist = prompt.playlist;
        item.wait_for_queue_start = later;
        item.scheduled_at = None;
        item.queue_name = "Main queue".into();
        item.status = DownloadStatus::Queued;
        item.status_text = if later { "Saved for later in Main queue".into() } else { "Waiting in queue".into() };
        self.runtime.store.add(item, true);
        if !later {
            self.runtime.engine.start(&id);
            self.status = self.tr("main.started");
        } else {
            self.status = "Saved for later in Main queue".into();
        }
        model::save_history(&self.snapshot_items());
        let _ = window;
        cx.notify();
    }

    fn toggle_pause(&self, id: &str) {
        let status = self.runtime.store.find(id).map(|i| i.lock().status);
        match status {
            Some(DownloadStatus::Downloading) | Some(DownloadStatus::Queued) => self.runtime.engine.pause(id),
            _ => self.runtime.engine.resume(id),
        }
    }

    fn remove_selected(&mut self) {
        let ids: Vec<String> = self.runtime.store.list().iter()
            .filter(|i| i.lock().selected).map(|i| i.lock().id.clone()).collect();
        for id in ids { self.runtime.engine.tx.send(EngineCommand::Remove(id)).ok(); }
        self.status = "Removed selected downloads".into();
        model::save_history(&self.snapshot_items());
    }

    fn remove_completed(&self) {
        let ids: Vec<String> = self.runtime.store.list().iter()
            .filter(|i| i.lock().status == DownloadStatus::Completed).map(|i| i.lock().id.clone()).collect();
        for id in ids { self.runtime.engine.tx.send(EngineCommand::Remove(id)).ok(); }
    }

    fn remove_all(&self) {
        let ids: Vec<String> = self.runtime.store.list().iter().map(|i| i.lock().id.clone()).collect();
        for id in ids { self.runtime.engine.tx.send(EngineCommand::Remove(id)).ok(); }
    }

    fn open_folder(&self) {
        let folder = self.runtime.settings.lock().download_folder.clone();
        #[cfg(target_os = "windows")]
        std::process::Command::new("explorer.exe").arg(&folder).spawn().ok();
        #[cfg(not(target_os = "windows"))]
        { let _ = folder; }
    }

    fn open_file(&mut self, path: &str) {
        if path.is_empty() || !std::path::Path::new(path).exists() {
            self.status.clone_from(&"File not available".to_string());
            return;
        }
        #[cfg(target_os = "windows")]
        std::process::Command::new("explorer.exe").arg(&path).spawn().ok();
        #[cfg(not(target_os = "windows"))]
        { let _ = path; }
    }

    fn save_settings(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.settings_draft.download_folder = self.settings_folder.read(cx).value().trim().to_string();
        let draft = self.settings_draft.clone();
        *self.runtime.settings.lock() = draft.clone();
        draft.save();
        self.runtime.localization.reload(&draft.language);
        self.settings_open = false;
        self.status = self.tr("main.settingsSaved");
        model::save_history(&self.snapshot_items());
        let _ = window;
        cx.notify();
    }
}

fn title_from_url(url: &str) -> String {
    url::Url::parse(url).ok()
        .and_then(|u| u.path().rsplit('/').find(|s| !s.is_empty()).map(|s| s.to_string()))
        .filter(|s| s.contains('.'))
        .unwrap_or_else(|| "Video_Download".into())
}

// ---------------------------------------------------------------------------
// Render
// ---------------------------------------------------------------------------

impl Render for AppView {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        if std::env::var_os("WD_MINIMAL_UI").is_some() {
            return div().size_full().child("minimal UI").into_any_element();
        }
        if self.prompt.as_ref().is_some_and(|p| p.dirty) {
            if let Some(p) = self.prompt.as_ref() {
                let title = p.title.clone(); let folder = p.folder.clone();
                self.prompt_title.update(cx, |s, cx| s.set_value(title, window, cx));
                self.prompt_folder.update(cx, |s, cx| s.set_value(folder, window, cx));
            }
            self.prompt.as_mut().unwrap().dirty = false;
        }

        let theme = cx.theme().clone();
        let mut root = div()
            .size_full()
            .bg(theme.background)
            .text_color(theme.foreground)
            .flex().flex_col();
        root = root.child(self.render_header(cx));
        root = root.child(self.render_toolbar(cx));
        root = root.child(self.render_list(cx));
        root = root.child(self.render_footer(cx));

        // Overlays
        if self.settings_open { root = root.child(self.render_settings(window, cx)); }
        if let Some(prompt) = self.prompt.as_ref() { root = root.child(self.render_prompt(prompt, cx)); }
        if self.about_open { root = root.child(self.render_about(cx)); }
        if let Some(picker) = self.torrent_picker.as_ref() { root = root.child(self.render_torrent_picker(picker, cx)); }
        if self.confirm_delete.is_some() { root = root.child(self.render_confirm_delete(cx)); }
        root.into_any_element()
    }
}

impl AppView {
    fn render_header(&self, cx: &mut Context<Self>) -> AnyElement {
        let theme = cx.theme().clone();
        div().flex().flex_row().items_center().gap_3().px_5().py_3()
            .border_b_1().border_color(theme.border)
            .child(Label::new("🔧").text_xl())
            .child(Label::new("Wrench Downloader").text_lg())
            .child(Label::new("v26.4.0").text_xs().text_color(theme.muted_foreground))
            .child(div().flex_1())
            .child(Input::new(&self.url_input).w(px(360.)))
            .child(Button::new("add-download").primary().label("Add Download")
                .on_click(cx.listener(|this, _, window, cx| this.add_url(window, cx))))
            .child(Button::new("open-folder").label("Open Folder")
                .on_click(cx.listener(|this, _, _, _| this.open_folder())))
            .into_any_element()
    }

    fn render_toolbar(&self, cx: &mut Context<Self>) -> AnyElement {
        let theme = cx.theme().clone();
        let items = self.runtime.store.list();
        let selected = items.iter().filter(|i| i.lock().selected).count();
        let active = items.iter().filter(|i| i.lock().status.is_active()).count();
        div().flex().flex_row().items_center().gap_2().px_5().py_2()
            .bg(theme.background)
            .border_b_1().border_color(theme.border)
            .child(Label::new(format!("{} downloads • {} active • {} selected", items.len(), active, selected)).text_sm())
            .child(Button::new("remove-selected").label("Remove Selected")
                .disabled(selected == 0)
                .on_click(cx.listener(|this, _, _, _| this.remove_selected())))
            .child(Button::new("remove-completed").label("Remove Completed")
                .on_click(cx.listener(|this, _, _, _| this.remove_completed())))
            .child(Button::new("remove-all").label("Remove All")
                .on_click(cx.listener(|this, _, _, _| this.remove_all())))
            .child(div().flex_1())
            .child(Label::new(if self.queue_paused { "Queue paused" } else { "● Bridge listening (127.0.0.1:45732)" }).text_sm()
                .text_color(if self.queue_paused { theme.muted_foreground } else { theme.accent }))
            .child(Button::new("queue-toggle").label(if self.queue_paused { "Resume Queue" } else { "Pause Queue" })
                .on_click(cx.listener(|this, _, _, _| {
                    this.queue_paused = !this.queue_paused;
                    this.runtime.engine.set_queue_paused(this.queue_paused);
                })))
            .child(Button::new("settings").label("Settings")
                .on_click(cx.listener(|this, _, _, cx| {
                    this.settings_draft = this.runtime.settings.lock().clone();
                    this.settings_open = true;
                    this.settings_dirty = true;
                    cx.notify();
                })))
            .child(Button::new("about").label("About")
                .on_click(cx.listener(|this, _, _, cx| { this.about_open = true; cx.notify(); })))
            .into_any_element()
    }

    fn render_list(&self, cx: &mut Context<Self>) -> AnyElement {
        let theme = cx.theme().clone();
        let items = self.runtime.store.list();
        let mut list = div().flex().flex_col().flex_1().p_4().gap_2();
        if items.is_empty() {
            list = list.child(
                div().flex_1().items_center().justify_center().py_16()
                    .child(Label::new("No downloads yet. Paste a URL above or capture one in your browser.")
                        .text_color(theme.muted_foreground))
            );
        }
        for item in items {
            list = list.child(self.render_card(&item, cx));
        }
        list.into_any_element()
    }

    fn render_card(&self, item: &std::sync::Arc<parking_lot::Mutex<DownloadItem>>, cx: &mut Context<Self>) -> AnyElement {
        let theme = cx.theme().clone();
        let snapshot = item.lock().clone();
        let id = snapshot.id.clone();
        let selected = snapshot.selected;
        let mut card = div().p_3().rounded_lg().border_1()
            .border_color(if selected { theme.accent } else { theme.border })
            .bg(theme.background)
            .flex().flex_col().gap_2()
            .child(
                div().flex().flex_row().gap_2()
                    .child(Checkbox::new(format!("select-{}", id)).checked(selected)
                        .on_change(cx.listener({
                            let store = self.runtime.store.clone();
                            let id = id.clone();
                            move |_this, v: &bool, _, cx| {
                                if let Some(i) = store.find(&id) { i.lock().selected = *v; }
                                cx.notify();
                            }
                        })))
                    .child(Label::new(&snapshot.title).flex_1().id(format!("title-{}", id)))
                    .child(Label::new(&snapshot.speed_text).text_sm().text_color(theme.accent).id(format!("speed-{}", id)))
            )
            .child(Progress::new(format!("progress-{}", id)).value(snapshot.progress as f32))
            .child(
                div().flex().flex_row().gap_3()
                    .child(Label::new(&snapshot.status_text).text_sm().flex_1().text_color(theme.muted_foreground).id(format!("status-{}", id)))
                    .child(Label::new(format!("{} • {}", snapshot.size_text, snapshot.eta_text)).text_xs().text_color(theme.muted_foreground).id(format!("size-{}", id)))
            );
        if snapshot.status != DownloadStatus::Completed {
            let action = snapshot.action_glyph().to_string();
            card = card.child(
                div().flex().flex_row().justify_end()
                    .child(Button::new(format!("toggle-{}", id)).label(action)
                        .on_click(cx.listener({
                            let id2 = id.clone();
                            move |this, _, _, _| this.toggle_pause(&id2)
                        })))
            );
        }
        card.into_any_element()
    }

    fn render_footer(&self, cx: &mut Context<Self>) -> AnyElement {
        let theme = cx.theme().clone();
        div().px_5().py_2().border_t_1().border_color(theme.border)
            .child(Label::new(&self.status).text_sm().text_color(theme.muted_foreground))
            .into_any_element()
    }
}

impl AppView {
    fn render_prompt(&self, prompt: &PromptState, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme().clone();
        let id = prompt.id.clone();
        let folder = prompt.folder.clone();
        let url = prompt.url.clone();
        let playlist = prompt.playlist;
        let later = prompt.later;
        div().absolute().inset_0().flex().items_center().justify_center()
            .bg(gpui::black().alpha(0.55))
            .child(
                div().w(px(560.)).max_h(px(760.)).p_5().rounded_lg()
                    .bg(theme.background).border_1().border_color(theme.border)
                    .flex().flex_col().gap_3()
                    .child(Label::new("Start Download").text_lg())
                    .child(Label::new("File name").text_sm())
                    .child(Input::new(&self.prompt_title))
                    .child(Label::new("URL").text_sm())
                    .child(div().max_h(px(90.)).p_2().rounded_md().bg(theme.input_background())
                        .child(Label::new(url).text_sm().text_color(theme.muted_foreground)))
                    .child(Checkbox::new("prompt-playlist").label("Download the full playlist")
                        .checked(playlist)
                        .on_change(cx.listener({
                            let id = id.clone();
                            move |this, v: &bool, _, cx| {
                                if let Some(p) = this.prompt.as_mut() { p.playlist = *v; }
                                let _ = &id;
                                cx.notify();
                            }
                        })))
                    .child(Label::new("Save folder").text_sm())
                    .child(Input::new(&self.prompt_folder))
                    .child(Checkbox::new("prompt-later").label("Download later")
                        .checked(later)
                        .on_change(cx.listener(|this, v: &bool, _, cx| {
                            if let Some(p) = this.prompt.as_mut() { p.later = *v; }
                            cx.notify();
                        })))
                    .child(Label::new(folder).text_xs().text_color(theme.muted_foreground))
                    .child(div().flex().flex_row().justify_end().gap_2().pt_2()
                        .child(Button::new("prompt-cancel").label("Cancel")
                            .on_click(cx.listener(|this, _, _, cx| { this.prompt = None; cx.notify(); })))
                        .child(Button::new("prompt-start").primary()
                            .label(if later { "Save for later" } else { "Start Download" })
                            .on_click(cx.listener(|this, _, window, cx| this.start_prompt(window, cx)))))
            )
    }

    fn render_settings(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        if self.settings_dirty {
            let folder = self.settings_draft.download_folder.clone();
            self.settings_folder.update(cx, |s, cx| s.set_value(folder, window, cx));
            self.settings_dirty = false;
        }
        let theme = cx.theme().clone();
        let tab = self.settings_tab;
        let mut content = div().flex().flex_col().flex_1().gap_3().p_4();
        match tab {
            0 => {
                content = content
                    .child(Label::new("Download location"))
                    .child(Input::new(&self.settings_folder))
                    .child(self.stepper("Maximum active downloads", self.settings_draft.maximum_concurrent_downloads, 1, 8,
                        |v, this| this.settings_draft.maximum_concurrent_downloads = v, cx))
                    .child(self.stepper("Parallel connections per download", self.settings_draft.concurrent_fragments, 1, 32,
                        |v, this| this.settings_draft.concurrent_fragments = v, cx))
                    .child(self.stepper("Max speed (KB/s, 0 = unlimited)", self.settings_draft.maximum_download_rate_kbps, 0, 1_000_000,
                        |v, this| this.settings_draft.maximum_download_rate_kbps = v, cx))
                    .child(self.stepper("Auto-retry attempts (0 = off)", self.settings_draft.auto_retry_count, 0, 10,
                        |v, this| this.settings_draft.auto_retry_count = v, cx))
                    .child(self.choices("Default quality", &["best", "2160p", "1440p", "1080p", "720p", "480p", "360p"],
                        &self.settings_draft.default_quality.clone(), |v, this| this.settings_draft.default_quality = v.to_string(), cx))
                    .child(self.toggle("Show download confirmation dialog", self.settings_draft.show_download_dialog,
                        |v, this| this.settings_draft.show_download_dialog = v, cx))
                    .child(self.toggle("Monitor clipboard for links", self.settings_draft.clipboard_monitor_enabled,
                        |v, this| this.settings_draft.clipboard_monitor_enabled = v, cx))
                    .child(self.toggle("Organize downloads into folders by type", self.settings_draft.organize_downloads_by_type,
                        |v, this| this.settings_draft.organize_downloads_by_type = v, cx));
            }
            1 => {
                content = content
                    .child(self.toggle("Minimize to system tray when closing", self.settings_draft.close_to_tray,
                        |v, this| this.settings_draft.close_to_tray = v, cx))
                    .child(self.toggle("Show completion dialog", self.settings_draft.show_complete_dialog,
                        |v, this| this.settings_draft.show_complete_dialog = v, cx))
                    .child(self.toggle("Play a sound on completion", self.settings_draft.play_sound_on_complete,
                        |v, this| this.settings_draft.play_sound_on_complete = v, cx))
                    .child(self.toggle("Confirm before deleting files", self.settings_draft.confirm_on_delete_file,
                        |v, this| this.settings_draft.confirm_on_delete_file = v, cx))
                    .child(self.toggle("Automatically resume interrupted downloads", self.settings_draft.auto_resume_interrupted,
                        |v, this| this.settings_draft.auto_resume_interrupted = v, cx));
            }
            2 => {
                content = content
                    .child(Label::new("Browser integration"))
                    .child(Label::new("1. Open chrome://extensions in your browser").text_sm())
                    .child(Label::new("2. Enable Developer mode").text_sm())
                    .child(Label::new("3. Choose “Load unpacked” and select the shipped extension folder").text_sm())
                    .child(Label::new("4. Pin the extension so the download badge is always visible").text_sm())
                    .child(Button::new("show-extension-folder").label("Show extension folder")
                        .on_click(cx.listener(|this, _, _, _| {
                            let path = crate::paths::Paths::get().resource("chrome extension");
                            #[cfg(target_os = "windows")] { std::process::Command::new("explorer.exe").arg(path).spawn().ok(); }
                            #[cfg(not(target_os = "windows"))] { let _ = path; }
                        })))
                    .child(Button::new("copy-extension-path").label("Copy extension path")
                        .on_click(cx.listener(|this, _, _, cx| {
                            let path = crate::paths::Paths::get().resource("chrome extension");
                            let text = path.to_string_lossy().into_owned();
                            if let Ok(mut clipboard) = arboard::Clipboard::new() {
                                if clipboard.set_text(&text).is_ok() {
                                    this.status = format!("Copied extension path: {}", text);
                                } else {
                                    this.status = "Could not copy extension path".into();
                                }
                            } else {
                                this.status = "Could not access clipboard".into();
                            }
                            cx.notify();
                        })));
            }
            _ => {
                content = content
                    .child(self.choices("Display mode", &["system", "light", "dark"], &self.settings_draft.app_theme.clone(),
                        |v, this| this.settings_draft.app_theme = v.to_string(), cx))
                    .child(self.choices("Language", &["en", "fr", "ar"], &self.settings_draft.language.clone(),
                        |v, this| this.settings_draft.language = v.to_string(), cx));
            }
        }
        let tabs = ["Downloads", "Behavior", "Browser integration", "Appearance"];
        let mut tab_bar = div().flex().flex_row().gap_1();
        for (i, name) in tabs.iter().enumerate() {
            tab_bar = tab_bar.child(Button::new(format!("tab-{}", i)).label(*name)
                .when(i == tab, |b| b.primary())
                .on_click(cx.listener(move |this, _, _, cx| { this.settings_tab = i; cx.notify(); })));
        }
        div().absolute().inset_0().flex().items_center().justify_center()
            .bg(gpui::black().alpha(0.55))
            .child(
                div().w(px(760.)).h(px(620.)).rounded_lg().bg(theme.background)
                    .border_1().border_color(theme.border).flex().flex_col()
                    .child(div().px_4().pt_3().child(tab_bar))
                    .child(content)
                    .child(div().flex().flex_row().justify_end().gap_2().p_4().border_t_1().border_color(theme.border)
                        .child(Button::new("settings-cancel").label("Cancel")
                            .on_click(cx.listener(|this, _, _, cx| { this.settings_open = false; cx.notify(); })))
                        .child(Button::new("settings-save").primary().label("Save")
                            .on_click(cx.listener(|this, _, window, cx| this.save_settings(window, cx)))))
            )
    }

    fn stepper(
        &self,
        label: &'static str,
        value: i32,
        min: i32,
        max: i32,
        setter: fn(i32, &mut Self),
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let minus = (value - 1).max(min);
        let plus = (value + 1).min(max);
        let key_minus = format!("{}-minus", label);
        let key_plus = format!("{}-plus", label);
        div().flex().flex_row().items_center().justify_between()
            .child(Label::new(label).text_sm())
            .child(div().flex().flex_row().items_center().gap_2()
                .child(Button::new(key_minus).label("−").disabled(value <= min)
                    .on_click(cx.listener(move |this, _, _, cx| { setter(minus, this); cx.notify(); })))
                .child(Label::new(value.to_string()).text_sm())
                .child(Button::new(key_plus).label("+").disabled(value >= max)
                    .on_click(cx.listener(move |this, _, _, cx| { setter(plus, this); cx.notify(); }))))
    }

    fn toggle(
        &self,
        label: &'static str,
        value: bool,
        setter: fn(bool, &mut Self),
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        Checkbox::new(label).label(label).checked(value)
            .on_click(cx.listener(move |this, v: &bool, _, cx| { setter(*v, this); cx.notify(); }))
    }

    fn choices(
        &self,
        label: &'static str,
        options: &'static [&'static str],
        current: &str,
        setter: fn(&str, &mut Self),
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let mut row = div().flex().flex_col().gap_1().child(Label::new(label).text_sm());
        let mut buttons = div().flex().flex_row().gap_1();
        for option in options {
            let option = *option;
            let selected = option == current;
            buttons = buttons.child(Button::new(format!("{}-{}", label, option)).label(option)
                .when(selected, |b| b.primary())
                .on_click(cx.listener(move |this, _, _, cx| { setter(option, this); cx.notify(); })));
        }
        row = row.child(buttons);
        row
    }

    fn render_about(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme().clone();
        div().absolute().inset_0().flex().items_center().justify_center()
            .bg(gpui::black().alpha(0.55))
            .child(div().w(px(440.)).p_5().rounded_lg().bg(theme.background)
                .border_1().border_color(theme.border).flex().flex_col().gap_2()
                .child(Label::new("Wrench Downloader").text_lg())
                .child(Label::new("Version 26.4.0 (Rust + GPUI Kit)").text_sm())
                .child(Label::new("A download manager that intercepts browser downloads and captures video streams structurally.").text_sm())
                .child(div().flex().flex_row().justify_end().pt_3()
                    .child(Button::new("about-close").primary().label("Close")
                        .on_click(cx.listener(|this, _, _, cx| { this.about_open = false; cx.notify(); })))))
    }

    fn render_torrent_picker(&self, picker: &TorrentPickerState, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme().clone();
        let id = picker.id.clone();
        let mut rows = div().flex().flex_col().gap_1().max_h(px(360.));
        for (idx, (path, size, checked)) in picker.files.iter().enumerate() {
            let id2 = id.clone();
            rows = rows.child(Checkbox::new(format!("torrent-file-{}", idx)).label(format!("{} — {}", path, crate::engine::util::human_size(*size)))
                .checked(*checked)
                .on_change(cx.listener(move |this, v: &bool, _, cx| {
                    if let Some(p) = this.torrent_picker.as_mut() { p.files[idx].2 = *v; }
                    let _ = &id2;
                    cx.notify();
                })));
        }
        div().absolute().inset_0().flex().items_center().justify_center()
            .bg(gpui::black().alpha(0.55))
            .child(div().w(px(620.)).p_5().rounded_lg().bg(theme.background)
                .border_1().border_color(theme.border).flex().flex_col().gap_3()
                .child(Label::new("Torrent Files").text_lg())
                .child(Label::new(&picker.name).text_sm())
                .child(rows)
                .child(div().flex().flex_row().justify_end().gap_2()
                    .child(Button::new("torrent-cancel").label("Cancel")
                        .on_click(cx.listener(|this, _, _, cx| { this.torrent_picker = None; cx.notify(); })))
                    .child(Button::new("torrent-start").primary().label("Start Download")
                        .on_click(cx.listener(|this, _, _, cx| {
                            if let Some(p) = this.torrent_picker.take() {
                                let files = p.files.into_iter().filter(|(_, _, s)| *s).map(|(f, _, _)| f).collect();
                                this.runtime.engine.tx.send(EngineCommand::TorrentFiles { id: p.id, files }).ok();
                            }
                            cx.notify();
                        })))))
    }

    fn render_confirm_delete(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme().clone();
        div().absolute().inset_0().flex().items_center().justify_center()
            .bg(gpui::black().alpha(0.55))
            .child(div().w(px(380.)).p_5().rounded_lg().bg(theme.background)
                .border_1().border_color(theme.border).flex().flex_col().gap_3()
                .child(Label::new("Delete this file from disk?").text_sm())
                .child(div().flex().flex_row().justify_end().gap_2()
                    .child(Button::new("delete-cancel").label("Cancel")
                        .on_click(cx.listener(|this, _, _, cx| { this.confirm_delete = None; cx.notify(); })))
                    .child(Button::new("delete-confirm").danger().label("Delete")
                        .on_click(cx.listener(|this, _, _, cx| { this.confirm_delete = None; cx.notify(); })))))
    }
}
