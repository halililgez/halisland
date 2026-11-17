# Katkı rehberi

Halisland'e katkı verdiğin için teşekkürler! Hata bildirimi, fikir ya da kod, hepsi işe yarar.

> **English:** Contributions are welcome. Open an issue first for bigger changes, then send a pull request from your fork. Issues and PRs in English are fine.

## Nasıl çalışıyoruz

- `main` korumalıdır; değişiklikler yalnızca pull request ile girer.
- Her PR'ı proje sahibi (@halililgez) inceler ve onaylar. Onay ve yeşil CI olmadan merge olmaz.
- PR'lar **squash** ile merge edilir, yani PR başlığı commit mesajı olur. Başlığı kısa ve açıklayıcı tut.
- Büyük bir değişiklikten (yeni özellik, mimari değişiklik) önce bir issue açıp konuşalım; boşa emek gitmesin.

## Adımlar

1. Repoyu fork'la ve bir branch aç (`duzeltme/muzik-kapagi`, `ozellik/pil-gostergesi` gibi).
2. Geliştirme ortamını kur (README'deki **Geliştirme** bölümü):
   ```bash
   npm install
   npm run tauri dev
   ```
3. Değişikliğini yap. Göndermeden önce şunlar temiz geçmeli:
   ```bash
   npm run build
   cd src-tauri && cargo clippy --all-targets -- -D warnings
   cd src-tauri && cargo test
   ```
4. PR aç ve şablondaki soruları doldur: ne değişti, nasıl denedin, hangi işletim sisteminde.

## Notlar

- **CI:** Her PR Windows ve macOS'ta derlenir. İlk katkında GitHub Actions'ın çalışması için onay gerekebilir; bu normal.
- **Platforma özgü kod:** Windows kodu `#[cfg(windows)]` altında (`media.rs`, `glass.rs`, `backdrop.rs`, `terminal.rs`). macOS desteği henüz eksik; o taraftaki katkılar özellikle değerli.
- **Görsel değişiklikler:** Önce/sonra ekran görüntüsü ya da kısa bir kayıt eklersen inceleme çok hızlanır.
- **Dil:** Arayüz metinleri Türkçe. Kod tanımlayıcıları İngilizce.

## Lisans

Gönderdiğin katkılar projenin [MIT lisansı](LICENSE) altında yayınlanır.
