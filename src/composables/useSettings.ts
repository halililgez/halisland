import { onBeforeUnmount, ref } from "vue";
import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import type { GlassMode } from "./useGlass";

/** Rust tarafındaki `Settings` ile aynı (src-tauri/src/settings.rs). */
export interface Settings {
  focusMinutes: number;
  glass: GlassMode;
  tint: boolean;
  autostart: boolean;
  /** Kilitliyken ada sürüklenemez. */
  locked: boolean;
}

const DEFAULTS: Settings = {
  focusMinutes: 25,
  glass: "acrylic",
  tint: true,
  autostart: false,
  locked: false,
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
