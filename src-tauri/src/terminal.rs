//! Claude Code'un çalıştığı pencereyi bulur ve öne getirir.
//!
//! Hook'lar `curl` ile bize bağlanır. Bağlantının istemci portundan
//! (Windows: `GetExtendedTcpTable`) curl'ün PID'ini buluruz. Sonra süreç
//! ağacında yukarı çıkarız: curl → kabuk → claude → kabuk → terminal
//! (Windows Terminal, VS Code, Claude masaüstü...). Görünür bir ana penceresi
//! olan ilk süreç, Claude'un çalıştığı penceredir.
//!
//! macOS: ileride. Şimdilik pencere bulunmaz, "Terminale dön" düğmesi gizlenir.

/// Pencere tutamacı (HWND) sayı olarak saklanır; iş parçacıkları arasında
/// güvenle taşınabilsin diye.
pub type WindowId = isize;

#[cfg(windows)]
pub use platform::{focus, window_for_client_port};

#[cfg(not(windows))]
pub fn window_for_client_port(_port: u16) -> Option<WindowId> {
    None
}

#[cfg(not(windows))]
pub fn focus(_window: WindowId) -> bool {
    false
}

#[cfg(windows)]
mod platform {
    use super::WindowId;
    use std::collections::HashMap;
    use std::ffi::c_void;
    use windows::Win32::Foundation::{CloseHandle, HWND, LPARAM};
    use windows::Win32::NetworkManagement::IpHelper::{
        GetExtendedTcpTable, MIB_TCPROW_OWNER_PID, TCP_TABLE_OWNER_PID_ALL,
    };
    use windows::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW,
        TH32CS_SNAPPROCESS,
    };
    use windows::Win32::UI::WindowsAndMessaging::{
        EnumWindows, GW_OWNER, GetWindow, GetWindowTextLengthW, GetWindowThreadProcessId,
        IsIconic, IsWindow, IsWindowVisible, SW_RESTORE, SetForegroundWindow, ShowWindow,
    };
    use windows::core::BOOL;

    const AF_INET: u32 = 2;
    const MAX_DEPTH: usize = 16;
    /// Bu süreçlere gelince dur: masaüstü kabuğu ve sistem süreçleri
    /// "Claude'un penceresi" değildir.
    const STOP_AT: [&str; 5] = [
        "explorer.exe",
        "svchost.exe",
        "services.exe",
        "wininit.exe",
        "sihost.exe",
    ];

    struct Proc {
        parent: u32,
        exe: String,
    }

    /// 127.0.0.1:<port> bağlantısının yerel ucundaki süreç (curl).
    fn pid_for_local_port(port: u16) -> Option<u32> {
        unsafe {
            let mut size = 0u32;
            GetExtendedTcpTable(None, &mut size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if size == 0 {
                return None;
            }
            // u32 hizalaması için u32 dizisi kullanıyoruz.
            let mut buf = vec![0u32; (size as usize).div_ceil(4)];
            let ret = GetExtendedTcpTable(
                Some(buf.as_mut_ptr() as *mut c_void),
                &mut size,
                false,
                AF_INET,
                TCP_TABLE_OWNER_PID_ALL,
                0,
            );
            if ret != 0 {
                return None;
            }
            let count = buf[0] as usize;
            let rows = buf.as_ptr().add(1) as *const MIB_TCPROW_OWNER_PID;
            let loopback = u32::from_ne_bytes([127, 0, 0, 1]);
            (0..count).map(|i| &*rows.add(i)).find_map(|row| {
                // Port, düşük 16 bitte ağ bayt sırasıyla durur.
                let local = u16::from_be(row.dwLocalPort as u16);
                (local == port && row.dwLocalAddr == loopback).then_some(row.dwOwningPid)
            })
        }
    }

    fn process_table() -> HashMap<u32, Proc> {
        let mut map = HashMap::new();
        unsafe {
            let Ok(snap) = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) else {
                return map;
            };
            let mut entry = PROCESSENTRY32W {
                dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32,
                ..Default::default()
            };
            let mut ok = Process32FirstW(snap, &mut entry).is_ok();
            while ok {
                let len = entry.szExeFile.iter().position(|&c| c == 0).unwrap_or(260);
                let exe = String::from_utf16_lossy(&entry.szExeFile[..len]).to_lowercase();
                map.insert(
                    entry.th32ProcessID,
                    Proc {
                        parent: entry.th32ParentProcessID,
                        exe,
                    },
                );
                ok = Process32NextW(snap, &mut entry).is_ok();
            }
            let _ = CloseHandle(snap);
        }
        map
    }

    /// PID → o sürecin görünür, sahipsiz ve başlıklı ilk üst düzey penceresi.
    fn top_windows() -> HashMap<u32, WindowId> {
        unsafe extern "system" fn collect(hwnd: HWND, lparam: LPARAM) -> BOOL {
            unsafe {
                let map = &mut *(lparam.0 as *mut HashMap<u32, WindowId>);
                let owned = GetWindow(hwnd, GW_OWNER).is_ok_and(|o| !o.is_invalid());
                if IsWindowVisible(hwnd).as_bool() && !owned && GetWindowTextLengthW(hwnd) > 0 {
                    let mut pid = 0u32;
                    GetWindowThreadProcessId(hwnd, Some(&mut pid));
                    map.entry(pid).or_insert(hwnd.0 as WindowId);
                }
                BOOL(1)
            }
        }
        let mut map: HashMap<u32, WindowId> = HashMap::new();
        unsafe {
            let _ = EnumWindows(Some(collect), LPARAM(&mut map as *mut _ as isize));
        }
        map
    }

    pub fn window_for_client_port(port: u16) -> Option<WindowId> {
        let pid = pid_for_local_port(port)?;
        let procs = process_table();
        let windows = top_windows();

        let mut cur = pid;
        for _ in 0..MAX_DEPTH {
            let proc = procs.get(&cur)?;
            if STOP_AT.contains(&proc.exe.as_str()) {
                return None;
            }
            if let Some(&w) = windows.get(&cur) {
                return Some(w);
            }
            // Klasik konsol: pencere, kabuğun alt süreci olan conhost.exe'ye aittir.
            let console = procs
                .iter()
                .filter(|(_, p)| p.parent == cur && p.exe == "conhost.exe")
                .find_map(|(pid, _)| windows.get(pid).copied());
            if console.is_some() {
                return console;
            }
            if proc.parent == 0 || proc.parent == cur {
                return None;
            }
            cur = proc.parent;
        }
        None
    }

    pub fn focus(window: WindowId) -> bool {
        let hwnd = HWND(window as *mut c_void);
        unsafe {
            if !IsWindow(Some(hwnd)).as_bool() {
                return false;
            }
            if IsIconic(hwnd).as_bool() {
                let _ = ShowWindow(hwnd, SW_RESTORE);
            }
            SetForegroundWindow(hwnd).as_bool()
        }
    }
}
