<script setup lang="ts">
/**
 * Asistan sekmesi: yerel modelle (Ollama) ya da bir bulut sağlayıcıyla
 * (Claude, ChatGPT, Gemini) sohbet. Başlıkta sağlayıcı (Model görünümü),
 * Geçmiş, Hafıza, "Düşün" anahtarı ve Yeni; altta mesajlar ve yazma kutusu.
 * Model, Geçmiş ve Hafıza aynı kutucuğun içinde açılır. Model görünümünün
 * altında Spotify hesabı bağlanır (asistan şarkıyı doğrudan çalabilsin).
 * Enter gönderir, Esc yanıtı durdurur (liste açıksa sohbete döner).
 */
import { computed, nextTick, ref, watch } from "vue";
import Tile from "./Tile.vue";
import {
  BIG_MODEL,
  PROVIDERS,
  providerInfo,
  type Assistant,
  type AssistantStatus,
  type ChatMsg,
} from "../composables/useAssistant";
import { timeAgo } from "../composables/useClipboard";

const props = defineProps<{ assistant: Assistant; active: boolean }>();
const a = props.assistant;

type View = "chat" | "model" | "history" | "memory";
const view = ref<View>("chat");

const draft = ref("");
const list = ref<HTMLElement | null>(null);
const input = ref<HTMLInputElement | null>(null);
/** Düşüncesi açılmış mesajlar. */
const openThoughts = ref(new Set<number>());
/** İki adımlı silme: ilk basışta "Sil?" olur, ikinci basış siler. */
const confirming = ref<string | null>(null);
let confirmTimer: number | undefined;

const STATUS_TEXT: Record<AssistantStatus, string> = {
  unsupported: "Asistan şimdilik yalnızca Windows'ta.",
  disabled: "Asistan kapalı. Ayarlardan açabilirsin.",
  connecting: "Asistan açılıyor…",
  offline: "Asistan servisi çalışmıyor.",
  "no-ollama": "Ollama kapalı ya da kurulu değil (ollama.com). Başlatınca burası kendiliğinden açılır.",
  "no-key": "",
  "provider-error": "",
  "no-model": "",
  ready: "",
};

/** Seçili sağlayıcı. */
const current = computed(() => providerInfo(a.provider.value));

const statusText = computed(() => {
  switch (a.status.value) {
    case "no-model":
      return a.local.value
        ? `${a.model.value} kurulu değil.`
        : `${a.model.value} bu hesapta yok; listeden başka bir model seç.`;
    case "no-key":
      return `${current.value.label} için API anahtarı gerekli.`;
    case "provider-error":
      return a.providerError.value || `${current.value.label} sunucusuna bağlanılamadı.`;
    default:
      return STATUS_TEXT[a.status.value];
  }
});

/** Yerel: Ollama yok ya da model kurulu değil; kurulum yardımı gösterilir. */
const needsSetup = computed(
  () => a.local.value && (a.status.value === "no-ollama" || a.status.value === "no-model"),
);
/** Bulut: anahtar yok, geçersiz ya da model yok; Model görünümüne yönlendirilir. */
const cloudSetup = computed(
  () =>
    !a.local.value &&
    (a.status.value === "no-key" || a.status.value === "provider-error" || a.status.value === "no-model"),
);

/** Model görünümünde seçilebilecek modeller (gömme modelleri hariç). */
const modelChoices = computed(() => a.installed.value.filter((m) => !/embed/i.test(m)));

// ---------- Bulut anahtarı ----------
const keyDraft = ref("");
const keyError = ref("");
const editingKey = ref(false);
const savingKey = ref(false);
const hasKey = computed(() => a.keys.value.includes(a.provider.value));

async function saveKey() {
  const key = keyDraft.value.trim();
  if (!key || savingKey.value || a.local.value) return;
  savingKey.value = true;
  keyError.value = (await a.saveKey(current.value.id, key)) ?? "";
  savingKey.value = false;
  if (!keyError.value) {
    keyDraft.value = "";
    editingKey.value = false;
  }
}

watch(
  () => a.provider.value,
  () => {
    keyDraft.value = "";
    keyError.value = "";
    editingKey.value = false;
  },
);
/** Önerilen modelin kurulum komutu. */
const pullModel = computed(() => a.advice.value?.model ?? a.model.value);
/** Geçilebilecek kurulu sohbet modelleri (gömme modelleri hariç). */
const choices = computed(() =>
  a.installed.value.filter((m) => m !== a.model.value && !/embed/i.test(m)).slice(0, 4),
);
/** Hazırken: zayıf makinede büyük model seçiliyse uyarı. */
const slowWarning = computed(() =>
  a.advice.value && a.advice.value.level !== "good" && a.model.value === BIG_MODEL
    ? a.advice.value.text
    : "",
);

const glow = computed(() => (a.busy.value ? "var(--c-focus)" : undefined));

/** Göreli zamanlar listeyi açınca güncellenir. */
const now = ref(Date.now());

function show(v: View) {
  confirming.value = null;
  if (view.value === v) {
    view.value = "chat";
    return;
  }
  view.value = v;
  now.value = Date.now();
  if (v === "history") a.refreshConversations();
  if (v === "memory") a.refreshMemories();
  if (v === "model") a.refreshSpotify();
}

// ---------- Spotify ----------
const spotifyDraft = ref("");
const spotifyError = ref("");
const editingSpotify = ref(false);
const copied = ref(false);
const sp = computed(() => a.spotify.value);
/** Client ID girme adımları gösterilsin mi. */
const spotifySetup = computed(() => !!sp.value && (!sp.value.configured || editingSpotify.value));

async function connectSpotify() {
  const id = spotifySetup.value ? spotifyDraft.value.trim() : undefined;
  if (spotifySetup.value && !id) return;
  spotifyError.value = (await a.connectSpotify(id)) ?? "";
  if (!spotifyError.value) {
    spotifyDraft.value = "";
    editingSpotify.value = false;
  }
}

async function copyRedirect() {
  if (!sp.value) return;
  await navigator.clipboard.writeText(sp.value.redirectUri).catch(() => {});
  copied.value = true;
  window.setTimeout(() => (copied.value = false), 1500);
}

function newChat() {
  a.newChat();
  view.value = "chat";
  nextTick(() => input.value?.focus({ preventScroll: true }));
}

async function openConversation(id: number) {
  await a.open(id);
  view.value = "chat";
  stick = true;
  await nextTick();
  if (list.value) list.value.scrollTop = list.value.scrollHeight;
}

/** İlk basış onay ister (2,5 sn), ikinci basış işlemi yapar. */
function confirmThen(key: string, action: () => void) {
  window.clearTimeout(confirmTimer);
  if (confirming.value === key) {
    confirming.value = null;
    action();
    return;
  }
  confirming.value = key;
  confirmTimer = window.setTimeout(() => (confirming.value = null), 2500);
}

function submit() {
  if (!a.ready.value || a.busy.value) return;
  const text = draft.value;
  if (!text.trim()) return;
  draft.value = "";
  stick = true;
  a.send(text);
}

function onKey(e: KeyboardEvent) {
  if (e.key === "Escape" && a.busy.value) {
    e.preventDefault();
    a.stop();
  }
}

function onPanelKey(e: KeyboardEvent) {
  if (e.key === "Escape" && view.value !== "chat") {
    e.preventDefault();
    view.value = "chat";
  }
}

function toggleThought(id: number) {
  const s = new Set(openThoughts.value);
  s.has(id) ? s.delete(id) : s.add(id);
  openThoughts.value = s;
}

/** Düşünürken yalnızca son satır görünür (yer dar). */
function lastLine(text: string): string {
  const lines = text.trim().split(/\n+/);
  return lines[lines.length - 1] ?? "";
}

function escapeHtml(s: string): string {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

/**
 * Modelin en sık kullandığı Markdown: ```kod bloğu```, **kalın** ve `kod`.
 * Metin önce HTML'den kaçırılır; v-html'e yalnızca bu etiketler girer.
 */
function format(text: string): string {
  return text
    .split(/```/)
    .map((part, i) => {
      if (i % 2 === 1) {
        // Kod bloğu: ilk satır dil adıysa atılır.
        const code = part.replace(/^[\w#+.-]*\n/, "").replace(/\n$/, "");
        return `<pre><code>${escapeHtml(code)}</code></pre>`;
      }
      return escapeHtml(part)
        .replace(/\*\*([^*\n]+)\*\*/g, "<strong>$1</strong>")
        .replace(/`([^`\n]+)`/g, "<code>$1</code>")
        .replace(/^#{1,6}\s+/gm, "");
    })
    .join("");
}

function thinkingOnly(m: ChatMsg) {
  return m.streaming && !m.content && !!m.thinking;
}

// Yeni parça gelince en alta kaydır (kullanıcı yukarıdaysa dokunma).
let stick = true;
function onScroll() {
  const el = list.value;
  if (el) stick = el.scrollHeight - el.scrollTop - el.clientHeight < 24;
}
watch(
  () => {
    const last = a.messages.value[a.messages.value.length - 1];
    return [
      a.messages.value.length,
      last?.content.length,
      last?.thinking?.length,
      last?.streaming,
      last?.error,
      last?.tools?.length,
      last?.confirms?.length,
    ];
  },
  async () => {
    await nextTick();
    if (stick && list.value) list.value.scrollTop = list.value.scrollHeight;
  },
);

// Sekme açılınca yazma kutusuna odaklan.
watch(
  () => props.active,
  async (on) => {
    if (!on) {
      view.value = "chat";
      return;
    }
    await nextTick();
    if (list.value) list.value.scrollTop = list.value.scrollHeight;
    input.value?.focus({ preventScroll: true });
  },
);
</script>

<template>
  <div class="assistant" @keydown="onPanelKey">
    <Tile title="Asistan" class="chat-tile" :glow="glow" :pulse="a.busy.value">
      <template #head>
        <span class="head">
          <span v-if="view === 'chat' && a.title.value" class="conv-title" :title="a.title.value">
            {{ a.title.value }}
          </span>
          <span class="actions">
            <button
              class="mini-chip"
              :class="{ active: view === 'model' }"
              :aria-pressed="view === 'model'"
              :title="a.model.value ? `Sağlayıcı ve model: ${a.model.value}` : 'Sağlayıcı ve model'"
              @click="show('model')"
            >
              {{ current.label }}
            </button>
            <button
              class="mini-chip"
              :class="{ active: view === 'history' }"
              :aria-pressed="view === 'history'"
              title="Geçmiş sohbetler"
              @click="show('history')"
            >
              Geçmiş
            </button>
            <button
              class="mini-chip"
              :class="{ active: view === 'memory' }"
              :aria-pressed="view === 'memory'"
              title="Asistanın senin hakkında hatırladıkları"
              @click="show('memory')"
            >
              Hafıza
            </button>
            <button
              class="mini-chip toggle"
              :class="{ on: a.think.value }"
              :aria-pressed="a.think.value"
              title="Düşünme açıkken cevaplar daha dikkatli ama daha yavaş"
              @click="a.think.value = !a.think.value"
            >
              <span class="knob" aria-hidden="true" />
              Düşün
            </button>
            <button
              class="mini-chip"
              title="Yeni sohbet (bu sohbet geçmişte kalır)"
              :disabled="view === 'chat' && !a.messages.value.length"
              @click="newChat"
            >
              Yeni
            </button>
          </span>
        </span>
      </template>

      <!-- Model: sağlayıcı, bulut anahtarı ve model seçimi -->
      <div v-if="view === 'model'" class="body model-view">
        <div class="segmented" role="radiogroup" aria-label="Asistanın sağlayıcısı">
          <button
            v-for="p in PROVIDERS"
            :key="p.id"
            role="radio"
            :aria-checked="a.provider.value === p.id"
            :class="{ active: a.provider.value === p.id }"
            @click="a.setProvider(p.id)"
          >
            {{ p.label }}
          </button>
        </div>

        <span class="setup-status">
          <span class="state-dot" :class="a.status.value" aria-hidden="true" />
          <span class="status-line">{{ a.ready.value ? `Hazır · ${a.model.value}` : statusText }}</span>
        </span>

        <template v-if="!a.local.value">
          <p class="privacy">
            Mesajların, hatırladıkların ve sohbet özetleri {{ current.company }} sunucularına gider; ücreti
            API hesabına yansır. Sohbet geçmişi yine bu bilgisayarda kalır.
          </p>
          <div v-if="hasKey && !editingKey" class="key-row">
            <span class="key-ok">Anahtar kayıtlı · Windows Kimlik Bilgileri</span>
            <button class="mini-chip" @click="editingKey = true">Değiştir</button>
            <button
              class="mini-chip danger"
              :class="{ armed: confirming === 'key' }"
              @click="confirmThen('key', () => a.removeKey(current.id))"
            >
              {{ confirming === "key" ? "Silinsin mi?" : "Sil" }}
            </button>
          </div>
          <form v-else class="key-row" @submit.prevent="saveKey">
            <input
              v-model="keyDraft"
              type="password"
              class="prompt key-input"
              :placeholder="`${current.label} API anahtarı`"
              autocomplete="off"
              spellcheck="false"
              :aria-label="`${current.label} API anahtarı`"
            />
            <button class="mini-chip" type="submit" :disabled="!keyDraft.trim() || savingKey">Kaydet</button>
            <button v-if="hasKey" type="button" class="mini-chip" @click="editingKey = false">Vazgeç</button>
          </form>
          <span v-if="!hasKey || editingKey" class="key-help">
            Anahtarı <span class="mono">{{ current.keyUrl }}</span> adresinden alabilirsin.
          </span>
          <p v-if="keyError" class="error">{{ keyError }}</p>
        </template>
        <span v-else-if="a.advice.value" class="advice" :class="a.advice.value.level">
          {{ a.advice.value.text }}
        </span>

        <div v-if="modelChoices.length" class="model-list">
          <button
            v-for="m in modelChoices"
            :key="m"
            class="mini-chip mono"
            :class="{ active: m === a.model.value, recommended: a.local.value && m === a.advice.value?.model }"
            :aria-pressed="m === a.model.value"
            @click="a.setModel(m)"
          >
            {{ m }}
          </button>
        </div>

        <div v-if="sp" class="spotify">
          <div class="key-row">
            <span class="spotify-name">Spotify</span>
            <span v-if="sp.connected" class="key-ok">Bağlı{{ sp.user ? ` · ${sp.user}` : "" }}</span>
            <span v-else-if="sp.pending" class="spotify-state">Tarayıcıda izin bekleniyor…</span>
            <span v-else class="spotify-state">Şarkıları doğrudan çalmak için bağla (Premium)</span>
            <template v-if="!spotifySetup">
              <button v-if="!sp.connected" class="mini-chip primary" @click="connectSpotify">Bağlan</button>
              <button v-if="!sp.connected" class="mini-chip" @click="editingSpotify = true">Client ID</button>
              <button
                v-if="sp.connected"
                class="mini-chip danger"
                :class="{ armed: confirming === 'spotify' }"
                @click="confirmThen('spotify', () => a.disconnectSpotify())"
              >
                {{ confirming === "spotify" ? "Kesilsin mi?" : "Bağlantıyı kes" }}
              </button>
            </template>
          </div>
          <template v-if="spotifySetup">
            <span class="key-help">
              <span class="mono">developer.spotify.com/dashboard</span>'da bir uygulama oluştur (Web API), Redirect URI
              olarak
              <button class="link mono" :title="'Kopyala'" @click="copyRedirect">{{ sp.redirectUri }}</button>
              {{ copied ? "(kopyalandı)" : "" }} ekle, Client ID'yi buraya yapıştır.
            </span>
            <form class="key-row" @submit.prevent="connectSpotify">
              <input
                v-model="spotifyDraft"
                class="prompt key-input mono"
                placeholder="Client ID"
                autocomplete="off"
                spellcheck="false"
                aria-label="Spotify Client ID"
              />
              <button class="mini-chip primary" type="submit" :disabled="!spotifyDraft.trim()">Bağlan</button>
              <button v-if="sp.configured" type="button" class="mini-chip" @click="editingSpotify = false">
                Vazgeç
              </button>
            </form>
          </template>
          <p v-if="spotifyError || sp.error" class="error">{{ spotifyError || sp.error }}</p>
        </div>
      </div>

      <!-- Geçmiş -->
      <div v-else-if="view === 'history'" class="body">
        <ul v-if="a.conversations.value.length" class="rows">
          <li
            v-for="c in a.conversations.value"
            :key="c.id"
            :class="{ current: c.id === a.conversationId.value }"
          >
            <button class="row-main" @click="openConversation(c.id)">
              <span class="row-text">{{ c.title || "Adsız sohbet" }}</span>
              <span class="row-meta mono">{{ timeAgo(c.updated, now) }}</span>
            </button>
            <button
              class="row-del"
              :class="{ armed: confirming === `c${c.id}` }"
              :title="confirming === `c${c.id}` ? 'Silmek için yeniden bas' : 'Sohbeti sil'"
              @click="confirmThen(`c${c.id}`, () => a.removeConversation(c.id))"
            >
              {{ confirming === `c${c.id}` ? "Sil?" : "×" }}
            </button>
          </li>
        </ul>
        <div v-else class="empty hint">Henüz kayıtlı sohbet yok.</div>
        <div v-if="a.conversations.value.length" class="list-foot">
          <span>{{ a.conversations.value.length }} sohbet · bu bilgisayarda</span>
          <button
            class="mini-chip danger"
            :class="{ armed: confirming === 'all' }"
            @click="confirmThen('all', a.removeAllConversations)"
          >
            {{ confirming === "all" ? "Hepsi silinsin mi?" : "Tümünü sil" }}
          </button>
        </div>
      </div>

      <!-- Hafıza -->
      <div v-else-if="view === 'memory'" class="body">
        <ul v-if="a.memories.value.length" class="rows">
          <li v-for="m in a.memories.value" :key="m.id">
            <span class="row-main static">
              <span class="row-text wrap">{{ m.text }}</span>
            </span>
            <button
              class="row-del"
              :class="{ armed: confirming === `m${m.id}` }"
              :title="confirming === `m${m.id}` ? 'Silmek için yeniden bas' : 'Unut'"
              @click="confirmThen(`m${m.id}`, () => a.removeMemory(m.id))"
            >
              {{ confirming === `m${m.id}` ? "Unut?" : "×" }}
            </button>
          </li>
        </ul>
        <div v-else class="empty hint">
          Asistan henüz senin hakkında bir şey hatırlamıyor. "Adım Halil, bunu hatırla" gibi söyleyebilirsin.
        </div>
        <div class="list-foot">
          <span>Her sohbette asistana verilir. Unutmasını istediğini buradan sil.</span>
        </div>
      </div>

      <!-- Sohbet -->
      <div v-else class="body">
        <div v-if="needsSetup && !a.messages.value.length" class="empty hint setup">
          <span class="setup-status">
            <span class="state-dot" :class="a.status.value" aria-hidden="true" />
            {{ statusText }}
          </span>
          <span v-if="a.advice.value" class="advice" :class="a.advice.value.level">
            {{ a.advice.value.text }}
          </span>
          <span v-if="a.hardware.value?.gpu" class="gpu mono">{{ a.hardware.value.gpu }}</span>
          <span v-if="choices.length" class="model-choices">
            Kurulu:
            <button
              v-for="m in choices"
              :key="m"
              class="mini-chip mono"
              :class="{ recommended: m === a.advice.value?.model }"
              :title="`${m} kullan`"
              @click="a.setModel(m)"
            >
              {{ m }}
            </button>
          </span>
          <code v-if="!choices.includes(pullModel)" class="pull mono">ollama pull {{ pullModel }}</code>
        </div>
        <div v-else-if="cloudSetup && !a.messages.value.length" class="empty hint setup">
          <span class="setup-status">
            <span class="state-dot" :class="a.status.value" aria-hidden="true" />
            {{ statusText }}
          </span>
          <button class="mini-chip" @click="show('model')">Ayarla</button>
        </div>
        <div v-else-if="!a.ready.value && !a.messages.value.length" class="empty">
          <span class="state-dot" :class="a.status.value" aria-hidden="true" />
          {{ statusText }}
        </div>
        <div v-else-if="!a.messages.value.length" class="empty hint">
          {{
            a.local.value
              ? "Bir şey sor. Her şey bu bilgisayarda kalır."
              : `Bir şey sor. Mesajların ${current.company} sunucularına gider.`
          }}
          <small class="mono">{{ a.model.value }}</small>
          <small v-if="slowWarning" class="advice weak">{{ slowWarning }}</small>
        </div>
        <ul v-else ref="list" class="messages" @scroll="onScroll">
          <li v-for="m in a.messages.value" :key="m.id" :class="['msg', m.role]">
            <template v-if="m.role === 'assistant'">
              <button
                v-if="m.thinking && !thinkingOnly(m)"
                class="thought-toggle"
                :aria-expanded="openThoughts.has(m.id)"
                @click="toggleThought(m.id)"
              >
                {{ openThoughts.has(m.id) ? "Düşünceyi gizle" : "Düşünceyi göster" }}
              </button>
              <p v-if="m.thinking && openThoughts.has(m.id)" class="thought">{{ m.thinking.trim() }}</p>
              <p v-if="thinkingOnly(m)" class="thinking">
                <span class="spinner" aria-hidden="true" />{{ lastLine(m.thinking ?? "") }}
              </p>
              <p v-else-if="m.streaming && !m.content && !m.tools?.length && !m.confirms?.length" class="thinking">
                <span class="spinner" aria-hidden="true" />{{ a.think.value ? "Düşünüyor…" : "Yazıyor…" }}
              </p>
              <div
                v-for="c in m.confirms"
                :key="c.id"
                class="confirm"
                :class="c.state"
                role="group"
                :aria-label="c.question"
              >
                <span class="confirm-q">{{ c.question }}</span>
                <span class="confirm-path mono" :title="c.detail">{{ c.detail }}</span>
                <span v-if="c.state === 'pending'" class="confirm-actions">
                  <button class="mini-chip primary" @click="a.answer(c, true)">Aç</button>
                  <button class="mini-chip" @click="a.answer(c, false)">Vazgeç</button>
                </span>
                <span v-else class="confirm-result">{{ c.state === "approved" ? "Onaylandı" : "Vazgeçildi" }}</span>
              </div>
              <span v-for="(t, i) in m.tools" :key="i" class="tool-note" :class="{ failed: !t.ok }">
                <svg viewBox="0 0 24 24" width="10" height="10" aria-hidden="true">
                  <path
                    :d="t.ok ? 'M5 12.5l4.5 4.5L19 7' : 'M7 7l10 10M17 7L7 17'"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="3"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                  />
                </svg>
                {{ t.text }}
              </span>
              <div v-if="m.content" class="text">
                <span v-html="format(m.content.trim())" /><span v-if="m.streaming" class="caret" />
              </div>
              <p v-else-if="!m.streaming && !m.error && !m.tools?.length" class="stopped">Durduruldu</p>
              <p v-if="m.error" class="error">{{ m.error }}</p>
              <span v-if="m.speed && !m.streaming" class="meta mono">{{ m.speed }} token/sn</span>
            </template>
            <p v-else class="text">{{ m.content }}</p>
          </li>
        </ul>

        <form class="composer" @submit.prevent="submit">
          <input
            ref="input"
            v-model="draft"
            class="prompt"
            :placeholder="a.ready.value ? 'Sor ve Enter\'a bas…' : statusText || 'Asistan hazır değil'"
            :disabled="!a.ready.value && !a.busy.value"
            spellcheck="false"
            maxlength="4000"
            aria-label="Asistana sor"
            @keydown="onKey"
          />
          <button
            v-if="a.busy.value"
            type="button"
            class="send stop"
            title="Durdur (Esc)"
            aria-label="Durdur"
            @click="a.stop()"
          >
            <svg viewBox="0 0 24 24" width="10" height="10" aria-hidden="true">
              <rect x="6" y="6" width="12" height="12" rx="2" fill="currentColor" />
            </svg>
          </button>
          <button
            v-else
            type="submit"
            class="send"
            :disabled="!a.ready.value || !draft.trim()"
            title="Gönder (Enter)"
            aria-label="Gönder"
          >
            <svg viewBox="0 0 24 24" width="12" height="12" aria-hidden="true">
              <path
                d="M12 19V5M5 12l7-7 7 7"
                fill="none"
                stroke="currentColor"
                stroke-width="2.6"
                stroke-linecap="round"
                stroke-linejoin="round"
              />
            </svg>
          </button>
        </form>
      </div>
    </Tile>
  </div>
</template>

<style scoped>
.assistant {
  display: flex;
  min-height: 0;
}

.chat-tile {
  flex: 1;
}

/* Kutucuğun gövdesi ortalanmasın; mesajlar üstten dolsun. */
.chat-tile :deep(.tile-body) {
  justify-content: stretch;
}

.head {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: center;
  gap: 8px;
}

.conv-title {
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
  font-size: 10.5px;
  font-weight: 500;
  letter-spacing: normal;
  text-transform: none;
  color: var(--text);
  opacity: 0.75;
}

.actions {
  margin-left: auto;
  flex: none;
  display: flex;
  align-items: center;
  gap: 5px;
}

.mini-chip {
  flex: none;
  padding: 2px 8px;
  border-radius: 999px;
  font-size: 10.5px;
  background: rgba(255, 255, 255, 0.08);
  text-transform: none;
  letter-spacing: normal;
  font-weight: 500;
  color: var(--text-dim);
}

.mini-chip:hover:not(:disabled),
.mini-chip.active {
  background: rgba(255, 255, 255, 0.16);
  color: var(--text);
}

.mini-chip:disabled {
  opacity: 0.4;
}

.mini-chip.danger:hover,
.mini-chip.danger.armed {
  background: rgba(255, 90, 90, 0.22);
  color: #ff8a8a;
}

.toggle {
  display: flex;
  align-items: center;
  gap: 5px;
}

.knob {
  position: relative;
  width: 16px;
  height: 9px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.18);
  transition: background 0.2s ease;
}

.knob::after {
  content: "";
  position: absolute;
  top: 1px;
  left: 1px;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--text);
  transition: transform 0.2s var(--ease);
}

.toggle.on {
  color: var(--text);
}

.toggle.on .knob {
  background: var(--c-focus);
}

.toggle.on .knob::after {
  transform: translateX(7px);
}

.body {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.empty {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 0 24px;
  font-size: 11.5px;
  color: var(--text-dim);
  text-align: center;
}

.empty:not(.hint) {
  flex-direction: row;
}

.empty small {
  font-size: 9.5px;
  opacity: 0.7;
}

.setup {
  gap: 5px;
}

.setup-status {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--text);
}

.advice.good {
  color: var(--c-seen);
}

.advice.ok,
.advice.weak {
  color: var(--c-waiting);
}

.empty small.advice {
  opacity: 0.9;
}

.gpu {
  font-size: 9.5px;
  opacity: 0.6;
}

.model-choices {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: center;
  gap: 4px;
}

.mini-chip.recommended {
  border-color: var(--c-seen);
  color: var(--text);
}

.pull {
  padding: 3px 8px;
  border-radius: 6px;
  background: rgba(255, 255, 255, 0.08);
  font-size: 10.5px;
  color: var(--text);
  user-select: text;
}

.state-dot {
  flex: none;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--c-hot);
}

.state-dot.connecting {
  background: var(--c-waiting);
  animation: blink 1.2s ease-in-out infinite;
}

.state-dot.ready {
  background: var(--c-seen);
}

.state-dot.unsupported,
.state-dot.disabled {
  background: rgba(255, 255, 255, 0.3);
}

/* ---------- Model görünümü ---------- */
.model-view {
  gap: 7px;
  font-size: 11px;
  color: var(--text-dim);
}

.model-view .setup-status {
  min-width: 0;
}

.status-line {
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.segmented {
  flex: none;
  display: flex;
  padding: 2px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid rgba(255, 255, 255, 0.06);
}

.segmented button {
  flex: 1;
  min-width: 0;
  padding: 3px 0;
  border-radius: 999px;
  font-size: 11px;
  color: var(--text-dim);
  transition:
    background 0.2s ease,
    color 0.2s ease;
}

.segmented button:hover {
  color: var(--text);
}

.segmented button.active {
  background: rgba(255, 255, 255, 0.16);
  color: var(--text);
  box-shadow: 0 1px 4px rgba(0, 0, 0, 0.25);
}

.privacy {
  margin: 0;
  font-size: 10.5px;
  line-height: 1.35;
}

.key-row {
  flex: none;
  display: flex;
  align-items: center;
  gap: 5px;
}

.key-ok {
  flex: 1;
  min-width: 0;
  color: var(--c-seen);
}

.key-input {
  height: 22px;
  font-size: 11px;
}

.key-help {
  font-size: 10px;
}

.key-help .mono {
  color: var(--text);
  user-select: text;
}

.model-view .error {
  margin: 0;
}

.spotify {
  flex: none;
  display: flex;
  flex-direction: column;
  gap: 5px;
  padding-top: 7px;
  border-top: 1px solid rgba(255, 255, 255, 0.08);
}

.spotify-name {
  flex: none;
  font-weight: 600;
  color: var(--text);
}

.spotify-state {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.spotify .key-ok {
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.link {
  padding: 0;
  border: 0;
  background: none;
  color: var(--text);
  font-size: inherit;
  text-decoration: underline dotted;
  cursor: pointer;
}

.model-list {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-wrap: wrap;
  align-content: flex-start;
  gap: 4px;
  overflow-y: auto;
  scrollbar-width: thin;
  scrollbar-color: rgba(255, 255, 255, 0.18) transparent;
}

.mini-chip.mono {
  font-size: 10px;
}

/* ---------- Geçmiş ve Hafıza listeleri ---------- */
.rows {
  flex: 1;
  min-height: 0;
  margin: 0;
  padding: 0;
  list-style: none;
  overflow-y: auto;
  scrollbar-width: thin;
  scrollbar-color: rgba(255, 255, 255, 0.18) transparent;
  font-size: 11.5px;
}

.rows li {
  display: flex;
  align-items: center;
  gap: 4px;
  border-radius: 8px;
}

.rows li:hover,
.rows li.current {
  background: rgba(255, 255, 255, 0.06);
}

.rows li.current .row-text {
  color: var(--text);
  font-weight: 600;
}

.row-main {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: baseline;
  gap: 10px;
  padding: 5px 8px;
  text-align: left;
}

.row-text {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
  color: color-mix(in srgb, var(--text) 88%, transparent);
}

.row-text.wrap {
  white-space: normal;
  overflow-wrap: anywhere;
}

.row-meta {
  flex: none;
  font-size: 10px;
  color: var(--text-dim);
}

.row-del {
  flex: none;
  min-width: 22px;
  height: 20px;
  margin-right: 4px;
  padding: 0 6px;
  border-radius: 999px;
  font-size: 12px;
  line-height: 1;
  color: var(--text-dim);
  opacity: 0;
  transition:
    opacity 0.15s ease,
    background 0.15s ease;
}

.rows li:hover .row-del,
.row-del:focus-visible,
.row-del.armed {
  opacity: 1;
}

.row-del:hover,
.row-del.armed {
  background: rgba(255, 90, 90, 0.22);
  color: #ff8a8a;
}

.row-del.armed {
  font-size: 10.5px;
  font-weight: 600;
}

.list-foot {
  flex: none;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  font-size: 10px;
  color: var(--text-dim);
}

/* ---------- Sohbet ---------- */
.messages {
  flex: 1;
  min-height: 0;
  margin: 0;
  padding: 0 2px 0 0;
  list-style: none;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 6px;
  font-size: 12px;
  line-height: 1.4;
  scrollbar-width: thin;
  scrollbar-color: rgba(255, 255, 255, 0.18) transparent;
}

.msg {
  /* Kaydırılan listede mesajlar sıkışıp üst üste binmesin. */
  flex: none;
  max-width: 88%;
}

.msg p {
  margin: 0;
}

.msg.user {
  align-self: flex-end;
  padding: 4px 10px;
  border-radius: 12px 12px 4px 12px;
  background: rgba(255, 255, 255, 0.12);
}

.msg.assistant {
  align-self: flex-start;
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
}

.text {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  user-select: text;
}

.text :deep(code) {
  padding: 0 4px;
  border-radius: 4px;
  background: rgba(255, 255, 255, 0.1);
  font-family: ui-monospace, "Cascadia Mono", Consolas, monospace;
  font-size: 11px;
}

.text :deep(pre) {
  margin: 4px 0;
  padding: 6px 8px;
  border-radius: 8px;
  background: rgba(0, 0, 0, 0.28);
  border: 1px solid rgba(255, 255, 255, 0.06);
  overflow-x: auto;
  white-space: pre;
}

.text :deep(pre code) {
  padding: 0;
  background: none;
}

.text :deep(strong) {
  font-weight: 650;
}

.tool-note {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 2px 8px;
  border-radius: 999px;
  font-size: 10.5px;
  color: var(--c-seen);
  background: color-mix(in srgb, var(--c-seen) 14%, transparent);
}

.confirm {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  align-items: center;
  gap: 2px 10px;
  max-width: 100%;
  padding: 6px 10px;
  border-radius: 10px;
  background: color-mix(in srgb, var(--c-waiting) 12%, transparent);
  border: 1px solid color-mix(in srgb, var(--c-waiting) 35%, transparent);
  font-size: 11px;
}

.confirm-q {
  font-weight: 600;
}

.confirm-path {
  grid-column: 1;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
  font-size: 10px;
  color: var(--text-dim);
}

.confirm-actions,
.confirm-result {
  grid-column: 2;
  grid-row: 1 / span 2;
  display: flex;
  gap: 5px;
}

.confirm-result {
  font-size: 10.5px;
  color: var(--text-dim);
}

.confirm.approved,
.confirm.denied {
  background: rgba(255, 255, 255, 0.04);
  border-color: rgba(255, 255, 255, 0.08);
}

.mini-chip.primary {
  background: color-mix(in srgb, var(--c-seen) 35%, transparent);
  color: var(--text);
}

.mini-chip.primary:hover {
  background: color-mix(in srgb, var(--c-seen) 55%, transparent);
}

.tool-note.failed {
  color: var(--text-dim);
  background: rgba(255, 255, 255, 0.06);
}

.thinking,
.thought,
.stopped {
  font-size: 11px;
  color: var(--text-dim);
  font-style: italic;
}

.thinking {
  display: flex;
  align-items: center;
  gap: 6px;
  max-width: 100%;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.thought {
  white-space: pre-wrap;
  padding-left: 8px;
  border-left: 2px solid color-mix(in srgb, var(--c-focus) 50%, transparent);
}

.thought-toggle {
  font-size: 10px;
  color: var(--text-dim);
}

.thought-toggle:hover {
  color: var(--text);
}

.error {
  font-size: 11px;
  color: var(--c-hot);
}

.meta {
  font-size: 9.5px;
  color: var(--text-dim);
}

.spinner {
  flex: none;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  border: 1.5px solid rgba(255, 255, 255, 0.2);
  border-top-color: var(--c-focus);
  animation: spin 0.8s linear infinite;
}

.caret {
  display: inline-block;
  width: 6px;
  height: 11px;
  margin-left: 2px;
  vertical-align: -1px;
  border-radius: 1px;
  background: var(--text-dim);
  animation: blink 1s steps(2) infinite;
}

.composer {
  flex: none;
  display: flex;
  gap: 6px;
}

.prompt {
  flex: 1;
  min-width: 0;
  height: 26px;
  box-sizing: border-box;
  padding: 0 10px;
  border-radius: 999px;
  border: 1px solid rgba(255, 255, 255, 0.1);
  background: rgba(255, 255, 255, 0.06);
  color: var(--text);
  font: inherit;
  font-size: 12px;
  outline: none;
}

.prompt:focus {
  border-color: rgba(255, 255, 255, 0.25);
}

.prompt::placeholder {
  color: var(--text-dim);
}

.prompt:disabled {
  opacity: 0.5;
}

.send {
  flex: none;
  display: grid;
  place-items: center;
  width: 26px;
  height: 26px;
  border-radius: 50%;
  background: color-mix(in srgb, var(--c-focus) 45%, transparent);
  color: var(--text);
  transition:
    background 0.2s ease,
    opacity 0.2s ease;
}

.send:hover:not(:disabled) {
  background: color-mix(in srgb, var(--c-focus) 65%, transparent);
}

.send:disabled {
  opacity: 0.35;
}

.send.stop {
  background: rgba(255, 255, 255, 0.16);
}

@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}

@keyframes blink {
  50% {
    opacity: 0.3;
  }
}
</style>
