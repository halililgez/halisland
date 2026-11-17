//! Bulut sağlayıcılarının API anahtarları (Faz 24).
//!
//! Anahtarlar Windows Kimlik Bilgileri Yöneticisi'nde (Credential Manager,
//! "Genel kimlik bilgileri") `Halisland/assistant/<sağlayıcı>` adıyla durur;
//! settings.json'a ya da tarayıcı deposuna hiç yazılmaz. Arayüz anahtarı
//! yalnızca kaydederken gönderir, geri okuyamaz: `has_key` yalnızca var mı
//! yok mu söyler. Anahtar backend'e başlarken ortam değişkeniyle verilir.

/// Anahtar isteyen sağlayıcılar (Ollama yerel, anahtarsız).
pub const CLOUD_PROVIDERS: [&str; 3] = ["anthropic", "openai", "gemini"];

pub fn is_cloud(provider: &str) -> bool {
    CLOUD_PROVIDERS.contains(&provider)
}

#[cfg(windows)]
fn target(provider: &str) -> Vec<u16> {
    format!("Halisland/assistant/{provider}\0").encode_utf16().collect()
}

#[cfg(windows)]
pub fn get(provider: &str) -> Option<String> {
    use windows::Win32::Security::Credentials::{CRED_TYPE_GENERIC, CREDENTIALW, CredFree, CredReadW};
    use windows::core::PCWSTR;
    let name = target(provider);
    let mut cred: *mut CREDENTIALW = std::ptr::null_mut();
    // SAFETY: `name` NUL ile biter; CredReadW başarılıysa `cred` geçerli bir
    // CREDENTIALW gösterir ve CredFree ile bırakılana dek okunabilir.
    unsafe {
        CredReadW(PCWSTR(name.as_ptr()), CRED_TYPE_GENERIC, None, &mut cred).ok()?;
        let c = &*cred;
        let blob = if c.CredentialBlob.is_null() {
            &[][..]
        } else {
            std::slice::from_raw_parts(c.CredentialBlob, c.CredentialBlobSize as usize)
        };
        let key = String::from_utf8(blob.to_vec()).ok();
        CredFree(cred as *const _);
        key.filter(|k| !k.is_empty())
    }
}

#[cfg(windows)]
pub fn set(provider: &str, key: &str) -> Result<(), String> {
    use windows::Win32::Security::Credentials::{
        CRED_FLAGS, CRED_PERSIST_LOCAL_MACHINE, CRED_TYPE_GENERIC, CREDENTIALW, CredWriteW,
    };
    use windows::core::PWSTR;
    let mut name = target(provider);
    let mut user: Vec<u16> = "Halisland\0".encode_utf16().collect();
    let mut blob = key.as_bytes().to_vec();
    let cred = CREDENTIALW {
        Flags: CRED_FLAGS(0),
        Type: CRED_TYPE_GENERIC,
        TargetName: PWSTR(name.as_mut_ptr()),
        CredentialBlobSize: blob.len() as u32,
        CredentialBlob: blob.as_mut_ptr(),
        Persist: CRED_PERSIST_LOCAL_MACHINE,
        UserName: PWSTR(user.as_mut_ptr()),
        ..Default::default()
    };
    // SAFETY: tüm işaretçiler bu işlev boyunca yaşayan yerel tamponları gösterir.
    unsafe { CredWriteW(&cred, 0) }.map_err(|e| e.message())
}

#[cfg(windows)]
pub fn delete(provider: &str) {
    use windows::Win32::Security::Credentials::{CRED_TYPE_GENERIC, CredDeleteW};
    use windows::core::PCWSTR;
    let name = target(provider);
    // SAFETY: `name` NUL ile biter. Kayıt yoksa hata döner; önemsiz.
    let _ = unsafe { CredDeleteW(PCWSTR(name.as_ptr()), CRED_TYPE_GENERIC, None) };
}

#[cfg(not(windows))]
pub fn get(_provider: &str) -> Option<String> {
    None
}

#[cfg(not(windows))]
pub fn set(_provider: &str, _key: &str) -> Result<(), String> {
    Err("Asistan yalnızca Windows'ta".into())
}

#[cfg(not(windows))]
pub fn delete(_provider: &str) {}

#[cfg(all(test, windows))]
mod tests {
    use super::*;

    #[test]
    fn anahtar_yazilir_okunur_silinir() {
        let p = "test-halisland";
        set(p, "sk-deneme-ğüş").unwrap();
        assert_eq!(get(p).as_deref(), Some("sk-deneme-ğüş"));
        delete(p);
        assert_eq!(get(p), None);
    }
}
