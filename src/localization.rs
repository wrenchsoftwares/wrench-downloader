use crate::paths::Paths;
use std::collections::HashMap;
use std::sync::RwLock;

#[derive(Debug)]
pub struct Localization {
    current: RwLock<HashMap<String, String>>,
    english: HashMap<String, String>,
}

impl Localization {
    pub fn new(lang: &str) -> Self {
        Self {
            current: RwLock::new(load_file(lang)),
            english: load_file("en"),
        }
    }

    pub fn reload(&self, lang: &str) {
        *self.current.write().unwrap() = load_file(lang);
    }

    pub fn get(&self, key: &str) -> String {
        self.current.read().unwrap()
            .get(key)
            .or_else(|| self.english.get(key))
            .cloned()
            .unwrap_or_else(|| key.to_string())
    }
}

fn load_file(lang: &str) -> HashMap<String, String> {
    let path = Paths::get().resource("Languages").join(format!("{}.json", lang));
    std::fs::read_to_string(path)
        .ok()
        .and_then(|t| serde_json::from_str::<HashMap<String, String>>(&t).ok())
        .unwrap_or_default()
}
