fn main() {
    // Derleme parametreleri (kök dizindeki features.json). "assistant": false
    // iken asistan derlemeye girer ama hiç başlatılmaz; tepsi menüsünde ve
    // arayüzde görünmez. Arayüz aynı dosyayı vite.config.ts'te okur.
    println!("cargo::rerun-if-changed=../features.json");
    println!("cargo::rustc-check-cfg=cfg(halisland_assistant)");
    let features = std::fs::read_to_string("../features.json").expect("features.json okunamadı");
    let compact: String = features.chars().filter(|c| !c.is_whitespace()).collect();
    if compact.contains("\"assistant\":true") {
        println!("cargo::rustc-cfg=halisland_assistant");
    } else if !compact.contains("\"assistant\":false") {
        panic!("features.json: \"assistant\" true ya da false olmalı");
    }

    tauri_build::build()
}
