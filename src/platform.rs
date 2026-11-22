//! Windows/native integration: protocol registration and clipboard monitoring.

use crate::engine::UiEvent;
use crate::model::{DownloadItem, Settings};
use parking_lot::Mutex;
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::mpsc::UnboundedSender;

pub fn register_magnet_protocol() {
    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt as _;
        const CREATE_NO_WINDOW: u32 = 0x08000000;
        let exe = std::env::current_exe().unwrap_or_default();
        let exe = exe.to_string_lossy();
        let command = format!("\"{}\" \"%1\"", exe);
        let calls = [
            vec!["add", r"HKCU\Software\Classes\magnet", "/ve", "/d", "URL:Magnet Protocol", "/f"],
            vec!["add", r"HKCU\Software\Classes\magnet", "/v", "URL Protocol", "/d", "", "/f"],
            vec!["add", r"HKCU\Software\Classes\magnet\shell\open\command", "/ve", "/d", &command, "/f"],
        ];
        for args in calls {
            let _ = std::process::Command::new("reg.exe").args(args)
                .creation_flags(CREATE_NO_WINDOW)
                .stdout(std::process::Stdio::null())
                .stderr(std::process::Stdio::null())
                .status();
        }
    }
}

pub fn start_clipboard_monitor(ui: UnboundedSender<UiEvent>, settings: Arc<Mutex<Settings>>) {
    std::thread::spawn(move || {
        let mut clipboard = match arboard::Clipboard::new() {
            Ok(c) => c,
            Err(_) => return,
        };
        let mut last = String::new();
        loop {
            std::thread::sleep(Duration::from_millis(1200));
            if !settings.lock().clipboard_monitor_enabled {
                continue;
            }
            let text = clipboard.get_text().unwrap_or_default();
            let text = text.trim().to_string();
            if text.is_empty() || text == last { continue; }
            last = text.clone();
            if !text.to_ascii_lowercase().starts_with("http://")
                && !text.to_ascii_lowercase().starts_with("https://")
                && !text.to_ascii_lowercase().starts_with("magnet:") {
                continue;
            }
            let quality = settings.lock().default_quality.clone();
            let mut item = DownloadItem::new(&text, title_from_url(&text));
            item.quality = quality;
            item.status_text = "Waiting in queue".into();
            if text.to_ascii_lowercase().starts_with("magnet:") { item.is_torrent = true; }
            let _ = ui.send(UiEvent::BridgeDownload { item, prompt: true });
        }
    });
}

fn title_from_url(url: &str) -> String {
    url::Url::parse(url).ok()
        .and_then(|u| u.path().rsplit('/').find(|s| !s.is_empty()).map(|s| s.to_string()))
        .filter(|s| s.contains('.'))
        .unwrap_or_else(|| "Video_Download".into())
}

// ---------------------------------------------------------------------------
// Release-only system tray (Win32 FFI, no extra dependencies)
// ---------------------------------------------------------------------------

#[cfg(target_os = "windows")]
mod tray {
    use super::*;
    use std::ffi::c_void;
    use std::sync::atomic::{AtomicBool, AtomicIsize, AtomicU32, Ordering};
    use std::sync::OnceLock;

    type Hwnd = isize;
    type WndProc = unsafe extern "system" fn(Hwnd, u32, usize, isize) -> isize;

    const WM_CLOSE: u32 = 0x0010;
    const WM_DESTROY: u32 = 0x0002;
    const WM_LBUTTONUP: u32 = 0x0202;
    const WM_LBUTTONDBLCLK: u32 = 0x0203;
    const WM_RBUTTONUP: u32 = 0x0205;
    const WM_USER: u32 = 0x0400;
    const WM_TRAYICON: u32 = WM_USER + 1;
    const SW_HIDE: isize = 0;
    const SW_RESTORE: isize = 9;
    const NIM_ADD: u32 = 0;
    const NIM_DELETE: u32 = 2;
    const NIF_MESSAGE: u32 = 0x01;
    const NIF_ICON: u32 = 0x02;
    const NIF_TIP: u32 = 0x04;
    const TPM_RIGHTBUTTON: u32 = 0x0002;
    const TPM_RETURNCMD: u32 = 0x0100;
    const GWLP_WNDPROC: i32 = -4;

    pub(crate) static EXITING: AtomicBool = AtomicBool::new(false);
    static FOUND_HWND: AtomicIsize = AtomicIsize::new(0);
    static BEST_AREA: AtomicIsize = AtomicIsize::new(0);
    static TARGET_PID: AtomicU32 = AtomicU32::new(0);
    static ORIGINAL_PROC: AtomicIsize = AtomicIsize::new(0);
    pub(crate) static TRAY_TX: OnceLock<UnboundedSender<UiEvent>> = OnceLock::new();

    #[repr(C)]
    #[derive(Default)]
    struct Point { x: i32, y: i32 }
    #[repr(C)]
    #[derive(Default)]
    struct Rect { left: i32, top: i32, right: i32, bottom: i32 }
    #[repr(C)]
    #[derive(Default)]
    struct Msg {
        hwnd: Hwnd, message: u32, w_param: usize,
        l_param: isize, time: u32, pt: Point,
    }
    #[repr(C)]
    struct WndClassW {
        style: u32,
        lpfn_wnd_proc: WndProc,
        cb_cls_extra: i32,
        cb_wnd_extra: i32,
        h_instance: isize,
        h_icon: isize,
        h_cursor: isize,
        hbr_background: isize,
        lpsz_menu_name: *const u16,
        lpsz_class_name: *const u16,
    }
    #[repr(C)]
    struct NotifyIconDataW {
        cb_size: u32,
        hwnd: Hwnd,
        u_id: u32,
        u_flags: u32,
        u_callback_message: u32,
        h_icon: isize,
        sz_tip: [u16; 128],
        dw_state: u32,
        dw_state_mask: u32,
        sz_info: [u16; 256],
        u_timeout: u32,
        sz_info_title: [u16; 64],
        dw_info_flags: u32,
        guid_item: [u8; 16],
        h_balloon_icon: isize,
    }

    impl Default for NotifyIconDataW {
        fn default() -> Self {
            Self {
                cb_size: 0, hwnd: 0, u_id: 0, u_flags: 0, u_callback_message: 0,
                h_icon: 0, sz_tip: [0; 128], dw_state: 0, dw_state_mask: 0,
                sz_info: [0; 256], u_timeout: 0, sz_info_title: [0; 64],
                dw_info_flags: 0, guid_item: [0; 16], h_balloon_icon: 0,
            }
        }
    }

    #[link(name = "user32")]
    unsafe extern "system" {
        fn RegisterClassW(lpWndClass: *const WndClassW) -> u16;
        fn CreateWindowExW(dwExStyle: u32, lpClassName: *const u16, lpWindowName: *const u16,
            dwStyle: u32, x: i32, y: i32, w: i32, h: i32, hWndParent: Hwnd,
            hMenu: isize, hInstance: isize, lpParam: *const c_void) -> Hwnd;
        fn DestroyWindow(hWnd: Hwnd) -> i32;
        fn DefWindowProcW(hWnd: Hwnd, Msg: u32, wParam: usize, lParam: isize) -> isize;
        fn GetMessageW(lpMsg: *mut Msg, hWnd: Hwnd, min: u32, max: u32) -> i32;
        fn TranslateMessage(lpMsg: *const Msg) -> i32;
        fn DispatchMessageW(lpMsg: *const Msg) -> isize;
        fn PostQuitMessage(nExitCode: i32);
        fn PostMessageW(hWnd: Hwnd, Msg: u32, wParam: usize, lParam: isize) -> i32;
        fn LoadIconW(hInstance: isize, lpIconName: *const u16) -> isize;
        fn ShowWindow(hWnd: Hwnd, nCmdShow: isize) -> i32;
        fn SetForegroundWindow(hWnd: Hwnd) -> i32;
        fn IsWindowVisible(hWnd: Hwnd) -> i32;
        fn GetWindowRect(hWnd: Hwnd, lpRect: *mut Rect) -> i32;
        fn EnumWindows(lpEnumFunc: unsafe extern "system" fn(Hwnd, isize) -> i32, lParam: isize) -> i32;
        fn GetWindowThreadProcessId(hWnd: Hwnd, lpdwProcessId: *mut u32) -> u32;
        fn SetWindowLongPtrW(hWnd: Hwnd, nIndex: i32, dwNewLong: isize) -> isize;
        fn CallWindowProcW(lpPrevWndFunc: isize, hWnd: Hwnd, Msg: u32, wParam: usize, lParam: isize) -> isize;
        fn GetCursorPos(lpPoint: *mut Point) -> i32;
        fn CreatePopupMenu() -> isize;
        fn AppendMenuW(hMenu: isize, uFlags: u32, uIDNewItem: usize, lpNewItem: *const u16) -> i32;
        fn TrackPopupMenuEx(hMenu: isize, uFlags: u32, x: i32, y: i32, hWnd: Hwnd, lptpm: *const c_void) -> i32;
        fn DestroyMenu(hMenu: isize) -> i32;
    }
    #[link(name = "shell32")]
    unsafe extern "system" {
        fn Shell_NotifyIconW(dwMessage: u32, lpData: *mut NotifyIconDataW) -> i32;
    }
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetCurrentProcessId() -> u32;
        fn GetModuleHandleW(lpModuleName: *const u16) -> isize;
    }

    unsafe extern "system" fn enum_windows_proc(hwnd: Hwnd, _: isize) -> i32 {
        let mut pid = 0u32;
        GetWindowThreadProcessId(hwnd, &mut pid);
        if pid == TARGET_PID.load(Ordering::Relaxed) && IsWindowVisible(hwnd) != 0 {
            let mut r = Rect::default();
            if GetWindowRect(hwnd, &mut r) != 0 {
                let area = ((r.right - r.left) as isize) * ((r.bottom - r.top) as isize);
                if area > BEST_AREA.load(Ordering::Relaxed) {
                    BEST_AREA.store(area, Ordering::Relaxed);
                    FOUND_HWND.store(hwnd, Ordering::Relaxed);
                }
            }
        }
        1
    }

    unsafe extern "system" fn main_subclass_proc(hwnd: Hwnd, msg: u32, wp: usize, lp: isize) -> isize {
        if msg == WM_CLOSE && !EXITING.load(Ordering::Relaxed) {
            // "Close" minimizes to the tray instead of quitting.
            ShowWindow(hwnd, SW_HIDE);
            return 0;
        }
        let prev = ORIGINAL_PROC.load(Ordering::Relaxed);
        if prev == 0 { DefWindowProcW(hwnd, msg, wp, lp) } else { CallWindowProcW(prev, hwnd, msg, wp, lp) }
    }

    fn tray_action(action: isize) {
        let hwnd = FOUND_HWND.load(Ordering::Relaxed);
        match action {
            1001 => { unsafe { ShowWindow(hwnd, SW_RESTORE); SetForegroundWindow(hwnd); } }
            1002 => {
                unsafe { ShowWindow(hwnd, SW_RESTORE); SetForegroundWindow(hwnd); }
                if let Some(tx) = TRAY_TX.get() { let _ = tx.send(UiEvent::TraySettings); }
            }
            1003 => {
                EXITING.store(true, Ordering::Relaxed);
                if let Some(tx) = TRAY_TX.get() { let _ = tx.send(UiEvent::TrayExit); }
            }
            _ => {}
        }
    }

    unsafe extern "system" fn tray_wnd_proc(hwnd: Hwnd, msg: u32, wp: usize, lp: isize) -> isize {
        match msg {
            WM_TRAYICON => {
                match lp as u32 {
                    WM_LBUTTONUP | WM_LBUTTONDBLCLK => tray_action(1001),
                    WM_RBUTTONUP => {
                        let mut pos = Point::default();
                        GetCursorPos(&mut pos);
                        SetForegroundWindow(hwnd);
                        let menu = CreatePopupMenu();
                        let open = "Open app\0".encode_utf16().collect::<Vec<_>>();
                        let settings = "Settings\0".encode_utf16().collect::<Vec<_>>();
                        let exit = "Exit\0".encode_utf16().collect::<Vec<_>>();
                        AppendMenuW(menu, 0, 1001, open.as_ptr());
                        AppendMenuW(menu, 0, 1002, settings.as_ptr());
                        AppendMenuW(menu, 0, 0, std::ptr::null());
                        AppendMenuW(menu, 0, 1003, exit.as_ptr());
                        let cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
                            pos.x, pos.y, hwnd, std::ptr::null());
                        DestroyMenu(menu);
                        PostMessageW(hwnd, 0, 0, 0);
                        tray_action(cmd as isize);
                    }
                    _ => {}
                }
                0
            }
            WM_DESTROY => { PostQuitMessage(0); 0 }
            _ => DefWindowProcW(hwnd, msg, wp, lp),
        }
    }

    pub(crate) unsafe fn run_tray() {
        TARGET_PID.store(GetCurrentProcessId(), Ordering::Relaxed);
        for _ in 0..30 {
            EnumWindows(enum_windows_proc, 0);
            if FOUND_HWND.load(Ordering::Relaxed) != 0 { break; }
            std::thread::sleep(Duration::from_millis(100));
        }
        let main_hwnd = FOUND_HWND.load(Ordering::Relaxed);
        if main_hwnd != 0 {
            let prev = SetWindowLongPtrW(main_hwnd, GWLP_WNDPROC, main_subclass_proc as isize);
            ORIGINAL_PROC.store(prev, Ordering::Relaxed);
        }

        let instance = GetModuleHandleW(std::ptr::null());
        let class_name = "WrenchTrayClass\0".encode_utf16().collect::<Vec<_>>();
        let wc = WndClassW {
            style: 0, lpfn_wnd_proc: tray_wnd_proc, cb_cls_extra: 0, cb_wnd_extra: 0,
            h_instance: instance, h_icon: 0, h_cursor: 0, hbr_background: 0,
            lpsz_menu_name: std::ptr::null(), lpsz_class_name: class_name.as_ptr(),
        };
        if RegisterClassW(&wc) == 0 { return; }
        let tray_hwnd = CreateWindowExW(0, class_name.as_ptr(), std::ptr::null(), 0,
            0, 0, 0, 0, -3, 0, instance, std::ptr::null());
        if tray_hwnd == 0 { return; }

        let mut icon = NotifyIconDataW {
            cb_size: std::mem::size_of::<NotifyIconDataW>() as u32,
            hwnd: tray_hwnd, u_id: 1,
            u_flags: NIF_MESSAGE | NIF_ICON | NIF_TIP,
            u_callback_message: WM_TRAYICON,
            h_icon: LoadIconW(0, 32512usize as *const u16),
            ..Default::default()
        };
        let tip = "Wrench Downloader\0".encode_utf16().collect::<Vec<_>>();
        let n = tip.len().min(icon.sz_tip.len());
        icon.sz_tip[..n].copy_from_slice(&tip[..n]);
        Shell_NotifyIconW(NIM_ADD, &mut icon);

        let mut msg = Msg::default();
        while GetMessageW(&mut msg, 0, 0, 0) > 0 {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        Shell_NotifyIconW(NIM_DELETE, &mut icon);
        if ORIGINAL_PROC.load(Ordering::Relaxed) != 0 && main_hwnd != 0 {
            SetWindowLongPtrW(main_hwnd, GWLP_WNDPROC, ORIGINAL_PROC.load(Ordering::Relaxed));
        }
        DestroyWindow(tray_hwnd);
    }
}

/// Start the release-only system tray. Debug builds intentionally have no tray.
pub fn start_tray(ui: UnboundedSender<UiEvent>) {
    #[cfg(target_os = "windows")]
    {
        let _ = tray::TRAY_TX.set(ui);
        std::thread::spawn(|| unsafe { tray::run_tray() });
    }
    #[cfg(not(target_os = "windows"))]
    { let _ = ui; }
}
