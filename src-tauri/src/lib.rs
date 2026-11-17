mod assistant;
#[cfg(windows)]
mod backdrop;
mod claude;
mod clipboard;
mod controls;
mod display;
mod glass;
mod hooks;
mod island;
mod location;
mod media;
mod power;
mod settings;
mod system;
mod terminal;
mod tray;

use tauri::{Manager, RunEvent, WindowEvent};
use tauri_plugin_autostart::MacosLauncher;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        // İkinci kez açılırsa yeni ada açma, var olanı göster. (Port 47821
        // zaten ilk kopyada açık olurdu.)
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            if let Some(window) = app.get_webview_window("main") {
                let _ = window.show();
            }
        }))
        .plugin(tauri_plugin_autostart::init(MacosLauncher::LaunchAgent, None))
        .manage(glass::GlassState::default())
        .manage(claude::ClaudeStore::default())
        .manage(system::SystemMonitor::default())
        .manage(clipboard::ClipStore::default())
        .manage(assistant::AssistantState::default())
        .invoke_handler(tauri::generate_handler![
            assistant::assistant_info,
            assistant::assistant_hardware,
            assistant::set_assistant_model,
            claude::focus_claude,
            claude::claude_status,
            clipboard::clipboard_list,
            clipboard::clipboard_copy,
            clipboard::clipboard_delete,
            clipboard::clipboard_pin,
            clipboard::clipboard_clear,
            controls::audio_state,
            controls::set_volume,
            controls::set_mute,
            controls::radios_state,
            controls::set_radio,
            controls::audio_devices,
            controls::set_audio_device,
            display::display_state,
            display::set_brightness,
            display::set_night_light,
            display::open_night_light_settings,
            island::resize_island,
            island::cursor_over_island,
            location::device_location,
            glass::glass_supported,
            glass::set_glass,
            glass::set_island_shape,
            hooks::claude_hooks_installed,
            media::media_control,
            media::media_artwork,
            power::power_action,
            settings::get_settings,
            settings::set_weather_city,
            system::system_stats,
            tray::settings_action
        ])
        .on_window_event(|window, event| {
            if let WindowEvent::Moved(_) = event
                && let Some(w) = window.app_handle().get_webview_window(window.label())
            {
                settings::remember_position(&w);
            }
        })
        .setup(|app| {
            // macOS: Dock'ta ve Cmd+Tab'de görünmesin, sadece ada olarak yaşasın.
            #[cfg(target_os = "macos")]
            app.set_activation_policy(tauri::ActivationPolicy::Accessory);

            let handle = app.handle().clone();
            app.manage(settings::SettingsStore::load(&handle));
            settings::sync_autostart(&handle);
            settings::start_saver(handle.clone());

            let window = app
                .get_webview_window("main")
                .expect("main penceresi bulunamadı");
            if !settings::restore_position(&window) {
                island::place_on_edge(&window)?;
            }
            window.show()?;
            // Cam katmanı (Windows 11: oval bulanıklık) tepsi menüsünden önce
            // kurulmalı; menü "Cam efekti"nin açık olup olmadığına bakıyor.
            glass::init(&window);

            tray::setup(&handle)?;
            glass::start_frame_guard(handle.clone());

            // Claude Code hook'larını dinleyen küçük yerel HTTP sunucusu.
            claude::start_server(handle.clone());

            // Pano geçmişi (Windows).
            clipboard::start_watcher(handle.clone());

            // Yerel asistan backend'i (Windows, .NET); ayarlarda açıksa.
            assistant::start_if_enabled(&handle);

            // Çalan müzik (Windows: SMTC).
            media::start_watcher(handle);
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("Halisland başlatılırken hata oluştu")
        .run(|app, event| {
            if let RunEvent::Exit = event {
                assistant::stop(app);
            }
        });
}
