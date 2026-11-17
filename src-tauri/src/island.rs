use serde::{Deserialize, Serialize};
use tauri::{LogicalSize, Manager, PhysicalPosition, PhysicalSize, WebviewWindow};

/// Adanın ekran kenarından uzaklığı (mantıksal piksel).
const EDGE_MARGIN: f64 = 8.0;

/// Adanın yapıştığı ekran kenarı. Üst ve altta ada yataydır, sağ ve solda
/// dikey. Ada her zaman bu kenardan ekranın içine doğru açılır.
#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum Edge {
    #[default]
    Top,
    Bottom,
    Left,
    Right,
}

impl Edge {
    pub const ALL: [Edge; 4] = [Edge::Top, Edge::Bottom, Edge::Left, Edge::Right];

    pub fn id(self) -> &'static str {
        match self {
            Edge::Top => "edge-top",
            Edge::Bottom => "edge-bottom",
            Edge::Left => "edge-left",
            Edge::Right => "edge-right",
        }
    }

    /// Pencerenin kenara bakan noktası (dayanak). Ada büyüyüp küçülürken bu
    /// nokta sabit kalır: üstte üst-orta, altta alt-orta, solda sol-orta,
    /// sağda sağ-orta.
    pub fn anchor(self, pos: PhysicalPosition<i32>, size: PhysicalSize<u32>) -> (i32, i32) {
        let (w, h) = (size.width as i32, size.height as i32);
        match self {
            Edge::Top => (pos.x + w / 2, pos.y),
            Edge::Bottom => (pos.x + w / 2, pos.y + h),
            Edge::Left => (pos.x, pos.y + h / 2),
            Edge::Right => (pos.x + w, pos.y + h / 2),
        }
    }

    /// `anchor`'un tersi: dayanak noktası ve boyuttan pencerenin sol üstü.
    pub fn origin(self, anchor: (i32, i32), size: PhysicalSize<u32>) -> PhysicalPosition<i32> {
        let (ax, ay) = anchor;
        let (w, h) = (size.width as i32, size.height as i32);
        let (x, y) = match self {
            Edge::Top => (ax - w / 2, ay),
            Edge::Bottom => (ax - w / 2, ay - h),
            Edge::Left => (ax, ay - h / 2),
            Edge::Right => (ax - w, ay - h / 2),
        };
        PhysicalPosition::new(x, y)
    }

    /// Adanın pencere içindeki yeri (cam bölgesi için). Pencere animasyon
    /// sırasında adadan büyük olabilir; ada kenara yaslı ve ortalıdır.
    pub fn inner_offset(self, win: (i32, i32), island: (i32, i32)) -> (i32, i32) {
        let (ww, wh) = win;
        let (w, h) = island;
        match self {
            Edge::Top => ((ww - w) / 2, 0),
            Edge::Bottom => ((ww - w) / 2, wh - h),
            Edge::Left => (0, (wh - h) / 2),
            Edge::Right => (ww - w, (wh - h) / 2),
        }
    }
}

pub fn current_edge(window: &WebviewWindow) -> Edge {
    window
        .try_state::<crate::settings::SettingsStore>()
        .map(|s| s.get().edge)
        .unwrap_or_default()
}

/// Pencereyi bulunduğu monitörde seçili kenarın ortasına yerleştirir.
/// Görev çubuğunun üstüne binmesin diye çalışma alanı kullanılır.
pub fn place_on_edge(window: &WebviewWindow) -> tauri::Result<()> {
    let Some(monitor) = window.current_monitor()?.or(window.primary_monitor()?) else {
        return Ok(());
    };
    let margin = (EDGE_MARGIN * monitor.scale_factor()) as i32;
    let area = monitor.work_area();
    let (x0, y0) = (area.position.x, area.position.y);
    let (w, h) = (area.size.width as i32, area.size.height as i32);
    let edge = current_edge(window);
    let anchor = match edge {
        Edge::Top => (x0 + w / 2, y0 + margin),
        Edge::Bottom => (x0 + w / 2, y0 + h - margin),
        Edge::Left => (x0 + margin, y0 + h / 2),
        Edge::Right => (x0 + w - margin, y0 + h / 2),
    };
    window.set_position(edge.origin(anchor, window.outer_size()?))?;
    Ok(())
}

/// Fare imleci adanın penceresinin üstünde mi? WebView odakta olmayan
/// pencerede `:hover` bilgisini güvenilir tutmadığı için gerçek imleç
/// konumuna bakarız. Hata olursa `true` (adayı yanlışlıkla kapatmayalım).
#[tauri::command]
pub fn cursor_over_island(window: WebviewWindow) -> bool {
    let (Ok(c), Ok(p), Ok(s)) = (
        window.cursor_position(),
        window.outer_position(),
        window.outer_size(),
    ) else {
        return true;
    };
    let (x0, y0) = (p.x as f64, p.y as f64);
    c.x >= x0 && c.x < x0 + s.width as f64 && c.y >= y0 && c.y < y0 + s.height as f64
}

/// Ada büyüyüp küçülürken pencereyi yeniden boyutlandırır. Kenara bakan
/// dayanak noktası sabit kalır; böylece kullanıcı adayı sürüklediyse yeni
/// konumu korunur ve ada hep ekranın içine doğru açılır.
#[tauri::command]
pub fn resize_island(window: WebviewWindow, width: f64, height: f64) -> Result<(), String> {
    let run = || -> tauri::Result<()> {
        let edge = current_edge(&window);
        let anchor = edge.anchor(window.outer_position()?, window.outer_size()?);
        let scale = window.scale_factor()?;
        let new_size = PhysicalSize::new(
            (width * scale).round() as u32,
            (height * scale).round() as u32,
        );

        window.set_size(LogicalSize::new(width, height))?;
        window.set_position(edge.origin(anchor, new_size))?;
        // Cam açıksa pencere bölgesini yeni boyuta göre yeniden yerleştir.
        crate::glass::apply_shape(&window);
        Ok(())
    };
    run().map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dayanak_ve_koken_birbirinin_tersi() {
        let pos = PhysicalPosition::new(100, 50);
        let size = PhysicalSize::new(320, 44);
        for edge in Edge::ALL {
            assert_eq!(edge.origin(edge.anchor(pos, size), size), pos, "{edge:?}");
        }
    }

    #[test]
    fn buyurken_kenar_sabit_kalir() {
        let small = PhysicalSize::new(44, 320);
        let big = PhysicalSize::new(720, 200);
        let pos = PhysicalPosition::new(1000, 400);
        // Sağda: sağ kenar ve dikey merkez korunur, ada sola doğru açılır.
        let a = Edge::Right.anchor(pos, small);
        let p = Edge::Right.origin(a, big);
        assert_eq!(p.x + 720, 1000 + 44);
        assert_eq!(p.y + 100, 400 + 160);
        // Altta: alt kenar korunur, ada yukarı doğru açılır.
        let a = Edge::Bottom.anchor(pos, small);
        let p = Edge::Bottom.origin(a, big);
        assert_eq!(p.y + 200, 400 + 320);
    }

    #[test]
    fn kenar_json_adi() {
        assert_eq!(serde_json::to_string(&Edge::Left).unwrap(), "\"left\"");
    }
}
