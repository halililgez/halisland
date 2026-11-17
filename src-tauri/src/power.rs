//! Güç sekmesi: kilitle, uyku, oturumu kapat, yeniden başlat, kapat.
//!
//! Geri alınamayan işlemler (oturumu kapat, yeniden başlat, kapat) için
//! onay frontend'de istenir (`PowerPanel.vue`: ikinci tıklama).

#[tauri::command]
pub fn power_action(action: String) -> Result<(), String> {
    imp::run(&action)
}

#[cfg(windows)]
mod imp {
    use std::os::windows::process::CommandExt;
    use std::process::Command;
    use windows::Win32::System::Power::SetSuspendState;
    use windows::Win32::System::Shutdown::LockWorkStation;

    /// `shutdown.exe` çalışırken konsol penceresi açılmasın.
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;

    fn shutdown(args: &[&str]) -> Result<(), String> {
        Command::new("shutdown")
            .args(args)
            .creation_flags(CREATE_NO_WINDOW)
            .spawn()
            .map(|_| ())
            .map_err(|e| e.to_string())
    }

    pub fn run(action: &str) -> Result<(), String> {
        match action {
            "lock" => unsafe { LockWorkStation() }.map_err(|e| e.to_string()),
            // hibernate=false: hazırda bekletme açık olsa bile uyku.
            "sleep" => {
                if unsafe { SetSuspendState(false, false, false) } {
                    Ok(())
                } else {
                    Err("Uyku moduna geçilemedi".into())
                }
            }
            "logout" => shutdown(&["/l"]),
            "restart" => shutdown(&["/r", "/t", "0"]),
            "shutdown" => shutdown(&["/s", "/t", "0"]),
            other => Err(format!("Bilinmeyen güç işlemi: {other}")),
        }
    }
}

// macOS: elle denenmedi.
#[cfg(target_os = "macos")]
mod imp {
    use std::process::Command;

    fn run_cmd(cmd: &str, args: &[&str]) -> Result<(), String> {
        Command::new(cmd)
            .args(args)
            .spawn()
            .map(|_| ())
            .map_err(|e| e.to_string())
    }

    fn system_events(verb: &str) -> Result<(), String> {
        run_cmd("osascript", &["-e", &format!("tell application \"System Events\" to {verb}")])
    }

    pub fn run(action: &str) -> Result<(), String> {
        match action {
            // Ekran uyuyunca, parola hemen isteniyorsa oturum kilitlenir.
            "lock" => run_cmd("pmset", &["displaysleepnow"]),
            "sleep" => run_cmd("pmset", &["sleepnow"]),
            "logout" => system_events("log out"),
            "restart" => system_events("restart"),
            "shutdown" => system_events("shut down"),
            other => Err(format!("Bilinmeyen güç işlemi: {other}")),
        }
    }
}

#[cfg(not(any(windows, target_os = "macos")))]
mod imp {
    pub fn run(action: &str) -> Result<(), String> {
        Err(format!("Bu sistemde desteklenmiyor: {action}"))
    }
}
