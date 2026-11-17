//! Kontrol sekmesindeki Ekran kutucuğu: parlaklık ve gece ışığı.
//!
//! **Parlaklık**
//! - Harici monitörler: DDC/CI (`dxva2`: `GetMonitorBrightness` /
//!   `SetMonitorBrightness`, belgeli). Monitörün menüsünde DDC/CI kapalıysa
//!   o ekran listede çıkmaz. Monitörler yavaş yanıt verir (~40–100 ms).
//! - Dizüstü ekranı: WMI (`root\WMI`: `WmiMonitorBrightness`,
//!   `WmiMonitorBrightnessMethods.WmiSetBrightness`, belgeli).
//!
//! **Gece ışığı**
//! Windows'un bunun için bir API'si yok. Durum, kayıt defterinde
//! `CloudStore\…\bluelightreductionstate` altındaki `Data` değerinde (Bond
//! CompactBinary) tutulur ve Windows bu değeri izler. Biçim:
//! - `[0..4]` "CB" başlığı (`43 42 01 00`), `[10..15]` değişiklik zamanı,
//! - `[18]` iç yapının uzunluğu, `[19..23]` iç yapının "CB" başlığı,
//! - gece ışığı açıkken iç başlığın hemen ardından `10 00` gelir.
//!
//! Açmak `10 00` eklemek ve uzunluğu 2 artırmak, kapatmak tersi; ardından
//! zaman damgası bir artırılır ki Windows değişikliği görsün. Değer yalnızca
//! bu yapı tutarlıysa yazılır; ilk yazmadan önce orijinali uygulama
//! klasörüne yedeklenir (`nightlight-backup.bin`).

use serde::Serialize;

#[derive(Serialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Screen {
    /// "ddc:<monitör>:<fiziksel>" ya da "wmi:<örnek adı>"
    pub id: String,
    pub name: String,
    /// 0–100
    pub brightness: u32,
    pub internal: bool,
}

#[derive(Serialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct DisplayState {
    pub screens: Vec<Screen>,
    /// `None`: bu sistemde okunamadı / biçim tanınmadı.
    pub night_light: Option<bool>,
}

const CB: [u8; 4] = [0x43, 0x42, 0x01, 0x00];
const ON_MARK: [u8; 2] = [0x10, 0x00];

/// Kayıt değerinden gece ışığının açık olup olmadığı. Biçim tanınmazsa `None`.
pub fn night_light_parse(d: &[u8]) -> Option<bool> {
    let n = d.len();
    let ok = n >= 26
        && d[..4] == CB
        && d[19..23] == CB
        && d[18] as usize == n - 22
        && d[n - 3..] == [0, 0, 0];
    ok.then(|| d[23..25] == ON_MARK)
}

/// Gece ışığını `on` yapan yeni değer. Zaten öyleyse ya da biçim tanınmazsa `None`.
pub fn night_light_toggle(d: &[u8], on: bool) -> Option<Vec<u8>> {
    let current = night_light_parse(d)?;
    if current == on {
        return None;
    }
    let mut out = Vec::with_capacity(d.len() + 2);
    if on {
        out.extend_from_slice(&d[..23]);
        out.extend_from_slice(&ON_MARK);
        out.extend_from_slice(&d[23..]);
        out[18] += 2;
    } else {
        out.extend_from_slice(&d[..23]);
        out.extend_from_slice(&d[25..]);
        out[18] -= 2;
    }
    // Zaman damgası (varint): taşmayan ilk baytı bir artır.
    if let Some(b) = out[10..15].iter_mut().find(|b| **b != 0xff) {
        *b += 1;
    }
    debug_assert_eq!(night_light_parse(&out), Some(on));
    Some(out)
}

async fn blocking<T: Send + 'static>(
    f: impl FnOnce() -> Result<T, String> + Send + 'static,
) -> Result<T, String> {
    tauri::async_runtime::spawn_blocking(f)
        .await
        .map_err(|e| e.to_string())?
}

#[tauri::command]
pub async fn display_state() -> Result<DisplayState, String> {
    blocking(|| {
        Ok(DisplayState {
            screens: imp::screens(),
            night_light: imp::read_night_light().and_then(|d| night_light_parse(&d)),
        })
    })
    .await
}

/// `value`: 0–100
#[tauri::command]
pub async fn set_brightness(id: String, value: u32) -> Result<(), String> {
    let v = value.min(100);
    blocking(move || imp::set_brightness(&id, v)).await
}

#[tauri::command]
pub async fn set_night_light(app: tauri::AppHandle, on: bool) -> Result<(), String> {
    use tauri::Manager;
    let backup = app
        .path()
        .app_config_dir()
        .ok()
        .map(|d| d.join("nightlight-backup.bin"));
    blocking(move || {
        let data = imp::read_night_light().ok_or(UNKNOWN)?;
        if night_light_parse(&data).is_none() {
            return Err(UNKNOWN.into());
        }
        let Some(next) = night_light_toggle(&data, on) else {
            return Ok(()); // zaten istenen durumda
        };
        if let Some(path) = backup
            && !path.exists()
        {
            if let Some(dir) = path.parent() {
                let _ = std::fs::create_dir_all(dir);
            }
            std::fs::write(&path, &data).map_err(|e| format!("Yedek alınamadı: {e}"))?;
        }
        imp::write_night_light(&next)
    })
    .await
}

const UNKNOWN: &str = "Bu Windows sürümünde gece ışığı ayarı tanınmadı";

/// Windows'un Gece ışığı ayarlarını açar (biçim tanınmazsa yedek yol).
#[tauri::command]
pub fn open_night_light_settings() -> Result<(), String> {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        // `start` boş başlık ister; CREATE_NO_WINDOW konsol açılmasın diye.
        std::process::Command::new("cmd")
            .args(["/c", "start", "", "ms-settings:nightlight"])
            .creation_flags(0x0800_0000)
            .spawn()
            .map(|_| ())
            .map_err(|e| e.to_string())
    }
    #[cfg(not(windows))]
    Err("Bu sistemde henüz desteklenmiyor".into())
}

#[cfg(windows)]
mod imp {
    use super::Screen;
    use std::mem::ManuallyDrop;
    use windows::Win32::Devices::Display::{
        DestroyPhysicalMonitors, GetMonitorBrightness, GetNumberOfPhysicalMonitorsFromHMONITOR,
        GetPhysicalMonitorsFromHMONITOR, PHYSICAL_MONITOR, SetMonitorBrightness,
    };
    use windows::Win32::Foundation::{ERROR_SUCCESS, LPARAM, RECT};
    use windows::Win32::Graphics::Gdi::{
        EnumDisplayMonitors, GetMonitorInfoW, HDC, HMONITOR, MONITORINFO,
    };
    use windows::Win32::System::Com::{
        CLSCTX_INPROC_SERVER, COINIT_MULTITHREADED, CoCreateInstance, CoInitializeEx,
        CoSetProxyBlanket, EOAC_NONE, RPC_C_AUTHN_LEVEL_CALL, RPC_C_IMP_LEVEL_IMPERSONATE,
    };
    use windows::Win32::System::Registry::{
        HKEY, HKEY_CURRENT_USER, KEY_QUERY_VALUE, KEY_SET_VALUE, REG_BINARY, REG_VALUE_TYPE,
        RegCloseKey, RegOpenKeyExW, RegQueryValueExW, RegSetValueExW,
    };
    use windows::Win32::System::Rpc::{RPC_C_AUTHN_WINNT, RPC_C_AUTHZ_NONE};
    use windows::Win32::System::Variant::{
        VARIANT, VARIANT_0, VARIANT_0_0, VARIANT_0_0_0, VT_UI1, VT_UI4, VariantClear,
    };
    use windows::Win32::UI::WindowsAndMessaging::MONITORINFOF_PRIMARY;
    use windows::Win32::System::Wmi::{
        IWbemClassObject, IWbemLocator, IWbemServices, WBEM_FLAG_FORWARD_ONLY,
        WBEM_FLAG_RETURN_IMMEDIATELY, WBEM_GENERIC_FLAG_TYPE, WBEM_INFINITE, WbemLocator,
    };
    use windows::core::{BOOL, BSTR, PCWSTR, w};

    fn com() {
        unsafe {
            let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
        }
    }

    /* ---------------- DDC/CI ---------------- */

    unsafe extern "system" fn collect(h: HMONITOR, _: HDC, _: *mut RECT, data: LPARAM) -> BOOL {
        unsafe { (*(data.0 as *mut Vec<HMONITOR>)).push(h) };
        BOOL(1)
    }

    fn monitors() -> Vec<HMONITOR> {
        let mut list: Vec<HMONITOR> = Vec::new();
        unsafe {
            let _ = EnumDisplayMonitors(None, None, Some(collect), LPARAM(&mut list as *mut _ as isize));
        }
        list
    }

    fn is_primary(h: HMONITOR) -> bool {
        let mut info = MONITORINFO {
            cbSize: size_of::<MONITORINFO>() as u32,
            ..Default::default()
        };
        unsafe { GetMonitorInfoW(h, &mut info).as_bool() && info.dwFlags & MONITORINFOF_PRIMARY != 0 }
    }

    /// Bir monitörün fiziksel monitörleri üzerinde `f`'i çalıştırır, sonra tutamaçları bırakır.
    fn with_physical<T>(h: HMONITOR, f: impl FnOnce(&[PHYSICAL_MONITOR]) -> T) -> Option<T> {
        unsafe {
            let mut n = 0u32;
            GetNumberOfPhysicalMonitorsFromHMONITOR(h, &mut n).ok()?;
            if n == 0 {
                return None;
            }
            let mut arr = vec![PHYSICAL_MONITOR::default(); n as usize];
            GetPhysicalMonitorsFromHMONITOR(h, &mut arr).ok()?;
            let out = f(&arr);
            let _ = DestroyPhysicalMonitors(&arr);
            Some(out)
        }
    }

    fn ddc_read(pm: &PHYSICAL_MONITOR) -> Option<(u32, u32, u32)> {
        let (mut min, mut cur, mut max) = (0u32, 0u32, 0u32);
        let ok = unsafe { GetMonitorBrightness(pm.hPhysicalMonitor, &mut min, &mut cur, &mut max) };
        (ok != 0 && max > min).then_some((min, cur, max))
    }

    fn ddc_screens() -> Vec<Screen> {
        let mut out = Vec::new();
        for (mi, h) in monitors().into_iter().enumerate() {
            let primary = is_primary(h);
            let found = with_physical(h, |arr| {
                arr.iter()
                    .enumerate()
                    .filter_map(|(pi, pm)| {
                        let (min, cur, max) = ddc_read(pm)?;
                        Some((pi, (cur.saturating_sub(min)) * 100 / (max - min)))
                    })
                    .collect::<Vec<_>>()
            })
            .unwrap_or_default();
            for (pi, brightness) in found {
                out.push(Screen {
                    id: format!("ddc:{mi}:{pi}"),
                    name: if primary {
                        "Ana ekran".to_string()
                    } else {
                        format!("Ekran {}", mi + 1)
                    },
                    brightness: brightness.min(100),
                    internal: false,
                });
            }
        }
        out
    }

    fn ddc_set(mi: usize, pi: usize, v: u32) -> Result<(), String> {
        let h = *monitors().get(mi).ok_or("Ekran bulunamadı")?;
        with_physical(h, |arr| {
            let pm = arr.get(pi).ok_or("Ekran bulunamadı")?;
            let (min, _, max) = ddc_read(pm).ok_or("Ekran parlaklık ayarını desteklemiyor")?;
            let target = min + (max - min) * v / 100;
            // DDC/CI hattı ara ara yanıt vermiyor (ölçüldü: 4 denemeden 2'si);
            // kısa aralıklarla birkaç kez dene.
            for attempt in 0..4 {
                if attempt > 0 {
                    std::thread::sleep(std::time::Duration::from_millis(60));
                }
                if unsafe { SetMonitorBrightness(pm.hPhysicalMonitor, target) } != 0 {
                    return Ok(());
                }
            }
            Err("Parlaklık ayarlanamadı".to_string())
        })
        .ok_or("Ekran bulunamadı")?
    }

    /* ---------------- WMI (dizüstü ekranı) ---------------- */

    fn wmi() -> windows::core::Result<IWbemServices> {
        com();
        unsafe {
            let loc: IWbemLocator = CoCreateInstance(&WbemLocator, None, CLSCTX_INPROC_SERVER)?;
            let svc = loc.ConnectServer(
                &BSTR::from("ROOT\\WMI"),
                &BSTR::new(),
                &BSTR::new(),
                &BSTR::new(),
                0,
                &BSTR::new(),
                None,
            )?;
            CoSetProxyBlanket(
                &svc,
                RPC_C_AUTHN_WINNT,
                RPC_C_AUTHZ_NONE,
                PCWSTR::null(),
                RPC_C_AUTHN_LEVEL_CALL,
                RPC_C_IMP_LEVEL_IMPERSONATE,
                None,
                EOAC_NONE,
            )?;
            Ok(svc)
        }
    }

    fn query(svc: &IWbemServices, q: &str) -> windows::core::Result<Vec<IWbemClassObject>> {
        let mut out = Vec::new();
        unsafe {
            let en = svc.ExecQuery(
                &BSTR::from("WQL"),
                &BSTR::from(q),
                WBEM_GENERIC_FLAG_TYPE(WBEM_FLAG_FORWARD_ONLY.0 | WBEM_FLAG_RETURN_IMMEDIATELY.0),
                None,
            )?;
            loop {
                let mut row = [None];
                let mut n = 0u32;
                en.Next(WBEM_INFINITE, &mut row, &mut n).ok()?;
                match (n, row[0].take()) {
                    (1, Some(o)) => out.push(o),
                    _ => break,
                }
            }
        }
        Ok(out)
    }

    /// Bir özelliği okur: sayıysa `Ok(Some(n))`, metinse `Err(metin)`.
    fn prop(o: &IWbemClassObject, name: PCWSTR) -> Option<Result<u32, String>> {
        unsafe {
            let mut v = VARIANT::default();
            o.Get(name, 0, &mut v, None, None).ok()?;
            let inner = &v.Anonymous.Anonymous;
            let r = match inner.vt {
                VT_UI1 => Some(Ok(inner.Anonymous.bVal as u32)),
                VT_UI4 => Some(Ok(inner.Anonymous.ulVal)),
                windows::Win32::System::Variant::VT_BSTR => {
                    Some(Err((*inner.Anonymous.bstrVal).to_string()))
                }
                _ => None,
            };
            let _ = VariantClear(&mut v);
            r
        }
    }

    fn wmi_screens() -> Vec<Screen> {
        let Ok(svc) = wmi() else { return Vec::new() };
        let Ok(rows) = query(&svc, "SELECT CurrentBrightness, InstanceName FROM WmiMonitorBrightness")
        else {
            return Vec::new();
        };
        rows.iter()
            .filter_map(|o| {
                let b = prop(o, w!("CurrentBrightness"))?.ok()?;
                let inst = prop(o, w!("InstanceName"))?.err()?;
                Some(Screen {
                    id: format!("wmi:{inst}"),
                    name: "Dizüstü ekranı".into(),
                    brightness: b.min(100),
                    internal: true,
                })
            })
            .collect()
    }

    fn variant(vt: windows::Win32::System::Variant::VARENUM, val: VARIANT_0_0_0) -> VARIANT {
        VARIANT {
            Anonymous: VARIANT_0 {
                Anonymous: ManuallyDrop::new(VARIANT_0_0 {
                    vt,
                    wReserved1: 0,
                    wReserved2: 0,
                    wReserved3: 0,
                    Anonymous: val,
                }),
            },
        }
    }

    fn wmi_set(instance: &str, v: u32) -> Result<(), String> {
        let e = |e: windows::core::Error| e.message().to_string();
        let svc = wmi().map_err(e)?;
        let rows = query(&svc, "SELECT * FROM WmiMonitorBrightnessMethods").map_err(e)?;
        let target = rows
            .iter()
            .find(|o| matches!(prop(o, w!("InstanceName")), Some(Err(ref s)) if s == instance))
            .ok_or("Ekran bulunamadı")?;
        let Some(Err(path)) = prop(target, w!("__PATH")) else {
            return Err("Ekran bulunamadı".into());
        };
        unsafe {
            let mut class = None;
            svc.GetObject(
                &BSTR::from("WmiMonitorBrightnessMethods"),
                WBEM_GENERIC_FLAG_TYPE(0),
                None,
                Some(&mut class),
                None,
            )
            .map_err(e)?;
            let class: IWbemClassObject = class.ok_or("WMI sınıfı yok")?;
            let mut sig = None;
            class
                .GetMethod(w!("WmiSetBrightness"), 0, &mut sig, std::ptr::null_mut())
                .map_err(e)?;
            let params = sig.ok_or("WMI yöntemi yok")?.SpawnInstance(0).map_err(e)?;
            let timeout = variant(VT_UI4, VARIANT_0_0_0 { ulVal: 0 });
            let level = variant(VT_UI1, VARIANT_0_0_0 { bVal: v as u8 });
            params.Put(w!("Timeout"), 0, &timeout, 0).map_err(e)?;
            params.Put(w!("Brightness"), 0, &level, 0).map_err(e)?;
            svc.ExecMethod(
                &BSTR::from(path),
                &BSTR::from("WmiSetBrightness"),
                WBEM_GENERIC_FLAG_TYPE(0),
                None,
                &params,
                None,
                None,
            )
            .map_err(e)?;
        }
        Ok(())
    }

    pub fn screens() -> Vec<Screen> {
        let mut list = wmi_screens();
        list.extend(ddc_screens());
        list
    }

    pub fn set_brightness(id: &str, v: u32) -> Result<(), String> {
        if let Some(inst) = id.strip_prefix("wmi:") {
            return wmi_set(inst, v);
        }
        let mut parts = id.strip_prefix("ddc:").ok_or("Ekran bulunamadı")?.split(':');
        let mi = parts.next().and_then(|s| s.parse().ok()).ok_or("Ekran bulunamadı")?;
        let pi = parts.next().and_then(|s| s.parse().ok()).ok_or("Ekran bulunamadı")?;
        ddc_set(mi, pi, v)
    }

    /* ---------------- Gece ışığı ---------------- */

    const NIGHT_KEY: PCWSTR = w!(
        "Software\\Microsoft\\Windows\\CurrentVersion\\CloudStore\\Store\\DefaultAccount\\Current\\default$windows.data.bluelightreduction.bluelightreductionstate\\windows.data.bluelightreduction.bluelightreductionstate"
    );

    fn open(access: windows::Win32::System::Registry::REG_SAM_FLAGS) -> Option<HKEY> {
        let mut key = HKEY::default();
        let r = unsafe { RegOpenKeyExW(HKEY_CURRENT_USER, NIGHT_KEY, None, access, &mut key) };
        (r == ERROR_SUCCESS).then_some(key)
    }

    pub fn read_night_light() -> Option<Vec<u8>> {
        let key = open(KEY_QUERY_VALUE)?;
        let mut buf = vec![0u8; 256];
        let mut len = buf.len() as u32;
        let mut ty = REG_VALUE_TYPE::default();
        let r = unsafe {
            RegQueryValueExW(key, w!("Data"), None, Some(&mut ty), Some(buf.as_mut_ptr()), Some(&mut len))
        };
        unsafe {
            let _ = RegCloseKey(key);
        }
        if r != ERROR_SUCCESS || ty != REG_BINARY {
            return None;
        }
        buf.truncate(len as usize);
        Some(buf)
    }

    pub fn write_night_light(data: &[u8]) -> Result<(), String> {
        let key = open(KEY_SET_VALUE).ok_or("Gece ışığı ayarı açılamadı")?;
        let r = unsafe { RegSetValueExW(key, w!("Data"), None, REG_BINARY, Some(data)) };
        unsafe {
            let _ = RegCloseKey(key);
        }
        if r == ERROR_SUCCESS {
            Ok(())
        } else {
            Err("Gece ışığı ayarı yazılamadı".into())
        }
    }
}

#[cfg(not(windows))]
mod imp {
    use super::Screen;

    pub fn screens() -> Vec<Screen> {
        Vec::new()
    }
    pub fn set_brightness(_: &str, _: u32) -> Result<(), String> {
        Err("Bu sistemde henüz desteklenmiyor".into())
    }
    pub fn read_night_light() -> Option<Vec<u8>> {
        None
    }
    pub fn write_night_light(_: &[u8]) -> Result<(), String> {
        Err("Bu sistemde henüz desteklenmiyor".into())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Windows 11 (26200) üzerinde okunan gerçek değer, gece ışığı kapalı.
    const WIN11_OFF: [u8; 38] = [
        0x43, 0x42, 0x01, 0x00, 0x0A, 0x02, 0x01, 0x00, 0x2A, 0x06, 0xC0, 0xA2, 0xEF, 0xD7, 0x06,
        0x2A, 0x2B, 0x0E, 0x10, 0x43, 0x42, 0x01, 0x00, 0xC6, 0x14, 0xF9, 0xCC, 0x8E, 0xEE, 0x81,
        0xC9, 0xDD, 0xEE, 0x01, 0x00, 0x00, 0x00, 0x00,
    ];

    #[test]
    fn win11_degeri_kapali_okunur() {
        assert_eq!(night_light_parse(&WIN11_OFF), Some(false));
    }

    #[test]
    fn ac_kapa_geri_doner() {
        let on = night_light_toggle(&WIN11_OFF, true).unwrap();
        assert_eq!(on.len(), 40);
        assert_eq!(on[18], 0x12);
        assert_eq!(&on[23..25], &ON_MARK);
        assert_eq!(on[10], 0xC1, "zaman damgası artmalı");
        assert_eq!(night_light_parse(&on), Some(true));

        let off = night_light_toggle(&on, false).unwrap();
        assert_eq!(night_light_parse(&off), Some(false));
        assert_eq!(off.len(), WIN11_OFF.len());
        assert_eq!(off[11..], WIN11_OFF[11..]);
    }

    #[test]
    fn ayni_durum_yazilmaz() {
        assert_eq!(night_light_toggle(&WIN11_OFF, false), None);
    }

    #[test]
    fn eski_bicim_de_taninir() {
        // Windows 10 / eski 11: kapalıyken 41 bayt, iç yapıda `D0 0A 02` alanı.
        let mut d = vec![0x43, 0x42, 0x01, 0x00, 0x0A, 0x02, 0x01, 0x00, 0x2A, 0x06];
        d.extend([0x10, 0x20, 0x30, 0x40, 0x05]);
        d.extend([0x2A, 0x2B, 0x0E, 0x13, 0x43, 0x42, 0x01, 0x00, 0xD0, 0x0A, 0x02, 0xC6, 0x14]);
        d.extend([0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x01, 0x00, 0x00, 0x00, 0x00]);
        assert_eq!(d.len(), 41);
        assert_eq!(night_light_parse(&d), Some(false));
        let on = night_light_toggle(&d, true).unwrap();
        assert_eq!((on.len(), on[18]), (43, 0x15));
    }

    #[test]
    fn bozuk_deger_reddedilir() {
        let mut d = WIN11_OFF.to_vec();
        d[18] = 0x30;
        assert_eq!(night_light_parse(&d), None);
        assert_eq!(night_light_toggle(&d, true), None);
        assert_eq!(night_light_parse(&[0x43, 0x42]), None);
    }
}
