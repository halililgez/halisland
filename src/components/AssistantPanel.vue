<script setup lang="ts">
/**
 * Asistan sekmesi: yerel modelle (Ollama) sohbet. Başlıkta durum, "Düşün"
 * anahtarı ve yeni sohbet; altta mesajlar ve yazma kutusu.
 * Enter gönderir, Esc yanıtı durdurur.
 */
import { computed, nextTick, ref, watch } from "vue";
import Tile from "./Tile.vue";
import type { Assistant, AssistantStatus, ChatMsg } from "../composables/useAssistant";

const props = defineProps<{ assistant: Assistant; active: boolean }>();
const a = props.assistant;

const draft = ref("");
const list = ref<HTMLElement | null>(null);
const input = ref<HTMLInputElement | null>(null);
/** Düşüncesi açılmış mesajlar. */
const openThoughts = ref(new Set<number>());

const STATUS_TEXT: Record<AssistantStatus, string> = {
  unsupported: "Asistan şimdilik yalnızca Windows'ta.",
  connecting: "Asistan açılıyor…",
  offline: "Asistan servisi çalışmıyor.",
  "no-ollama": "Ollama kapalı. Başlatınca burası kendiliğinden açılır.",
  "no-model": "",
  ready: "",
};

const statusText = computed(() =>
  a.status.value === "no-model"
    ? `${a.model.value} kurulu değil: ollama pull ${a.model.value}`
    : STATUS_TEXT[a.status.value],
);

const glow = computed(() => (a.busy.value ? "var(--c-focus)" : undefined));

function submit() {
  if (!a.ready.value || a.busy.value) return;
  const text = draft.value;
  if (!text.trim()) return;
  draft.value = "";
  a.send(text);
}

function onKey(e: KeyboardEvent) {
  if (e.key === "Escape" && a.busy.value) {
    e.preventDefault();
    a.stop();
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

/**
 * Modelin en sık kullandığı iki Markdown biçimi: **kalın** ve `kod`.
 * Metin önce HTML'den kaçırılır; v-html'e yalnızca bu iki etiket girer.
 */
function format(text: string): string {
  const escaped = text
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
  return escaped
    .replace(/\*\*([^*\n]+)\*\*/g, "<strong>$1</strong>")
    .replace(/`([^`\n]+)`/g, "<code>$1</code>")
    .replace(/^#{1,6}\s+/gm, "");
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
    if (!on) return;
    await nextTick();
    input.value?.focus({ preventScroll: true });
  },
);
</script>

<template>
  <div class="assistant">
    <Tile title="Asistan" class="chat-tile" :glow="glow" :pulse="a.busy.value">
      <template #head>
        <span class="head">
          <span v-if="a.ready.value" class="model mono" :title="`Model: ${a.model.value}`">{{ a.model.value }}</span>
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
            v-if="a.messages.value.length"
            class="mini-chip"
            title="Yeni sohbet"
            @click="a.clear()"
          >
            Yeni
          </button>
        </span>
      </template>

      <div class="body">
        <div v-if="!a.ready.value && !a.messages.value.length" class="empty">
          <span class="state-dot" :class="a.status.value" aria-hidden="true" />
          {{ statusText }}
        </div>
        <div v-else-if="!a.messages.value.length" class="empty hint">
          Bir şey sor. Her şey bu bilgisayarda kalır.
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
              <p v-else-if="m.streaming && !m.content" class="thinking">
                <span class="spinner" aria-hidden="true" />{{ a.think.value ? "Düşünüyor…" : "Yazıyor…" }}
              </p>
              <p v-if="m.content" class="text">
                <span v-html="format(m.content.trim())" /><span v-if="m.streaming" class="caret" />
              </p>
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
            :placeholder="a.ready.value ? 'Sor ve Enter\'a bas…' : 'Asistan hazır değil'"
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
  margin-left: auto;
  display: flex;
  align-items: center;
  gap: 6px;
}

.model {
  font-size: 9.5px;
  font-weight: 500;
  letter-spacing: normal;
  text-transform: none;
  opacity: 0.8;
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

.mini-chip:hover {
  background: rgba(255, 255, 255, 0.16);
  color: var(--text);
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
  align-items: center;
  justify-content: center;
  gap: 7px;
  font-size: 11.5px;
  color: var(--text-dim);
  text-align: center;
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

.state-dot.unsupported {
  background: rgba(255, 255, 255, 0.3);
}

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

.text :deep(strong) {
  font-weight: 650;
}

.thinking,
.thought {
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
