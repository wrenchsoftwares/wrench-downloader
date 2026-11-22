//! Standalone download-confirmation window (one OS window per captured item).

use crate::engine::UiEvent;
use crate::model::{DownloadItem, DownloadStatus};
use crate::paths;
use crate::ui::AppRuntime;
use gpui_kit::component::button::{Button, ButtonVariants};
use gpui_kit::component::checkbox::Checkbox;
use gpui_kit::component::input::{Input, InputState};
use gpui_kit::component::label::Label;
use gpui_kit::component::{ActiveTheme, Disableable};
use gpui_kit::*;

pub struct PromptView {
    runtime: AppRuntime,
    item: DownloadItem,
    title_input: Entity<InputState>,
    folder_input: Entity<InputState>,
    playlist: bool,
    later: bool,
}

impl PromptView {
    fn new(
        runtime: AppRuntime,
        item: DownloadItem,
        title_input: Entity<InputState>,
        folder_input: Entity<InputState>,
    ) -> Self {
        let playlist = item.download_playlist;
        let later = item.wait_for_queue_start;
        Self { runtime, item, title_input, folder_input, playlist, later }
    }

    fn start(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let title = self.title_input.read(cx).value().trim().to_string();
        let folder = self.folder_input.read(cx).value().trim().to_string();
        let mut item = self.item.clone();
        if !title.is_empty() { item.title = title; }
        if std::path::Path::new(&folder).is_dir() { item.target_folder = folder; }
        item.download_playlist = self.playlist;
        item.wait_for_queue_start = self.later;
        item.scheduled_at = None;
        item.queue_name = "Main queue".into();
        item.status = DownloadStatus::Queued;
        item.status_text = if self.later { "Saved for later in Main queue".into() } else { "Waiting in queue".into() };

        let id = item.id.clone();
        paths::log_line(&format!("prompt start clicked for {}", id));
        self.runtime.store.add(item, true);
        if !self.later {
            self.runtime.engine.start(&id);
        }
        let message = if self.later { "Download saved for later".to_string() } else { format!("Started {}", self.item.title) };
        let _ = self.runtime.ui_tx.send(UiEvent::Status(message));
        window.remove_window();
    }

    fn cancel(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        paths::log_line(&format!("prompt cancelled for {}", self.item.id));
        let _ = self.runtime.ui_tx.send(UiEvent::Status("Download cancelled".into()));
        let _ = cx;
        window.remove_window();
    }
}

impl Render for PromptView {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let theme = cx.theme().clone();
        let url = if self.item.page_url == self.item.url && !self.item.page_url.is_empty() {
            "Fresh stream capture pending - choose Start to resolve it.".to_string()
        } else {
            self.item.url.clone()
        };
        div().size_full().p_5().bg(theme.background).text_color(theme.foreground)
            .flex().flex_col().gap_3()
            .child(Label::new("Start Download").text_lg())
            .child(Label::new("File name").text_sm())
            .child(Input::new(&self.title_input))
            .child(Label::new("URL").text_sm())
            .child(div().max_h(px(90.)).p_2().rounded_md().bg(theme.input_background())
                .child(Label::new(url).text_sm().text_color(theme.muted_foreground)))
            .child(Checkbox::new("prompt-playlist").label("Download the full playlist")
                .checked(self.playlist)
                .on_change(cx.listener(|this, v: &bool, _, cx| { this.playlist = *v; cx.notify(); })))
            .child(Label::new("Save folder").text_sm())
            .child(Input::new(&self.folder_input))
            .child(Checkbox::new("prompt-later").label("Download later")
                .checked(self.later)
                .on_change(cx.listener(|this, v: &bool, _, cx| { this.later = *v; cx.notify(); })))
            .child(div().flex().flex_row().justify_end().gap_2().pt_2()
                .child(Button::new("prompt-cancel").label("Cancel")
                    .on_click(cx.listener(|this, _, window, cx| this.cancel(window, cx))))
                .child(Button::new("prompt-start").primary()
                    .label(if self.later { "Save for later" } else { "Start Download" })
                    .on_click(cx.listener(|this, _, window, cx| this.start(window, cx)))))
    }
}

pub fn open_prompt_window(runtime: AppRuntime, item: DownloadItem, cx: &mut App) -> anyhow::Result<()> {
    let bounds = WindowBounds::centered(
        Size { width: px(560.0), height: px(480.0) },
        cx,
    );
    let options = WindowOptions {
        window_bounds: Some(bounds),
        focus: true,
        show: true,
        ..Default::default()
    };
    cx.activate(true);
    gpui_kit::open_window(options, cx, |window, cx| {
        window.activate_window();
        let title_input = cx.new(|cx| InputState::new(window, cx));
        let folder_input = cx.new(|cx| InputState::new(window, cx));
        let title = item.title.clone();
        let folder = runtime.settings.lock().download_folder.clone();
        title_input.update(cx, |s, cx| s.set_value(title, window, cx));
        folder_input.update(cx, |s, cx| s.set_value(folder, window, cx));
        cx.new(|_| PromptView::new(runtime.clone(), item.clone(), title_input, folder_input))
    })?;
    Ok(())
}
