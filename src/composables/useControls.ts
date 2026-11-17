import { onBeforeUnmount, ref, watch, type Ref } from "vue";
import { invoke } from "@tauri-apps/api/core";

/** Rust'taki `AudioState` (src-tauri/src/controls.rs). */
export interface AudioState {
  /** 0–1 */
  volume: number;
  muted: boolean;
  device: string;
}

export interface AudioDevice {
  id: string;
  name: string;
  default: boolean;
}

export type RadioStatus = "on" | "off" | "disabled" | "missing";

export interface RadiosState {
  wifi: RadioStatus;
  bluetooth: RadioStatus;
  network: string | null;
  wlan: boolean;
}

const AUDIO_MS = 1000;
const RADIO_MS = 2500;
/** Kaydırıcı sürüklenirken Rust'a en fazla bu sıklıkta yazılır. */
const VOLUME_THROTTLE_MS = 60;

/**
 * Ses ve radyo durumu. Yalnızca Kontrol sekmesi görünürken sorulur; klavye
 * ses tuşları gibi dışarıdan yapılan değişiklikler de böylece görünür.
 * Kaydırıcı sürüklenirken gelen ölçüm kullanıcının değerini ezmez.
 */
export function useControls(active: Ref<boolean>) {
  const audio = ref<AudioState | null>(null);
  const radios = ref<RadiosState | null>(null);
  const audioError = ref("");
  const radioError = ref("");
  const dragging = ref(false);
  /** Değiştirilmekte olan radyo; yanıt gelene kadar düğme beklemede. */
  const busy = ref<"wifi" | "bluetooth" | null>(null);

  async function pollAudio() {
    try {
      const a = await invoke<AudioState>("audio_state");
      if (!dragging.value) audio.value = a;
      audioError.value = "";
    } catch (e) {
      audioError.value = String(e);
    }
  }

  async function pollRadios() {
    if (busy.value) return;
    try {
      radios.value = await invoke<RadiosState>("radios_state");
      radioError.value = "";
    } catch (e) {
      radioError.value = String(e);
    }
  }

  let audioTimer: number | undefined;
  let radioTimer: number | undefined;
  function stop() {
    window.clearInterval(audioTimer);
    window.clearInterval(radioTimer);
    audioTimer = radioTimer = undefined;
  }

  watch(
    active,
    (on) => {
      stop();
      if (!on) return;
      pollAudio();
      pollRadios();
      audioTimer = window.setInterval(pollAudio, AUDIO_MS);
      radioTimer = window.setInterval(pollRadios, RADIO_MS);
    },
    { immediate: true },
  );
  onBeforeUnmount(stop);

  let lastWrite = 0;
  let trailing: number | undefined;
  /** Ses seviyesini hemen ekranda, kısılmış olarak Rust'ta günceller. */
  function setVolume(v: number) {
    const volume = Math.min(1, Math.max(0, v));
    if (audio.value) audio.value = { ...audio.value, volume, muted: volume > 0 ? false : audio.value.muted };
    const send = () => {
      lastWrite = Date.now();
      invoke("set_volume", { volume }).catch((e) => (audioError.value = String(e)));
    };
    window.clearTimeout(trailing);
    const wait = VOLUME_THROTTLE_MS - (Date.now() - lastWrite);
    if (wait <= 0) send();
    else trailing = window.setTimeout(send, wait);
  }

  function toggleMute() {
    if (!audio.value) return;
    const muted = !audio.value.muted;
    audio.value = { ...audio.value, muted };
    invoke("set_mute", { muted }).catch((e) => (audioError.value = String(e)));
  }

  async function toggleRadio(kind: "wifi" | "bluetooth") {
    const current = radios.value?.[kind];
    if (!radios.value || (current !== "on" && current !== "off") || busy.value) return;
    const on = current === "off";
    busy.value = kind;
    radios.value = { ...radios.value, [kind]: on ? "on" : "off" };
    try {
      await invoke("set_radio", { kind, on });
      radioError.value = "";
    } catch (e) {
      radioError.value = String(e);
    } finally {
      busy.value = null;
      pollRadios();
    }
  }

  onBeforeUnmount(() => window.clearTimeout(trailing));

  /** Ses çıkış aygıtları; seçici açılınca istenir. */
  const devices = ref<AudioDevice[]>([]);
  const switching = ref(false);
  async function loadDevices() {
    try {
      devices.value = await invoke<AudioDevice[]>("audio_devices");
    } catch (e) {
      audioError.value = String(e);
    }
  }

  async function setDevice(id: string) {
    if (switching.value) return;
    switching.value = true;
    devices.value = devices.value.map((d) => ({ ...d, default: d.id === id }));
    try {
      await invoke("set_audio_device", { id });
      audioError.value = "";
    } catch (e) {
      audioError.value = String(e);
    } finally {
      switching.value = false;
      loadDevices();
      pollAudio();
    }
  }

  return {
    audio,
    radios,
    audioError,
    radioError,
    dragging,
    busy,
    devices,
    switching,
    setVolume,
    toggleMute,
    toggleRadio,
    loadDevices,
    setDevice,
  };
}
