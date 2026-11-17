//! Kontrol sekmesi: ses seviyesi ve sessiz, Wi-Fi ve Bluetooth aç/kapa.
//!
//! Yalnızca belgeli Windows API'leri:
//! - Ses: Core Audio (`IMMDeviceEnumerator` → varsayılan çıkış →
//!   `IAudioEndpointVolume`). Çıkış cihazının adı özellik deposundan okunur;
//!   cihaz değiştirme (belgesiz `IPolicyConfig`) yapılmaz.
//! - Wi-Fi / Bluetooth: `Windows.Devices.Radios`. Donanım anahtarı ya da
//!   uçak modu radyoyu kilitlemişse durum `disabled` olur, değiştirilemez.
//! - Bağlı ağ adı: `NetworkInformation::GetInternetConnectionProfile`.
//!
//! Durum frontend'den sorulur (Kontrol sekmesi açıkken saniyede bir); klavye
//! ses tuşlarıyla yapılan değişiklikler de böylece görünür. WinRT/COM
//! çağrıları bekleyerek yapıldığı için komutlar ayrı iş parçacığında çalışır.

use serde::Serialize;

#[derive(Serialize, Clone, Debug, Default, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AudioState {
    /// 0–1
    pub volume: f32,
    pub muted: bool,
    /// Varsayılan çıkış cihazının adı, ör. "Hoparlör (Realtek Audio)".
    pub device: String,
}

#[derive(Serialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum RadioStatus {
    On,
    Off,
    /// Donanım anahtarı / uçak modu / sürücü kapalı: değiştirilemez.
    Disabled,
    /// Bu bilgisayarda yok.
    Missing,
}

#[derive(Serialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct RadiosState {
    pub wifi: RadioStatus,
    pub bluetooth: RadioStatus,
    /// İnternete bağlı ağın adı (Wi-Fi'de SSID).
    pub network: Option<String>,
    /// Bağlantı Wi-Fi mi (değilse kablolu/hücresel)?
    pub wlan: bool,
}

async fn blocking<T: Send + 'static>(
    f: impl FnOnce() -> Result<T, String> + Send + 'static,
) -> Result<T, String> {
    tauri::async_runtime::spawn_blocking(f)
        .await
        .map_err(|e| e.to_string())?
}

#[tauri::command]
pub async fn audio_state() -> Result<AudioState, String> {
    blocking(imp::audio_state).await
}

/// `volume`: 0–1. Sesi açmak sessizi de kaldırır.
#[tauri::command]
pub async fn set_volume(volume: f32) -> Result<(), String> {
    let v = volume.clamp(0.0, 1.0);
    blocking(move || imp::set_volume(v)).await
}

#[tauri::command]
pub async fn set_mute(muted: bool) -> Result<(), String> {
    blocking(move || imp::set_mute(muted)).await
}

#[tauri::command]
pub async fn radios_state() -> Result<RadiosState, String> {
    blocking(imp::radios_state).await
}

/// `kind`: "wifi" ya da "bluetooth".
#[tauri::command]
pub async fn set_radio(kind: String, on: bool) -> Result<(), String> {
    blocking(move || imp::set_radio(&kind, on)).await
}

#[cfg(windows)]
mod imp {
    use super::{AudioState, RadioStatus, RadiosState};
    use windows::Devices::Radios::{Radio, RadioAccessStatus, RadioKind, RadioState};
    use windows::Networking::Connectivity::NetworkInformation;
    use windows::Win32::Devices::FunctionDiscovery::PKEY_Device_FriendlyName;
    use windows::Win32::Media::Audio::Endpoints::IAudioEndpointVolume;
    use windows::Win32::Media::Audio::{
        IMMDevice, IMMDeviceEnumerator, MMDeviceEnumerator, eConsole, eRender,
    };
    use windows::Win32::System::Com::StructuredStorage::PropVariantToBSTR;
    use windows::Win32::System::Com::{
        CLSCTX_ALL, COINIT_MULTITHREADED, CoCreateInstance, CoInitializeEx, STGM_READ,
    };

    fn err(e: windows::core::Error) -> String {
        e.message().to_string()
    }

    /// İş parçacığı havuzundaki her iş parçacığında bir kez yeterli; tekrar
    /// çağrılması zararsız (S_FALSE döner).
    fn com() {
        unsafe {
            let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
        }
    }

    fn endpoint() -> windows::core::Result<(IMMDevice, IAudioEndpointVolume)> {
        com();
        unsafe {
            let en: IMMDeviceEnumerator = CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL)?;
            let dev = en.GetDefaultAudioEndpoint(eRender, eConsole)?;
            let vol: IAudioEndpointVolume = dev.Activate(CLSCTX_ALL, None)?;
            Ok((dev, vol))
        }
    }

    fn device_name(dev: &IMMDevice) -> Option<String> {
        unsafe {
            let store = dev.OpenPropertyStore(STGM_READ).ok()?;
            let value = store.GetValue(&PKEY_Device_FriendlyName).ok()?;
            let name = PropVariantToBSTR(&value).ok()?.to_string();
            (!name.is_empty()).then_some(name)
        }
    }

    pub fn audio_state() -> Result<AudioState, String> {
        let (dev, vol) = endpoint().map_err(|_| "Ses çıkışı bulunamadı".to_string())?;
        unsafe {
            Ok(AudioState {
                volume: vol.GetMasterVolumeLevelScalar().map_err(err)?,
                muted: vol.GetMute().map_err(err)?.as_bool(),
                device: device_name(&dev).unwrap_or_default(),
            })
        }
    }

    pub fn set_volume(v: f32) -> Result<(), String> {
        let (_, vol) = endpoint().map_err(err)?;
        unsafe {
            vol.SetMasterVolumeLevelScalar(v, std::ptr::null()).map_err(err)?;
            if v > 0.0 {
                vol.SetMute(false, std::ptr::null()).map_err(err)?;
            }
        }
        Ok(())
    }

    pub fn set_mute(muted: bool) -> Result<(), String> {
        let (_, vol) = endpoint().map_err(err)?;
        unsafe { vol.SetMute(muted, std::ptr::null()).map_err(err) }
    }

    fn radios() -> Result<Vec<Radio>, String> {
        let access = Radio::RequestAccessAsync().and_then(|op| op.join()).map_err(err)?;
        if access != RadioAccessStatus::Allowed {
            return Err("Windows radyo erişimine izin vermedi".into());
        }
        let list = Radio::GetRadiosAsync().and_then(|op| op.join()).map_err(err)?;
        Ok(list.into_iter().collect())
    }

    /// Aynı türden birden fazla radyo olabilir (ör. iki Wi-Fi kartı): biri
    /// açıksa açık, hepsi kilitliyse kilitli sayılır.
    fn status(list: &[Radio], kind: RadioKind) -> RadioStatus {
        let states: Vec<RadioState> = list
            .iter()
            .filter(|r| r.Kind().ok() == Some(kind))
            .filter_map(|r| r.State().ok())
            .collect();
        if states.is_empty() {
            RadioStatus::Missing
        } else if states.contains(&RadioState::On) {
            RadioStatus::On
        } else if states.contains(&RadioState::Off) {
            RadioStatus::Off
        } else {
            RadioStatus::Disabled
        }
    }

    pub fn radios_state() -> Result<RadiosState, String> {
        com();
        let list = radios()?;
        let profile = NetworkInformation::GetInternetConnectionProfile().ok();
        let network = profile
            .as_ref()
            .and_then(|p| p.ProfileName().ok())
            .map(|s| s.to_string())
            .filter(|s| !s.is_empty());
        let wlan = profile
            .as_ref()
            .and_then(|p| p.IsWlanConnectionProfile().ok())
            .unwrap_or(false);
        Ok(RadiosState {
            wifi: status(&list, RadioKind::WiFi),
            bluetooth: status(&list, RadioKind::Bluetooth),
            network,
            wlan,
        })
    }

    pub fn set_radio(kind: &str, on: bool) -> Result<(), String> {
        com();
        let kind = match kind {
            "wifi" => RadioKind::WiFi,
            "bluetooth" => RadioKind::Bluetooth,
            other => return Err(format!("Bilinmeyen radyo: {other}")),
        };
        let target = if on { RadioState::On } else { RadioState::Off };
        let mut found = false;
        for r in radios()?.iter().filter(|r| r.Kind().ok() == Some(kind)) {
            found = true;
            if r.State().ok() == Some(RadioState::Disabled) {
                continue;
            }
            let res = r.SetStateAsync(target).and_then(|op| op.join()).map_err(err)?;
            if res != RadioAccessStatus::Allowed {
                return Err("Windows bu değişikliğe izin vermedi".into());
            }
        }
        if found {
            Ok(())
        } else {
            Err("Bu bilgisayarda bulunamadı".into())
        }
    }
}

// macOS: henüz yok. Frontend "desteklenmiyor" hatasını gösterir.
#[cfg(not(windows))]
mod imp {
    use super::{AudioState, RadiosState};

    const UNSUPPORTED: &str = "Bu sistemde henüz desteklenmiyor";

    pub fn audio_state() -> Result<AudioState, String> {
        Err(UNSUPPORTED.into())
    }
    pub fn set_volume(_: f32) -> Result<(), String> {
        Err(UNSUPPORTED.into())
    }
    pub fn set_mute(_: bool) -> Result<(), String> {
        Err(UNSUPPORTED.into())
    }
    pub fn radios_state() -> Result<RadiosState, String> {
        Err(UNSUPPORTED.into())
    }
    pub fn set_radio(_: &str, _: bool) -> Result<(), String> {
        Err(UNSUPPORTED.into())
    }
}
