// Windows GUI app: no console window when launched from Explorer/taskbar.
#![cfg_attr(target_os = "windows", windows_subsystem = "windows")]

mod bridge;
mod engine;
mod localization;
mod model;
mod paths;
mod platform;
mod prompt;
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

    // If the app is already running (possibly hidden in the tray), just show
    // that instance instead of starting a second one.
    if !platform::is_first_instance() {
        platform::focus_existing();
        return Ok(());
    }

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

    // Windows "Start with Windows" launches with --background: start hidden.
    let background = std::env::args().any(|a| a == "--background" || a == "/background");

    let app_runtime = ui::AppRuntime {
        store,
        engine,
        settings,
        localization,
        ui_tx: ui_tx.clone(),
    };

    // Restore the window where the user last left it (position, size and
    // maximized state). The tray captures these on size/move and on close.
    let win = app_runtime.settings.lock().clone();
    let saved_window_bounds = {
        let bounds = Bounds {
            origin: Point { x: px(win.window_x as f32), y: px(win.window_y as f32) },
            size: Size {
                width: px(win.window_width as f32),
                height: px(win.window_height as f32),
            },
        };
        if win.window_maximized {
            WindowBounds::Maximized(bounds)
        } else {
            WindowBounds::Windowed(bounds)
        }
    };

    gpui_kit::application()
        .with_assets(gpui_kit::assets::Assets)
        .run(move |cx| {
        gpui_kit::init(cx);

        // Apply saved appearance choices before the first window is built:
        // mirror RTL languages and restore light/dark/system mode.
        {
            let settings = app_runtime.settings.lock();
            ui::apply_language(&app_runtime.localization, &settings.language, cx);
            ui::apply_theme(&settings.app_theme, cx);
        }

        gpui_kit::open_window(
            WindowOptions {
                window_bounds: Some(saved_window_bounds),
                focus: !background,
                show: !background,
                ..Default::default()
            },
            cx,
            |window, cx| {
                window.set_window_title("Wrench Downloader");
                let runtime = app_runtime.clone();
                let url_placeholder = runtime.localization.get("main.urlPlaceholder");
                let url_input = cx.new(|cx| InputState::new(window, cx).placeholder(url_placeholder));
                let settings_folder = cx.new(|cx| InputState::new(window, cx));
                let view = cx.new(|_| ui::AppView::new(runtime, url_input, settings_folder));

                // Relay engine events onto the UI thread.
                let weak = view.downgrade();
                let mut ui_rx = ui_rx;
                cx.spawn(async move |app| {
                    while let Some(event) = ui_rx.recv().await {
                        if matches!(event, UiEvent::TrayExit) {
                            let _ = app.update(|cx| cx.quit());
                            continue;
                        }
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

        // Release builds minimize to tray on close and expose a tray menu.
        #[cfg(not(debug_assertions))]
        platform::start_tray(ui_tx.clone(), app_runtime.settings.clone());
    });

    Ok(())
}
