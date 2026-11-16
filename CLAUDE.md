# Halisland: Claude için çalışma kuralları

Halisland, Windows + macOS için masaüstü Dynamic Island uygulaması. Halil ile birlikte yazıyoruz.

## Proje

- Repo: github.com/halililgez/halisland (public).
- Teknoloji: Tauri 2 + Vue 3 + TypeScript. Rust tarafında `windows` crate (SMTC) ve `tiny_http`.
- Yerel klasör: `Desktop\claude projects\Halisland` (Windows). Halil uygulamayı orada `git pull; npm run tauri dev` ile çalıştırır.

## Her oturumda

- İşe `docs/ILERLEME.md`'yi okuyarak başla. Fazlar, kurallar ve günlük orada.
- İş bitince günlüğü ve faz tablosunu güncelle.

## Git ve PR

- Her faz ayrı bir branch (`faz-N-...`) ve ayrı bir PR olur. PR'lar squash ile merge edilir, branch silinir.
- Commit'ler yalnızca `Halil İlgez <65055768+halililgez@users.noreply.github.com>` adına atılır:
  ```bash
  git config user.name "Halil İlgez"
  git config user.email "65055768+halililgez@users.noreply.github.com"
  ```
- Commit mesajlarına ve PR açıklamalarına `Co-Authored-By` satırı, Claude imzası ya da "Generated with Claude" notu **eklenmez**. Contributors listesinde yalnızca Halil görünmeli.
- Commit ve PR başlıkları Türkçe.

## Kontroller

- Merge öncesi şunlar temiz geçmeli:
  ```bash
  npm run build
  cd src-tauri && cargo clippy --all-targets -- -D warnings
  ```
- PR'daki GitHub Actions CI (Windows + macOS) yeşil olmadan merge etme.
- Windows'a özgü Rust kodu (`#[cfg(windows)]`, ör. `src-tauri/src/media.rs`) Linux bulut ortamında derlenemez. Bu kodun doğrulaması CI'da yapılır.

## İletişim

- Türkçe, samimi ve iş arkadaşı tonunda konuş.
- Halil .NET/C# ağırlıklı çalışıyor ve Vue biliyor. Rust tarafını gerektiğinde kısaca açıkla.
