//! Kullanıcı ayarları: odak süresi, cam efekti, kapak rengi, açılışta
//! başlatma ve adanın konumu.
//!
//! Ayarlar `<uygulama yapılandırma klasörü>/settings.json` dosyasında durur
//! (Windows: `%APPDATA%\com.halil.halisland\settings.json`). Değişiklikler
//! tepsi menüsünden yapılır; her değişiklikte frontend'e `settings-changed`
//! olayı gider.

use crate::glass::GlassMode;
use serde::{Deserialize, Serialize};
use std::path::PathBuf;
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;
use std::time::Duration;
use tauri::{AppHandle, Emitter, Manager, PhysicalPosition, WebviewWindow};
use tauri_plugin_autostart::ManagerExt;

pub const FOCUS_CHOICES: [u32; 5] = [15, 25, 30, 45, 60];

/// Adanın kaydedilmiş konumu: yatay merkez ve üst kenar (fiziksel piksel).
/// Merkez saklanır çünkü ada açılıp kapanırken genişliği değişir.
#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct SavedPosition {
    pub center_x: i32,
    pub y: i32,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase", default)]
pub struct Settings {
    pub focus_minutes: u32,
    pub glass: GlassMode,
    /// Ada çalan şarkının kapak rengini alsın mı?
    pub tint: bool,
    pub autostart: bool,
    pub position: Option<SavedPosition>,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            focus_minutes: 25,
            glass: GlassMode::Acrylic,
            tint: true,
            autostart: false,
            position: None,
        }
    }
}

pub struct SettingsStore {
    inner: Mutex<Settings>,
    path: Option<PathBuf>,
    /// Konum sürüklerken çok sık değişir; diske arada bir yazılır.
    dirty: AtomicBool,
}

impl SettingsStore {
    pub fn load(app: &AppHandle) -> Self {
        let path = app
            .path()
            .app_config_dir()
            .ok()
            .map(|d| d.join("settings.json"));
        let settings = path
            .as_ref()
            .and_then(|p| std::fs::read_to_string(p).ok())
            .and_then(|s| serde_json::from_str::<Settings>(&s).ok())
            .map(sanitize)
            .unwrap_or_default();
        Self {
            inner: Mutex::new(settings),
            path,
            dirty: AtomicBool::new(false),
        }
    }

    pub fn get(&self) -> Settings {
        self.inner.lock().unwrap().clone()
    }

    fn save(&self) {
        let Some(path) = &self.path else { return };
        let json = serde_json::to_string_pretty(&*self.inner.lock().unwrap());
        if let (Ok(json), Some(dir)) = (json, path.parent()) {
            let _ = std::fs::create_dir_all(dir);
            if let Err(e) = std::fs::write(path, json) {
                eprintln!("[halisland] ayarlar kaydedilemedi: {e}");
            }
        }
        self.dirty.store(false, Ordering::Relaxed);
    }
}

/// Elle düzenlenmiş dosyadaki saçma değerleri düzeltir.
fn sanitize(mut s: Settings) -> Settings {
    s.focus_minutes = s.focus_minutes.clamp(1, 180);
    s
}

/// Ayarı değiştirir, kaydeder, frontend'e ve tepsi menüsüne bildirir.
pub fn update(app: &AppHandle, f: impl FnOnce(&mut Settings)) {
    let store = app.state::<SettingsStore>();
    let next = {
        let mut s = store.inner.lock().unwrap();
        f(&mut s);
        *s = sanitize(s.clone());
        s.clone()
    };
    store.save();
    let _ = app.emit("settings-changed", &next);
    crate::tray::sync(app, &next);
}

#[tauri::command]
pub fn get_settings(app: AppHandle) -> Settings {
    app.state::<SettingsStore>().get()
}

pub fn set_autostart(app: &AppHandle, enabled: bool) {
    let launcher = app.autolaunch();
    let result = if enabled { launcher.enable() } else { launcher.disable() };
    if let Err(e) = result {
        eprintln!("[halisland] açılışta başlatma ayarlanamadı: {e}");
    }
    let actual = launcher.is_enabled().unwrap_or(false);
    update(app, |s| s.autostart = actual);
}

/// Açılışta: işletim sistemindeki gerçek durumu ayarlara yansıt (kullanıcı
/// başlangıç uygulamalarından kapatmış olabilir).
pub fn sync_autostart(app: &AppHandle) {
    let actual = app.autolaunch().is_enabled().unwrap_or(false);
    let store = app.state::<SettingsStore>();
    if store.get().autostart != actual {
        store.inner.lock().unwrap().autostart = actual;
        store.save();
    }
}

/// Pencere taşındığında çağrılır (sürükleme ya da yeniden boyutlandırma).
pub fn remember_position(window: &WebviewWindow) {
    let (Ok(pos), Ok(size)) = (window.outer_position(), window.outer_size()) else {
        return;
    };
    let next = SavedPosition {
        center_x: pos.x + size.width as i32 / 2,
        y: pos.y,
    };
    // Pencere, ayarlar yüklenmeden önce de taşınmış olabilir.
    let Some(store) = window.try_state::<SettingsStore>() else {
        return;
    };
    let mut s = store.inner.lock().unwrap();
    if s.position != Some(next) {
        s.position = Some(next);
        store.dirty.store(true, Ordering::Relaxed);
    }
}

/// Kaydedilmiş konumu uygular. Konum artık bağlı olmayan bir monitördeyse
/// `false` döner; o zaman ada varsayılan yere (üst orta) konur.
pub fn restore_position(window: &WebviewWindow) -> bool {
    let Some(saved) = window.state::<SettingsStore>().get().position else {
        return false;
    };
    let Ok(monitors) = window.available_monitors() else {
        return false;
    };
    let visible = monitors.iter().any(|m| {
        let (p, s) = (m.position(), m.size());
        saved.center_x >= p.x
            && saved.center_x < p.x + s.width as i32
            && saved.y >= p.y
            && saved.y < p.y + s.height as i32
    });
    if !visible {
        return false;
    }
    let width = window.outer_size().map(|s| s.width as i32).unwrap_or(0);
    window
        .set_position(PhysicalPosition::new(saved.center_x - width / 2, saved.y))
        .is_ok()
}

/// Konumu her 2 saniyede bir, değiştiyse diske yazar.
pub fn start_saver(app: AppHandle) {
    thread::spawn(move || {
        loop {
            thread::sleep(Duration::from_secs(2));
            let store = app.state::<SettingsStore>();
            if store.dirty.load(Ordering::Relaxed) {
                store.save();
            }
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn eksik_alanlar_varsayilan_olur() {
        let s: Settings = serde_json::from_str(r#"{ "focusMinutes": 45 }"#).unwrap();
        assert_eq!(s.focus_minutes, 45);
        assert_eq!(s.glass, GlassMode::Acrylic);
        assert!(s.tint);
        assert_eq!(s.position, None);
    }

    #[test]
    fn sacma_degerler_duzeltilir() {
        let s = sanitize(Settings {
            focus_minutes: 0,
            ..Default::default()
        });
        assert_eq!(s.focus_minutes, 1);
    }
}
