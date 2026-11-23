//! Always-compiled Win32 helpers: single-instance gate and window restore state.
//! The tray-specific FFI lives in `tray` and is compiled only for release builds.

use super::*;
use std::ffi::c_void;
use std::sync::OnceLock;

pub(super) type Hwnd = isize;

const SW_MAXIMIZE: isize = 3;
const SW_RESTORE: isize = 9;

static MUTEX_HANDLE: OnceLock<isize> = OnceLock::new();
/// Shared with the tray so it can save the current placement and reopen the
/// window with the same maximized state.
pub(super) static WINDOW_SETTINGS: OnceLock<Arc<Mutex<Settings>>> = OnceLock::new();

#[link(name = "user32")]
unsafe extern "system" {
    pub(super) fn ShowWindow(hWnd: Hwnd, nCmdShow: isize) -> i32;
    pub(super) fn SetForegroundWindow(hWnd: Hwnd) -> i32;
    fn FindWindowW(lpClassName: *const u16, lpWindowName: *const u16) -> Hwnd;
}
#[link(name = "kernel32")]
unsafe extern "system" {
    fn GetLastError() -> u32;
    fn CreateMutexW(lpAttributes: *const c_void, bInitialOwner: i32, lpName: *const u16) -> isize;
}

/// False when another instance already owns the single-instance mutex.
pub(super) fn is_first_instance() -> bool {
    unsafe {
        let name = "WrenchDownloader_SingleInstance\0".encode_utf16().collect::<Vec<_>>();
        let handle = CreateMutexW(std::ptr::null(), 1, name.as_ptr());
        let first = GetLastError() != 183; // ERROR_ALREADY_EXISTS
        let _ = MUTEX_HANDLE.set(handle);
        first
    }
}

fn saved_maximized() -> bool {
    if let Some(settings) = WINDOW_SETTINGS.get() {
        return settings.lock().window_maximized;
    }
    crate::model::Settings::load().window_maximized
}

/// Restore the saved state instead of always un-maximizing the window.
pub(super) fn show_command() -> isize {
    if saved_maximized() { SW_MAXIMIZE } else { SW_RESTORE }
}

/// Bring an already-running (possibly tray-hidden) instance to the front.
pub(super) fn focus_existing() -> bool {
    unsafe {
        let title = "Wrench Downloader\0".encode_utf16().collect::<Vec<_>>();
        let hwnd = FindWindowW(std::ptr::null(), title.as_ptr());
        if hwnd == 0 { return false; }
        ShowWindow(hwnd, show_command());
        SetForegroundWindow(hwnd);
        true
    }
}
