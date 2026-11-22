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
        let english = load_file("en");
        let current = load_file(lang);
        Self {
            lang: RwLock::new(lang.to_string()),
            current: RwLock::new(current),
            english,
        }
    }

    pub fn reload(&self, lang: &str) {
        *self.lang.write().unwrap() = lang.to_string();
        *self.current.write().unwrap() = load_file(lang);
    }

    pub fn lang(&self) -> String { self.lang.read().unwrap().clone() }
    pub fn is_rtl(&self) -> bool { self.lang() == "ar" }

    pub fn get(&self, key: &str) -> String {
        self.current.read().unwrap()
            .get(key)
            .or_else(|| self.english.get(key))
            .cloned()
            .unwrap_or_else(|| key.to_string())
    }

    pub fn format(&self, key: &str, args: &[&str]) -> String {
        let mut text = self.get(key);
        for (i, arg) in args.iter().enumerate() {
            text = text.replace(&format!("{{{}}}", i), arg);
        }
        text
    }
}

fn load_file(lang: &str) -> HashMap<String, String> {
    let path = Paths::get().resource("Languages").join(format!("{}.json", lang));
    std::fs::read_to_string(path)
        .ok()
        .and_then(|t| serde_json::from_str::<HashMap<String, String>>(&t).ok())
        .unwrap_or_default()
}
