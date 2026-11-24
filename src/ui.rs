use crate::engine::{DownloadStore, EngineHandle, EngineCommand, UiEvent};
use crate::localization::Localization;
use crate::model::{self, DownloadItem, DownloadStatus, Settings};
use gpui::prelude::FluentBuilder;
use gpui_kit::base::LogicalStyledExt as _;
use gpui_kit::component::button::{Button, ButtonVariants};
use gpui_kit::component::checkbox::Checkbox;
use gpui_kit::component::input::{Input, InputState};
use gpui_kit::component::label::Label;
use gpui_kit::component::list::ListItem;
use gpui_kit::component::progress::Progress;
use gpui_kit::component::{ActiveTheme, Disableable, Theme, ThemeMode};
use gpui_kit::*;
use parking_lot::Mutex;
use std::sync::Arc;

#[derive(Clone)]
pub struct AppRuntime {
    pub store: DownloadStore,
    pub engine: EngineHandle,
    pub settings: Arc<Mutex<Settings>>,
    pub localization: Arc<Localization>,
    pub ui_tx: tokio::sync::mpsc::UnboundedSender<UiEvent>,
}

struct TorrentPickerState {
    id: String,
    name: String,
    files: Vec<(String, u64, bool)>,
}

pub struct AppView {
    runtime: AppRuntime,
    url_input: Entity<InputState>,
    settings_folder: Entity<InputState>,
    settings_dirty: bool,
    status: String,
    queue_paused: bool,
    settings_open: bool,
    settings_draft: Settings,
    settings_original: Settings,
    about_open: bool,
    torrent_picker: Option<TorrentPickerState>,
    confirm_delete: Option<String>,
    settings_tab: usize,
}

impl AppView {
    pub fn new(
        runtime: AppRuntime,
        url_input: Entity<InputState>,
        settings_folder: Entity<InputState>,
    ) -> Self {
        let status = runtime.localization.get("main.ready");
        Self {
            runtime,
            url_input,
            settings_folder,
            settings_dirty: false,
            status,
            queue_paused: false,
            settings_open: false,
            settings_draft: Settings::default(),
            settings_original: Settings::default(),
            about_open: false,
            torrent_picker: None,
            confirm_delete: None,
            settings_tab: 0,
        }
    }

    fn tr(&self, key: &str) -> String { self.runtime.localization.get(key) }

    fn fmt(&self, key: &str, args: &[&str]) -> String { self.runtime.localization.format(key, args) }

    /// Engine status/speed text is plain English; resolve it at render time.
    fn tr_status(&self, text: &str) -> String { self.runtime.localization.translate_text(text) }

    /// InputState caches its placeholder, so a live language switch must push
    /// the translated placeholder into the input explicitly.
    fn update_placeholder(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let text = self.runtime.localization.get("main.urlPlaceholder");
        self.url_input.update(cx, |s, cx| s.set_placeholder(text, window, cx));
    }

    fn open_settings(&mut self, cx: &mut Context<Self>) {
        let original = self.runtime.settings.lock().clone();
        self.settings_draft = original.clone();
        self.settings_original = original;
        self.settings_open = true;
        self.settings_dirty = true;
        cx.notify();
    }

    /// Close without committing: put the live theme/language back to what was
    /// active when the dialog opened, and drop all other draft edits.
    fn cancel_settings(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let original = self.settings_original.clone();
        self.settings_draft = original.clone();
        self.settings_dirty = true;
        self.settings_open = false;
        apply_language(&self.runtime.localization, &original.language, cx);
        apply_theme(&original.app_theme, cx);
        self.update_placeholder(window, cx);
        self.status = self.tr("main.settingsCancelled");
        self.settings_folder.update(cx, |s, cx| s.set_value(original.download_folder, window, cx));
        cx.notify();
    }

    pub fn handle_event(&mut self, event: UiEvent, cx: &mut Context<Self>) {
        match event {
            UiEvent::ItemUpdated(_) => cx.notify(),
            UiEvent::DownloadsChanged => {
                model::save_history(&self.snapshot_items());
                cx.notify();
            }
            UiEvent::Status(text) => { self.status = self.tr_status(&text); cx.notify(); }
            UiEvent::QueuePaused(paused) => { self.queue_paused = paused; cx.notify(); }
            UiEvent::Completed(id) => {
                if let Some(item) = self.runtime.store.find(&id) {
                    let name = item.lock().title.clone();
                    self.status = self.fmt("main.completedStatus", &[&name]);
                }
                model::save_history(&self.snapshot_items());
                cx.notify();
            }
            UiEvent::Failed(_) => {
                self.status = self.tr("main.failedStatus");
                model::save_history(&self.snapshot_items());
                cx.notify();
            }
            UiEvent::BridgeDownload { item, prompt } => {
                let should_prompt = prompt && self.runtime.settings.lock().show_download_dialog;
                if should_prompt {
                    crate::paths::log_line(&format!("prompt shown for {}", item.id));
                    match crate::prompt::open_prompt_window(self.runtime.clone(), item.clone(), cx) {
                        Ok(_) => crate::paths::log_line("prompt window opened"),
                        Err(e) => crate::paths::log_line(&format!("prompt window FAILED: {}", e)),
                    }
                } else {
                    self.runtime.store.add(item.clone(), true);
                    self.runtime.engine.start(&item.id);
                    model::save_history(&self.snapshot_items());
                }
                cx.notify();
            }
            UiEvent::TrayExit => {
                // Handled by the main event loop (calls cx.quit()).
            }
            UiEvent::TraySettings => self.open_settings(cx),
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
        item.status_text = self.tr("download.queued");
        if url.to_ascii_lowercase().starts_with("magnet:") { item.is_torrent = true; }
        self.runtime.store.add(item.clone(), true);
        self.runtime.engine.start(&item.id);
        self.status = self.fmt("main.started", &[&url]);
        model::save_history(&self.snapshot_items());
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
        let count = ids.len().to_string();
        for id in ids { self.runtime.engine.tx.send(EngineCommand::Remove(id)).ok(); }
        self.status = self.fmt("main.removedItems", &[&count]);
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

    fn save_settings(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.settings_draft.download_folder = self.settings_folder.read(cx).value().trim().to_string();
        let draft = self.settings_draft.clone();
        *self.runtime.settings.lock() = draft.clone();
        draft.save();
        apply_language(&self.runtime.localization, &draft.language, cx);
        apply_theme(&draft.app_theme, cx);
        self.update_placeholder(window, cx);
        self.settings_open = false;
        self.status = self.fmt("main.settingsSaved", &[&draft.download_folder]);
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

/// Apply a locale to the UI immediately. Layout direction is app-global;
/// translated strings come from the shared Localization instance.
pub fn apply_language(localization: &Localization, lang: &str, cx: &mut App) {
    localization.reload(lang);
    gpui_kit::base::LayoutDirection::from_locale(lang).set_global(cx);
}

/// Apply a display-mode choice immediately to every window.
pub fn apply_theme(theme: &str, cx: &mut App) {
    match theme {
        "light" => Theme::change(ThemeMode::Light, None, cx),
        "dark" => Theme::change(ThemeMode::Dark, None, cx),
        _ => Theme::sync_system_appearance(None, cx),
    }
}

fn theme_selected(_value: &str, this: &mut AppView, _window: &mut Window, cx: &mut Context<AppView>) {
    let theme = this.settings_draft.app_theme.clone();
    apply_theme(&theme, cx);
}

fn language_selected(_value: &str, this: &mut AppView, window: &mut Window, cx: &mut Context<AppView>) {
    let lang = this.settings_draft.language.clone();
    apply_language(&this.runtime.localization, &lang, cx);
    this.update_placeholder(window, cx);
}

// ---------------------------------------------------------------------------
// Render
// ---------------------------------------------------------------------------

impl Render for AppView {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        if std::env::var_os("WD_MINIMAL_UI").is_some() {
            return div().size_full().child("minimal UI").into_any_element();
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
        if self.about_open { root = root.child(self.render_about(cx)); }
        if let Some(picker) = self.torrent_picker.as_ref() { root = root.child(self.render_torrent_picker(picker, cx)); }
        if self.confirm_delete.is_some() { root = root.child(self.render_confirm_delete(cx)); }
        root.into_any_element()
    }
}

impl AppView {
    fn render_header(&self, cx: &mut Context<Self>) -> AnyElement {
        let theme = cx.theme().clone();
        div().flex_row_dir(cx).items_center().gap_3().px_5().py_3()
            .border_b_1().border_color(theme.border)
            .child(Label::new("🔧").text_xl())
            .child(Label::new(self.tr("app.title")).text_lg())
            .child(Label::new(self.fmt("main.version", &["26.4.4"])).text_xs().text_color(theme.muted_foreground))
            .child(div().flex_1())
            .child(Input::new(&self.url_input).w(px(360.)))
            .child(Button::new("add-download").primary().label(self.tr("main.add"))
                .on_click(cx.listener(|this, _, window, cx| this.add_url(window, cx))))
            .child(Button::new("open-folder").label(self.tr("main.openFolder"))
                .on_click(cx.listener(|this, _, _, _| this.open_folder())))
            .into_any_element()
    }

    fn render_toolbar(&self, cx: &mut Context<Self>) -> AnyElement {
        let theme = cx.theme().clone();
        let items = self.runtime.store.list();
        let selected = items.iter().filter(|i| i.lock().selected).count();
        let active = items.iter().filter(|i| i.lock().status.is_active()).count();
        div().flex_row_dir(cx).items_center().gap_2().px_5().py_2()
            .bg(theme.background)
            .border_b_1().border_color(theme.border)
            .child(Label::new(self.fmt("main.count", &[&items.len().to_string(), &active.to_string(), &selected.to_string()])).text_sm())
            .child(Button::new("remove-selected").label(self.tr("main.removeSelected"))
                .disabled(selected == 0)
                .on_click(cx.listener(|this, _, _, _| this.remove_selected())))
            .child(Button::new("remove-completed").label(self.tr("main.removeCompleted"))
                .on_click(cx.listener(|this, _, _, _| this.remove_completed())))
            .child(Button::new("remove-all").label(self.tr("main.removeAll"))
                .on_click(cx.listener(|this, _, _, _| this.remove_all())))
            .child(div().flex_1())
            .child(Label::new(if self.queue_paused { self.tr("queue.paused") } else { format!("● {}", self.tr("main.listening")) }).text_sm()
                .text_color(if self.queue_paused { theme.muted_foreground } else { theme.accent }))
            .child(Button::new("queue-toggle").label(if self.queue_paused { self.tr("queue.start") } else { self.tr("queue.pause") })
                .on_click(cx.listener(|this, _, _, _| {
                    this.queue_paused = !this.queue_paused;
                    this.runtime.engine.set_queue_paused(this.queue_paused);
                })))
            .child(Button::new("settings").label(self.tr("main.settings"))
                .on_click(cx.listener(|this, _, _, cx| this.open_settings(cx))))
            .child(Button::new("about").label(self.tr("main.aboutTitle"))
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
                    .child(Label::new(self.tr("main.listEmptyHint"))
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
                div().flex_row_dir(cx).gap_2()
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
                    .child(Label::new(self.tr_status(&snapshot.speed_text)).text_sm().text_color(theme.accent).id(format!("speed-{}", id)))
            )
            .child(Progress::new(format!("progress-{}", id)).value(snapshot.progress as f32))
            .child(
                div().flex_row_dir(cx).gap_3()
                    .child(Label::new(self.tr_status(&snapshot.status_text)).text_sm().flex_1().text_color(theme.muted_foreground).id(format!("status-{}", id)))
                    .child(Label::new(format!("{} • {}", snapshot.size_text, snapshot.eta_text)).text_xs().text_color(theme.muted_foreground).id(format!("size-{}", id)))
            );
        if snapshot.status != DownloadStatus::Completed {
            let action = snapshot.action_glyph().to_string();
            card = card.child(
                div().flex_row_dir(cx).justify_flex_end()
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
            // 0: Behavior (general) -> 1: Downloads -> 2: Appearance -> 3: Browser integration
            0 => {
                content = content
                    .child(self.toggle(&self.tr("settings.closeToTray"), self.settings_draft.close_to_tray,
                        |v, this| this.settings_draft.close_to_tray = v, cx))
                    .child(self.toggle(&self.tr("settings.showCompleteDialog"), self.settings_draft.show_complete_dialog,
                        |v, this| this.settings_draft.show_complete_dialog = v, cx))
                    .child(self.toggle(&self.tr("settings.playSound"), self.settings_draft.play_sound_on_complete,
                        |v, this| this.settings_draft.play_sound_on_complete = v, cx))
                    .child(self.toggle(&self.tr("settings.confirmDeleteFile"), self.settings_draft.confirm_on_delete_file,
                        |v, this| this.settings_draft.confirm_on_delete_file = v, cx))
                    .child(self.toggle(&self.tr("settings.autoResume"), self.settings_draft.auto_resume_interrupted,
                        |v, this| this.settings_draft.auto_resume_interrupted = v, cx));
            }
            1 => {
                content = content
                    .child(Label::new(self.tr("settings.downloadLocation")))
                    .child(Input::new(&self.settings_folder))
                    .child(self.stepper(&self.tr("settings.maximumDownloads"), self.settings_draft.maximum_concurrent_downloads, 1, 8,
                        |v, this| this.settings_draft.maximum_concurrent_downloads = v, cx))
                    .child(self.stepper(&self.tr("settings.parallelFragments"), self.settings_draft.concurrent_fragments, 1, 32,
                        |v, this| this.settings_draft.concurrent_fragments = v, cx))
                    .child(self.stepper(&self.tr("settings.maximumRate"), self.settings_draft.maximum_download_rate_kbps, 0, 1_000_000,
                        |v, this| this.settings_draft.maximum_download_rate_kbps = v, cx))
                    .child(self.stepper(&self.tr("settings.autoRetry"), self.settings_draft.auto_retry_count, 0, 10,
                        |v, this| this.settings_draft.auto_retry_count = v, cx))
                    .child(self.choices(&self.tr("settings.defaultQuality"), &["best", "2160p", "1440p", "1080p", "720p", "480p", "360p"],
                        &self.settings_draft.default_quality.clone(), |v, this| this.settings_draft.default_quality = v.to_string(), cx))
                    .child(self.toggle(&self.tr("settings.confirmation"), self.settings_draft.show_download_dialog,
                        |v, this| this.settings_draft.show_download_dialog = v, cx))
                    .child(self.toggle(&self.tr("settings.clipboardMonitor"), self.settings_draft.clipboard_monitor_enabled,
                        |v, this| this.settings_draft.clipboard_monitor_enabled = v, cx))
                    .child(self.toggle(&self.tr("settings.organizeDownloads"), self.settings_draft.organize_downloads_by_type,
                        |v, this| this.settings_draft.organize_downloads_by_type = v, cx));
            }
            2 => {
                content = content
                    .child(self.choices_named(&self.tr("settings.themeHeader"), &[
                        ("system".to_string(), self.tr("theme.system")),
                        ("light".to_string(), self.tr("theme.light")),
                        ("dark".to_string(), self.tr("theme.dark")),
                    ], &self.settings_draft.app_theme.clone(),
                        |v, this| this.settings_draft.app_theme = v.to_string(),
                        theme_selected, cx))
                    .child(self.choices_named(&self.tr("settings.languageHeader"), &[
                        ("en".to_string(), self.tr("language.english")),
                        ("fr".to_string(), self.tr("language.french")),
                        ("ar".to_string(), self.tr("language.arabic")),
                    ], &self.settings_draft.language.clone(),
                        |v, this| this.settings_draft.language = v.to_string(),
                        language_selected, cx));
            }
            _ => {
                content = content
                    .child(Label::new(self.tr("settings.browserHeader")))
                    .child(Label::new(format!("{}{}{}",
                        self.tr("settings.browserStep1Before"), "chrome://extensions",
                        self.tr("settings.browserStep1After"))).text_sm())
                    .child(Label::new(self.tr("settings.browserStep2")).text_sm())
                    .child(Label::new(self.tr("settings.browserStep3")).text_sm())
                    .child(Label::new(self.tr("settings.browserStep4")).text_sm())
                    .child(Button::new("show-extension-folder").label(self.tr("ext.showFolder"))
                        .on_click(cx.listener(|_, _, _, _| {
                            let path = crate::paths::Paths::get().resource("chrome extension");
                            #[cfg(target_os = "windows")] { std::process::Command::new("explorer.exe").arg(path).spawn().ok(); }
                            #[cfg(not(target_os = "windows"))] { let _ = path; }
                        })))
                    .child(Button::new("copy-extension-path").label(self.tr("ext.copyPath"))
                        .on_click(cx.listener(|this, _, _, cx| {
                            let path = crate::paths::Paths::get().resource("chrome extension");
                            let text = path.to_string_lossy().into_owned();
                            if let Ok(mut clipboard) = arboard::Clipboard::new() {
                                if clipboard.set_text(&text).is_ok() {
                                    this.status = this.fmt("ext.copiedPath", &[&text]);
                                } else {
                                    this.status = this.tr("ext.copyFailed");
                                }
                            } else {
                                this.status = this.tr("ext.clipboardUnavailable");
                            }
                            cx.notify();
                        })));
            }
        }
        // Categories are a real selectable list on the left; only the content
        // pane changes, so Save/Cancel stay in a stable footer.
        let tabs = [
            self.tr("settings.behaviorTab"),
            self.tr("settings.downloadsTab"),
            self.tr("settings.appearanceTab"),
            self.tr("settings.browsersTab"),
        ];
        let weak = cx.entity().downgrade();
        let mut tab_list = div().flex().flex_col().gap_1();
        for (i, name) in tabs.iter().enumerate() {
            let weak = weak.clone();
            tab_list = tab_list.child(
                ListItem::new(format!("settings-tab-{}", i))
                    .accessibility_label(name.as_str())
                    .selected(i == tab)
                    .on_click(move |_, _, app| {
                        let _ = weak.update(app, |this, cx| {
                            this.settings_tab = i;
                            cx.notify();
                        });
                    })
                    .child(Label::new(name.as_str()).id(format!("settings-tab-label-{}", i))),
            );
        }
        div().absolute().inset_0().flex().items_center().justify_center()
            .bg(gpui::black().alpha(0.55))
            .child(
                div().w(px(760.)).h(px(620.)).rounded_lg().bg(theme.background)
                    .border_1().border_color(theme.border).flex().flex_col()
                    .child(div().px_5().py_3().border_b_1().border_color(theme.border)
                        .child(Label::new(self.tr("settings.title")).text_lg()))
                    .child(div().flex_1().min_h_0().flex_row_dir(cx)
                        .child(div().w(px(200.)).flex().flex_col().gap_1().p_3()
                            .border_e(px(1.), cx).border_color(theme.border)
                            .child(tab_list))
                        .child(content.min_w_0()))
                    .child(div().flex_row_dir(cx).justify_flex_end().gap_2().p_4().border_t_1().border_color(theme.border)
                        .child(Button::new("settings-cancel").label(self.tr("settings.cancel"))
                            .on_click(cx.listener(|this, _, window, cx| this.cancel_settings(window, cx))))
                        .child(Button::new("settings-save").primary().label(self.tr("settings.save"))
                            .on_click(cx.listener(|this, _, window, cx| this.save_settings(window, cx)))))
            )
    }

    fn stepper(
        &self,
        label: &str,
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
        div().flex_row_dir(cx).items_center().justify_between()
            .child(Label::new(label).text_sm().id(format!("stepper-label-{}", label)))
            .child(div().flex_row_dir(cx).items_center().gap_2()
                .child(Button::new(key_minus).label("−").disabled(value <= min)
                    .on_click(cx.listener(move |this, _, _, cx| { setter(minus, this); cx.notify(); })))
                .child(Label::new(value.to_string()).text_sm().id(format!("stepper-value-{}", label)))
                .child(Button::new(key_plus).label("+").disabled(value >= max)
                    .on_click(cx.listener(move |this, _, _, cx| { setter(plus, this); cx.notify(); }))))
    }

    fn toggle(
        &self,
        label: &str,
        value: bool,
        setter: fn(bool, &mut Self),
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        Checkbox::new(label.to_string()).label(label.to_string()).checked(value)
            .on_click(cx.listener(move |this, v: &bool, _, cx| { setter(*v, this); cx.notify(); }))
    }

    fn choices(
        &self,
        label: &str,
        options: &'static [&'static str],
        current: &str,
        setter: fn(&str, &mut Self),
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let mut row = div().flex().flex_col().gap_1()
            .child(Label::new(label).text_sm().id(format!("choices-label-{}", label)));
        let mut buttons = div().flex_row_dir(cx).gap_1();
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

    /// A choice row whose visible labels differ from the stored values, e.g.
    /// native language names ("العربية") mapped to locale codes ("ar").
    fn choices_named(
        &self,
        label: &str,
        options: &[(String, String)],
        current: &str,
        setter: fn(&str, &mut Self),
        on_select: fn(&str, &mut Self, &mut Window, &mut Context<Self>),
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let row = div().flex().flex_col().gap_1()
            .child(Label::new(label).text_sm().id(format!("choices-named-label-{}", label)));
        let mut buttons = div().flex_row_dir(cx).gap_1().flex_wrap();
        for (value, display) in options {
            let value = value.clone();
            let display = display.clone();
            let selected = value == current;
            buttons = buttons.child(Button::new(format!("{}-{}", label, value)).label(display)
                .when(selected, |b| b.primary())
                .on_click(cx.listener(move |this, _, window, cx| {
                    setter(&value, this);
                    on_select(&value, this, window, cx);
                    cx.notify();
                })));
        }
        row.child(buttons)
    }

    fn render_about(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme().clone();
        div().absolute().inset_0().flex().items_center().justify_center()
            .bg(gpui::black().alpha(0.55))
            .child(div().w(px(440.)).p_5().rounded_lg().bg(theme.background)
                .border_1().border_color(theme.border).flex().flex_col().gap_2()
                .child(Label::new(self.tr("app.title")).text_lg())
                .child(Label::new(self.fmt("main.version", &["26.4.4 (Rust + GPUI Kit)"])).text_sm())
                .child(Label::new(self.tr("main.aboutDescription")).text_sm())
                .child(div().flex_row_dir(cx).justify_flex_end().pt_3()
                    .child(Button::new("about-close").primary().label(self.tr("common.close"))
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
                .child(Label::new(self.tr("torrent.title")).text_lg())
                .child(Label::new(&picker.name).text_sm())
                .child(rows)
                .child(div().flex_row_dir(cx).justify_flex_end().gap_2()
                    .child(Button::new("torrent-cancel").label(self.tr("settings.cancel"))
                        .on_click(cx.listener(|this, _, _, cx| { this.torrent_picker = None; cx.notify(); })))
                    .child(Button::new("torrent-start").primary().label(self.tr("prompt.start"))
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
                .child(Label::new(self.fmt("dialog.deleteMessage", &["1"])).text_sm())
                .child(div().flex_row_dir(cx).justify_flex_end().gap_2()
                    .child(Button::new("delete-cancel").label(self.tr("settings.cancel"))
                        .on_click(cx.listener(|this, _, _, cx| { this.confirm_delete = None; cx.notify(); })))
                    .child(Button::new("delete-confirm").danger().label(self.tr("common.delete"))
                        .on_click(cx.listener(|this, _, _, cx| { this.confirm_delete = None; cx.notify(); })))))
    }
}
