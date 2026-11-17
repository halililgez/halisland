# Halisland

Windows ve macOS için masaüstü **Dynamic Island**. Ekranın üst ortasında duran, üzerine gelince açılan cam görünümlü bir ada: saat, odak sayacı, Claude Code durumu ve çalan müzik.

> Durum: erken geliştirme. Fazlar için `docs/ILERLEME.md`.

## Özellikler

- **Ada:** Şeffaf, her zaman üstte, görev çubuğunda görünmez. Üzerine gelince açılır, ayrılınca kapanır. Tutup sürükleyebilirsin.
- **Gerçek cam (Windows):** Adanın arkasındaki masaüstü bulanık görünür (acrylic). macOS'ta şimdilik koyu yarı saydam arka plan.
- **Odak sayacı:** 25 dakikalık sayaç (başlat / duraklat / sıfırla). Çalışırken kompakt adada kalan süre görünür.
- **Claude Code:** Claude çalışırken kutucuk turuncu parlar, onay beklerken sarıya döner. İş bitince mavi olur, sen adaya bakınca yeşile döner ve söner. Açık adada proje adı, son istemin, şu an ne yaptığı (komut çalıştırıyor, dosya düzenliyor...), neden beklediği ve değişen dosyalar görünür. **Terminale dön** düğmesi Claude'un çalıştığı pencereyi (Windows Terminal, VS Code, Claude masaüstü...) öne getirir.
- **Müzik (Windows):** Spotify, tarayıcıda YouTube ya da sistem medya kontrollerine kayıt olan her uygulamada çalan şarkı, kapak ve ilerleme. Oynat/duraklat, ileri ve geri. Ada, kapağın rengini alır. macOS desteği sonraki fazlarda.

## Geliştirme

Gerekenler: [Node.js](https://nodejs.org) 20+, [Rust](https://rustup.rs) ve Tauri'nin [platform gereksinimleri](https://tauri.app/start/prerequisites/).

```bash
npm install
npm run tauri dev
```

Derleme:

```bash
npm run build                      # tip kontrolü + frontend
cd src-tauri && cargo build        # Rust tarafı
npm run tauri build                # kurulum dosyası
```

## Claude Code bağlantısı

Halisland, `127.0.0.1:47821` adresinde küçük bir yerel sunucu açar. Claude Code hook'ları her olayın JSON'unu buraya gönderir. `~/.claude/settings.json` dosyana şunu ekle:

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
  composables/
    useIsland.ts           aç/kapa + pencere boyutlandırma
    useGlass.ts            cam efekti + pencereyi ada şekline kırpma
    useClock.ts            saat ve tarih
    useFocusTimer.ts       odak sayacı
    useClaudeStatus.ts     Claude Code durum makinesi
    useMedia.ts            çalan müzik ve kontroller
  utils/color.ts           kapaktan vurgu rengi
src-tauri/src/
  lib.rs                   uygulama kurulumu
  island.rs                pencere konumu ve boyutu (resize_island komutu)
  glass.rs                 cam efekti (Windows acrylic + SetWindowRgn)
  claude.rs                Claude Code hook sunucusu (port 47821), oturum durumu
  terminal.rs              Claude'un çalıştığı pencereyi bulma ve öne getirme
  media.rs                 çalan müzik (Windows SMTC)
docs/ILERLEME.md           fazlar ve geliştirme günlüğü
```
