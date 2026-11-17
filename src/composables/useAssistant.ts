import { computed, onBeforeUnmount, ref, watch, type Ref } from "vue";
import { invoke } from "@tauri-apps/api/core";

/**
 * Asistan. İsteğe bağlı (Faz 23): ayarlarda açılınca backend
 * `assistant/` (.NET) Halisland ile birlikte başlar;
 * arayüz onunla doğrudan `127.0.0.1:47822` üzerinden konuşur.
 * Backend yerel modelle (Ollama) ya da bir bulut sağlayıcıyla konuşur
 * (Faz 24); bulut anahtarları Windows Kimlik Bilgileri'nde durur, arayüz
 * onları yalnızca kaydederken görür.
 * Sohbetler, mesajlar ve hatırlananlar backend'in SQLite veritabanında
 * (Faz 22); burada yalnızca açık sohbetin görüntüsü tutulur.
 */

interface AssistantInfo {
  supported: boolean;
  url: string;
  token: string;
}

interface Health {
  ok: boolean;
  provider: string;
  local: boolean;
  /** Sağlayıcıya ulaşıldı (Ollama açık ya da anahtar geçerli). */
  reachable: boolean;
  error: string | null;
  model: string;
  modelInstalled: boolean;
  models: string[];
}

export type ProviderId = "ollama" | "anthropic" | "openai" | "gemini";

export interface ProviderInfo {
  id: ProviderId;
  /** Seçicide görünen kısa ad. */
  label: string;
  /** Verilerin gittiği şirket (yerelde boş). */
  company: string;
  /** Anahtarın alınacağı sayfa. */
  keyUrl: string;
}

export const PROVIDERS: ProviderInfo[] = [
  { id: "ollama", label: "Yerel", company: "", keyUrl: "" },
  { id: "anthropic", label: "Claude", company: "Anthropic", keyUrl: "console.anthropic.com" },
  { id: "openai", label: "ChatGPT", company: "OpenAI", keyUrl: "platform.openai.com/api-keys" },
  { id: "gemini", label: "Gemini", company: "Google", keyUrl: "aistudio.google.com/apikey" },
];

export function providerInfo(id: string): ProviderInfo {
  return PROVIDERS.find((p) => p.id === id) ?? PROVIDERS[0];
}

/** Yerel model için donanım (`assistant_hardware`). */
export interface Hardware {
  gpu: string | null;
  vramMb: number;
  ramMb: number;
}

/** Donanıma göre önerilen model ve kısa açıklama. */
export interface Advice {
  model: string;
  level: "good" | "ok" | "weak";
  text: string;
}

/** Önerilen modeller (Ollama adları). */
export const BIG_MODEL = "qwen3:8b";
export const SMALL_MODEL = "qwen3.5:2b";

/**
 * qwen3:8b 8192 bağlamla ~6,5 GB VRAM ister (Faz 21 ölçümü); 8 GB kartta
 * tamamen GPU'da kalır. Daha küçük kartta küçük model; tümleşik ekran
 * kartında model işlemcide çalışır ve yavaştır.
 */
export function advise(h: Hardware): Advice {
  const gb = (mb: number) => `${Math.round(mb / 1024)} GB`;
  if (h.vramMb >= 7.5 * 1024)
    return { model: BIG_MODEL, level: "good", text: `Ekran kartın (${gb(h.vramMb)}) yerel model için yeterli.` };
  if (h.vramMb >= 3.5 * 1024)
    return {
      model: SMALL_MODEL,
      level: "ok",
      text: `Ekran kartın ${gb(h.vramMb)}; büyük model sığmaz, küçük model önerilir.`,
    };
  if (h.ramMb >= 15 * 1024)
    return {
      model: SMALL_MODEL,
      level: "weak",
      text: "Güçlü bir ekran kartı yok; küçük model işlemcide çalışır, yavaş olabilir.",
    };
  return {
    model: SMALL_MODEL,
    level: "weak",
    text: `Bu bilgisayar (${gb(h.ramMb)} RAM, ayrı ekran kartı yok) yerel model için zayıf; cevaplar çok yavaş olabilir.`,
  };
}

type StreamEvent =
  | { type: "conversation"; conversationId: number }
  | { type: "thinking"; text: string }
  | { type: "token"; text: string }
  | { type: "tool"; text: string; approved?: boolean }
  | { type: "confirm"; id: string; message: string; text: string }
  | { type: "confirm-end"; id: string; approved: boolean }
  | { type: "done"; tokensPerSec?: number }
  | { type: "title"; text: string; conversationId: number }
  | { type: "error"; message: string };

export type AssistantStatus =
  | "unsupported" // macOS / tarayıcı
  | "disabled" // ayarlarda kapalı (varsayılan)
  | "connecting" // backend açılıyor (dev'de `dotnet run` birkaç saniye sürer)
  | "offline" // backend yok
  | "no-ollama"
  | "no-key" // bulut sağlayıcı seçili, anahtar yok
  | "provider-error" // anahtar geçersiz, kota, ağ yok...
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
  /** Bu cevapta çalışan araçların kısa notları (ör. "Hatırlayacağım: …"). */
  tools?: ToolNote[];
  /** Kullanıcı onayı isteyen araçlar (ör. dosya açma). */
  confirms?: Confirm[];
}

export interface ToolNote {
  text: string;
  /** false: iş yapılmadı (bulunamadı, reddedildi, hata). */
  ok: boolean;
}

export interface Confirm {
  id: string;
  /** "Dosya açılsın mı?" */
  question: string;
  /** Açılacak yol. */
  detail: string;
  state: "pending" | "approved" | "denied";
}

export interface ConversationInfo {
  id: number;
  title: string;
  /** Unix ms */
  updated: number;
  count: number;
}

export interface Memory {
  id: number;
  text: string;
  created: number;
}

interface StoredMessage {
  id: number;
  role: "user" | "assistant";
  content: string;
  thinking: string | null;
  error: string | null;
  speed: number | null;
}

const THINK_KEY = "halisland.assistant.think";
const CONV_KEY = "halisland.assistant.conversation";
/** Backend ilk açılışta bu süre boyunca "bağlanıyor" sayılır. */
const STARTUP_GRACE_MS = 30_000;

function load(key: string): string | null {
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}

function save(key: string, value: string | null) {
  try {
    if (value === null) window.localStorage.removeItem(key);
    else window.localStorage.setItem(key, value);
  } catch {
    /* depo kapalıysa yalnızca bu oturumda geçerli */
  }
}

export function useAssistant(active: Ref<boolean>, enabled: Ref<boolean>, provider: Ref<string>) {
  const status = ref<AssistantStatus>(enabled.value ? "connecting" : "disabled");
  const model = ref("");
  /** Sağlayıcının modelleri (Ollama'da kurulu olanlar). */
  const installed = ref<string[]>([]);
  /** Sağlayıcının hata mesajı (status "provider-error"). */
  const providerError = ref("");
  /** Anahtarı kayıtlı bulut sağlayıcıları. */
  const keys = ref<string[]>([]);
  const local = computed(() => provider.value === "ollama");
  const hardware = ref<Hardware | null>(null);
  const advice = computed(() => (hardware.value ? advise(hardware.value) : null));
  const messages = ref<ChatMsg[]>([]);
  const think = ref(load(THINK_KEY) === "1");
  const busy = ref(false);
  /** Açık sohbet; null: yeni (henüz kaydedilmemiş) sohbet. */
  const conversationId = ref<number | null>(Number(load(CONV_KEY)) || null);
  const title = ref("");
  const conversations = ref<ConversationInfo[]>([]);
  const memories = ref<Memory[]>([]);

  let info: AssistantInfo | null = null;
  let nextId = -1; // yerel geçici kimlikler (veritabanınınkiyle çakışmasın diye eksi)
  let controller: AbortController | null = null;
  let warmedUp = false;
  /** Açılışta son sohbet bir kez yüklenir. */
  let restored = false;
  /** Backend'in (yeniden) başlatıldığı an; açılış süresince "bağlanıyor". */
  let startedAt = Date.now();

  watch(think, (v) => save(THINK_KEY, v ? "1" : "0"));
  watch(conversationId, (id) => save(CONV_KEY, id === null ? null : String(id)));

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

  /** Backend'e JSON isteği; hata olursa null. */
  async function api<T>(path: string, method = "GET"): Promise<T | null> {
    const i = await getInfo();
    if (!i?.supported) return null;
    try {
      const res = await fetch(`${i.url}${path}`, { method, headers: headers(i) });
      if (!res.ok) return null;
      return res.status === 204 ? (null as T) : ((await res.json()) as T);
    } catch {
      return null;
    }
  }

  async function check() {
    if (!enabled.value) {
      status.value = "disabled";
      return;
    }
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
      // Sağlayıcı değişip backend henüz yeniden başlamadıysa eskisi cevap verir.
      if (h.provider !== provider.value) throw new Error("eski backend");
      model.value = h.model;
      installed.value = h.models;
      providerError.value = h.error ?? "";
      status.value = h.reachable
        ? h.modelInstalled
          ? "ready"
          : "no-model"
        : h.local
          ? "no-ollama"
          : keys.value.includes(h.provider)
            ? "provider-error"
            : "no-key";
    } catch {
      status.value = Date.now() - startedAt < STARTUP_GRACE_MS ? "connecting" : "offline";
    }
    if (status.value === "connecting" || status.value === "offline") return;

    if (!restored) {
      restored = true;
      if (conversationId.value !== null && !messages.value.length) await open(conversationId.value);
    }
    if (status.value === "ready" && active.value && !warmedUp) {
      warmedUp = true;
      // Model GPU'ya yüklensin; ilk soruda beklemeyelim.
      fetch(`${i.url}/warmup`, { method: "POST", headers: headers(i) }).catch(() => (warmedUp = false));
    }
  }

  // Sekme görünürken 3 sn'de bir, değilken 30 sn'de bir bakılır. Asistan
  // kapalıyken hiç bakılmaz.
  let timer: number | undefined;
  function schedule() {
    window.clearTimeout(timer);
    if (!enabled.value) return;
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
  watch(enabled, (on) => {
    if (on) {
      startedAt = Date.now();
      status.value = "connecting";
      warmedUp = false;
      restored = false;
      loadHardware();
    } else {
      stop();
      status.value = "disabled";
    }
    check().then(schedule);
  });

  watch(provider, () => {
    if (enabled.value) restarting();
  });

  async function loadKeys() {
    keys.value = await invoke<string[]>("assistant_keys").catch(() => []);
  }
  loadKeys();

  async function loadHardware() {
    if (hardware.value) return;
    hardware.value = await invoke<Hardware>("assistant_hardware").catch(() => null);
  }
  if (enabled.value) loadHardware();

  /** Backend yeniden başlarken: "bağlanıyor" göster, birazdan yeniden bak. */
  function restarting() {
    stop();
    startedAt = Date.now();
    status.value = "connecting";
    warmedUp = false;
    window.clearTimeout(timer);
    timer = window.setTimeout(() => check().then(schedule), 1500);
  }

  /** Modeli değiştirir; backend yeniden başlar. */
  async function setModel(name: string) {
    if (name === model.value && status.value === "ready") return;
    model.value = name;
    await invoke("set_assistant_model", { model: name }).catch(() => {});
    restarting();
  }

  /** Sağlayıcıyı değiştirir; model varsayılana döner, backend yeniden başlar. */
  async function setProvider(id: ProviderId) {
    if (id === provider.value) return;
    installed.value = [];
    model.value = "";
    await invoke("set_assistant_provider", { provider: id }).catch(() => {});
    // Ayar olayı (settings-changed) provider'ı güncelleyince restarting() çağrılır.
  }

  /** Bulut anahtarını kaydeder; hata olursa mesajı döndürür. */
  async function saveKey(id: ProviderId, key: string): Promise<string | null> {
    try {
      await invoke("set_assistant_key", { provider: id, key });
    } catch (e) {
      return String(e);
    }
    await loadKeys();
    if (id === provider.value) restarting();
    return null;
  }

  async function removeKey(id: ProviderId) {
    await invoke("set_assistant_key", { provider: id, key: null }).catch(() => {});
    await loadKeys();
    if (id === provider.value) restarting();
  }
  onBeforeUnmount(() => {
    window.clearTimeout(timer);
    controller?.abort();
  });

  async function send(text: string) {
    const content = text.trim();
    const i = await getInfo();
    if (!content || busy.value || !i?.supported) return;

    messages.value.push({ id: nextId--, role: "user", content });
    messages.value.push({ id: nextId--, role: "assistant", content: "", streaming: true });
    // Reaktif vekil üzerinden güncellenmeli ki ekran yenilensin.
    const reply = messages.value[messages.value.length - 1];

    busy.value = true;
    // Bu isteğe ait denetleyici: "done"dan sonra akış başlık için açık
    // kalırken yeni bir mesaj gönderilirse, eski isteğin bitişi yenisini bozmasın.
    const ctl = new AbortController();
    controller = ctl;
    try {
      const res = await fetch(`${i.url}/chat`, {
        method: "POST",
        headers: headers(i, true),
        body: JSON.stringify({
          text: content,
          conversationId: conversationId.value ?? undefined,
          think: think.value,
        }),
        signal: ctl.signal,
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
      if (controller === ctl) {
        busy.value = false;
        controller = null;
      }
    }
  }

  function apply(reply: ChatMsg, e: StreamEvent) {
    switch (e.type) {
      case "conversation":
        conversationId.value = e.conversationId;
        break;
      case "thinking":
        reply.thinking = (reply.thinking ?? "") + e.text;
        break;
      case "token":
        reply.content += e.text;
        break;
      case "tool":
        reply.tools = [...(reply.tools ?? []), { text: e.text, ok: e.approved !== false }];
        // Hatırlananlar değişmiş olabilir.
        refreshMemories();
        break;
      case "confirm":
        reply.confirms = [
          ...(reply.confirms ?? []),
          { id: e.id, question: e.message, detail: e.text, state: "pending" },
        ];
        break;
      case "confirm-end": {
        const c = reply.confirms?.find((x) => x.id === e.id);
        if (c) c.state = e.approved ? "approved" : "denied";
        break;
      }
      case "done":
        reply.speed = e.tokensPerSec;
        // Cevap bitti; başlık için akış biraz daha açık kalabilir.
        reply.streaming = false;
        busy.value = false;
        break;
      case "title":
        if (e.conversationId === conversationId.value) title.value = e.text;
        break;
      case "error":
        reply.error = e.message;
        break;
    }
  }

  /** Onay isteyen araca cevap (Aç / Vazgeç). */
  async function answer(c: Confirm, approved: boolean) {
    if (c.state !== "pending") return;
    c.state = approved ? "approved" : "denied";
    const i = await getInfo();
    if (!i?.supported) return;
    await fetch(`${i.url}/confirm/${c.id}`, {
      method: "POST",
      headers: headers(i, true),
      body: JSON.stringify({ approved }),
    }).catch(() => {});
  }

  function stop() {
    controller?.abort();
    controller = null;
    busy.value = false;
  }

  /** Yeni sohbet: eskisi silinmez, geçmişte kalır. */
  function newChat() {
    stop();
    messages.value = [];
    conversationId.value = null;
    title.value = "";
  }

  /** Geçmişten bir sohbeti açar. */
  async function open(id: number) {
    if (busy.value) stop();
    const list = await api<StoredMessage[]>(`/conversations/${id}/messages`);
    if (!list) {
      // Silinmiş ya da bulunamadı.
      if (conversationId.value === id) newChat();
      return;
    }
    conversationId.value = id;
    title.value = conversations.value.find((c) => c.id === id)?.title ?? "";
    messages.value = list
      .filter((m) => m.role === "user" || m.role === "assistant")
      .map((m) => ({
        id: m.id,
        role: m.role,
        content: m.content,
        thinking: m.thinking ?? undefined,
        error: m.error ?? undefined,
        speed: m.speed ?? undefined,
      }));
  }

  async function refreshConversations() {
    const list = await api<ConversationInfo[]>("/conversations");
    if (!list) return;
    conversations.value = list;
    const current = list.find((c) => c.id === conversationId.value);
    if (current) title.value = current.title;
  }

  async function removeConversation(id: number) {
    await api(`/conversations/${id}`, "DELETE");
    conversations.value = conversations.value.filter((c) => c.id !== id);
    if (conversationId.value === id) newChat();
  }

  async function removeAllConversations() {
    await api("/conversations", "DELETE");
    conversations.value = [];
    newChat();
  }

  async function refreshMemories() {
    const list = await api<Memory[]>("/memories");
    if (list) memories.value = list;
  }

  async function removeMemory(id: number) {
    await api(`/memories/${id}`, "DELETE");
    memories.value = memories.value.filter((m) => m.id !== id);
  }

  const ready = computed(() => status.value === "ready");

  return {
    status,
    model,
    installed,
    hardware,
    advice,
    setModel,
    provider,
    local,
    providerError,
    keys,
    setProvider,
    saveKey,
    removeKey,
    messages,
    think,
    busy,
    ready,
    conversationId,
    title,
    conversations,
    memories,
    send,
    answer,
    stop,
    newChat,
    open,
    refreshConversations,
    removeConversation,
    removeAllConversations,
    refreshMemories,
    removeMemory,
  };
}

export type Assistant = ReturnType<typeof useAssistant>;
