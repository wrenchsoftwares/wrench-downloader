use std::path::{Path, PathBuf};
use std::sync::OnceLock;

/// Portable-vs-installed path resolution, mirroring C# `PortablePaths`.
#[derive(Debug, Clone)]
pub struct Paths {
    pub base_dir: PathBuf,
    pub data_dir: PathBuf,
    pub exe_dir: PathBuf,
    pub portable: bool,
}

static PATHS: OnceLock<Paths> = OnceLock::new();

impl Paths {
    pub fn init() -> &'static Paths {
        PATHS.get_or_init(|| {
            let exe = std::env::current_exe().unwrap_or_else(|_| PathBuf::from("."));
            let exe_dir = exe.parent().unwrap_or(Path::new(".")).to_path_buf();
            // Portable layout: package dir is the parent of the binary directory
            // (matches C#: PackageDirectory = parent of AppContext.BaseDirectory).
            let package_dir = exe_dir.parent().unwrap_or(&exe_dir).to_path_buf();
            let portable = package_dir.join("portable.mode").exists();
            let data_dir = if portable {
                package_dir.join("Data")
            } else {
                std::env::var_os("LOCALAPPDATA")
                    .map(PathBuf::from)
                    .unwrap_or_else(|| exe_dir.clone())
                    .join("WrenchDownloader")
            };
            Paths {
                base_dir: if portable { package_dir } else { data_dir.clone() },
                data_dir,
                exe_dir,
                portable,
            }
        })
    }

    pub fn get() -> &'static Paths {
        Self::init()
    }

    pub fn history(&self) -> PathBuf { self.data_dir.join("history.json") }
    pub fn settings(&self) -> PathBuf { self.data_dir.join("settings.json") }
    pub fn startup_log(&self) -> PathBuf { self.data_dir.join("startup.log") }
    pub fn parts(&self) -> PathBuf { self.data_dir.join("Parts") }
    pub fn torrent_cache(&self) -> PathBuf { self.data_dir.join("torrent-cache") }

    /// Locate a shipped folder both in portable layout and next to the binary.
    pub fn resource(&self, name: &str) -> PathBuf {
        let a = self.base_dir.join(name);
        if a.exists() { return a; }
        let b = self.exe_dir.join(name);
        if b.exists() { return b; }
        // Development: rust-app/ lives inside the repository next to the C# app.
        let c = self.exe_dir.parent().map(|p| p.join(name)).unwrap_or_default();
        if c.exists() { return c; }
        a
    }
}

pub fn log_line(line: &str) {
    let path = Paths::get().startup_log();
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    use std::io::Write;
    if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(path) {
        let _ = writeln!(f, "[{}] {}", chrono::Local::now().format("%Y-%m-%d %H:%M:%S"), line);
    }
}
