//! Yerel asistan backend'i (`assistant/`, .NET).
//!
//! Asistan isteğe bağlıdır (Faz 23): ayarlarda ya da tepsi menüsünde
//! "Asistan" açılmadıkça backend hiç başlatılmaz. Açıkken Windows'ta
//! Halisland ile birlikte alt süreç olarak başlar, kapanınca durur. Arayüz
//! backend'le doğrudan `http://127.0.0.1:47822` üzerinden konuşur; her
//! istekte buradaki rastgele anahtarı gönderir.
//!
//! - Sürüm: kurulum dosyasındaki `assistant/halisland-assistant.exe`
//!   (release.yml, `tauri.release.conf.json`).
//! - Geliştirme (`tauri dev`): `dotnet run --project assistant`.
//!
//! Backend, `HALISLAND_PARENT_PID` ile Halisland'i izler; Halisland çökerse
//! ya da zorla kapatılırsa kendisi de kapanır. Asistan kapatılınca önce
//! `/shutdown` ile kendisi kapanması istenir (`dotnet run` alt süreci
//! öldürülünce asıl uygulama arkada kalırdı), sonra süreç sonlandırılır.

use crate::settings::{self, SettingsStore};
use serde::Serialize;
use std::collections::hash_map::RandomState;
use std::hash::{BuildHasher, Hasher};
use std::io::{Read, Write};
use std::net::{Ipv4Addr, SocketAddr, TcpStream};
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use tauri::{AppHandle, Manager, State};

pub const PORT: u16 = 47822;

pub struct AssistantState {
    token: String,
    child: Mutex<Option<Child>>,
    /// Aç/kapa ve model değişikliği sırayla yapılsın.
    lifecycle: Mutex<()>,
}

impl Default for AssistantState {
    fn default() -> Self {
        Self {
            token: random_token(),
            child: Mutex::new(None),
            lifecycle: Mutex::new(()),
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

/// Yerel model için donanım: en çok belleği olan ekran kartı ve RAM.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Hardware {
    gpu: Option<String>,
    /// Ekran kartının kendi belleği (MB); tümleşik kartlarda çok küçük olur.
    vram_mb: u64,
    ram_mb: u64,
}

#[tauri::command]
pub fn assistant_hardware() -> Hardware {
    let ram = sysinfo::System::new()
        .map(|mut sys| {
            sys.refresh_memory();
            sys.total_memory()
        })
        .unwrap_or_default();
    let (gpu, vram) = gpu().map_or((None, 0), |(n, v)| (Some(n), v));
    Hardware {
        gpu,
        vram_mb: vram / 1024 / 1024,
        ram_mb: ram / 1024 / 1024,
    }
}

/// Kullanılacak Ollama modelini seçer (`None`: backend'in varsayılanı).
/// Asistan açıksa backend yeni modelle yeniden başlatılır.
#[tauri::command]
pub fn set_assistant_model(app: AppHandle, model: Option<String>) {
    let model = model.map(|m| m.trim().to_string()).filter(|m| !m.is_empty());
    settings::update(&app, |s| s.assistant_model = model);
    apply(&app);
}

/// Ayardaki duruma göre backend'i başlatır ya da durdurur (arka planda;
/// kapanmayı beklemek arayüzü kilitlemesin). Açıksa yeniden başlatır.
pub fn apply(app: &AppHandle) {
    let app = app.clone();
    std::thread::spawn(move || {
        let state = app.state::<AssistantState>();
        let _guard = state.lifecycle.lock();
        stop(&app);
        if app.state::<SettingsStore>().get().assistant {
            wait_port_free(Duration::from_secs(5));
            start(&app);
        }
    });
}

/// Açılışta: asistan açıksa backend'i başlatır.
pub fn start_if_enabled(app: &AppHandle) {
    if app.state::<SettingsStore>().get().assistant {
        start(app);
    }
}

/// Backend'i arka planda başlatır. Port zaten doluysa (ör. elle açılmış
/// `dotnet run`) yenisini açmaz.
fn start(app: &AppHandle) {
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
    if let Some(model) = app.state::<SettingsStore>().get().assistant_model {
        cmd.env("HALISLAND_MODEL", model);
    }
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

/// Backend'i durdurur (asistan kapatılınca ve uygulama kapanırken).
pub fn stop(app: &AppHandle) {
    let state = app.state::<AssistantState>();
    let Ok(mut slot) = state.child.lock() else { return };
    let Some(mut child) = slot.take() else { return };
    request_shutdown(&state.token);
    // Kendisi kapanmazsa biraz bekleyip sonlandır.
    let deadline = Instant::now() + Duration::from_secs(2);
    while Instant::now() < deadline {
        if matches!(child.try_wait(), Ok(Some(_))) {
            return;
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    let _ = child.kill();
    let _ = child.wait();
}

/// `POST /shutdown`; yanıtı beklemeden döner.
fn request_shutdown(token: &str) {
    let addr = SocketAddr::from((Ipv4Addr::LOCALHOST, PORT));
    let Ok(mut s) = TcpStream::connect_timeout(&addr, Duration::from_millis(300)) else {
        return;
    };
    let _ = s.set_read_timeout(Some(Duration::from_millis(500)));
    let req = format!(
        "POST /shutdown HTTP/1.1\r\nHost: 127.0.0.1:{PORT}\r\nAuthorization: Bearer {token}\r\n\
         Content-Length: 0\r\nConnection: close\r\n\r\n"
    );
    if s.write_all(req.as_bytes()).is_ok() {
        let _ = s.read(&mut [0u8; 64]);
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

fn wait_port_free(max: Duration) {
    let deadline = Instant::now() + max;
    while port_in_use() && Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(100));
    }
}

/// En çok kendi belleği olan donanım ekran kartı (DXGI): ad ve bayt.
#[cfg(windows)]
fn gpu() -> Option<(String, u64)> {
    use windows::Win32::Graphics::Dxgi::{
        CreateDXGIFactory1, DXGI_ADAPTER_FLAG_SOFTWARE, IDXGIFactory1,
    };
    // SAFETY: DXGI fabrikası ve bağdaştırıcılar yalnızca bu işlevde,
    // tanımlarını okumak için kullanılır.
    unsafe {
        let factory: IDXGIFactory1 = CreateDXGIFactory1().ok()?;
        let mut best: Option<(String, u64)> = None;
        let mut i = 0;
        while let Ok(adapter) = factory.EnumAdapters1(i) {
            i += 1;
            let Ok(desc) = adapter.GetDesc1() else { continue };
            if desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE.0 as u32 != 0 {
                continue;
            }
            let vram = desc.DedicatedVideoMemory as u64;
            let len = desc.Description.iter().position(|&c| c == 0).unwrap_or(desc.Description.len());
            let name = String::from_utf16_lossy(&desc.Description[..len]).trim().to_string();
            if best.as_ref().is_none_or(|(_, v)| vram > *v) {
                best = Some((name, vram));
            }
        }
        best
    }
}

#[cfg(not(windows))]
fn gpu() -> Option<(String, u64)> {
    None
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn donanim_okunur() {
        let h = assistant_hardware();
        println!("ekran kartı: {:?}, {} MB; RAM {} MB", h.gpu, h.vram_mb, h.ram_mb);
        assert!(h.ram_mb > 0);
    }
}
