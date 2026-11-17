import { onBeforeUnmount, ref } from "vue";
import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import type { GlassMode } from "./useGlass";
import type { WeatherCity } from "./useWeather";
import type { Edge } from "./useIsland";

/** Rust tarafındaki `Settings` ile aynı (src-tauri/src/settings.rs). */
export interface Settings {
  focusMinutes: number;
  glass: GlassMode;
  tint: boolean;
  autostart: boolean;
  /** Kilitliyken ada sürüklenemez. */
  locked: boolean;
  /** Pano geçmişi tutulsun mu? */
  clipboard: boolean;
  /** Bugün sekmesindeki hava durumunun şehri. */
  weatherCity: WeatherCity | null;
  /** Adanın yapıştığı ekran kenarı. */
  edge: Edge;
  /** Asistan açık mı? Varsayılan kapalı: yerel model güçlü bir ekran kartı ister. */
  assistant: boolean;
  /** Asistanın Ollama modeli; null: backend'in varsayılanı (qwen3:8b). */
  assistantModel: string | null;
}

const DEFAULTS: Settings = {
  focusMinutes: 25,
  glass: "acrylic",
  tint: true,
  autostart: false,
  locked: false,
  clipboard: true,
  weatherCity: null,
  edge: "top",
  assistant: false,
  assistantModel: null,
};

/**
 * Ayarlar Rust'ta tutulur ve tepsi menüsünden değiştirilir. Burada yalnızca
 * okunur; her değişiklikte `settings-changed` olayı gelir.
 */
export function useSettings() {
  const settings = ref<Settings>({ ...DEFAULTS });
  let unlisten: UnlistenFn | undefined;

  invoke<Settings>("get_settings")
    .then((s) => (settings.value = { ...DEFAULTS, ...s }))
    .catch(() => {});

  listen<Settings>("settings-changed", (e) => {
    settings.value = { ...DEFAULTS, ...e.payload };
  }).then((fn) => (unlisten = fn));

  onBeforeUnmount(() => unlisten?.());

  return settings;
}
