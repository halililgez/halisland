//! Tepsi (tray) simgesi ve menüsü: ayarlar, adayı gizle/göster, çıkış.

use crate::glass::GlassMode;
use crate::settings::{self, FOCUS_CHOICES, Settings};
use std::sync::Mutex;
use tauri::menu::{CheckMenuItem, Menu, MenuEvent, MenuItem, PredefinedMenuItem, Submenu};
use tauri::tray::TrayIconBuilder;
use tauri::{AppHandle, Manager, Wry};

const GLASS_CHOICES: [(GlassMode, &str, &str); 3] = [
    (GlassMode::Acrylic, "glass-acrylic", "Buzlu cam"),
    (GlassMode::Blur, "glass-blur", "Sade bulanıklık"),
    (GlassMode::Off, "glass-off", "Kapalı"),
];

/// Onay işaretlerini güncelleyebilmek için menü öğelerini saklarız.
pub struct TrayItems {
    focus: Vec<(u32, CheckMenuItem<Wry>)>,
    glass: Vec<(GlassMode, CheckMenuItem<Wry>)>,
    tint: CheckMenuItem<Wry>,
    autostart: CheckMenuItem<Wry>,
    toggle: MenuItem<Wry>,
}

pub struct TrayState(Mutex<Option<TrayItems>>);

pub fn setup(app: &AppHandle) -> tauri::Result<()> {
    let s = app.state::<settings::SettingsStore>().get();

    let focus: Vec<_> = FOCUS_CHOICES
        .iter()
        .map(|&m| {
            let item = CheckMenuItem::with_id(
                app,
                format!("focus-{m}"),
                format!("{m} dakika"),
                true,
                s.focus_minutes == m,
                None::<&str>,
            )?;
            Ok((m, item))
        })
        .collect::<tauri::Result<_>>()?;
    let glass: Vec<_> = GLASS_CHOICES
        .iter()
        .map(|&(mode, id, label)| {
            let item = CheckMenuItem::with_id(app, id, label, true, s.glass == mode, None::<&str>)?;
            Ok((mode, item))
        })
        .collect::<tauri::Result<_>>()?;

    let tint = CheckMenuItem::with_id(app, "tint", "Kapak rengini al", true, s.tint, None::<&str>)?;
    let autostart = CheckMenuItem::with_id(
        app,
        "autostart",
        "Oturum açılınca başlat",
        true,
        s.autostart,
        None::<&str>,
    )?;
    let toggle = MenuItem::with_id(app, "toggle", "Adayı gizle", true, None::<&str>)?;

    let focus_refs: Vec<&dyn tauri::menu::IsMenuItem<Wry>> =
        focus.iter().map(|(_, i)| i as &dyn tauri::menu::IsMenuItem<Wry>).collect();
    let glass_refs: Vec<&dyn tauri::menu::IsMenuItem<Wry>> =
        glass.iter().map(|(_, i)| i as &dyn tauri::menu::IsMenuItem<Wry>).collect();
    let focus_menu = Submenu::with_items(app, "Odak süresi", true, &focus_refs)?;
    let glass_menu = Submenu::with_items(app, "Cam efekti", true, &glass_refs)?;

    let menu = Menu::with_items(
        app,
        &[
            &toggle,
            &MenuItem::with_id(app, "reset-position", "Konumu sıfırla", true, None::<&str>)?,
            &PredefinedMenuItem::separator(app)?,
            &focus_menu,
            &glass_menu,
            &tint,
            &PredefinedMenuItem::separator(app)?,
            &autostart,
            &PredefinedMenuItem::separator(app)?,
            &MenuItem::with_id(app, "quit", "Çıkış", true, None::<&str>)?,
        ],
    )?;

    let mut builder = TrayIconBuilder::with_id("main")
        .tooltip("Halisland")
        .menu(&menu)
        .on_menu_event(on_menu_event);
    if let Some(icon) = app.default_window_icon() {
        builder = builder.icon(icon.clone());
    }
    builder.build(app)?;

    app.manage(TrayState(Mutex::new(Some(TrayItems {
        focus,
        glass,
        tint,
        autostart,
        toggle,
    }))));
    Ok(())
}

fn on_menu_event(app: &AppHandle, event: MenuEvent) {
    let id = event.id().as_ref();
    match id {
        "quit" => app.exit(0),
        "toggle" => toggle_window(app),
        "reset-position" => reset_position(app),
        "tint" => settings::update(app, |s| s.tint = !s.tint),
        "autostart" => {
            let enabled = !app.state::<settings::SettingsStore>().get().autostart;
            settings::set_autostart(app, enabled);
        }
        _ => {
            if let Some(m) = id.strip_prefix("focus-").and_then(|m| m.parse().ok()) {
                settings::update(app, |s| s.focus_minutes = m);
            } else if let Some(&(mode, _, _)) = GLASS_CHOICES.iter().find(|(_, gid, _)| *gid == id)
            {
                settings::update(app, |s| s.glass = mode);
            }
        }
    }
}

fn toggle_window(app: &AppHandle) {
    let Some(window) = app.get_webview_window("main") else { return };
    let visible = window.is_visible().unwrap_or(true);
    let _ = if visible { window.hide() } else { window.show() };
    if let Some(items) = &*app.state::<TrayState>().0.lock().unwrap() {
        let _ = items
            .toggle
            .set_text(if visible { "Adayı göster" } else { "Adayı gizle" });
    }
}

fn reset_position(app: &AppHandle) {
    let Some(window) = app.get_webview_window("main") else { return };
    let _ = crate::island::place_top_center(&window);
    settings::update(app, |s| s.position = None);
}

/// Menüdeki onay işaretlerini ayarlarla eşitler. Tıklanan onay kutusu kendi
/// durumunu zaten değiştirir; "radyo" gibi davranan gruplarda diğerlerini
/// biz kaldırırız.
pub fn sync(app: &AppHandle, s: &Settings) {
    let Some(state) = app.try_state::<TrayState>() else { return };
    let guard = state.0.lock().unwrap();
    let Some(items) = &*guard else { return };
    for (m, item) in &items.focus {
        let _ = item.set_checked(*m == s.focus_minutes);
    }
    for (mode, item) in &items.glass {
        let _ = item.set_checked(*mode == s.glass);
    }
    let _ = items.tint.set_checked(s.tint);
    let _ = items.autostart.set_checked(s.autostart);
}
