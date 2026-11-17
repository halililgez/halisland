# Halisland

Windows ve macOS için masaüstü **Dynamic Island**. Ekranın üst ortasında duran, üzerine gelince açılan cam görünümlü bir ada: saat, odak sayacı, Claude Code durumu ve çalan müzik.

> Durum: v0.2.0, Windows. macOS desteği sürüyor. Fazlar için `docs/ILERLEME.md`.

## Özellikler

- **Ada:** Şeffaf, her zaman üstte, görev çubuğunda görünmez. Üzerine gelince açılır, ayrılınca kapanır. Tutup sürükleyebilirsin.
- **Gerçek cam (Windows):** Adanın arkasındaki masaüstü bulanık görünür. Windows 11'de Windows.UI.Composition ile oval kırpılmış bulanıklık, Windows 10'da acrylic. macOS'ta şimdilik koyu yarı saydam arka plan.
- **Odak sayacı:** 25 dakikalık sayaç (başlat / duraklat / sıfırla). Çalışırken kompakt adada kalan süre görünür.
- **Claude Code:** Claude çalışırken kutucuk turuncu parlar, onay beklerken sarıya döner. İş bitince mavi olur, sen adaya bakınca yeşile döner ve söner. Açık adada proje adı, son istemin, şu an ne yaptığı (komut çalıştırıyor, dosya düzenliyor...), neden beklediği ve değişen dosyalar görünür. **Terminale dön** düğmesi Claude'un çalıştığı pencereyi (Windows Terminal, VS Code, Claude masaüstü...) öne getirir.
- **Müzik (Windows):** Spotify, tarayıcıda YouTube ya da sistem medya kontrollerine kayıt olan her uygulamada çalan şarkı, kapak ve ilerleme. Oynat/duraklat, ileri ve geri. Ada, kapağın rengini alır. macOS desteği sonraki fazlarda.
- **Asistan (Windows, isteğe bağlı):** Varsayılan olarak kapalıdır; ayarlardaki (ya da tepsi menüsündeki) **Asistan** anahtarıyla açılır. Kapalıyken sekmesi görünmez ve arka planda hiçbir şey çalışmaz. Bilgisayarında çalışan yerel bir dil modeliyle ([Ollama](https://ollama.com), varsayılan `qwen3:8b`) sohbet. Cevaplar yazıldıkça akar; **Düşün** anahtarı açıkken model önce düşünür (daha dikkatli ama daha yavaş), düşünceyi sonradan açıp okuyabilirsin. Enter gönderir, Esc durdurur. Sohbet bu sekmede ada aşağı doğru uzar. Sohbetler kaydedilir: **Geçmiş**'ten eski bir sohbeti açıp sürdürebilir ya da silebilirsin, **Yeni** boş bir sohbet açar. "Adım Halil, bunu hatırla" dersen asistan bunu sonraki sohbetlerde de bilir; hatırladıklarını **Hafıza**'da görüp silebilirsin. Uzun sohbetlerin eski kısmı otomatik özetlenir. Her şey bilgisayarındaki `%APPDATA%\com.halil.halisland\assistant.db` dosyasında durur. Hiçbir şey internete gitmez. Ollama'nın kurulu ve açık olması gerekir. Yerel model güçlü bir ekran kartı ister: Asistan sekmesi ekran kartına bakıp model önerir (8 GB ve üstü: `qwen3:8b`, daha azı: `qwen3.5:2b`), Ollama'da kurulu modeller arasından tek tıkla geçilir.
- **Sekmeler:** Açık adanın üstündeki şeritten **Ana**, **Asistan** (açıksa), **Bugün**, **Kontrol**, **Pano**, **Sistem** ve **Güç** arasında geçilir. Ada kapanınca bir sonraki açılış Ana sekmeyle başlar.
- **Kontrol (Windows):** Ses seviyesi kaydırıcısı (fare tekerleğiyle de), sessiz düğmesi, 0/25/50/75/100 hızlı seviyeler; çıkış aygıtının adına tıklayıp başka bir aygıta (kulaklık, hoparlör, monitör) geçme. Ekran başına parlaklık (harici monitörlerde DDC/CI, dizüstünde Windows'un kendi ayarı) ve gece ışığı. Wi-Fi ve Bluetooth'u aç/kapa, bağlı ağın adı. Klavyenin ses tuşlarıyla yapılan değişiklikler de görünür.
- **Hız:** İnternet hız testi: gecikme (ping), titreşim, indirme ve yükleme. Ölçüm Cloudflare'in hız testi sunucularıyla yapılır (anahtar gerekmez), yalnızca **Başlat**'a basınca çalışır ve ~15 saniye sürer; bağlantı hızına göre birkaç yüz MB'a kadar veri kullanabilir. Ada kapanınca test sürer, kompakt adada canlı hız görünür. Son 5 sonuç bu bilgisayarda saklanır.
- **Bugün:** Tarih ve bu haftanın günleri; hava durumu (anlık sıcaklık, durum, en yüksek/en düşük; Open-Meteo, anahtar gerekmez). Konum için **Konumumu kullan** (Windows konum servisi; yaklaşık 1 km'ye yuvarlanır, hava her yenilendiğinde konum da tazelenir) ya da şehir araması; halkalı odak sayacı ve 15/25/45/60 dakika seçici; günün yapılacakları (tamamlananlar ertesi gün kendiliğinden temizlenir).
- **Pano geçmişi (Windows):** Kopyaladığın metinler, bağlantılar ve görseller Pano sekmesinde birikir. Arama ve Metin / Bağlantı / Görsel filtreleri var. Satıra tıklayınca yeniden panoya kopyalanır. Öğeleri sabitleyebilir, silebilir ya da hepsini temizleyebilirsin. Geçmiş yalnızca bellekte tutulur, diske yazılmaz; uygulama kapanınca gider. Parola yöneticilerinin "izleme" dışı bıraktığı kopyalamalar alınmaz. Ayarlardan "Pano geçmişi" kapatılınca geçmiş silinir.
- **Sistem:** İşlemci ve bellek halkaları, en çok bellek kullanan dört uygulama (aynı uygulamanın süreçleri tek satırda toplanır), sistem diskinin doluluğu, anlık indirme/yükleme hızı ve bilgisayarın açık kalma süresi. Ölçüm yalnızca sekme açıkken, iki saniyede bir yapılır.
- **Güç:** Kilitle, uyku, oturumu kapat, yeniden başlat, kapat. Son üçü geri alınamadığı için önce sayfanın üstünde bir onay şeridi açar ("Bilgisayar kapatılsın mı?" [Vazgeç] [Kapat]); Esc ya da sekmeden çıkmak iptal eder.
- **Ayarlar:** Adaya sağ tıkla, açık adadaki dişliye bas ya da görev çubuğundaki Halisland simgesine sağ tıkla (Windows 11 bu simgeyi `^` alanına saklayabilir): adayı gizle/göster, adanın yapışacağı kenar (yön tuşlarıyla üst / alt / sol / sağ; sağda ve solda ada dikey olur, hep ekranın içine doğru açılır), konumu sıfırla, odak süresi, cam efekti (buzlu / sade / kapalı), kapak rengi, Claude Code'u bağla, pano geçmişi, oturum açılınca başlat, çıkış. Adayı sürüklediğin yer hatırlanır.

## Kurulum (Windows)

[Releases](https://github.com/halililgez/halisland/releases) sayfasından `Halisland_<sürüm>_x64-setup.exe` dosyasını indirip çalıştır. Yönetici izni istemez, yalnızca senin kullanıcına kurulur.

Yeni sürüm çıkarmak için `src-tauri/tauri.conf.json`, `src-tauri/Cargo.toml` ve `package.json` içindeki sürümü artır, sonra:

```bash
git tag v0.2.0
git push origin v0.2.0
```

GitHub Actions kurulum dosyasını derleyip **taslak** bir release oluşturur; GitHub'da kontrol edip yayınlarsın.

## Geliştirme

Gerekenler: [Node.js](https://nodejs.org) 20+, [Rust](https://rustup.rs), [.NET 10 SDK](https://dotnet.microsoft.com/download) (asistan) ve Tauri'nin [platform gereksinimleri](https://tauri.app/start/prerequisites/).

`npm run tauri dev` asistan backend'ini (`assistant/`) `dotnet run` ile kendisi başlatır; ilk açılışta derlendiği için sekme birkaç saniye "Asistan açılıyor…" der.

```bash
npm install
npm run tauri dev
```

Derleme:

```bash
npm run build                      # tip kontrolü + frontend
cd src-tauri && cargo build        # Rust tarafı
dotnet build assistant             # asistan backend'i
```

Kurulum dosyası asistanı da içerir; önce backend yayınlanır:

```bash
dotnet publish assistant -c Release -r win-x64 -o src-tauri/assistant-bin
npm run tauri build -- --bundles nsis --config src-tauri/tauri.release.conf.json
```

## Claude Code bağlantısı

Halisland, `127.0.0.1:47821` adresinde küçük bir yerel sunucu açar. Claude Code hook'ları her olayın JSON'unu buraya gönderir.

**Kolay yol:** Adaya sağ tıkla (ya da açık adadaki dişliye bas) → **Claude Code'u bağla**. Halisland hook'ları `~/.claude/settings.json`'a kendisi ekler. Dosyadaki diğer ayarlara dokunmaz, önceki hâlini `settings.json.halisland.bak` olarak yedekler. Aynı maddeye tekrar basınca hook'lar kaldırılır. Yeni açılan Claude Code oturumları bağlanır.

Elle kurmak istersen `~/.claude/settings.json` dosyana şunu ekle:

```json
{
  "hooks": {
    "SessionStart": [{ "hooks": [{ "type": "command", "command": "curl -s -m 1 --data-binary @- http://127.0.0.1:47821/claude/hook" }] }],
    "UserPromptSubmit": [{ "hooks": [{ "type": "command", "command": "curl -s -m 1 --data-binary @- http://127.0.0.1:47821/claude/hook" }] }],
    "PreToolUse": [{ "hooks": [{ "type": "command", "command": "curl -s -m 1 --data-binary @- http://127.0.0.1:47821/claude/hook" }] }],
    "PostToolUse": [{ "hooks": [{ "type": "command", "command": "curl -s -m 1 --data-binary @- http://127.0.0.1:47821/claude/hook" }] }],
    "Notification": [{ "hooks": [{ "type": "command", "command": "curl -s -m 1 --data-binary @- http://127.0.0.1:47821/claude/hook" }] }],
    "Stop": [{ "hooks": [{ "type": "command", "command": "curl -s -m 1 --data-binary @- http://127.0.0.1:47821/claude/hook" }] }],
    "SessionEnd": [{ "hooks": [{ "type": "command", "command": "curl -s -m 1 --data-binary @- http://127.0.0.1:47821/claude/hook" }] }]
  }
}
```

Eski kurulum (`POST /claude/working`, `/claude/waiting`, `/claude/done`, `/claude/idle`, gövdesiz) da çalışmaya devam eder, ama istem ve dosya bilgisi gelmez.

Elle denemek için Halisland açıkken bir terminalde:

```bash
curl -X POST http://127.0.0.1:47821/claude/working
curl -X POST http://127.0.0.1:47821/claude/done
```

## Proje yapısı

```
src/
  App.vue                  ada: kompakt ve genişletilmiş görünüm
  components/
    Tile.vue               parlayan kutucuk
    MusicTile.vue          müzik kutucuğu
    ClaudeTile.vue         Claude Code kutucuğu (istem, dosyalar, terminale dön)
    SettingsPanel.vue      ada içindeki ayar sayfası
    ControlsPanel.vue      Kontrol sekmesi (ses, Wi-Fi, Bluetooth)
    TodayPanel.vue         Bugün sekmesi (tarih, hava durumu, halkalı sayaç, yapılacaklar)
    WeatherGlyph.vue       hava durumu simgeleri
    AssistantPanel.vue     Asistan sekmesi (sohbet, Geçmiş, Hafıza, düşünme anahtarı)
    ClipboardPanel.vue     Pano sekmesi (liste, arama, önizleme)
    SystemPanel.vue        Sistem sekmesi (Ring.vue: yüzde halkası)
    PowerPanel.vue         Güç sekmesi (onaylı düğmeler)
  composables/
    useIsland.ts           aç/kapa + pencere boyutlandırma
    useGlass.ts            cam efekti + pencereyi ada şekline kırpma
    useClock.ts            saat ve tarih
    useFocusTimer.ts       odak sayacı
    useClaudeStatus.ts     Claude Code durum makinesi
    useSettings.ts         ayarları Rust'tan okur
    useMedia.ts            çalan müzik ve kontroller
    useSystemStats.ts      sistem ölçümleri (sekme açıkken)
    useClipboard.ts        pano geçmişi listesi ve komutları
    useControls.ts         ses, çıkış aygıtı ve radyo durumu
    useDisplay.ts          parlaklık ve gece ışığı
    useWeather.ts          hava durumu ve şehir arama (Open-Meteo)
    useTodos.ts            yapılacaklar (localStorage)
    useAssistant.ts        asistan backend'iyle sohbet (akış, durdurma, geçmiş, hafıza)
  utils/color.ts           kapaktan vurgu rengi
src-tauri/src/
  lib.rs                   uygulama kurulumu
  island.rs                pencere konumu, kenar ve boyutu (resize_island komutu)
  glass.rs                 cam efekti (Windows 10 acrylic, pencere bölgesi, başlık şeridi koruması)
  backdrop.rs              Windows 11 oval bulanıklık (Windows.UI.Composition)
  settings.rs              ayarlar (settings.json), konum hatırlama, açılışta başlatma
  tray.rs                  tepsi simgesi ve menüsü (sağ tık menüsü de bu)
  hooks.rs                 Claude Code hook'larını ~/.claude/settings.json'a kurar/kaldırır
  claude.rs                Claude Code hook sunucusu (port 47821), oturum durumu
  terminal.rs              Claude'un çalıştığı pencereyi bulma ve öne getirme
  media.rs                 çalan müzik (Windows SMTC)
  clipboard.rs             pano geçmişi (bellekte; Windows izleyicisi)
  controls.rs              ses ve çıkış aygıtı (Core Audio, IPolicyConfig), Wi-Fi/Bluetooth, bağlı ağ
  display.rs               parlaklık (DDC/CI, WMI) ve gece ışığı
  location.rs              cihaz konumu (Windows konum servisi, ~1 km)
  system.rs                işlemci, bellek, uygulamalar, disk, ağ (sysinfo)
  power.rs                 kilitle, uyku, oturumu kapat, yeniden başlat, kapat
  assistant.rs             asistan backend'ini başlatır/durdurur, anahtarı arayüze verir
assistant/                 asistan backend'i (.NET 10, port 47822): Ollama ile akışlı sohbet,
                           SQLite hafıza (Store.cs), hatırlama aracı (Tools.cs), başlık ve özet (Chat.cs)
docs/ILERLEME.md           fazlar ve geliştirme günlüğü
```

## Katkı

Hata bildirimleri, fikirler ve pull request'ler açık. Başlamadan önce [CONTRIBUTING.md](CONTRIBUTING.md)'ye göz at. Güvenlik açıkları için [SECURITY.md](SECURITY.md).

## Lisans

[MIT](LICENSE) © 2026 Halil İlgez
