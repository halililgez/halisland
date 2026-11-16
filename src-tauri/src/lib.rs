mod claude;
mod island;

use tauri::Manager;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![island::resize_island])
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
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("Halisland başlatılırken hata oluştu");
}
