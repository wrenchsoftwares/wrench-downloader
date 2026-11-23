//! Release-only system tray: Win32 shell icon, close-to-tray and window
//! placement capture. Debug builds do not compile this module at all.

use super::*;
use super::win32::*;
use std::ffi::c_void;
use std::sync::atomic::{AtomicBool, AtomicIsize, AtomicU32, AtomicU64, Ordering};
use std::sync::OnceLock;

type WndProc = unsafe extern "system" fn(Hwnd, u32, usize, isize) -> isize;

const WM_SIZE: u32 = 0x0005;
const WM_CLOSE: u32 = 0x0010;
const WM_DESTROY: u32 = 0x0002;
const WM_WINDOWPOSCHANGED: u32 = 0x0047;
const WM_LBUTTONUP: u32 = 0x0202;
const WM_LBUTTONDBLCLK: u32 = 0x0203;
const WM_RBUTTONUP: u32 = 0x0205;
const WM_USER: u32 = 0x0400;
const WM_TRAYICON: u32 = WM_USER + 1;
const SW_HIDE: isize = 0;
const SW_SHOWMAXIMIZED: u32 = 3; // WINDOWPLACEMENT.showCmd
const NIM_ADD: u32 = 0;
const NIM_DELETE: u32 = 2;
const NIF_MESSAGE: u32 = 0x01;
const NIF_ICON: u32 = 0x02;
const NIF_TIP: u32 = 0x04;
const TPM_RIGHTBUTTON: u32 = 0x0002;
const TPM_RETURNCMD: u32 = 0x0100;
const GWLP_WNDPROC: i32 = -4;

static EXITING: AtomicBool = AtomicBool::new(false);
static FOUND_HWND: AtomicIsize = AtomicIsize::new(0);
static BEST_AREA: AtomicIsize = AtomicIsize::new(0);
static TARGET_PID: AtomicU32 = AtomicU32::new(0);
static ORIGINAL_PROC: AtomicIsize = AtomicIsize::new(0);
pub(crate) static TRAY_TX: OnceLock<UnboundedSender<UiEvent>> = OnceLock::new();
static LAST_WINDOW_SAVE_MS: AtomicU64 = AtomicU64::new(0);

#[repr(C)]
#[derive(Default)]
struct Point { x: i32, y: i32 }
#[repr(C)]
#[derive(Default)]
struct Rect { left: i32, top: i32, right: i32, bottom: i32 }
#[repr(C)]
#[derive(Default)]
struct WindowPlacement {
    length: u32,
    flags: u32,
    show_cmd: u32,
    min_position: Point,
    max_position: Point,
    normal_position: Rect,
}
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
    fn IsWindowVisible(hWnd: Hwnd) -> i32;
    fn GetWindowRect(hWnd: Hwnd, lpRect: *mut Rect) -> i32;
    fn GetWindowPlacement(hWnd: Hwnd, lpWndPl: *mut WindowPlacement) -> i32;
    fn GetDpiForWindow(hWnd: Hwnd) -> u32;
    fn EnumWindows(lpEnumFunc: unsafe extern "system" fn(Hwnd, isize) -> i32, lParam: isize) -> i32;
    fn GetWindowThreadProcessId(hWnd: Hwnd, lpdwProcessId: *mut u32) -> u32;
    fn SetWindowLongPtrW(hWnd: Hwnd, nIndex: i32, dwNewLong: isize) -> isize;
    fn CallWindowProcW(lpPrevWndFunc: isize, hWnd: Hwnd, Msg: u32, wParam: usize, lParam: isize) -> isize;
    fn GetCursorPos(lpPoint: *mut Point) -> i32;
    fn CreatePopupMenu() -> isize;
    fn AppendMenuW(hMenu: isize, uFlags: u32, uIDNewItem: usize, lpNewItem: *const u16) -> i32;
    fn TrackPopupMenuEx(hMenu: isize, uFlags: u32, x: i32, y: i32, hWnd: Hwnd, lptpm: *const c_void) -> isize;
    fn DestroyMenu(hMenu: isize) -> i32;
    fn SendMessageW(hWnd: Hwnd, Msg: u32, wParam: usize, lParam: isize) -> isize;
    fn LoadImageW(hInst: isize, lpszName: *const u16, uType: u32, cx: i32, cy: i32, fuLoad: u32) -> isize;
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

unsafe fn load_app_icon(size: i32) -> isize {
    if let Some(dir) = std::env::current_exe().ok().and_then(|p| p.parent().map(|d| d.to_path_buf())) {
        let path = dir.join("app.ico");
        let wide = path.to_string_lossy().encode_utf16().chain(std::iter::once(0)).collect::<Vec<_>>();
        let icon = LoadImageW(0, wide.as_ptr(), 1, size, size, 0x10); // IMAGE_ICON | LR_LOADFROMFILE
        if icon != 0 { return icon; }
    }
    LoadIconW(0, 32512usize as *const u16) // IDI_APPLICATION fallback
}

unsafe fn set_window_icons(hwnd: Hwnd) {
    const WM_SETICON: u32 = 0x0080;
    SendMessageW(hwnd, WM_SETICON, 0, load_app_icon(16)); // ICON_SMALL
    SendMessageW(hwnd, WM_SETICON, 1, load_app_icon(32)); // ICON_BIG
}

fn now_ms() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

/// Persist the window's normal rect and maximized state so startup and
/// tray opens restore exactly how the user left it.
unsafe fn capture_window_state(hwnd: Hwnd, force: bool) {
    let Some(settings) = WINDOW_SETTINGS.get() else { return; };
    let visible = IsWindowVisible(hwnd) != 0;
    if !visible && !force { return; }
    let mut placement = WindowPlacement {
        length: std::mem::size_of::<WindowPlacement>() as u32,
        ..Default::default()
    };
    if GetWindowPlacement(hwnd, &mut placement) == 0 { return; }
    // Win32 reports physical pixels; GPUI's WindowBounds uses logical
    // pixels. Convert with the window's DPI so the restore size/position
    // is correct on scaled displays.
    let dpi = GetDpiForWindow(hwnd);
    let scale = if dpi > 0 { dpi as f32 / 96.0 } else { 1.0 };
    let to_logical = |v: i32| (v as f32 / scale).round() as i32;
    let r = placement.normal_position;
    let x = to_logical(r.left);
    let y = to_logical(r.top);
    let width = to_logical(r.right - r.left);
    let height = to_logical(r.bottom - r.top);
    if width <= 0 || height <= 0 { return; }
    let now = now_ms();
    // Size/move messages fire rapidly while dragging; coalesce the writes.
    if !force && now.saturating_sub(LAST_WINDOW_SAVE_MS.load(Ordering::Relaxed)) < 1000 {
        return;
    }
    let mut s = settings.lock();
    // A hidden window reports showCmd = SW_HIDE; keep the last known
    // maximized flag instead of corrupting it.
    let maximized = if visible {
        placement.show_cmd == SW_SHOWMAXIMIZED
    } else {
        s.window_maximized
    };
    if s.window_x == x && s.window_y == y && s.window_width == width
        && s.window_height == height && s.window_maximized == maximized {
        return;
    }
    s.window_x = x;
    s.window_y = y;
    s.window_width = width;
    s.window_height = height;
    s.window_maximized = maximized;
    s.save();
    drop(s);
    LAST_WINDOW_SAVE_MS.store(now, Ordering::Relaxed);
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
    if msg == WM_SIZE || msg == WM_WINDOWPOSCHANGED {
        capture_window_state(hwnd, false);
    }
    if msg == WM_CLOSE && !EXITING.load(Ordering::Relaxed) {
        // Save the final placement before hiding to the tray.
        capture_window_state(hwnd, true);
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
        1001 => { unsafe { ShowWindow(hwnd, show_command()); SetForegroundWindow(hwnd); } }
        1002 => {
            unsafe { ShowWindow(hwnd, show_command()); SetForegroundWindow(hwnd); }
            if let Some(tx) = TRAY_TX.get() { let _ = tx.send(UiEvent::TraySettings); }
        }
        1003 => {
            unsafe { capture_window_state(hwnd, true); }
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

pub(crate) unsafe fn run_tray(settings: Arc<Mutex<Settings>>) {
    let _ = WINDOW_SETTINGS.set(settings);
    TARGET_PID.store(GetCurrentProcessId(), Ordering::Relaxed);
    for _ in 0..30 {
        EnumWindows(enum_windows_proc, 0);
        if FOUND_HWND.load(Ordering::Relaxed) != 0 { break; }
        std::thread::sleep(Duration::from_millis(100));
    }
    let main_hwnd = FOUND_HWND.load(Ordering::Relaxed);
    if main_hwnd != 0 {
        set_window_icons(main_hwnd);
        let prev = SetWindowLongPtrW(main_hwnd, GWLP_WNDPROC, main_subclass_proc as *const () as isize);
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
        h_icon: load_app_icon(16),
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
