use std::path::{Path, PathBuf};
use std::sync::OnceLock;

/// Portable-vs-installed path resolution, mirroring C# `PortablePaths`.
#[derive(Debug, Clone)]
pub struct Paths {
    pub base_dir: PathBuf,
    pub package_dir: PathBuf,
    pub data_dir: PathBuf,
    pub exe_dir: PathBuf,
}

static PATHS: OnceLock<Paths> = OnceLock::new();

fn find_package_dir(exe_dir: &Path) -> PathBuf {
    let mut current = Some(exe_dir);
    for _ in 0..6 {
        let Some(dir) = current else { break };
        let is_root = dir.join("portable.mode").exists()
            || dir.join("Cargo.toml").exists()
            || dir.join("Languages").is_dir()
            || dir.join("chrome extension").is_dir();
        if is_root { return dir.to_path_buf(); }
        current = dir.parent();
    }
    exe_dir.to_path_buf()
}

impl Paths {
    pub fn init() -> &'static Paths {
        PATHS.get_or_init(|| {
            let exe = std::env::current_exe().unwrap_or_else(|_| PathBuf::from("."));
            let exe_dir = exe.parent().unwrap_or(Path::new(".")).to_path_buf();
            // Find the package root by walking up from the binary. This works
            // both in development (target/debug) and when the exe sits directly
            // next to portable.mode / Languages / the extension folder.
            let package_dir = find_package_dir(&exe_dir);
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
                base_dir: if portable { package_dir.clone() } else { data_dir.clone() },
                package_dir,
                data_dir,
                exe_dir,
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

    /// Locate a shipped folder next to the package root or the binary.
    pub fn resource(&self, name: &str) -> PathBuf {
        for candidate in [
            self.package_dir.join(name),
            self.exe_dir.join(name),
            self.base_dir.join(name),
        ] {
            if candidate.exists() { return candidate; }
        }
        self.package_dir.join(name)
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
