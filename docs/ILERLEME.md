# Halisland: İlerleme Notları

Bu dosya geliştirmenin nerede kaldığını tutar. Her oturum işe buradan başlar, iş bitince burayı günceller.

## Çalışma kuralları

- Her faz ayrı bir branch (`faz-N-...`) ve ayrı bir PR olur. PR'lar squash ile merge edilir ve branch silinir.
- Commit'ler yalnızca Halil adına atılır (`Halil İlgez <65055768+halililgez@users.noreply.github.com>`). Co-author satırı eklenmez.
- Merge'den önce `npm run build` (tip kontrolü) ve `cargo build` (src-tauri) hatasız geçmelidir.
- Masaüstü: `npm run tauri dev`. Ada, adanın boyutundaki şeffaf bir penceredir. Açılırken önce pencere büyür, kapanırken ada küçüldükten sonra pencere küçülür (`src/composables/useIsland.ts`). Boyutlar `COMPACT` / `EXPANDED` sabitlerinde tutulur.
- Claude Code durumu `127.0.0.1:47821` üzerinden gelir (`src-tauri/src/claude.rs`). Hook ayarları README'de.

## Fazlar

| Faz | İçerik | Durum |
| --- | --- | --- |
| 0 | İskelet: şeffaf ada penceresi, aç/kapa, sürükleme, saat, odak sayacı, Claude Code durumu | 🔄 PR |
| 1 | Müzik: Windows SMTC ile çalan şarkı, kapak, oynat/duraklat/ileri; ada kapak rengini alır | ⏳ |
| 2 | Gerçek cam: Windows'ta acrylic/mica, macOS'ta vibrancy (bulanık arka plan) | ⏳ |
| 3 | macOS müzik: "şu an çalan" bilgisi (macOS 15.4+ kısıtlaması için geçici çözüm) | ⏳ |
| 4 | Claude Code genişletilmiş görünüm: son istem, değişen dosyalar, tek tıkla terminale dön | ⏳ |
| 5 | Ayarlar: konum hatırlama, açılışta başlatma, odak süresi, tema; tepsi (tray) menüsü | ⏳ |
| 6 | Dağıtım: Windows NSIS kurulum, macOS .dmg, GitHub Actions ile derleme | ⏳ |

## Günlük

- **2026-11-17 ~02:15:** Proje açıldı. Teknoloji: Tauri 2 + Vue 3 + TypeScript. Faz 0 iskeleti yazıldı: şeffaf, kenarlıksız, her zaman üstte ada penceresi (ekranın üst ortası), hover ile aç/kapa, `data-tauri-drag-region` ile sürükleme, saat/tarih, 25 dk odak sayacı, Claude Code durum kutucuğu (working/waiting/done/seen) ve `tiny_http` ile hook sunucusu. `npm run build` ve `cargo check` temiz. Henüz gerçek Windows/macOS üzerinde elle denenmedi. Sıradaki: Faz 1 (müzik, Windows).
