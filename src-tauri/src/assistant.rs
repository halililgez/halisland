//! Yerel asistan backend'i (`assistant/`, .NET).
//!
//! Windows'ta Halisland açılınca alt süreç olarak başlatılır, kapanınca
//! durdurulur. Arayüz backend'le doğrudan `http://127.0.0.1:47822`
//! üzerinden konuşur; her istekte buradaki rastgele anahtarı gönderir.
//!
//! - Sürüm: kurulum dosyasındaki `assistant/halisland-assistant.exe`
//!   (release.yml, `tauri.release.conf.json`).
//! - Geliştirme (`tauri dev`): `dotnet run --project assistant`.
//!
//! Backend, `HALISLAND_PARENT_PID` ile Halisland'i izler; Halisland çökerse
//! ya da zorla kapatılırsa kendisi de kapanır.

use serde::Serialize;
use std::collections::hash_map::RandomState;
use std::hash::{BuildHasher, Hasher};
use std::net::{Ipv4Addr, SocketAddr, TcpStream};
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use tauri::{AppHandle, Manager, State};

pub const PORT: u16 = 47822;

pub struct AssistantState {
    token: String,
    child: Mutex<Option<Child>>,
}

impl Default for AssistantState {
    fn default() -> Self {
        Self {
            token: random_token(),
            child: Mutex::new(None),
        }
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AssistantInfo {
    /// Asistan şimdilik yalnızca Windows'ta.
    supported: bool,
    url: String,
    token: String,
}

#[tauri::command]
pub fn assistant_info(state: State<'_, AssistantState>) -> AssistantInfo {
    AssistantInfo {
        supported: cfg!(windows),
        url: format!("http://127.0.0.1:{PORT}"),
        token: state.token.clone(),
    }
}

/// Backend'i arka planda başlatır. Port zaten doluysa (ör. elle açılmış
/// `dotnet run`) yenisini açmaz.
pub fn start(app: &AppHandle) {
    if !cfg!(windows) {
        return;
    }
    if port_in_use() {
        eprintln!("asistan: {PORT} portu dolu, var olan backend kullanılacak");
        return;
    }
    let Some(mut cmd) = command(app) else {
        eprintln!("asistan: backend bulunamadı, asistan kapalı");
        return;
    };
    let state = app.state::<AssistantState>();
    cmd.env("HALISLAND_ASSISTANT_PORT", PORT.to_string())
        .env("HALISLAND_ASSISTANT_TOKEN", &state.token)
        .env("HALISLAND_PARENT_PID", std::process::id().to_string())
        .stdin(Stdio::null());
    if !cfg!(debug_assertions) {
        cmd.stdout(Stdio::null()).stderr(Stdio::null());
    }
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        cmd.creation_flags(CREATE_NO_WINDOW);
    }
    match cmd.spawn() {
        Ok(child) => {
            if let Ok(mut slot) = state.child.lock() {
                *slot = Some(child);
            }
        }
        Err(e) => eprintln!("asistan: başlatılamadı: {e}"),
    }
}

/// Uygulama kapanırken backend'i durdurur.
pub fn stop(app: &AppHandle) {
    let state = app.state::<AssistantState>();
    let Ok(mut slot) = state.child.lock() else { return };
    if let Some(mut child) = slot.take() {
        let _ = child.kill();
        let _ = child.wait();
    }
}

fn command(app: &AppHandle) -> Option<Command> {
    if cfg!(debug_assertions) {
        // `tauri dev`: projeyi derleyip çalıştırır (ilk açılış birkaç saniye).
        let project = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("..")
            .join("assistant")
            .join("Halisland.Assistant.csproj");
        if !project.exists() {
            return None;
        }
        let mut cmd = Command::new("dotnet");
        cmd.arg("run").arg("--project").arg(project);
        return Some(cmd);
    }
    let exe = app
        .path()
        .resource_dir()
        .ok()?
        .join("assistant")
        .join("halisland-assistant.exe");
    exe.exists().then(|| Command::new(exe))
}

fn port_in_use() -> bool {
    let addr = SocketAddr::from((Ipv4Addr::LOCALHOST, PORT));
    TcpStream::connect_timeout(&addr, Duration::from_millis(200)).is_ok()
}

/// 128 bit rastgele anahtar (std'nin rastgele tohumlu hasher'ından).
fn random_token() -> String {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or_default();
    let part = |salt: u64| {
        let mut h = RandomState::new().build_hasher();
        h.write_u128(nanos);
        h.write_u64(salt);
        h.write_u32(std::process::id());
        h.finish()
    };
    format!("{:016x}{:016x}", part(1), part(2))
}
