import { onBeforeUnmount, ref, watch, type Ref, type WatchSource } from "vue";
import { invoke } from "@tauri-apps/api/core";

export type GlassMode = "acrylic" | "blur" | "off";

/**
 * Gerçek cam efekti (Windows). Rust tarafı pencerenin arkasını bulanıklaştırır
 * ve pencereyi adanın şekline kırpar. Ada CSS ile animasyonla büyüyüp
 * küçüldüğü için her karede adanın boyutunu ve köşe yarıçapını bildiririz.
 *
 * Cam açılamazsa (macOS, eski Windows, tarayıcı) `active` false kalır ve
 * ada CSS'teki yarı saydam koyu arka planla görünür.
 */
export function useGlass(el: Ref<HTMLElement | null>, mode: WatchSource<GlassMode>) {
  const active = ref(false);
  let frame: number | undefined;
  let last = "";
  let observer: ResizeObserver | undefined;

  function sendShape() {
    frame = undefined;
    const node = el.value;
    if (!node || !active.value) return;
    const rect = node.getBoundingClientRect();
    const radius = parseFloat(getComputedStyle(node).borderTopLeftRadius) || 0;
    const shape = { width: rect.width, height: rect.height, radius };
    const key = `${shape.width.toFixed(1)}x${shape.height.toFixed(1)}r${radius.toFixed(1)}`;
    if (key === last) return;
    last = key;
    invoke("set_island_shape", shape).catch(() => {});
  }

  function schedule() {
    if (frame === undefined) frame = requestAnimationFrame(sendShape);
  }

  async function apply(next: GlassMode) {
    try {
      active.value = await invoke<boolean>("set_glass", { mode: next });
    } catch {
      active.value = false;
    }
    document.documentElement.classList.toggle("glass", active.value);
    last = "";
    schedule();
  }

  watch(mode, apply, { immediate: true });

  watch(
    el,
    (node, _, onCleanup) => {
      if (!node) return;
      observer = new ResizeObserver(schedule);
      observer.observe(node);
      // Köşe yarıçapı da animasyonla değişiyor; boyut değişmese bile yakala.
      node.addEventListener("transitionend", schedule);
      onCleanup(() => {
        observer?.disconnect();
        node.removeEventListener("transitionend", schedule);
      });
    },
    { immediate: true },
  );

  onBeforeUnmount(() => {
    if (frame !== undefined) cancelAnimationFrame(frame);
  });

  return { active };
}
