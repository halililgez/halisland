import { onBeforeUnmount, ref, watch, type Ref } from "vue";
import { invoke } from "@tauri-apps/api/core";

export type Edge = "top" | "bottom" | "left" | "right";

export const COMPACT = { width: 320, height: 44 };
/** Sağ ve sol kenarda kompakt ada dikeydir. */
export const COMPACT_VERTICAL = { width: 44, height: 320 };
export const EXPANDED = { width: 720, height: 200 };

export const isVertical = (edge: Edge) => edge === "left" || edge === "right";
export const compactSize = (edge: Edge) => (isVertical(edge) ? COMPACT_VERTICAL : COMPACT);

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
export function useIsland(edge: Ref<Edge>, onExpand?: () => void) {
  const expanded = ref(false);
  let pending: number | undefined;
  /** Açılış/kapanış sırasında pencere küçültmesi. */
  let settle: number | undefined;

  // Dikey kompakt ada (44x320) açık adadan (720x200) daha uzun. Geçiş
  // boyunca ada kırpılmasın diye pencere ikisini de kapsayacak boyuta
  // getirilir, animasyon bitince hedef boyuta indirilir. Üst ve altta bu
  // zaten açık boyuttur.
  const union = () => {
    const c = compactSize(edge.value);
    return {
      width: Math.max(c.width, EXPANDED.width),
      height: Math.max(c.height, EXPANDED.height),
    };
  };

  function clearSettle() {
    if (settle !== undefined) window.clearTimeout(settle);
    settle = undefined;
  }

  // Kenar değişince (ayar ya da tepsi menüsü) kompakt adanın yönü değişir.
  watch(edge, () => {
    if (!expanded.value && pending === undefined) resize(compactSize(edge.value));
  });

  // Güvenlik ağı: WebView bazen fare gerçekten üstünde değilken `mouseenter`
  // gönderiyor (ör. açılışta) ya da hızlı çıkışta `mouseleave`'i kaçırıyor.
  // Ada açıkken imleç gerçekten pencerenin dışındaysa kapat.
  const guard = window.setInterval(async () => {
    if (!expanded.value || pending !== undefined) return;
    const over = await invoke<boolean>("cursor_over_island").catch(() => true);
    if (!over && expanded.value && pending === undefined) hoverOut();
  }, 1500);
  onBeforeUnmount(() => {
    window.clearInterval(guard);
    clearSettle();
  });

  const resize = (size: { width: number; height: number }) =>
    invoke("resize_island", size).catch((e) => console.error("resize_island", e));

  function clearPending() {
    if (pending !== undefined) window.clearTimeout(pending);
    pending = undefined;
  }

  async function expand() {
    clearPending();
    if (expanded.value) return;
    clearSettle();
    await resize(union());
    expanded.value = true;
    onExpand?.();
    settle = window.setTimeout(() => {
      settle = undefined;
      if (expanded.value) resize(EXPANDED);
    }, TRANSITION_MS);
  }

  function collapse() {
    clearPending();
    if (!expanded.value) return;
    expanded.value = false;
    clearSettle();
    resize(union());
    pending = window.setTimeout(() => {
      pending = undefined;
      if (!expanded.value) resize(compactSize(edge.value));
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
