//! Gerçek cam efekti: adanın arkasındaki masaüstü bulanıklaşır.
//!
//! Windows: `SetWindowCompositionAttribute` ile acrylic ya da düz bulanıklık.
//! Windows 11'in yeni "system backdrop" API'si (Mica/Acrylic) pencere odakta
//! değilken düz renge döner. Ada hiç odak almadığı için eski API'yi kullanıyoruz;
//! o odaktan bağımsız çalışır.
//!
//! Pencere köşeli bir dikdörtgen, ada ise yuvarlak köşeli ve animasyonla
//! büyüyüp küçülüyor. Frontend adanın o anki boyutunu `set_island_shape` ile
//! bildirir, biz de pencereyi `SetWindowRgn` ile tam adanın şekline kırparız
//! (cam kapalıyken de: köşelerdeki boşluk tıklamaları yutmasın).
//!
//! Windows 11 sınırı: bu API'nin bulanıklığı pencere bölgesini YOK SAYIYOR,
//! hep tam dikdörtgen çiziliyor. Oval adanın arkasında köşeli koyu bir
//! çerçeve görünüyordu (bölge 100 px'e indirilerek ölçüldü: web içeriği
//! kırpılıyor, bulanıklık kırpılmıyor). Bu yüzden Windows 11'de (derleme
//! 22000+) bulanıklık kapalı; ada yarı saydam koyu arka planla oval kalır.
//!
//! macOS: ileride (vibrancy). Şimdilik cam kapalı döner, frontend CSS
//! arka planıyla devam eder.

use serde::{Deserialize, Serialize};
use std::sync::Mutex;
use std::thread;
use std::time::Duration;
use tauri::{AppHandle, Manager, WebviewWindow};

#[derive(Clone, Copy, PartialEq, Eq, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum GlassMode {
    /// Buzlu cam (bulanık + hafif doku)
    #[default]
    Acrylic,
    /// Sade bulanıklık
    Blur,
    /// Cam yok, yarı saydam koyu arka plan
    Off,
}

/// Adanın o anki şekli (mantıksal piksel). Ada pencerede yatay ortalı ve
/// üste yaslı durduğu için x/y gerekmez.
#[derive(Clone, Copy, Debug, Default)]
struct Shape {
    width: f64,
    height: f64,
    radius: f64,
}

#[derive(Default)]
pub struct GlassState {
    shape: Mutex<Option<Shape>>,
}

/// Bu sistemde bulanıklık adanın oval şekline uyuyor mu? (Bkz. modül notu.)
pub fn blur_supported() -> bool {
    platform::blur_supported()
}

/// Cam efektini açar/kapatır. Bulanıklık gerçekten açıldıysa `true` döner.
#[tauri::command]
pub fn set_glass(window: WebviewWindow, mode: GlassMode) -> bool {
    let mode = if blur_supported() { mode } else { GlassMode::Off };
    let active = platform::apply(&window, mode);
    apply_shape(&window);
    active
}

#[tauri::command]
pub fn set_island_shape(window: WebviewWindow, width: f64, height: f64, radius: f64) {
    let state = window.state::<GlassState>();
    *state.shape.lock().unwrap() = Some(Shape { width, height, radius });
    apply_shape(&window);
}

/// Pencere boyutu değişince de çağrılır (`island::resize_island`).
pub fn apply_shape(window: &WebviewWindow) {
    let state = window.state::<GlassState>();
    let Some(shape) = *state.shape.lock().unwrap() else {
        return;
    };
    let (Ok(size), Ok(scale)) = (window.inner_size(), window.scale_factor()) else {
        return;
    };
    let w = (shape.width * scale).round() as i32;
    let h = (shape.height * scale).round() as i32;
    let r = (shape.radius * scale).round() as i32;
    let x = (size.width as i32 - w) / 2;
    platform::set_region(window, x, 0, w, h, r);
}

/// Pencere kütüphanesi (tao) bazı sistem olaylarında (kilit ekranından dönüş,
/// ekran/DPI değişikliği) pencere stillerini yeniden uygular. O zaman
/// kaldırdığımız başlık çubuğu geri gelir ve adanın üstünde gri bir şerit
/// görünür. Şekil değişmediği sürece bölge yeniden kurulmadığı için bu,
/// bir sonraki aç-kapaya kadar kalıyordu. Saniyede bir stile bakıp gerekirse
/// çerçeveyi yeniden temizliyoruz (yalnızca bir `GetWindowLongPtr` çağrısı).
pub fn start_frame_guard(app: AppHandle) {
    thread::spawn(move || {
        loop {
            thread::sleep(Duration::from_secs(1));
            let Some(window) = app.get_webview_window("main") else {
                continue;
            };
            if platform::has_frame(&window) {
                let w = window.clone();
                let _ = app.run_on_main_thread(move || apply_shape(&w));
            }
        }
    });
}

#[cfg(windows)]
mod platform {
    use super::GlassMode;
    use std::ffi::c_void;
    use std::sync::OnceLock;
    use std::sync::atomic::{AtomicBool, Ordering};
    use windows::Win32::System::Registry::{HKEY_LOCAL_MACHINE, RRF_RT_REG_SZ, RegGetValueW};
    use tauri::WebviewWindow;
    use windows::Win32::Foundation::{HMODULE, HWND, LPARAM, LRESULT, WPARAM};
    use windows::Win32::Graphics::Dwm::{
        DWMWA_BORDER_COLOR, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_DONOTROUND,
        DwmSetWindowAttribute,
    };
    use windows::Win32::Graphics::Gdi::{
        CreateRoundRectRgn, RDW_ALLCHILDREN, RDW_ERASE, RDW_FRAME, RDW_INVALIDATE, RedrawWindow,
        SetWindowRgn,
    };
    use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};
    use windows::Win32::UI::Shell::{DefSubclassProc, SetWindowSubclass};
    use windows::Win32::UI::WindowsAndMessaging::{
        GWL_STYLE, GetWindowLongPtrW, SWP_FRAMECHANGED, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE,
        SWP_NOZORDER, SetWindowLongPtrW, SetWindowPos, WINDOW_STYLE, WM_NCACTIVATE, WM_NCPAINT,
        WS_CAPTION, WS_MAXIMIZEBOX, WS_MINIMIZEBOX, WS_POPUP, WS_SYSMENU, WS_THICKFRAME,
    };
    use windows::core::{BOOL, s, w};

    const WCA_ACCENT_POLICY: u32 = 19;
    const ACCENT_DISABLED: u32 = 0;
    const ACCENT_ENABLE_BLURBEHIND: u32 = 3;
    const ACCENT_ENABLE_ACRYLICBLURBEHIND: u32 = 4;
    /// GradientColor alanını kullan.
    const ACCENT_FLAG_GRADIENT: u32 = 2;

    #[repr(C)]
    struct AccentPolicy {
        accent_state: u32,
        accent_flags: u32,
        /// AABBGGRR
        gradient_color: u32,
        animation_id: u32,
    }

    #[repr(C)]
    struct CompositionData {
        attribute: u32,
        data: *mut c_void,
        size: usize,
    }

    type SetWindowCompositionAttribute =
        unsafe extern "system" fn(HWND, *mut CompositionData) -> BOOL;

    /// Belgelenmemiş bir user32 fonksiyonu; dinamik olarak yüklenir.
    fn swca() -> Option<SetWindowCompositionAttribute> {
        unsafe {
            let user32: HMODULE = LoadLibraryW(w!("user32.dll")).ok()?;
            let f = GetProcAddress(user32, s!("SetWindowCompositionAttribute"))?;
            Some(std::mem::transmute::<
                unsafe extern "system" fn() -> isize,
                SetWindowCompositionAttribute,
            >(f))
        }
    }

    /// Windows 10'da (derleme < 22000) bulanıklık bölgeye uyuyor; 11'de uymuyor.
    pub fn blur_supported() -> bool {
        static CACHE: OnceLock<bool> = OnceLock::new();
        *CACHE.get_or_init(|| windows_build().is_some_and(|b| b < 22000))
    }

    fn windows_build() -> Option<u32> {
        let mut buf = [0u16; 32];
        let mut size = std::mem::size_of_val(&buf) as u32;
        let err = unsafe {
            RegGetValueW(
                HKEY_LOCAL_MACHINE,
                w!("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion"),
                w!("CurrentBuildNumber"),
                RRF_RT_REG_SZ,
                None,
                Some(buf.as_mut_ptr() as *mut c_void),
                Some(&mut size),
            )
        };
        if err.is_err() {
            return None;
        }
        let len = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
        String::from_utf16_lossy(&buf[..len]).trim().parse().ok()
    }

    pub fn apply(window: &WebviewWindow, mode: GlassMode) -> bool {
        let Ok(hwnd) = window.hwnd() else {
            return false;
        };
        let Some(f) = swca() else {
            return false;
        };
        // Koyu ton: R=16 G=16 B=20. Acrylic'te alfa, rengin ne kadar
        // baskın olacağını belirler.
        let (state, alpha) = match mode {
            GlassMode::Acrylic => (ACCENT_ENABLE_ACRYLICBLURBEHIND, 0x66u32),
            GlassMode::Blur => (ACCENT_ENABLE_BLURBEHIND, 0x00),
            GlassMode::Off => (ACCENT_DISABLED, 0x00),
        };
        let mut policy = AccentPolicy {
            accent_state: state,
            accent_flags: ACCENT_FLAG_GRADIENT,
            gradient_color: (alpha << 24) | (20 << 16) | (16 << 8) | 16,
            animation_id: 0,
        };
        let mut data = CompositionData {
            attribute: WCA_ACCENT_POLICY,
            data: &mut policy as *mut _ as *mut c_void,
            size: std::mem::size_of::<AccentPolicy>(),
        };
        let ok = unsafe { f(hwnd, &mut data) }.as_bool();
        ok && mode != GlassMode::Off
    }

    /// Kenarlıksız Tauri pencereleri başlık çubuğu stilini (WS_CAPTION) korur,
    /// çerçeveyi yalnızca çizimde gizler. Pencereye bölge verilince Windows o
    /// çerçeveyi eski tip başlık çubuğu olarak çizmeye başlıyor; bu yüzden
    /// çerçeve stillerini tamamen kaldırıp pencereyi düz bir popup yapıyoruz.
    const FRAME: WINDOW_STYLE =
        WINDOW_STYLE(WS_CAPTION.0 | WS_SYSMENU.0 | WS_THICKFRAME.0 | WS_MINIMIZEBOX.0 | WS_MAXIMIZEBOX.0);

    fn style(hwnd: HWND) -> WINDOW_STYLE {
        WINDOW_STYLE(unsafe { GetWindowLongPtrW(hwnd, GWL_STYLE) } as u32)
    }

    /// Çerçeve stilleri geri gelmiş mi?
    pub fn has_frame(window: &WebviewWindow) -> bool {
        window
            .hwnd()
            .is_ok_and(|hwnd| style(hwnd) & FRAME != WINDOW_STYLE(0))
    }

    /// Çerçeve stillerini kaldırır; bir şey değiştiyse `true`.
    fn strip_frame(hwnd: HWND) -> bool {
        let style = style(hwnd);
        if style & FRAME == WINDOW_STYLE(0) {
            return false;
        }
        unsafe {
            let next = (style & !FRAME) | WS_POPUP;
            SetWindowLongPtrW(hwnd, GWL_STYLE, next.0 as isize);
            let _ = SetWindowPos(
                hwnd,
                None,
                0,
                0,
                0,
                0,
                SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE,
            );
        }
        true
    }

    const WM_NCUAHDRAWCAPTION: u32 = 0x00AE;
    const WM_NCUAHDRAWFRAME: u32 = 0x00AF;

    /// Pencere mesajlarının arasına girer (C#'taki `WndProc` override'ı gibi).
    ///
    /// Stil temiz olsa bile pencere etkinleşince (tıklama, sağ tık menüsü)
    /// Windows'un varsayılan işleyicisi "başlık alanını" pencerenin üstüne
    /// boyuyor; adanın üstünde "Halisland" yazan gri bir şerit kalıyordu.
    /// Çerçeve/başlık çizim mesajlarını yutuyoruz.
    unsafe extern "system" fn subclass_proc(
        hwnd: HWND,
        msg: u32,
        wparam: WPARAM,
        lparam: LPARAM,
        _id: usize,
        _data: usize,
    ) -> LRESULT {
        unsafe {
            match msg {
                // lParam = -1: etkinlik durumu değişsin ama çerçeve yeniden çizilmesin.
                WM_NCACTIVATE => DefSubclassProc(hwnd, msg, wparam, LPARAM(-1)),
                WM_NCPAINT | WM_NCUAHDRAWCAPTION | WM_NCUAHDRAWFRAME => LRESULT(0),
                _ => DefSubclassProc(hwnd, msg, wparam, lparam),
            }
        }
    }

    static PREPARED: AtomicBool = AtomicBool::new(false);

    /// Bir kez: mesaj yakalayıcıyı kur, Windows 11'in kendi kenarlığını ve
    /// köşe yuvarlamasını kapat (bölgeyle çakışıp adanın arkasında çerçeve
    /// gibi görünüyordu).
    fn prepare(hwnd: HWND) {
        if PREPARED.swap(true, Ordering::SeqCst) {
            return;
        }
        unsafe {
            let _ = SetWindowSubclass(hwnd, Some(subclass_proc), 1, 0);
            let no_round = DWMWCP_DONOTROUND;
            let _ = DwmSetWindowAttribute(
                hwnd,
                DWMWA_WINDOW_CORNER_PREFERENCE,
                &no_round as *const _ as *const c_void,
                std::mem::size_of_val(&no_round) as u32,
            );
            // DWMWA_COLOR_NONE: kenarlık çizme.
            let no_border: u32 = 0xFFFF_FFFE;
            let _ = DwmSetWindowAttribute(
                hwnd,
                DWMWA_BORDER_COLOR,
                &no_border as *const _ as *const c_void,
                4,
            );
        }
    }

    pub fn set_region(window: &WebviewWindow, x: i32, y: i32, w: i32, h: i32, r: i32) {
        let Ok(hwnd) = window.hwnd() else { return };
        prepare(hwnd);
        let stripped = strip_frame(hwnd);
        unsafe {
            // Sağ/alt kenar bölgeye dahil değil; x + w tam w piksel eder.
            // (Eskiden +1 vardı, bölge adadan 1 piksel taşıyor ve sağda/altta
            // koyu bir cam şeridi bırakıyordu.)
            let rgn = CreateRoundRectRgn(x, y, x + w, y + h, r * 2, r * 2);
            // Başarılı olursa bölgenin sahibi sistem olur; silmiyoruz.
            SetWindowRgn(hwnd, Some(rgn), true);
            if stripped {
                // Daha önce boyanmış başlık kalıntısını temizle.
                let _ = RedrawWindow(
                    Some(hwnd),
                    None,
                    None,
                    RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN,
                );
            }
        }
    }
}

#[cfg(not(windows))]
mod platform {
    use super::GlassMode;
    use tauri::WebviewWindow;

    pub fn apply(_window: &WebviewWindow, _mode: GlassMode) -> bool {
        false
    }

    pub fn has_frame(_window: &WebviewWindow) -> bool {
        false
    }

    pub fn set_region(_window: &WebviewWindow, _x: i32, _y: i32, _w: i32, _h: i32, _r: i32) {}

    pub fn blur_supported() -> bool {
        false
    }
}
