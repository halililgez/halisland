//! Claude Code hook'larını `~/.claude/settings.json`'a kurar ya da kaldırır.
//!
//! Elle kurulum (README) zahmetli olduğu için tepsi / sağ tık menüsündeki
//! "Claude Code'u bağla" maddesi bunu yapar. Dosyadaki diğer ayarlara ve
//! başka hook'lara dokunulmaz; yalnızca bizim komutumuzu içeren girdiler
//! eklenir ya da silinir. Dosya bozuksa (geçersiz JSON) hiçbir şey yazılmaz.

use serde_json::{Map, Value, json};
use std::path::PathBuf;
use tauri::{AppHandle, Emitter, Manager};

/// Bizim hook'umuzu tanımak için komutta aranan adres.
const MARKER: &str = "127.0.0.1:47821/claude";
const COMMAND: &str = "curl -s -m 1 --data-binary @- http://127.0.0.1:47821/claude/hook";
const EVENTS: [&str; 7] = [
    "SessionStart",
    "UserPromptSubmit",
    "PreToolUse",
    "PostToolUse",
    "Notification",
    "Stop",
    "SessionEnd",
];

fn is_ours(hook: &Value) -> bool {
    hook.get("command")
        .and_then(Value::as_str)
        .is_some_and(|c| c.contains(MARKER))
}

fn event_has_ours(groups: Option<&Value>) -> bool {
    groups.and_then(Value::as_array).is_some_and(|groups| {
        groups.iter().any(|g| {
            g.get("hooks")
                .and_then(Value::as_array)
                .is_some_and(|hooks| hooks.iter().any(is_ours))
        })
    })
}

/// Bütün olaylar için hook'umuz var mı?
fn installed(settings: &Value) -> bool {
    let hooks = settings.get("hooks");
    EVENTS
        .iter()
        .all(|e| event_has_ours(hooks.and_then(|h| h.get(*e))))
}

fn install(settings: &mut Value) {
    if !settings.is_object() {
        *settings = json!({});
    }
    let root = settings.as_object_mut().unwrap();
    let hooks = root.entry("hooks").or_insert_with(|| json!({}));
    if !hooks.is_object() {
        *hooks = json!({});
    }
    let hooks = hooks.as_object_mut().unwrap();
    for event in EVENTS {
        if event_has_ours(hooks.get(event)) {
            continue;
        }
        let groups = hooks.entry(event).or_insert_with(|| json!([]));
        if !groups.is_array() {
            *groups = json!([]);
        }
        groups
            .as_array_mut()
            .unwrap()
            .push(json!({ "hooks": [{ "type": "command", "command": COMMAND }] }));
    }
}

fn uninstall(settings: &mut Value) {
    let Some(hooks) = settings.get_mut("hooks").and_then(Value::as_object_mut) else {
        return;
    };
    for groups in hooks.values_mut() {
        let Some(list) = groups.as_array_mut() else { continue };
        for group in list.iter_mut() {
            if let Some(inner) = group.get_mut("hooks").and_then(Value::as_array_mut) {
                inner.retain(|h| !is_ours(h));
            }
        }
        // İçinde hook kalmayan grupları da sil.
        list.retain(|g| {
            g.get("hooks")
                .and_then(Value::as_array)
                .is_none_or(|h| !h.is_empty())
        });
    }
    hooks.retain(|_, g| g.as_array().is_none_or(|l| !l.is_empty()));
    if hooks.is_empty()
        && let Some(root) = settings.as_object_mut()
    {
        root.remove("hooks");
    }
}

fn settings_path(app: &AppHandle) -> Option<PathBuf> {
    app.path()
        .home_dir()
        .ok()
        .map(|h| h.join(".claude").join("settings.json"))
}

/// Dosyayı okur. Yoksa boş nesne; bozuksa hata.
fn read(path: &PathBuf) -> Result<Value, String> {
    match std::fs::read_to_string(path) {
        Ok(s) if s.trim().is_empty() => Ok(Value::Object(Map::new())),
        Ok(s) => serde_json::from_str(&s).map_err(|e| format!("settings.json okunamadı: {e}")),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(Value::Object(Map::new())),
        Err(e) => Err(e.to_string()),
    }
}

/// Hook'lar kurulu mu? (Dosya okunamazsa `false`.)
#[tauri::command]
pub fn claude_hooks_installed(app: AppHandle) -> bool {
    settings_path(&app)
        .and_then(|p| read(&p).ok())
        .is_some_and(|v| installed(&v))
}

/// Kurar ya da kaldırır; son durumu döner ve frontend'e bildirir.
pub fn set_installed(app: &AppHandle, enable: bool) -> bool {
    let result = (|| -> Result<(), String> {
        let path = settings_path(app).ok_or("ev klasörü bulunamadı")?;
        let mut value = read(&path)?;
        if installed(&value) == enable {
            return Ok(());
        }
        if path.exists() {
            let _ = std::fs::copy(&path, path.with_extension("json.halisland.bak"));
        }
        if enable {
            install(&mut value);
        } else {
            uninstall(&mut value);
        }
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
        }
        let text = serde_json::to_string_pretty(&value).map_err(|e| e.to_string())?;
        std::fs::write(&path, text + "\n").map_err(|e| e.to_string())
    })();
    if let Err(e) = result {
        eprintln!("[halisland] Claude Code hook'ları ayarlanamadı: {e}");
    }
    let now = claude_hooks_installed(app.clone());
    let _ = app.emit("claude-hooks-changed", now);
    now
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bos_dosyaya_kurulur() {
        let mut v = json!({});
        assert!(!installed(&v));
        install(&mut v);
        assert!(installed(&v));
        assert_eq!(v["hooks"]["Stop"][0]["hooks"][0]["command"], COMMAND);
    }

    #[test]
    fn diger_ayarlar_ve_hooklar_korunur() {
        let mut v = json!({
            "theme": "dark",
            "hooks": { "Stop": [{ "hooks": [{ "type": "command", "command": "echo bitti" }] }] }
        });
        install(&mut v);
        assert_eq!(v["theme"], "dark");
        assert_eq!(v["hooks"]["Stop"].as_array().unwrap().len(), 2);
        assert_eq!(v["hooks"]["Stop"][0]["hooks"][0]["command"], "echo bitti");

        uninstall(&mut v);
        assert!(!installed(&v));
        assert_eq!(v["theme"], "dark");
        assert_eq!(v["hooks"]["Stop"].as_array().unwrap().len(), 1);
        assert!(v["hooks"].get("PreToolUse").is_none());
    }

    #[test]
    fn iki_kez_kurmak_cogaltmaz() {
        let mut v = json!({});
        install(&mut v);
        install(&mut v);
        assert_eq!(v["hooks"]["PreToolUse"].as_array().unwrap().len(), 1);
    }

    #[test]
    fn eski_govdesiz_kurulum_da_taninir() {
        let mut v = json!({});
        for e in EVENTS {
            v["hooks"][e] = json!([{ "hooks": [{ "type": "command",
                "command": "curl -X POST http://127.0.0.1:47821/claude/working" }] }]);
        }
        assert!(installed(&v));
    }

    #[test]
    fn kaldirinca_bos_hooks_silinir() {
        let mut v = json!({ "model": "opus" });
        install(&mut v);
        uninstall(&mut v);
        assert_eq!(v, json!({ "model": "opus" }));
    }
}
