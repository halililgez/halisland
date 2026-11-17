//! Sistem sekmesi: işlemci, bellek, en çok bellek kullanan uygulamalar,
//! sistem diski, ağ hızı ve açık kalma süresi.
//!
//! Ölçüm yalnızca frontend istedikçe yapılır (Sistem sekmesi açıkken
//! iki saniyede bir `system_stats`). İşlemci yüzdesi ve ağ hızı iki ölçüm
//! arasındaki farktan hesaplandığı için `sysinfo` nesneleri bellekte tutulur;
//! ilk çağrıda işlemci 0, ağ hızı 0 döner.

use serde::Serialize;
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::Instant;
use sysinfo::{Disks, Networks, ProcessRefreshKind, ProcessesToUpdate, System, UpdateKind};
use tauri::State;

/// Listede gösterilen uygulama sayısı.
const TOP_APPS: usize = 4;

#[derive(Serialize, Clone, Default)]
#[serde(rename_all = "camelCase")]
pub struct AppUsage {
    pub name: String,
    /// Uygulamanın tüm süreçlerinin toplam belleği (bayt).
    pub memory: u64,
}

#[derive(Serialize, Clone, Default)]
#[serde(rename_all = "camelCase")]
pub struct SystemStats {
    /// 0–100
    pub cpu: f32,
    pub memory_used: u64,
    pub memory_total: u64,
    pub top_apps: Vec<AppUsage>,
    pub disk_name: String,
    pub disk_used: u64,
    pub disk_total: u64,
    /// Bayt/saniye
    pub net_down: u64,
    pub net_up: u64,
    /// Saniye
    pub uptime: u64,
}

struct Monitor {
    system: System,
    networks: Networks,
    disks: Disks,
    last_net: Instant,
    /// exe yolu → görünen ad (Windows'ta dosya açıklaması, ör. "Visual Studio Code").
    names: HashMap<PathBuf, String>,
}

#[derive(Default)]
pub struct SystemMonitor(Mutex<Option<Monitor>>);

impl Monitor {
    fn new() -> Result<Self, String> {
        let err = |e: sysinfo::Error| e.to_string();
        Ok(Self {
            system: System::new().map_err(err)?,
            networks: Networks::new_with_refreshed_list().map_err(err)?,
            disks: Disks::new_with_refreshed_list().map_err(err)?,
            last_net: Instant::now(),
            names: HashMap::new(),
        })
    }

    fn sample(&mut self) -> SystemStats {
        let sys = &mut self.system;
        sys.refresh_cpu_usage();
        sys.refresh_memory();
        sys.refresh_processes_specifics(
            ProcessesToUpdate::All,
            true,
            ProcessRefreshKind::nothing()
                .with_memory()
                .with_exe(UpdateKind::OnlyIfNotSet),
        );

        // Aynı uygulamanın süreçleri (ör. tarayıcının onlarca alt süreci) tek satır.
        let mut by_app: HashMap<String, u64> = HashMap::new();
        for p in sys.processes().values() {
            if p.memory() == 0 {
                continue;
            }
            let name = match p.exe() {
                Some(exe) => self
                    .names
                    .entry(exe.to_path_buf())
                    .or_insert_with(|| display_name(exe))
                    .clone(),
                None => strip_exe(&p.name().to_string_lossy()),
            };
            *by_app.entry(name).or_default() += p.memory();
        }
        let top_apps = top_n(by_app, TOP_APPS);

        // Ağ: son ölçümden bu yana alınan/gönderilen bayt.
        self.networks.refresh(true);
        let secs = self.last_net.elapsed().as_secs_f64().max(0.001);
        self.last_net = Instant::now();
        let (mut down, mut up) = (0u64, 0u64);
        for (name, data) in &self.networks {
            let name = name.to_string_lossy().to_lowercase();
            if name.contains("loopback") || name == "lo" || name == "lo0" {
                continue;
            }
            down += data.received();
            up += data.transmitted();
        }

        self.disks.refresh(true);
        let root = system_root();
        let disk = self
            .disks
            .iter()
            .find(|d| d.mount_point() == root)
            .or_else(|| self.disks.iter().max_by_key(|d| d.total_space()));
        let (disk_name, disk_used, disk_total) = disk
            .map(|d| {
                (
                    disk_label(d.mount_point()),
                    d.total_space().saturating_sub(d.available_space()),
                    d.total_space(),
                )
            })
            .unwrap_or_default();

        SystemStats {
            cpu: sys.global_cpu_usage(),
            memory_used: sys.used_memory(),
            memory_total: sys.total_memory(),
            top_apps,
            disk_name,
            disk_used,
            disk_total,
            net_down: (down as f64 / secs) as u64,
            net_up: (up as f64 / secs) as u64,
            uptime: System::uptime().unwrap_or(0),
        }
    }
}

/// Belleğe göre en büyük `n` uygulama, büyükten küçüğe.
fn top_n(by_app: HashMap<String, u64>, n: usize) -> Vec<AppUsage> {
    let mut apps: Vec<_> = by_app
        .into_iter()
        .map(|(name, memory)| AppUsage { name, memory })
        .collect();
    apps.sort_by(|a, b| b.memory.cmp(&a.memory).then_with(|| a.name.cmp(&b.name)));
    apps.truncate(n);
    apps
}

fn strip_exe(name: &str) -> String {
    let lower = name.to_ascii_lowercase();
    if lower.ends_with(".exe") {
        name[..name.len() - 4].to_string()
    } else {
        name.to_string()
    }
}

fn system_root() -> PathBuf {
    #[cfg(windows)]
    {
        let drive = std::env::var("SystemDrive").unwrap_or_else(|_| "C:".into());
        PathBuf::from(format!("{drive}\\"))
    }
    #[cfg(not(windows))]
    {
        PathBuf::from("/")
    }
}

/// "C:\" → "C:", "/" → "Disk"
fn disk_label(mount: &Path) -> String {
    let s = mount.to_string_lossy();
    let trimmed = s.trim_end_matches(['\\', '/']);
    if trimmed.is_empty() {
        "Disk".into()
    } else {
        trimmed.to_string()
    }
}

/// Uygulamanın görünen adı. Windows'ta exe'nin sürüm bilgisindeki dosya
/// açıklaması ("Code.exe" → "Visual Studio Code"), yoksa dosya adı.
fn display_name(exe: &Path) -> String {
    #[cfg(windows)]
    if let Some(desc) = win::file_description(exe) {
        return desc;
    }
    exe.file_stem()
        .map(|s| s.to_string_lossy().into_owned())
        .unwrap_or_default()
}

#[cfg(windows)]
mod win {
    use std::os::windows::ffi::OsStrExt;
    use std::path::Path;
    use windows::Win32::Storage::FileSystem::{
        GetFileVersionInfoSizeW, GetFileVersionInfoW, VerQueryValueW,
    };
    use windows::core::PCWSTR;

    fn wide(s: impl AsRef<std::ffi::OsStr>) -> Vec<u16> {
        s.as_ref().encode_wide().chain(Some(0)).collect()
    }

    /// exe'nin `FileDescription` alanı. Boşsa ya da okunamazsa `None`.
    pub fn file_description(exe: &Path) -> Option<String> {
        let path = wide(exe);
        unsafe {
            let size = GetFileVersionInfoSizeW(PCWSTR(path.as_ptr()), None);
            if size == 0 {
                return None;
            }
            let mut buf = vec![0u8; size as usize];
            GetFileVersionInfoW(PCWSTR(path.as_ptr()), None, size, buf.as_mut_ptr().cast()).ok()?;

            // Dil/kod sayfası çiftlerinden ilkini kullan.
            let mut ptr = std::ptr::null_mut();
            let mut len = 0u32;
            let key = wide("\\VarFileInfo\\Translation");
            if !VerQueryValueW(buf.as_ptr().cast(), PCWSTR(key.as_ptr()), &mut ptr, &mut len)
                .as_bool()
                || len < 4
            {
                return None;
            }
            let pair = ptr as *const u16;
            let (lang, cp) = (*pair, *pair.add(1));

            let key = wide(format!("\\StringFileInfo\\{lang:04x}{cp:04x}\\FileDescription"));
            if !VerQueryValueW(buf.as_ptr().cast(), PCWSTR(key.as_ptr()), &mut ptr, &mut len)
                .as_bool()
                || len == 0
            {
                return None;
            }
            let chars = std::slice::from_raw_parts(ptr as *const u16, len as usize);
            let end = chars.iter().position(|&c| c == 0).unwrap_or(chars.len());
            let desc = String::from_utf16_lossy(&chars[..end]).trim().to_string();
            (!desc.is_empty()).then_some(desc)
        }
    }
}

#[tauri::command]
pub fn system_stats(state: State<'_, SystemMonitor>) -> Result<SystemStats, String> {
    let mut guard = state.0.lock().map_err(|e| e.to_string())?;
    if guard.is_none() {
        *guard = Some(Monitor::new()?);
    }
    Ok(guard.as_mut().expect("az önce kuruldu").sample())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exe_soneki_atilir() {
        assert_eq!(strip_exe("Code.EXE"), "Code");
        assert_eq!(strip_exe("bash"), "bash");
    }

    #[test]
    fn en_buyukler_sirali() {
        let apps = HashMap::from([
            ("a".to_string(), 10),
            ("b".to_string(), 30),
            ("c".to_string(), 20),
        ]);
        let top: Vec<_> = top_n(apps, 2).into_iter().map(|a| a.name).collect();
        assert_eq!(top, ["b", "c"]);
    }

    #[test]
    fn disk_etiketi() {
        assert_eq!(disk_label(Path::new("C:\\")), "C:");
        assert_eq!(disk_label(Path::new("/")), "Disk");
    }

    #[test]
    fn olcum_calisir() {
        let mut m = Monitor::new().expect("sysinfo");
        let s = m.sample();
        assert!(s.memory_total > 0);
        assert!(s.memory_used <= s.memory_total);
    }
}
