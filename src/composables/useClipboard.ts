import { onBeforeUnmount, ref, watch, type Ref } from "vue";
import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";

/** Rust'taki `ClipItem` ile aynı (src-tauri/src/clipboard.rs). */
export interface ClipItem {
  id: number;
  kind: "text" | "link" | "image";
  /** Metin önizlemesi (en fazla 1500 karakter). */
  text: string | null;
  /** Metnin tam uzunluğu (karakter) ya da görselin boyutu (bayt). */
  size: number;
  /** Görsel küçük resmi (data URL). */
  thumb: string | null;
  width: number;
  height: number;
  /** Unix ms */
  time: number;
  pinned: boolean;
}

/**
 * Pano geçmişi. Liste yalnızca Pano sekmesi görünürken Rust'tan istenir;
 * `clipboard-changed` olayı geldiğinde sekme açıksa yenilenir, kapalıysa
 * bir dahaki açılışta.
 */
export function useClipboard(active: Ref<boolean>) {
  const items = ref<ClipItem[]>([]);
  let stale = true;
  let unlisten: UnlistenFn | undefined;

  async function refresh() {
    stale = false;
    try {
      items.value = await invoke<ClipItem[]>("clipboard_list");
    } catch (e) {
      console.error("clipboard_list", e);
    }
  }

  listen("clipboard-changed", () => {
    if (active.value) refresh();
    else stale = true;
  }).then((fn) => (unlisten = fn));

  watch(
    active,
    (on) => {
      if (on && stale) refresh();
    },
    { immediate: true },
  );

  onBeforeUnmount(() => unlisten?.());

  const call = (cmd: string, id?: number) =>
    invoke(cmd, id === undefined ? {} : { id }).catch((e) => {
      console.error(cmd, e);
      throw e;
    });

  return {
    items,
    copy: (id: number) => call("clipboard_copy", id),
    remove: (id: number) => call("clipboard_delete", id),
    pin: (id: number) => call("clipboard_pin", id),
    clear: () => call("clipboard_clear"),
  };
}

/** "şimdi", "5 dk", "3 sa", "dün", "4 gün" */
export function timeAgo(ms: number, now = Date.now()): string {
  const s = Math.max(0, Math.floor((now - ms) / 1000));
  if (s < 45) return "şimdi";
  const m = Math.round(s / 60);
  if (m < 60) return `${m} dk`;
  const h = Math.round(m / 60);
  if (h < 24) return `${h} sa`;
  const d = Math.round(h / 24);
  return d === 1 ? "dün" : `${d} gün`;
}
