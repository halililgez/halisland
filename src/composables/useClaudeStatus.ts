import { computed, onBeforeUnmount, ref } from "vue";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import { invoke } from "@tauri-apps/api/core";

/**
 * idle    → boşta
 * working → Claude çalışıyor (turuncu, nabız gibi parlar)
 * waiting → senden onay/girdi bekliyor (sarı)
 * done    → iş bitti, henüz bakmadın (mavi)
 * seen    → baktın; yeşile döner ve birkaç saniyede söner
 */
export type ClaudeState = "idle" | "working" | "waiting" | "done" | "seen";

/** Rust tarafındaki `ClaudeStatus` ile aynı (src-tauri/src/claude.rs). */
export interface ClaudeInfo {
  state: Exclude<ClaudeState, "seen">;
  at: number;
  sessionId: string;
  project: string;
  prompt: string;
  tool: string;
  message: string;
  files: string[];
  canFocus: boolean;
  sessions: number;
}

const EMPTY: ClaudeInfo = {
  state: "idle",
  at: 0,
  sessionId: "",
  project: "",
  prompt: "",
  tool: "",
  message: "",
  files: [],
  canFocus: false,
  sessions: 0,
};

const SEEN_FADE_MS = 4000;

/** Claude Code araç adlarını Türkçe kısa etiketlere çevirir. */
const TOOL_LABELS: Record<string, string> = {
  Bash: "Komut çalıştırıyor",
  Edit: "Dosya düzenliyor",
  MultiEdit: "Dosya düzenliyor",
  Write: "Dosya yazıyor",
  Read: "Dosya okuyor",
  Grep: "Arıyor",
  Glob: "Dosya arıyor",
  WebFetch: "Web'e bakıyor",
  WebSearch: "Web'de arıyor",
  Task: "Alt ajan çalıştırıyor",
  Agent: "Alt ajan çalıştırıyor",
  TodoWrite: "Plan yapıyor",
  NotebookEdit: "Not defteri düzenliyor",
};

export function toolLabel(tool: string): string {
  if (!tool) return "";
  if (TOOL_LABELS[tool]) return TOOL_LABELS[tool];
  // MCP araçları: mcp__sunucu__arac → "sunucu: arac"
  const mcp = tool.match(/^mcp__(.+?)__(.+)$/);
  return mcp ? `${mcp[1]}: ${mcp[2]}` : tool;
}

/** Claude Code'un sık gönderdiği bildirimleri Türkçeleştirir. */
export function translateMessage(message: string): string {
  const perm = message.match(/needs your permission to use (.+)$/i);
  if (perm) return `${perm[1]} için izin istiyor`;
  if (/waiting for your input/i.test(message)) return "Girdini bekliyor";
  if (/needs your (approval|attention)/i.test(message)) return "Onayını bekliyor";
  return message;
}

export function useClaudeStatus() {
  const info = ref<ClaudeInfo>({ ...EMPTY });
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

  function receive(payload: ClaudeInfo) {
    info.value = { ...EMPTY, ...payload };
    // Baktıktan sonra aynı "bitti" tekrar gelirse yeşilde kal.
    if (payload.state === "done" && state.value === "seen") return;
    set(payload.state);
  }

  /** Kullanıcı adaya baktığında çağrılır. */
  function markSeen() {
    if (state.value !== "done") return;
    set("seen");
    fadeTimer = window.setTimeout(() => set("idle"), SEEN_FADE_MS);
  }

  /** Claude'un çalıştığı terminali / pencereyi öne getirir. */
  async function focusTerminal() {
    try {
      const ok = await invoke<boolean>("focus_claude");
      if (ok) markSeen();
      return ok;
    } catch {
      return false;
    }
  }

  listen<ClaudeInfo>("claude-status", (e) => receive(e.payload)).then((fn) => (unlisten = fn));

  /** Hook'lar ~/.claude/settings.json'a kurulu mu? (Menüden kurulur.) */
  const connected = ref(true);
  let unlistenHooks: UnlistenFn | undefined;
  invoke<boolean>("claude_hooks_installed")
    .then((v) => (connected.value = v))
    .catch(() => {});
  listen<boolean>("claude-hooks-changed", (e) => (connected.value = e.payload)).then(
    (fn) => (unlistenHooks = fn),
  );

  // Uygulama yeniden yüklendiyse kaçırılan son durumu al.
  invoke<ClaudeInfo>("claude_status")
    .then((s) => {
      if (s.sessionId) receive(s);
    })
    .catch(() => {});

  onBeforeUnmount(() => {
    unlisten?.();
    unlistenHooks?.();
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

  /** Durum satırı: ne yapıyor / neden bekliyor. */
  const detail = computed(() => {
    const i = info.value;
    if (state.value === "working") return toolLabel(i.tool) || "Düşünüyor";
    if (state.value === "waiting") return translateMessage(i.message) || "Onay bekliyor";
    if (state.value === "done" || state.value === "seen") {
      const n = i.files.length;
      return n ? `${n} dosya değişti` : "Dosya değişmedi";
    }
    return "";
  });

  return { state, info, label, detail, elapsed, connected, markSeen, focusTerminal };
}
