import { ref } from "vue";
import { invoke } from "@tauri-apps/api/core";

export const COMPACT = { width: 320, height: 44 };
export const EXPANDED = { width: 720, height: 200 };

/** CSS geçiş süresiyle aynı olmalı (App.vue → .island transition). */
const TRANSITION_MS = 380;
const EXPAND_DELAY_MS = 120;
const COLLAPSE_DELAY_MS = 450;

/**
 * Ada açılıp kapanırken pencereyi de boyutlandırır.
 * Pencere hep adanın boyutunda tutulur; böylece adanın dışındaki
 * alan tıklamaları engellemez.
 *  - Açılırken: önce pencere büyür, sonra ada animasyonla açılır.
 *  - Kapanırken: önce ada animasyonla kapanır, sonra pencere küçülür.
 */
export function useIsland(onExpand?: () => void) {
  const expanded = ref(false);
  let pending: number | undefined;

  const resize = (size: { width: number; height: number }) =>
    invoke("resize_island", size).catch((e) => console.error("resize_island", e));

  function clearPending() {
    if (pending !== undefined) window.clearTimeout(pending);
    pending = undefined;
  }

  async function expand() {
    clearPending();
    if (expanded.value) return;
    await resize(EXPANDED);
    expanded.value = true;
    onExpand?.();
  }

  function collapse() {
    clearPending();
    if (!expanded.value) return;
    expanded.value = false;
    pending = window.setTimeout(() => {
      if (!expanded.value) resize(COMPACT);
    }, TRANSITION_MS);
  }

  function hoverIn() {
    clearPending();
    pending = window.setTimeout(expand, EXPAND_DELAY_MS);
  }

  function hoverOut() {
    clearPending();
    pending = window.setTimeout(collapse, COLLAPSE_DELAY_MS);
  }

  function toggle() {
    expanded.value ? collapse() : expand();
  }

  return { expanded, expand, collapse, hoverIn, hoverOut, toggle };
}
