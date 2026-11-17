//! Windows 11: oval bulanıklık (Windows.UI.Composition).
//!
//! Eski `SetWindowCompositionAttribute` bulanıklığı Windows 11'de pencere
//! bölgesini yok sayıp hep dikdörtgen çiziyor (bkz. `glass.rs`). Burada
//! bulanıklığı DWM'nin kompozisyon katmanında kendimiz kuruyoruz:
//!
//! ```text
//! DesktopWindowTarget (pencerenin arkasında, web görünümünün altında)
//! └─ container  ← yuvarlak kırpma (CompositionRoundedRectangleGeometry)
//!    ├─ blur    ← HostBackdropBrush: arkadaki masaüstünün bulanık hâli
//!    └─ tint    ← koyu ton (buzlu camda biraz daha belirgin)
//! ```
//!
//! Kırpmayı DWM uyguladığı için bulanıklık adanın şekline tam uyar. Bütün
//! nesneler UI (ana) iş parçacığına bağlıdır; bu modüldeki fonksiyonlar
//! yalnızca ana iş parçacığından çağrılmalı (`glass::on_main`).

use crate::glass::GlassMode;
use std::cell::RefCell;
use std::ffi::c_void;
use windows::System::DispatcherQueueController;
use windows::UI::Color;
use windows::UI::Composition::Desktop::DesktopWindowTarget;
use windows::UI::Composition::{
    CompositionColorBrush, CompositionRoundedRectangleGeometry, Compositor, ContainerVisual,
};
use windows::Win32::Foundation::HWND;
use windows::Win32::Graphics::Dwm::{DWMWA_USE_HOSTBACKDROPBRUSH, DwmSetWindowAttribute};
use windows::Win32::System::WinRT::Composition::ICompositorDesktopInterop;
use windows::Win32::System::WinRT::{
    CreateDispatcherQueueController, DQTAT_COM_NONE, DQTYPE_THREAD_CURRENT,
    DispatcherQueueOptions,
};
use windows::core::{BOOL, Interface, Result};
use windows_numerics::{Vector2, Vector3};

/// Görsellerin boyutu: pencere ne kadar büyürse büyüsün yetecek kadar.
/// Görünen kısmı zaten kırpma belirliyor.
const CANVAS: f32 = 8192.0;

struct Backdrop {
    // Bunlar yaşadığı sürece görsel ağaç ekranda kalır.
    _controller: Option<DispatcherQueueController>,
    _target: DesktopWindowTarget,
    container: ContainerVisual,
    geometry: CompositionRoundedRectangleGeometry,
    tint: CompositionColorBrush,
}

thread_local! {
    static BACKDROP: RefCell<Option<Backdrop>> = const { RefCell::new(None) };
}

fn tint_color(mode: GlassMode) -> Color {
    // Koyu ton R=16 G=16 B=20; alfa bulanıklığın ne kadar örtüleceği.
    let a = match mode {
        GlassMode::Acrylic => 0x66,
        _ => 0x10,
    };
    Color { A: a, R: 16, G: 16, B: 20 }
}

fn create(hwnd: HWND) -> Result<Backdrop> {
    // Compositor, iş parçacığında bir DispatcherQueue ister.
    let controller = unsafe {
        CreateDispatcherQueueController(DispatcherQueueOptions {
            dwSize: std::mem::size_of::<DispatcherQueueOptions>() as u32,
            threadType: DQTYPE_THREAD_CURRENT,
            apartmentType: DQTAT_COM_NONE,
        })
    }
    .ok();

    // HostBackdropBrush'ın bu pencere için çalışmasına izin ver.
    let enable = BOOL::from(true);
    unsafe {
        DwmSetWindowAttribute(
            hwnd,
            DWMWA_USE_HOSTBACKDROPBRUSH,
            &enable as *const _ as *const c_void,
            std::mem::size_of::<BOOL>() as u32,
        )?;
    }

    let compositor = Compositor::new()?;
    let interop: ICompositorDesktopInterop = compositor.cast()?;
    // false: görseller pencerenin içeriğinin (web görünümünün) arkasında.
    let target = unsafe { interop.CreateDesktopWindowTarget(hwnd, false)? };

    let container = compositor.CreateContainerVisual()?;
    container.SetSize(Vector2::new(CANVAS, CANVAS))?;

    let geometry = compositor.CreateRoundedRectangleGeometry()?;
    let clip = compositor.CreateGeometricClipWithGeometry(&geometry)?;
    container.SetClip(&clip)?;

    let blur = compositor.CreateSpriteVisual()?;
    blur.SetSize(Vector2::new(CANVAS, CANVAS))?;
    blur.SetBrush(&compositor.CreateHostBackdropBrush()?)?;

    let tint = compositor.CreateColorBrushWithColor(tint_color(GlassMode::Acrylic))?;
    let tint_visual = compositor.CreateSpriteVisual()?;
    tint_visual.SetSize(Vector2::new(CANVAS, CANVAS))?;
    tint_visual.SetBrush(&tint)?;

    let children = container.Children()?;
    children.InsertAtBottom(&blur)?;
    children.InsertAtTop(&tint_visual)?;
    container.SetOffset(Vector3::new(0.0, 0.0, 0.0))?;
    // Şekil gelene kadar hiçbir şey görünmesin.
    geometry.SetSize(Vector2::new(0.0, 0.0))?;
    target.SetRoot(&container)?;

    Ok(Backdrop {
        _controller: controller,
        _target: target,
        container,
        geometry,
        tint,
    })
}

/// Ana iş parçacığında bir kez çağrılır. Başarılıysa `true`.
pub fn init(hwnd: HWND) -> bool {
    BACKDROP.with(|b| {
        if b.borrow().is_some() {
            return true;
        }
        match create(hwnd) {
            Ok(backdrop) => {
                *b.borrow_mut() = Some(backdrop);
                true
            }
            Err(e) => {
                eprintln!("[halisland] oval bulanıklık kurulamadı: {e}");
                false
            }
        }
    })
}

/// Bulanıklığı açar/kapatır. Kurulu değilse `false`.
pub fn set_mode(mode: GlassMode) -> bool {
    BACKDROP.with(|b| {
        let Some(bd) = &*b.borrow() else { return false };
        let on = mode != GlassMode::Off;
        let _ = bd.container.SetIsVisible(on);
        let _ = bd.tint.SetColor(tint_color(mode));
        on
    })
}

/// Kırpmayı adanın o anki şekline getirir (fiziksel piksel).
pub fn set_shape(x: i32, y: i32, w: i32, h: i32, r: i32) {
    BACKDROP.with(|b| {
        let Some(bd) = &*b.borrow() else { return };
        let _ = bd.geometry.SetOffset(Vector2::new(x as f32, y as f32));
        let _ = bd.geometry.SetSize(Vector2::new(w as f32, h as f32));
        let _ = bd.geometry.SetCornerRadius(Vector2::new(r as f32, r as f32));
    });
}
