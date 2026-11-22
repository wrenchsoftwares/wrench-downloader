mod bridge;
mod engine;
mod localization;
mod model;
mod paths;
mod platform;
mod ui;

use engine::{DownloadStore, UiEvent};
use gpui_kit::component::input::InputState;
use gpui_kit::*;
use parking_lot::Mutex;
use std::sync::Arc;
use tokio::sync::mpsc;

fn main() -> anyhow::Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "wrench_downloader=info".into()),
        )
        .init();
    paths::Paths::init();

    let runtime = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .worker_threads(4)
        .build()?;

    let settings = Arc::new(Mutex::new(model::Settings::load()));
    let localization = Arc::new(localization::Localization::new(&settings.lock().language));
    let store = DownloadStore::default();
    for item in model::load_history() {
        store.add(item, false);
    }

    let (bridge_tx, bridge_rx) = mpsc::unbounded_channel();
    let (ui_tx, ui_rx) = mpsc::unbounded_channel::<UiEvent>();
    let engine = engine::spawn_engine(&runtime, store.clone(), settings.clone(), ui_tx.clone(), bridge_rx);

    let serve_tx = bridge_tx.clone();
    runtime.spawn(async move {
        if let Err(e) = bridge::serve(bridge::DEFAULT_PORT, serve_tx).await {
            paths::log_line(&format!("bridge error: {}", e));
        }
    });

    platform::register_magnet_protocol();
    platform::start_clipboard_monitor(ui_tx.clone(), settings.clone());

    let app_runtime = ui::AppRuntime {
        store,
        engine,
        settings,
        localization,
    };

    gpui_kit::application()
        .with_assets(gpui_kit::assets::Assets)
        .run(move |cx| {
        gpui_kit::init(cx);

        gpui_kit::open_window(
            WindowOptions {
                window_bounds: Some(WindowBounds::Maximized(Bounds {
                    origin: Point { x: px(80.0), y: px(80.0) },
                    size: Size { width: px(1280.0), height: px(800.0) },
                })),
                ..Default::default()
            },
            cx,
            |window, cx| {
                let runtime = app_runtime.clone();
                let url_input = cx.new(|cx| InputState::new(window, cx).placeholder("Paste download or video URL..."));
                let prompt_title = cx.new(|cx| InputState::new(window, cx));
                let prompt_folder = cx.new(|cx| InputState::new(window, cx));
                let settings_folder = cx.new(|cx| InputState::new(window, cx));
                let view = cx.new(|_| ui::AppView::new(runtime, url_input, prompt_title, prompt_folder, settings_folder));

                // Relay engine events onto the UI thread.
                let weak = view.downgrade();
                let mut ui_rx = ui_rx;
                cx.spawn(async move |app| {
                    while let Some(event) = ui_rx.recv().await {
                        let weak = weak.clone();
                        let event = event.clone();
                        let _ = app.update(|cx| {
                            if let Some(view) = weak.upgrade() {
                                view.update(cx, |view, cx| view.handle_event(event, cx));
                            }
                        });
                    }
                }).detach();

                view
            },
        )
        .expect("failed to open main window");
    });

    Ok(())
}
