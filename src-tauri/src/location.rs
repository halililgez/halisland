//! Cihaz konumu (Bugün sekmesindeki hava durumu için).
//!
//! Windows: `Windows.Devices.Geolocation` (Wi-Fi/IP/GPS; Windows'un kendi
//! konum servisi). Konum yalnızca kullanıcı "Konumumu kullan"ı seçtiyse
//! istenir. Koordinatlar yaklaşık 1 km'ye yuvarlanır; hava durumu için bu
//! yeterli ve tam konum ne ayar dosyasına ne Open-Meteo'ya gider.
//!
//! İzin yoksa (Windows Ayarları › Gizlilik ve güvenlik › Konum: "Konum
//! hizmetleri" ya da "Masaüstü uygulamalarının konumunuza erişmesine izin
//! verin" kapalıysa) açıklayıcı bir hata döner.

use serde::Serialize;

#[derive(Serialize, Clone, Copy, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Coords {
    pub latitude: f64,
    pub longitude: f64,
}

/// İki ondalık basamak ≈ 1,1 km.
fn coarse(v: f64) -> f64 {
    (v * 100.0).round() / 100.0
}

#[tauri::command]
pub async fn device_location() -> Result<Coords, String> {
    // WinRT çağrıları bekleyerek yapılır; ana iş parçacığını tutmasın.
    let c = tauri::async_runtime::spawn_blocking(imp::locate)
        .await
        .map_err(|e| e.to_string())??;
    Ok(Coords {
        latitude: coarse(c.latitude),
        longitude: coarse(c.longitude),
    })
}

#[cfg(windows)]
mod imp {
    use super::Coords;
    use windows::Devices::Geolocation::{GeolocationAccessStatus, Geolocator, PositionAccuracy};
    use windows::Foundation::TimeSpan;
    use windows::Win32::System::WinRT::{RO_INIT_MULTITHREADED, RoInitialize};

    /// TimeSpan 100 ns biriminde.
    const fn secs(s: i64) -> TimeSpan {
        TimeSpan { Duration: s * 10_000_000 }
    }

    const DENIED: &str =
        "Konum kapalı. Ayarlar › Gizlilik › Konum'dan masaüstü uygulamalarına izin ver.";

    pub fn locate() -> Result<Coords, String> {
        unsafe {
            let _ = RoInitialize(RO_INIT_MULTITHREADED);
        }
        let run = || -> windows::core::Result<Result<Coords, String>> {
            let status = Geolocator::RequestAccessAsync()?.join()?;
            if status != GeolocationAccessStatus::Allowed {
                return Ok(Err(DENIED.into()));
            }
            let g = Geolocator::new()?;
            g.SetDesiredAccuracy(PositionAccuracy::Default)?;
            // 10 dakikalık bir ölçüm yeterince taze; en fazla 10 sn bekle.
            let pos = g.GetGeopositionAsyncWithAgeAndTimeout(secs(600), secs(10))?.join()?;
            let p = pos.Coordinate()?.Point()?.Position()?;
            Ok(Ok(Coords {
                latitude: p.Latitude,
                longitude: p.Longitude,
            }))
        };
        run().unwrap_or_else(|e| Err(format!("Konum alınamadı: {}", e.message())))
    }
}

#[cfg(not(windows))]
mod imp {
    use super::Coords;

    pub fn locate() -> Result<Coords, String> {
        Err("Cihaz konumu bu sistemde henüz desteklenmiyor; şehir arayarak seçebilirsin.".into())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn konum_yuvarlanir() {
        assert_eq!(coarse(40.987_654), 40.99);
        assert_eq!(coarse(-29.034_9), -29.03);
    }
}
