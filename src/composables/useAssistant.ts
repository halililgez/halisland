import { computed, onBeforeUnmount, ref, watch, type Ref } from "vue";
import { invoke } from "@tauri-apps/api/core";

/**
 * Yerel asistan (Faz 21). Backend `assistant/` (.NET) Halisland ile birlikte
 * başlar; arayüz onunla doğrudan `127.0.0.1:47822` üzerinden konuşur.
 * Sohbet şimdilik yalnızca bellekte; kalıcı hafıza Faz 22'de.
 */

interface AssistantInfo {
  supported: boolean;
  url: string;
  token: string;
}

interface Health {
  ok: boolean;
  ollama: boolean;
  model: string;
  modelInstalled: boolean;
  models: string[];
}

type StreamEvent =
  | { type: "thinking"; text: string }
  | { type: "token"; text: string }
  | { type: "done"; tokens?: number; tokensPerSec?: number }
  | { type: "error"; message: string };

export type AssistantStatus =
  | "unsupported" // macOS / tarayıcı
  | "connecting" // backend açılıyor (dev'de `dotnet run` birkaç saniye sürer)
  | "offline" // backend yok
  | "no-ollama"
  | "no-model"
  | "ready";

export interface ChatMsg {
  id: number;
  role: "user" | "assistant";
  content: string;
  thinking?: string;
  error?: string;
  streaming?: boolean;
  /** Cevap hızı (token/sn). */
  speed?: number;
}

const THINK_KEY = "halisland.assistant.think";
/** Backend ilk açılışta bu süre boyunca "bağlanıyor" sayılır. */
const STARTUP_GRACE_MS = 30_000;

function loadThink(): boolean {
  try {
    return window.localStorage.getItem(THINK_KEY) === "1";
  } catch {
    return false;
  }
}

export function useAssistant(active: Ref<boolean>) {
  const status = ref<AssistantStatus>("connecting");
  const model = ref("");
  const messages = ref<ChatMsg[]>([]);
  const think = ref(loadThink());
  const busy = ref(false);

  let info: AssistantInfo | null = null;
  let nextId = 1;
  let controller: AbortController | null = null;
  let warmedUp = false;
  const startedAt = Date.now();

  watch(think, (v) => {
    try {
      window.localStorage.setItem(THINK_KEY, v ? "1" : "0");
    } catch {
      /* depo kapalıysa yalnızca bu oturumda geçerli */
    }
  });

  async function getInfo(): Promise<AssistantInfo | null> {
    if (info) return info;
    info = await invoke<AssistantInfo>("assistant_info").catch(() => null);
    return info;
  }

  function headers(i: AssistantInfo, json = false): HeadersInit {
    const h: Record<string, string> = { authorization: `Bearer ${i.token}` };
    if (json) h["content-type"] = "application/json";
    return h;
  }

  async function check() {
    const i = await getInfo();
    if (!i?.supported) {
      status.value = "unsupported";
      return;
    }
    try {
      const res = await fetch(`${i.url}/health`, {
        headers: headers(i),
        signal: AbortSignal.timeout(4000),
      });
      if (!res.ok) throw new Error(String(res.status));
      const h = (await res.json()) as Health;
      model.value = h.model;
      status.value = !h.ollama ? "no-ollama" : !h.modelInstalled ? "no-model" : "ready";
    } catch {
      status.value = Date.now() - startedAt < STARTUP_GRACE_MS ? "connecting" : "offline";
    }
    if (status.value === "ready" && active.value && !warmedUp) {
      warmedUp = true;
      // Model GPU'ya yüklensin; ilk soruda beklemeyelim.
      fetch(`${i.url}/warmup`, { method: "POST", headers: headers(i) }).catch(() => (warmedUp = false));
    }
  }

  // Sekme görünürken 3 sn'de bir, değilken 30 sn'de bir bakılır.
  let timer: number | undefined;
  function schedule() {
    window.clearTimeout(timer);
    timer = window.setTimeout(
      async () => {
        await check();
        schedule();
      },
      active.value || status.value === "connecting" ? 3000 : 30_000,
    );
  }
  check().then(schedule);
  watch(active, (on) => {
    if (on) check().then(schedule);
  });
  onBeforeUnmount(() => {
    window.clearTimeout(timer);
    controller?.abort();
  });

  async function send(text: string) {
    const content = text.trim();
    const i = await getInfo();
    if (!content || busy.value || !i?.supported) return;

    // Modele giden geçmiş: hatasız, boş olmayan mesajlar.
    const history = messages.value
      .filter((m) => !m.error && m.content.trim())
      .map((m) => ({ role: m.role, content: m.content }));
    history.push({ role: "user", content });

    messages.value.push({ id: nextId++, role: "user", content });
    messages.value.push({ id: nextId++, role: "assistant", content: "", streaming: true });
    // Reaktif vekil üzerinden güncellenmeli ki ekran yenilensin.
    const reply = messages.value[messages.value.length - 1];

    busy.value = true;
    controller = new AbortController();
    try {
      const res = await fetch(`${i.url}/chat`, {
        method: "POST",
        headers: headers(i, true),
        body: JSON.stringify({ messages: history, think: think.value }),
        signal: controller.signal,
      });
      if (!res.ok || !res.body) throw new Error(`Asistan ${res.status} döndürdü.`);

      const reader = res.body.getReader();
      const decoder = new TextDecoder();
      let buffer = "";
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });
        let nl: number;
        while ((nl = buffer.indexOf("\n")) >= 0) {
          const line = buffer.slice(0, nl).trim();
          buffer = buffer.slice(nl + 1);
          if (line) apply(reply, JSON.parse(line) as StreamEvent);
        }
      }
    } catch (e) {
      if ((e as Error).name !== "AbortError") {
        reply.error = (e as Error).message || "Asistana ulaşılamadı.";
        check();
      }
    } finally {
      reply.streaming = false;
      busy.value = false;
      controller = null;
    }
  }

  function apply(reply: ChatMsg, e: StreamEvent) {
    switch (e.type) {
      case "thinking":
        reply.thinking = (reply.thinking ?? "") + e.text;
        break;
      case "token":
        reply.content += e.text;
        break;
      case "done":
        reply.speed = e.tokensPerSec;
        break;
      case "error":
        reply.error = e.message;
        break;
    }
  }

  function stop() {
    controller?.abort();
  }

  function clear() {
    stop();
    messages.value = [];
  }

  const ready = computed(() => status.value === "ready");

  return { status, model, messages, think, busy, ready, send, stop, clear };
}

export type Assistant = ReturnType<typeof useAssistant>;
