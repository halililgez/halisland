# Halisland

Windows ve macOS için masaüstü **Dynamic Island**. Ekranın üst ortasında duran, üzerine gelince açılan cam görünümlü bir ada: saat, odak sayacı, Claude Code durumu ve (yakında) çalan müzik.

> Durum: erken geliştirme (Faz 0, iskelet).

## Özellikler

- **Ada:** Şeffaf, her zaman üstte, görev çubuğunda görünmez. Üzerine gelince açılır, ayrılınca kapanır. Tutup sürükleyebilirsin.
- **Odak sayacı:** 25 dakikalık sayaç (başlat / duraklat / sıfırla). Çalışırken kompakt adada kalan süre görünür.
- **Claude Code durumu:** Claude çalışırken kutucuk turuncu parlar, onay beklerken sarıya döner. İş bitince mavi olur, sen adaya bakınca yeşile döner ve söner.
- **Müzik:** Sıradaki faz.

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

Halisland, `127.0.0.1:47821` adresinde küçük bir yerel sunucu açar. Claude Code hook'ları buraya durum gönderir. `~/.claude/settings.json` dosyana şunu ekle:

```json
{
  "hooks": {
    "UserPromptSubmit": [
      { "hooks": [{ "type": "command", "command": "curl -s -m 1 -X POST http://127.0.0.1:47821/claude/working" }] }
    ],
    "PreToolUse": [
      { "hooks": [{ "type": "command", "command": "curl -s -m 1 -X POST http://127.0.0.1:47821/claude/working" }] }
    ],
    "Notification": [
      { "hooks": [{ "type": "command", "command": "curl -s -m 1 -X POST http://127.0.0.1:47821/claude/waiting" }] }
    ],
    "Stop": [
      { "hooks": [{ "type": "command", "command": "curl -s -m 1 -X POST http://127.0.0.1:47821/claude/done" }] }
    ]
  }
}
```

Elle denemek için Halisland açıkken bir terminalde:

```bash
curl -X POST http://127.0.0.1:47821/claude/working
curl -X POST http://127.0.0.1:47821/claude/done
```

## Proje yapısı

```
src/
  App.vue                  ada: kompakt ve genişletilmiş görünüm
  components/Tile.vue      parlayan kutucuk
  composables/
    useIsland.ts           aç/kapa + pencere boyutlandırma
    useClock.ts            saat ve tarih
    useFocusTimer.ts       odak sayacı
    useClaudeStatus.ts     Claude Code durum makinesi
src-tauri/src/
  lib.rs                   uygulama kurulumu
  island.rs                pencere konumu ve boyutu (resize_island komutu)
  claude.rs                Claude Code hook sunucusu (port 47821)
docs/ILERLEME.md           fazlar ve geliştirme günlüğü
```
