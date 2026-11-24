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

    fn lookup(&self, key: &str) -> String {
        self.current.read().unwrap()
            .get(key)
            .or_else(|| self.english.get(key))
            .cloned()
            .unwrap_or_else(|| key.to_string())
    }

    fn wrap_rtl(&self, text: String) -> String {
        // No Unicode bidi controls here: the patched DirectWrite backend
        // (vendor/gpui-pre-windows) sets the paragraph reading direction from
        // the text itself, which reorders Arabic/Hebrew runs and keeps embedded
        // Latin words and numbers left-to-right. Injecting RLO/RLI here would
        // fight the shaper and corrupt the glyph order.
        text
    }

    pub fn get(&self, key: &str) -> String {
        self.wrap_rtl(self.lookup(key))
    }

    /// Look up a resource string and substitute `{0}`, `{1}`, `{0:F1}`,
    /// `{0:g}`, ... with the supplied arguments (in order).
    pub fn format(&self, key: &str, args: &[&str]) -> String {
        let mut text = self.lookup(key);
        for (i, arg) in args.iter().enumerate() {
            text = text.replace(&format!("{{{}}}", i), arg);
            let prefix = format!("{{{}:", i);
            while let Some(start) = text.find(&prefix) {
                let Some(rel_end) = text[start..].find('}') else { break };
                let end = start + rel_end + 1;
                text.replace_range(start..end, arg);
            }
        }
        self.wrap_rtl(text)
    }

    /// Translate a raw status string produced by the engine. The engine works
    /// in English and has no locale context, so at render time we resolve it
    /// back through the English resource map (exact matches) and reformat the
    /// common dynamic shapes ("Saved to X", "Error: X", "N% downloaded").
    /// Unknown text is passed through unchanged.
    pub fn translate_text(&self, text: &str) -> String {
        if let Some(key) = self.english.iter()
            .find(|(_, value)| value.as_str() == text)
            .map(|(key, _)| key.clone())
        {
            return self.get(&key);
        }
        if let Some(rest) = text.strip_prefix("Saved to ") {
            return self.format("download.saved", &[rest]);
        }
        if let Some(rest) = text.strip_prefix("Error: ") {
            return self.format("download.error", &[rest]);
        }
        if let Some(rest) = text.strip_suffix("% downloaded") {
            if rest.chars().all(|c| c.is_ascii_digit() || c == '.') {
                return self.format("download.percentDownloaded", &[rest]);
            }
        }
        if let Some((left, right)) = text.split_once("% of ") {
            if left.chars().all(|c| c.is_ascii_digit() || c == '.') {
                return self.format("download.percentOf", &[left, right]);
            }
        }
        text.to_string()
    }
}

fn load_file(lang: &str) -> HashMap<String, String> {
    let path = Paths::get().resource("Languages").join(format!("{}.json", lang));
    std::fs::read_to_string(path)
        .ok()
        .and_then(|t| serde_json::from_str::<HashMap<String, String>>(&t).ok())
        .unwrap_or_default()
}
