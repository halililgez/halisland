# Halisland: İlerleme Notları

Bu dosya geliştirmenin nerede kaldığını tutar. Her oturum işe buradan başlar, iş bitince burayı günceller.

## Çalışma kuralları

- Her faz ayrı bir branch (`faz-N-...`) ve ayrı bir PR olur. PR'lar squash ile merge edilir ve branch silinir.
- Commit'ler yalnızca Halil adına atılır (`Halil İlgez <65055768+halililgez@users.noreply.github.com>`). Co-author satırı eklenmez.
- Merge'den önce `npm run build` (tip kontrolü) ve `cargo build` (src-tauri) hatasız geçmelidir.
- Masaüstü: `npm run tauri dev`. Ada, adanın boyutundaki şeffaf bir penceredir. Açılırken önce pencere büyür, kapanırken ada küçüldükten sonra pencere küçülür (`src/composables/useIsland.ts`). Boyutlar `COMPACT` / `EXPANDED` sabitlerinde tutulur.
- CI (`.github/workflows/ci.yml`): her PR'da Windows ve macOS üzerinde `npm run build` + `cargo clippy -D warnings`. Windows'a özgü kod (SMTC) bu oturumun Linux ortamında derlenemiyor; doğrulaması CI'dadır, PR yeşil olmadan merge edilmez.
- Müzik `src-tauri/src/media.rs` (Windows: `windows` crate 0.62, async çağrılar `.join()` ile beklenir). Olaylar: `media-update` (saniyede bir), `media-artwork` (şarkı değişince, data URL). Kapak rengi `src/utils/color.ts`.
- Cam efekti `src-tauri/src/glass.rs` + `src/composables/useGlass.ts`. Windows'ta `SetWindowCompositionAttribute` (acrylic/blur) kullanılır; Windows 11'in Mica/Acrylic backdrop API'si pencere odakta değilken düz renge döndüğü için uygun değil. Pencere, frontend'in her karede bildirdiği ada şekline `SetWindowRgn` ile kırpılır. Bölge verilen pencerede Windows eski başlık çubuğunu çizdiği için çerçeve stilleri (WS_CAPTION vb.) kaldırılır. Cam açıkken `<html>` `glass` sınıfını alır, ada arka planı daha saydam olur.
- Claude Code durumu `127.0.0.1:47821` üzerinden gelir (`src-tauri/src/claude.rs`). Hook ayarları README'de.

## Fazlar

| Faz | İçerik | Durum |
| --- | --- | --- |
| 0 | İskelet: şeffaf ada penceresi, aç/kapa, sürükleme, saat, odak sayacı, Claude Code durumu | ✅ PR merge edildi |
| 1 | Müzik: Windows SMTC ile çalan şarkı, kapak, oynat/duraklat/ileri; ada kapak rengini alır | ✅ PR merge edildi |
| 2 | Gerçek cam: Windows'ta acrylic/blur (bulanık arka plan) | ✅ Windows (macOS vibrancy Faz 3'e kaldı) |
| 3 | macOS: "şu an çalan" bilgisi (macOS 15.4+ kısıtlaması için geçici çözüm) ve vibrancy | ⏳ sonraya bırakıldı |
| 4 | Claude Code genişletilmiş görünüm: son istem, değişen dosyalar, tek tıkla terminale dön | ⏳ |
| 5 | Ayarlar: konum hatırlama, açılışta başlatma, odak süresi, tema; tepsi (tray) menüsü | ⏳ |
| 6 | Dağıtım: Windows NSIS kurulum, macOS .dmg, GitHub Actions ile derleme | ⏳ |

## Günlük

- **2026-11-17 ~02:15:** Proje açıldı. Teknoloji: Tauri 2 + Vue 3 + TypeScript. Faz 0 iskeleti yazıldı: şeffaf, kenarlıksız, her zaman üstte ada penceresi (ekranın üst ortası), hover ile aç/kapa, `data-tauri-drag-region` ile sürükleme, saat/tarih, 25 dk odak sayacı, Claude Code durum kutucuğu (working/waiting/done/seen) ve `tiny_http` ile hook sunucusu. `npm run build` ve `cargo check` temiz. Henüz gerçek Windows/macOS üzerinde elle denenmedi. Sıradaki: Faz 1 (müzik, Windows).
- **2026-11-17 ~02:20:** PR #1 (Faz 0) merge edildi. Halil Windows'ta `npm run tauri dev` ile denedi, her şey çalışıyor.
- **2026-11-17 ~02:45:** Faz 1 bitti: `media.rs` (SMTC: başlık, sanatçı, kaynak uygulama, ilerleme, kapak; `media_control` komutu ile oynat/duraklat/ileri/geri; macOS'ta şimdilik boş), `useMedia` (ilerlemeyi arada yürütür, iyimser oynat/duraklat), `MusicTile`, kompakt adada mini kapak + ekolayzır, kapak renginden `--accent` (ada ve kutucuk renk alır). Genişletilmiş ada 720x200. GitHub Actions CI eklendi. Örnek verilerle tarayıcıda görüntü kontrol edildi; gerçek SMTC Windows'ta denenecek. Sıradaki: Faz 2 (gerçek cam efekti).
- **2026-11-17 ~03:00:** Faz 2 (Windows) bitti: `glass.rs` acrylic bulanıklık + pencereyi adanın yuvarlak şekline kırpma (`set_glass`, `set_island_shape`), `useGlass` ResizeObserver ile animasyon boyunca şekli Rust'a iletir. Halil'in makinesinde `tauri dev` ile denendi: arkadaki renkli test penceresi bulanık görünüyor, köşeler yuvarlak, başlık çubuğu yok. Ayrıca Opera gibi `Opera.1674602008` kimlikli kaynaklarda müzik kutucuğu sayı yerine "Opera" gösteriyor. Not: acrylic bazı Windows sürümlerinde sürüklerken takılabilir; Faz 5'te ayarlardan "blur" ya da "kapalı" seçilebilecek.
