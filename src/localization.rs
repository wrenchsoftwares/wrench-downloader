use crate::paths::Paths;
use std::collections::HashMap;
use std::sync::RwLock;

#[derive(Debug)]
pub struct Localization {
    lang: RwLock<String>,
    current: RwLock<HashMap<String, String>>,
    english: HashMap<String, String>,
}

impl Localization {
    pub fn new(lang: &str) -> Self {
        Self {
            lang: RwLock::new(lang.to_string()),
            current: RwLock::new(load_file(lang)),
            english: load_file("en"),
        }
    }

    pub fn reload(&self, lang: &str) {
        *self.lang.write().unwrap() = lang.to_string();
        *self.current.write().unwrap() = load_file(lang);
    }

    pub fn is_rtl(&self) -> bool {
        let lang = self.lang.read().unwrap();
        lang.split(['-', '_']).next().unwrap_or(&lang).eq_ignore_ascii_case("ar")
    }

    pub fn get(&self, key: &str) -> String {
        let text = self.current.read().unwrap()
            .get(key)
            .or_else(|| self.english.get(key))
            .cloned()
            .unwrap_or_else(|| key.to_string());
        // GPUI's Windows text backend (DirectWrite) lays text out with an LTR
        // reading direction unless told otherwise, so Arabic strings reach it
        // with glyphs in logical (reversed-looking) order. An RTL isolate makes
        // the bidi algorithm treat the whole string as one RTL run.
        if self.is_rtl() && !text.is_empty() {
            format!("\u{2067}{}\u{2069}", text)
        } else {
            text
        }
    }
}

fn load_file(lang: &str) -> HashMap<String, String> {
    let path = Paths::get().resource("Languages").join(format!("{}.json", lang));
    std::fs::read_to_string(path)
        .ok()
        .and_then(|t| serde_json::from_str::<HashMap<String, String>>(&t).ok())
        .unwrap_or_default()
}
