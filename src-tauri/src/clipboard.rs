//! Pano geçmişi: kopyalanan metinler, bağlantılar ve görseller.
//!
//! - Geçmiş yalnızca bellekte tutulur, diske yazılmaz. Uygulama kapanınca ya
//!   da ayarlardan "Pano geçmişi" kapatılınca silinir.
//! - Windows'ta bir iş parçacığı `GetClipboardSequenceNumber` ile panonun
//!   değişip değişmediğine bakar (ucuz bir sayı okuması). Değiştiyse içerik
//!   `arboard` ile okunur.
//! - Parola yöneticileri kopyaladıkları şeyi izleyicilerden gizlemek için
//!   panoya özel biçimler ekler (`ExcludeClipboardContentFromMonitorProcessing`,
//!   `CanIncludeInClipboardHistory` = 0, `Clipboard Viewer Ignore`). Bunlar
//!   varsa içerik geçmişe alınmaz.
//! - Frontend listeyi `clipboard_list` ile ister; her değişiklikte yalnızca
//!   yüksüz `clipboard-changed` olayı gider (görsel küçük resimleri her olayda
//!   taşımamak için).
//! - macOS'ta izleyici henüz yok; liste boş kalır.

// macOS'ta izleyici olmadığı için geçmişe ekleme yolu orada kullanılmıyor.
#![cfg_attr(not(windows), allow(dead_code))]

use base64::Engine;
use serde::Serialize;
use std::collections::hash_map::DefaultHasher;
use std::hash::{Hash, Hasher};
use std::io::Cursor;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};
use tauri::{AppHandle, Emitter, Manager, State};

/// Geçmişte tutulan en fazla öğe (sabitlenenler hariç).
const MAX_ITEMS: usize = 40;
/// Görseller bellekte yer kapladığı için ayrıca sınırlanır (sabitlenenler hariç).
const MAX_IMAGES: usize = 10;
/// Bundan uzun metinler geçmişe alınmaz (ör. kopyalanan koca bir günlük dosyası).
const MAX_TEXT_BYTES: usize = 1 << 20;
/// Frontend'e gönderilen metin önizlemesinin uzunluğu (karakter).
const PREVIEW_CHARS: usize = 1500;
/// Görsel küçük resminin en büyük kenarı (piksel).
const THUMB_MAX: u32 = 360;

#[derive(Serialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum ClipKind {
    Text,
    Link,
    Image,
}

/// Frontend'e giden öğe.
#[derive(Serialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct ClipItem {
    pub id: u64,
    pub kind: ClipKind,
    /// Metin önizlemesi (en fazla `PREVIEW_CHARS` karakter).
    pub text: Option<String>,
    /// Metnin tam uzunluğu (karakter) ya da görselin PNG boyutu (bayt).
    pub size: usize,
    /// Görsel küçük resmi (data URL).
    pub thumb: Option<String>,
    pub width: u32,
    pub height: u32,
    /// Kopyalanma zamanı (Unix ms).
    pub time: u64,
    pub pinned: bool,
}

/// Kopyalanan içerik.
#[derive(Clone)]
enum Content {
    Text(String),
    /// Tam boy görsel, PNG olarak (bellekte daha az yer kaplar).
    Image { png: Vec<u8>, thumb: String, width: u32, height: u32 },
}

struct Entry {
    id: u64,
    hash: u64,
    content: Content,
    time: u64,
    pinned: bool,
}

impl Entry {
    fn to_item(&self) -> ClipItem {
        match &self.content {
            Content::Text(t) => ClipItem {
                id: self.id,
                kind: if is_link(t) { ClipKind::Link } else { ClipKind::Text },
                text: Some(t.chars().take(PREVIEW_CHARS).collect()),
                size: t.chars().count(),
                thumb: None,
                width: 0,
                height: 0,
                time: self.time,
                pinned: self.pinned,
            },
            Content::Image { png, thumb, width, height } => ClipItem {
                id: self.id,
                kind: ClipKind::Image,
                text: None,
                size: png.len(),
                thumb: Some(thumb.clone()),
                width: *width,
                height: *height,
                time: self.time,
                pinned: self.pinned,
            },
        }
    }

    fn is_image(&self) -> bool {
        matches!(self.content, Content::Image { .. })
    }
}

#[derive(Default)]
struct History {
    /// En yeni başta.
    entries: Vec<Entry>,
    next_id: u64,
}

impl History {
    /// Yeni içeriği başa ekler. Aynısı zaten varsa yenisini eklemez, var
    /// olanı başa taşır. Sınırları aşan en eski (sabitlenmemiş) öğeler silinir.
    fn push(&mut self, hash: u64, content: Content, time: u64) {
        if let Some(i) = self.entries.iter().position(|e| e.hash == hash) {
            let mut e = self.entries.remove(i);
            e.time = time;
            self.entries.insert(0, e);
            return;
        }
        self.next_id += 1;
        self.entries.insert(
            0,
            Entry { id: self.next_id, hash, content, time, pinned: false },
        );
        self.trim();
    }

    fn trim(&mut self) {
        let (mut items, mut images) = (0, 0);
        self.entries.retain(|e| {
            if e.pinned {
                return true;
            }
            items += 1;
            if e.is_image() {
                images += 1;
            }
            items <= MAX_ITEMS && (!e.is_image() || images <= MAX_IMAGES)
        });
    }

    /// Sabitlenenler önce, sonra en yeniden eskiye.
    fn list(&self) -> Vec<ClipItem> {
        let pinned = self.entries.iter().filter(|e| e.pinned);
        let rest = self.entries.iter().filter(|e| !e.pinned);
        pinned.chain(rest).map(Entry::to_item).collect()
    }

    fn touch(&mut self, id: u64, time: u64) -> Option<Content> {
        let i = self.entries.iter().position(|e| e.id == id)?;
        let mut e = self.entries.remove(i);
        e.time = time;
        let content = e.content.clone();
        self.entries.insert(0, e);
        Some(content)
    }

    fn remove(&mut self, id: u64) {
        self.entries.retain(|e| e.id != id);
    }

    fn toggle_pin(&mut self, id: u64) {
        if let Some(e) = self.entries.iter_mut().find(|e| e.id == id) {
            e.pinned = !e.pinned;
        }
        self.trim();
    }

    /// Sabitlenenler kalır.
    fn clear(&mut self) {
        self.entries.retain(|e| e.pinned);
    }
}

#[derive(Default)]
pub struct ClipStore {
    history: Mutex<History>,
    /// Kendi kopyalamamızdan sonraki pano sıra numarası; izleyici bunu atlar.
    own_seq: Mutex<Option<u32>>,
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

fn hash_of(parts: impl Hash) -> u64 {
    let mut h = DefaultHasher::new();
    parts.hash(&mut h);
    h.finish()
}

/// Tek satırlık, boşluksuz bir http(s) adresi mi?
fn is_link(text: &str) -> bool {
    let t = text.trim();
    (t.starts_with("http://") || t.starts_with("https://"))
        && !t.chars().any(char::is_whitespace)
        && t.len() > 8
}

/// Panodaki metni geçmiş içeriğine çevirir. Boş ya da çok uzunsa `None`.
fn text_content(text: String) -> Option<(u64, Content)> {
    if text.trim().is_empty() || text.len() > MAX_TEXT_BYTES {
        return None;
    }
    Some((hash_of(("text", &text)), Content::Text(text)))
}

/// Panodaki görseli (RGBA) PNG'ye ve küçük resme çevirir.
fn image_content(width: u32, height: u32, rgba: Vec<u8>) -> Option<(u64, Content)> {
    let hash = hash_of(("image", width, height, &rgba));
    let img = image::RgbaImage::from_raw(width, height, rgba)?;
    let img = image::DynamicImage::ImageRgba8(img);

    let mut png = Vec::new();
    img.write_to(&mut Cursor::new(&mut png), image::ImageFormat::Png).ok()?;

    let mut small = Vec::new();
    img.thumbnail(THUMB_MAX, THUMB_MAX)
        .write_to(&mut Cursor::new(&mut small), image::ImageFormat::Png)
        .ok()?;
    let thumb = format!(
        "data:image/png;base64,{}",
        base64::engine::general_purpose::STANDARD.encode(small)
    );
    Some((hash, Content::Image { png, thumb, width, height }))
}

/// İçeriği panoya geri koyar.
fn write_to_clipboard(content: &Content) -> Result<(), String> {
    let mut cb = arboard::Clipboard::new().map_err(|e| e.to_string())?;
    match content {
        Content::Text(t) => cb.set_text(t.as_str()).map_err(|e| e.to_string()),
        Content::Image { png, .. } => {
            let img = image::load_from_memory_with_format(png, image::ImageFormat::Png)
                .map_err(|e| e.to_string())?
                .into_rgba8();
            let (w, h) = img.dimensions();
            cb.set_image(arboard::ImageData {
                width: w as usize,
                height: h as usize,
                bytes: img.into_raw().into(),
            })
            .map_err(|e| e.to_string())
        }
    }
}

fn changed(app: &AppHandle) {
    let _ = app.emit("clipboard-changed", ());
}

/// Geçmişi siler (ayar kapatılınca; sabitlenenler de gider).
pub fn forget_all(app: &AppHandle) {
    app.state::<ClipStore>().history.lock().unwrap().entries.clear();
    changed(app);
}

#[tauri::command]
pub fn clipboard_list(store: State<'_, ClipStore>) -> Vec<ClipItem> {
    store.history.lock().unwrap().list()
}

/// Öğeyi panoya kopyalar ve listenin başına taşır.
#[tauri::command]
pub fn clipboard_copy(app: AppHandle, store: State<'_, ClipStore>, id: u64) -> Result<(), String> {
    let content = store
        .history
        .lock()
        .unwrap()
        .touch(id, now_ms())
        .ok_or("Öğe bulunamadı")?;
    write_to_clipboard(&content)?;
    #[cfg(windows)]
    {
        *store.own_seq.lock().unwrap() = Some(win::sequence_number());
    }
    changed(&app);
    Ok(())
}

#[tauri::command]
pub fn clipboard_delete(app: AppHandle, store: State<'_, ClipStore>, id: u64) {
    store.history.lock().unwrap().remove(id);
    changed(&app);
}

#[tauri::command]
pub fn clipboard_pin(app: AppHandle, store: State<'_, ClipStore>, id: u64) {
    store.history.lock().unwrap().toggle_pin(id);
    changed(&app);
}

#[tauri::command]
pub fn clipboard_clear(app: AppHandle, store: State<'_, ClipStore>) {
    store.history.lock().unwrap().clear();
    changed(&app);
}

#[cfg(windows)]
pub fn start_watcher(app: AppHandle) {
    use std::time::Duration;

    std::thread::spawn(move || {
        // Açılıştan önce panoda duran şeyi almıyoruz; geçmiş açılışla başlar.
        let mut last = win::sequence_number();
        loop {
            std::thread::sleep(Duration::from_millis(400));
            let seq = win::sequence_number();
            if seq == last {
                continue;
            }
            last = seq;

            let store = app.state::<ClipStore>();
            if store.own_seq.lock().unwrap().take() == Some(seq) {
                continue;
            }
            if !app.state::<crate::settings::SettingsStore>().get().clipboard {
                continue;
            }
            if win::is_private() {
                continue;
            }
            let Some((hash, content)) = read_clipboard() else { continue };
            store.history.lock().unwrap().push(hash, content, now_ms());
            changed(&app);
        }
    });
}

#[cfg(not(windows))]
pub fn start_watcher(_app: AppHandle) {}

/// Panodaki içeriği okur: önce metin, yoksa görsel. (Dosya listesi vb. alınmaz.)
#[cfg(windows)]
fn read_clipboard() -> Option<(u64, Content)> {
    let mut cb = arboard::Clipboard::new().ok()?;
    if let Ok(text) = cb.get_text() {
        return text_content(text);
    }
    let img = cb.get_image().ok()?;
    image_content(img.width as u32, img.height as u32, img.bytes.into_owned())
}

#[cfg(windows)]
mod win {
    use windows::Win32::Foundation::HGLOBAL;
    use windows::Win32::System::DataExchange::{
        CloseClipboard, GetClipboardData, GetClipboardSequenceNumber, IsClipboardFormatAvailable,
        OpenClipboard, RegisterClipboardFormatW,
    };
    use windows::Win32::System::Memory::{GlobalLock, GlobalSize, GlobalUnlock};
    use windows::core::w;

    pub fn sequence_number() -> u32 {
        unsafe { GetClipboardSequenceNumber() }
    }

    /// Kopyalayan uygulama içeriği izleyicilerden gizlemek istemiş mi?
    pub fn is_private() -> bool {
        unsafe {
            let exclude = RegisterClipboardFormatW(w!("ExcludeClipboardContentFromMonitorProcessing"));
            let ignore = RegisterClipboardFormatW(w!("Clipboard Viewer Ignore"));
            if IsClipboardFormatAvailable(exclude).is_ok() || IsClipboardFormatAvailable(ignore).is_ok() {
                return true;
            }
            let history = RegisterClipboardFormatW(w!("CanIncludeInClipboardHistory"));
            if IsClipboardFormatAvailable(history).is_err() {
                return false;
            }
            // Biçim var: değeri 0 ise geçmişe alınmamalı. Okuyamazsak gizli sayarız.
            read_dword(history).is_none_or(|v| v == 0)
        }
    }

    unsafe fn read_dword(format: u32) -> Option<u32> {
        unsafe {
            OpenClipboard(None).ok()?;
            let value = GetClipboardData(format).ok().and_then(|h| {
                let mem = HGLOBAL(h.0);
                if GlobalSize(mem) < 4 {
                    return None;
                }
                let ptr = GlobalLock(mem) as *const u32;
                if ptr.is_null() {
                    return None;
                }
                let v = ptr.read_unaligned();
                let _ = GlobalUnlock(mem);
                Some(v)
            });
            let _ = CloseClipboard();
            value
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn text(h: &mut History, s: &str, t: u64) {
        let (hash, c) = text_content(s.to_string()).unwrap();
        h.push(hash, c, t);
    }

    fn texts(h: &History) -> Vec<String> {
        h.list().into_iter().map(|i| i.text.unwrap_or_default()).collect()
    }

    #[test]
    fn ayni_metin_basa_tasinir() {
        let mut h = History::default();
        text(&mut h, "a", 1);
        text(&mut h, "b", 2);
        text(&mut h, "a", 3);
        assert_eq!(texts(&h), ["a", "b"]);
        assert_eq!(h.list()[0].time, 3);
    }

    #[test]
    fn sinir_asilinca_en_eski_silinir_sabitlenen_kalir() {
        let mut h = History::default();
        text(&mut h, "ilk", 0);
        let first = h.list()[0].id;
        h.toggle_pin(first);
        for i in 0..MAX_ITEMS + 5 {
            text(&mut h, &format!("m{i}"), i as u64 + 1);
        }
        let list = h.list();
        assert_eq!(list.len(), MAX_ITEMS + 1);
        assert_eq!(list[0].text.as_deref(), Some("ilk"));
        assert!(list[0].pinned);
        assert_eq!(list[1].text.as_deref(), Some(format!("m{}", MAX_ITEMS + 4).as_str()));
    }

    #[test]
    fn temizle_sabitlenenleri_birakir() {
        let mut h = History::default();
        text(&mut h, "a", 1);
        text(&mut h, "b", 2);
        let a = h.list().iter().find(|i| i.text.as_deref() == Some("a")).unwrap().id;
        h.toggle_pin(a);
        h.clear();
        assert_eq!(texts(&h), ["a"]);
    }

    #[test]
    fn bos_ve_cok_uzun_metin_alinmaz() {
        assert!(text_content("  \n".into()).is_none());
        assert!(text_content("x".repeat(MAX_TEXT_BYTES + 1)).is_none());
    }

    #[test]
    fn baglanti_tanima() {
        assert!(is_link("https://github.com/halililgez/halisland"));
        assert!(is_link("  http://ornek.com/a?b=1 \n"));
        assert!(!is_link("https://a.com ve sonra metin"));
        assert!(!is_link("ftp://a.com"));
    }

    #[test]
    fn gorsel_kucuk_resim_uretir() {
        let rgba = vec![200u8; 800 * 400 * 4];
        let (_, c) = image_content(800, 400, rgba).unwrap();
        let mut h = History::default();
        h.push(1, c, 1);
        let item = &h.list()[0];
        assert_eq!(item.kind, ClipKind::Image);
        assert_eq!((item.width, item.height), (800, 400));
        assert!(item.thumb.as_deref().unwrap().starts_with("data:image/png;base64,"));
    }

    #[test]
    fn gorsel_siniri() {
        let mut h = History::default();
        for i in 0..MAX_IMAGES + 3 {
            let (_, c) = image_content(2, 2, vec![i as u8; 16]).unwrap();
            h.push(i as u64 + 100, c, i as u64);
        }
        assert_eq!(h.list().len(), MAX_IMAGES);
    }
}
