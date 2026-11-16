import { computed, onBeforeUnmount, ref } from "vue";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import { invoke } from "@tauri-apps/api/core";
import { accentFromImage } from "../utils/color";

/** Rust tarafındaki `MediaState` ile aynı (src-tauri/src/media.rs). */
export interface MediaState {
  active: boolean;
  playing: boolean;
  title: string;
  artist: string;
  album: string;
  source: string;
  positionMs: number;
  durationMs: number;
  trackKey: string;
}

const EMPTY: MediaState = {
  active: false,
  playing: false,
  title: "",
  artist: "",
  album: "",
  source: "",
  positionMs: 0,
  durationMs: 0,
  trackKey: "",
};

/**
 * "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" → "Spotify", "chrome.exe" → "chrome",
 * "Opera.1674602008" → "Opera" (sayısal son parça bir kimliktir, ad değil).
 */
export function sourceName(id: string): string {
  let name = id.includes("!") ? id.slice(id.lastIndexOf("!") + 1) : id;
  name = name.replace(/\.exe$/i, "");
  const parts = name.split(".").filter((p) => p && !/^\d+$/.test(p));
  return parts.length ? parts[parts.length - 1] : "";
}

export function formatMs(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000));
  const m = Math.floor(total / 60);
  const s = (total % 60).toString().padStart(2, "0");
  return `${m}:${s}`;
}

export function useMedia() {
  const state = ref<MediaState>({ ...EMPTY });
  const artwork = ref("");
  const accent = ref<string | undefined>();
  /** Rust bilgiyi saniyede bir gönderir; arada ilerlemeyi biz yürütürüz. */
  const position = ref(0);

  let receivedAt = Date.now();
  const unlisteners: UnlistenFn[] = [];

  const ticker = window.setInterval(() => {
    const s = state.value;
    if (!s.playing) return;
    const p = s.positionMs + (Date.now() - receivedAt);
    position.value = s.durationMs > 0 ? Math.min(p, s.durationMs) : p;
  }, 250);

  listen<MediaState>("media-update", (e) => {
    const prevKey = state.value.trackKey;
    state.value = e.payload;
    receivedAt = Date.now();
    position.value = e.payload.positionMs;
    if (e.payload.trackKey !== prevKey) {
      // Yeni şarkının kapağı gelene kadar eskisini gösterme.
      artwork.value = "";
      accent.value = undefined;
    }
  }).then((fn) => unlisteners.push(fn));

  listen<{ trackKey: string; dataUrl: string }>("media-artwork", async (e) => {
    if (e.payload.trackKey !== state.value.trackKey) return;
    artwork.value = e.payload.dataUrl;
    accent.value = e.payload.dataUrl ? await accentFromImage(e.payload.dataUrl) : undefined;
  }).then((fn) => unlisteners.push(fn));

  onBeforeUnmount(() => {
    window.clearInterval(ticker);
    unlisteners.forEach((fn) => fn());
  });

  function control(action: "playPause" | "next" | "previous") {
    // Anında tepki: oynat/duraklat durumunu iyimser olarak çevir.
    if (action === "playPause") {
      state.value = { ...state.value, playing: !state.value.playing, positionMs: position.value };
      receivedAt = Date.now();
    }
    invoke("media_control", { action }).catch((err) => console.error("media_control", err));
  }

  const progress = computed(() =>
    state.value.durationMs > 0 ? Math.min(1, position.value / state.value.durationMs) : 0,
  );
  const source = computed(() => sourceName(state.value.source));

  return { state, artwork, accent, position, progress, source, control };
}
