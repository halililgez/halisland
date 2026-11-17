mod claude;
mod glass;
mod island;
mod media;
mod terminal;

use tauri::Manager;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .manage(glass::GlassState::default())
        .manage(claude::ClaudeStore::default())
        .invoke_handler(tauri::generate_handler![
            claude::focus_claude,
            claude::claude_status,
            island::resize_island,
            glass::set_glass,
            glass::set_island_shape,
            media::media_control
        ])
        .setup(|app| {
            // macOS: Dock'ta ve Cmd+Tab'de görünmesin, sadece ada olarak yaşasın.
            #[cfg(target_os = "macos")]
            app.set_activation_policy(tauri::ActivationPolicy::Accessory);

            let window = app
                .get_webview_window("main")
                .expect("main penceresi bulunamadı");
            island::place_top_center(&window)?;
            window.show()?;

            // Claude Code hook'larını dinleyen küçük yerel HTTP sunucusu.
            claude::start_server(app.handle().clone());

            // Çalan müzik (Windows: SMTC).
            media::start_watcher(app.handle().clone());
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("Halisland başlatılırken hata oluştu");
}
