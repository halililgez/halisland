import { computed, onBeforeUnmount, ref, toValue, watch, type MaybeRefOrGetter } from "vue";

export type FocusState = "idle" | "running" | "paused" | "finished";

/**
 * Odak sayacı. Süre ayarlardan değişirse sayaç boştaysa hemen, çalışıyorsa
 * bir sonraki sıfırlamada uygulanır.
 */
export function useFocusTimer(minutes: MaybeRefOrGetter<number> = 25) {
  const durationMs = ref(toValue(minutes) * 60_000);
  const remainingMs = ref(durationMs.value);
  const state = ref<FocusState>("idle");

  watch(
    () => toValue(minutes),
    (m) => {
      durationMs.value = m * 60_000;
      if (state.value === "idle") remainingMs.value = durationMs.value;
    },
  );

  let endsAt = 0;
  let ticker: number | undefined;

  function tick() {
    remainingMs.value = Math.max(0, endsAt - Date.now());
    if (remainingMs.value === 0) {
      stopTicker();
      state.value = "finished";
    }
  }

  function stopTicker() {
    if (ticker !== undefined) window.clearInterval(ticker);
    ticker = undefined;
  }

  function start() {
    if (state.value === "finished") remainingMs.value = durationMs.value;
    endsAt = Date.now() + remainingMs.value;
    state.value = "running";
    stopTicker();
    ticker = window.setInterval(tick, 250);
  }

  function pause() {
    tick();
    stopTicker();
    state.value = "paused";
  }

  function reset() {
    stopTicker();
    remainingMs.value = durationMs.value;
    state.value = "idle";
  }

  function toggle() {
    state.value === "running" ? pause() : start();
  }

  onBeforeUnmount(stopTicker);

  const label = computed(() => {
    const total = Math.ceil(remainingMs.value / 1000);
    const m = Math.floor(total / 60).toString().padStart(2, "0");
    const s = (total % 60).toString().padStart(2, "0");
    return `${m}:${s}`;
  });

  const progress = computed(() =>
    Math.min(1, Math.max(0, 1 - remainingMs.value / durationMs.value)),
  );

  return { state, label, progress, start, pause, reset, toggle };
}
