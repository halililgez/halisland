import { onBeforeUnmount, ref, watch, type Ref } from "vue";
import { invoke } from "@tauri-apps/api/core";

/** Rust'taki `Screen` (src-tauri/src/display.rs). */
export interface Screen {
  id: string;
  name: string;
  /** 0–100 */
  brightness: number;
  internal: boolean;
}

export interface DisplayState {
  screens: Screen[];
  /** null: okunamadı ya da bu Windows sürümünde biçim tanınmadı. */
  nightLight: boolean | null;
}

/** DDC/CI yavaş (~40–100 ms/monitör); seyrek sorulur. */
const POLL_MS = 5000;
/** Monitöre en fazla bu sıklıkta yazılır. */
const THROTTLE_MS = 250;

/**
 * Ekran parlaklığı ve gece ışığı. Yalnızca Kontrol sekmesi görünürken
 * sorulur. Sürüklenen kaydırıcıyı gelen ölçüm ezmez.
 */
export function useDisplay(active: Ref<boolean>) {
  const state = ref<DisplayState | null>(null);
  const error = ref("");
  const dragging = ref<string | null>(null);
  const nightBusy = ref(false);
  /** Bir süre önce elle değiştirilen ekranların değeri ölçümle ezilmesin. */
  const touched = new Map<string, number>();

  async function poll() {
    try {
      const s = await invoke<DisplayState>("display_state");
      const now = Date.now();
      if (state.value) {
        for (const sc of s.screens) {
          const old = state.value.screens.find((x) => x.id === sc.id);
          const recent = (touched.get(sc.id) ?? 0) > now - 1500;
          if (old && (dragging.value === sc.id || recent)) sc.brightness = old.brightness;
        }
        if (nightBusy.value) s.nightLight = state.value.nightLight;
      }
      state.value = s;
      error.value = "";
    } catch (e) {
      error.value = String(e);
    }
  }

  let timer: number | undefined;
  watch(
    active,
    (on) => {
      window.clearInterval(timer);
      timer = undefined;
      if (!on) return;
      poll();
      timer = window.setInterval(poll, POLL_MS);
    },
    { immediate: true },
  );

  const last = new Map<string, number>();
  const pending = new Map<string, number>();
  function setBrightness(id: string, value: number) {
    const v = Math.round(Math.min(100, Math.max(0, value)));
    touched.set(id, Date.now());
    const sc = state.value?.screens.find((x) => x.id === id);
    if (sc) sc.brightness = v;
    const send = () => {
      last.set(id, Date.now());
      touched.set(id, Date.now());
      invoke("set_brightness", { id, value: v }).catch((e) => (error.value = String(e)));
    };
    window.clearTimeout(pending.get(id));
    const wait = THROTTLE_MS - (Date.now() - (last.get(id) ?? 0));
    if (wait <= 0) send();
    else pending.set(id, window.setTimeout(send, wait));
  }

  async function toggleNight() {
    if (!state.value || state.value.nightLight === null || nightBusy.value) return;
    const on = !state.value.nightLight;
    nightBusy.value = true;
    state.value = { ...state.value, nightLight: on };
    try {
      await invoke("set_night_light", { on });
      error.value = "";
    } catch (e) {
      error.value = String(e);
      state.value = { ...state.value, nightLight: !on };
    } finally {
      nightBusy.value = false;
    }
  }

  function openNightSettings() {
    invoke("open_night_light_settings").catch((e) => (error.value = String(e)));
  }

  onBeforeUnmount(() => {
    window.clearInterval(timer);
    pending.forEach((t) => window.clearTimeout(t));
  });

  return { state, error, dragging, nightBusy, setBrightness, toggleNight, openNightSettings };
}
