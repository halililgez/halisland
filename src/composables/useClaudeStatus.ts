import { computed, onBeforeUnmount, ref } from "vue";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";

/**
 * idle    → boşta
 * working → Claude çalışıyor (turuncu, nabız gibi parlar)
 * waiting → senden onay/girdi bekliyor (sarı)
 * done    → iş bitti, henüz bakmadın (mavi)
 * seen    → baktın; yeşile döner ve birkaç saniyede söner
 */
export type ClaudeState = "idle" | "working" | "waiting" | "done" | "seen";

const SEEN_FADE_MS = 4000;

export function useClaudeStatus() {
  const state = ref<ClaudeState>("idle");
  const since = ref(Date.now());
  const now = ref(Date.now());

  let fadeTimer: number | undefined;
  let unlisten: UnlistenFn | undefined;
  const clock = window.setInterval(() => (now.value = Date.now()), 1000);

  function set(next: ClaudeState) {
    if (fadeTimer !== undefined) window.clearTimeout(fadeTimer);
    fadeTimer = undefined;
    if (next !== state.value) since.value = Date.now();
    state.value = next;
  }

  /** Kullanıcı adaya baktığında çağrılır. */
  function markSeen() {
    if (state.value !== "done") return;
    set("seen");
    fadeTimer = window.setTimeout(() => set("idle"), SEEN_FADE_MS);
  }

  listen<{ state: Exclude<ClaudeState, "seen">; at: number }>("claude-status", (e) => {
    set(e.payload.state);
  }).then((fn) => (unlisten = fn));

  onBeforeUnmount(() => {
    unlisten?.();
    window.clearInterval(clock);
    if (fadeTimer !== undefined) window.clearTimeout(fadeTimer);
  });

  const elapsed = computed(() => {
    const s = Math.max(0, Math.floor((now.value - since.value) / 1000));
    return s < 60 ? `${s}sn` : `${Math.floor(s / 60)}dk ${s % 60}sn`;
  });

  const label = computed(
    () =>
      ({
        idle: "Hazır",
        working: "Çalışıyor",
        waiting: "Seni bekliyor",
        done: "Bitti",
        seen: "Bitti",
      })[state.value],
  );

  return { state, label, elapsed, markSeen };
}
