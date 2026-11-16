use tauri::{LogicalSize, PhysicalPosition, WebviewWindow};

/// Adanın ekranın üst kenarından uzaklığı (mantıksal piksel).
const TOP_MARGIN: f64 = 8.0;

/// Pencereyi bulunduğu monitörün üst ortasına yerleştirir.
pub fn place_top_center(window: &WebviewWindow) -> tauri::Result<()> {
    let Some(monitor) = window.current_monitor()?.or(window.primary_monitor()?) else {
        return Ok(());
    };
    let scale = monitor.scale_factor();
    let mpos = monitor.position();
    let msize = monitor.size();
    let wsize = window.outer_size()?;

    let x = mpos.x + (msize.width as i32 - wsize.width as i32) / 2;
    let y = mpos.y + (TOP_MARGIN * scale) as i32;
    window.set_position(PhysicalPosition::new(x, y))?;
    Ok(())
}

/// Ada büyüyüp küçülürken pencereyi yeniden boyutlandırır.
/// Yatay merkez ve üst kenar sabit kalır; böylece kullanıcı adayı
/// sürüklediyse yeni konumu korunur.
#[tauri::command]
pub fn resize_island(window: WebviewWindow, width: f64, height: f64) -> Result<(), String> {
    let run = || -> tauri::Result<()> {
        let pos = window.outer_position()?;
        let old = window.outer_size()?;
        let scale = window.scale_factor()?;

        let center_x = pos.x + old.width as i32 / 2;
        let new_w = (width * scale).round() as i32;

        window.set_size(LogicalSize::new(width, height))?;
        window.set_position(PhysicalPosition::new(center_x - new_w / 2, pos.y))?;
        Ok(())
    };
    run().map_err(|e| e.to_string())
}
