<script setup lang="ts">
/**
 * Pano sekmesi: solda geçmiş listesi (arama + tür filtresi), sağda üzerine
 * gelinen öğenin önizlemesi. Satıra tıklamak öğeyi yeniden panoya kopyalar.
 */
import { computed, onBeforeUnmount, ref, watch } from "vue";
import Tile from "./Tile.vue";
import { timeAgo, type ClipItem } from "../composables/useClipboard";
import { formatBytes } from "../composables/useSystemStats";

const props = defineProps<{
  items: ClipItem[];
  enabled: boolean;
}>();

const emit = defineEmits<{
  copy: [id: number];
  remove: [id: number];
  pin: [id: number];
  clear: [];
}>();

type Filter = "all" | "text" | "link" | "image";
/** Seçili filtreye yeniden basınca hepsi görünür ("Tümü" düğmesi yer kaplamasın). */
const FILTERS: { id: Exclude<Filter, "all">; label: string }[] = [
  { id: "text", label: "Metin" },
  { id: "link", label: "Bağlantı" },
  { id: "image", label: "Görsel" },
];

const filter = ref<Filter>("all");
const query = ref("");

const visible = computed(() => {
  const q = query.value.trim().toLocaleLowerCase("tr");
  return props.items.filter(
    (i) =>
      (filter.value === "all" || i.kind === filter.value) &&
      (!q || (i.text ?? "").toLocaleLowerCase("tr").includes(q)),
  );
});

/** Önizlemede gösterilen öğe: üzerine gelinen, yoksa listenin ilki. */
const hoveredId = ref<number | null>(null);
const selected = computed(
  () => visible.value.find((i) => i.id === hoveredId.value) ?? visible.value[0] ?? null,
);

/** Az önce kopyalanan satır kısa süre "Kopyalandı" der. */
const copiedId = ref<number | null>(null);
let copiedTimer: number | undefined;
function copy(id: number) {
  emit("copy", id);
  copiedId.value = id;
  window.clearTimeout(copiedTimer);
  copiedTimer = window.setTimeout(() => (copiedId.value = null), 1400);
}

/** "Şimdi" gibi göreli zamanlar dakikada bir yenilensin. */
const now = ref(Date.now());
const nowTimer = window.setInterval(() => (now.value = Date.now()), 30_000);
onBeforeUnmount(() => {
  window.clearInterval(nowTimer);
  window.clearTimeout(copiedTimer);
});
watch(
  () => props.items,
  () => (now.value = Date.now()),
);

function firstLine(text: string | null): string {
  return (text ?? "").trim().split(/\r?\n/)[0] ?? "";
}

function meta(i: ClipItem): string {
  if (i.kind === "image") return `${i.width}×${i.height} · ${formatBytes(i.size)}`;
  const kind = i.kind === "link" ? "Bağlantı" : "Metin";
  return `${kind} · ${i.size.toLocaleString("tr-TR")} karakter`;
}
</script>

<template>
  <div class="clip">
    <Tile title="Pano" class="list-tile">
      <template #head>
        <span class="head">
          <input
            v-model="query"
            class="search"
            type="search"
            placeholder="Ara…"
            spellcheck="false"
            aria-label="Pano geçmişinde ara"
          />
          <span class="filters" role="tablist">
            <button
              v-for="f in FILTERS"
              :key="f.id"
              role="tab"
              :aria-selected="filter === f.id"
              :class="{ active: filter === f.id }"
              @click="filter = filter === f.id ? 'all' : f.id"
            >
              {{ f.label }}
            </button>
          </span>
        </span>
      </template>

      <ul v-if="visible.length" class="list" @mouseleave="hoveredId = null">
        <li
          v-for="i in visible"
          :key="i.id"
          :class="{ current: selected?.id === i.id, copied: copiedId === i.id }"
          :title="'Tıkla: panoya kopyala'"
          @mouseenter="hoveredId = i.id"
          @click="copy(i.id)"
        >
          <img v-if="i.thumb" class="mini" :src="i.thumb" alt="" />
          <span v-else class="kind" :class="i.kind" aria-hidden="true">
            {{ i.kind === "link" ? "↗" : "¶" }}
          </span>
          <span class="line">
            {{ i.kind === "image" ? `Görsel ${i.width}×${i.height}` : firstLine(i.text) }}
          </span>
          <span v-if="copiedId === i.id" class="when ok">Kopyalandı</span>
          <span v-else class="when mono">
            <svg v-if="i.pinned" viewBox="0 0 24 24" width="10" height="10" aria-label="Sabit">
              <path fill="currentColor" d="M15 3l6 6-3 1-4 4 1 5-2 2-4-4-5 5-1-1 5-5-4-4 2-2 5 1 4-4z" />
            </svg>
            {{ timeAgo(i.time, now) }}
          </span>
        </li>
      </ul>
      <div v-else class="empty">
        <template v-if="!enabled">Pano geçmişi kapalı. Ayarlardan açabilirsin.</template>
        <template v-else-if="items.length">Eşleşen bir şey yok.</template>
        <template v-else>Kopyaladığın metin, bağlantı ve görseller burada birikir.</template>
      </div>
    </Tile>

    <Tile title="Önizleme" class="preview-tile">
      <template #head>
        <button
          v-if="items.length"
          class="head clear"
          title="Sabitlenenler hariç hepsini sil"
          @click="emit('clear')"
        >
          Temizle
        </button>
      </template>

      <template v-if="selected">
        <div class="preview">
          <img v-if="selected.thumb" :src="selected.thumb" alt="Kopyalanan görsel" />
          <p v-else class="text" :class="{ link: selected.kind === 'link' }">{{ selected.text }}</p>
        </div>
        <div class="foot">
          <span class="meta">{{ meta(selected) }}</span>
          <span class="actions">
            <button class="chip" @click="copy(selected.id)">Kopyala</button>
            <button class="chip" :class="{ on: selected.pinned }" @click="emit('pin', selected.id)">
              {{ selected.pinned ? "Sabit" : "Sabitle" }}
            </button>
            <button class="chip danger" title="Sil" aria-label="Sil" @click="emit('remove', selected.id)">
              ✕
            </button>
          </span>
        </div>
      </template>
      <div v-else class="empty">—</div>
    </Tile>
  </div>
</template>

<style scoped>
.clip {
  flex: 1;
  display: flex;
  gap: 10px;
  min-height: 0;
}

.list-tile {
  flex: 1.55;
}

.preview-tile {
  flex: 1;
}

.clip :deep(.tile-body) {
  justify-content: flex-start;
  min-height: 0;
}

/* Başlık satırındaki araçlar başlığın büyük harf stilini almasın. */
.head {
  margin-left: auto;
  display: flex;
  align-items: center;
  gap: 6px;
  text-transform: none;
  letter-spacing: normal;
  font-weight: 500;
}

.search {
  width: 84px;
  height: 20px;
  box-sizing: border-box;
  padding: 0 8px;
  border-radius: 999px;
  border: 1px solid rgba(255, 255, 255, 0.08);
  background: rgba(255, 255, 255, 0.06);
  color: var(--text);
  font: inherit;
  font-size: 11px;
  outline: none;
  transition:
    width 0.25s var(--ease),
    border-color 0.2s ease;
}

.search:focus {
  width: 120px;
  border-color: rgba(255, 255, 255, 0.22);
}

.search::placeholder {
  color: var(--text-dim);
}

.search::-webkit-search-cancel-button {
  display: none;
}

.filters {
  display: flex;
  padding: 1px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.06);
}

.filters button {
  padding: 2px 7px;
  border-radius: 999px;
  font-size: 10.5px;
  color: var(--text-dim);
  transition:
    background 0.2s ease,
    color 0.2s ease;
}

.filters button:hover,
.filters button.active {
  color: var(--text);
}

.filters button.active {
  background: rgba(255, 255, 255, 0.16);
}

/* ---------- Liste ---------- */
.list {
  list-style: none;
  margin: 0 -6px;
  padding: 0;
  overflow-y: auto;
  min-height: 0;
  scrollbar-width: thin;
  scrollbar-color: rgba(255, 255, 255, 0.15) transparent;
}

.list li {
  display: flex;
  align-items: center;
  gap: 8px;
  height: 24px;
  padding: 0 6px;
  border-radius: 8px;
  font-size: 12px;
  cursor: pointer;
  transition: background 0.15s ease;
}

.list li.current {
  background: rgba(255, 255, 255, 0.08);
}

.list li:active {
  background: rgba(255, 255, 255, 0.14);
}

.mini {
  flex: none;
  width: 16px;
  height: 16px;
  border-radius: 4px;
  object-fit: cover;
}

.kind {
  flex: none;
  display: grid;
  place-items: center;
  width: 16px;
  height: 16px;
  border-radius: 4px;
  font-size: 10px;
  background: rgba(255, 255, 255, 0.08);
  color: var(--text-dim);
}

.kind.link {
  color: var(--c-done);
}

.line {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.when {
  flex: none;
  display: flex;
  align-items: center;
  gap: 3px;
  font-size: 10.5px;
  color: var(--text-dim);
}

.when.ok {
  color: var(--c-seen);
  font-weight: 600;
}

.empty {
  margin: auto 0;
  font-size: 11px;
  color: var(--text-dim);
  text-align: center;
}

/* ---------- Önizleme ---------- */
.clear {
  padding: 1px 8px;
  border-radius: 999px;
  font-size: 10.5px;
  color: var(--text-dim);
  background: rgba(255, 255, 255, 0.06);
}

.clear:hover {
  color: #ff8a8a;
  background: rgba(255, 90, 90, 0.18);
}

.preview {
  flex: 1;
  min-height: 0;
  display: flex;
  overflow: hidden;
}

.preview img {
  max-width: 100%;
  max-height: 100%;
  margin: auto;
  border-radius: 8px;
  object-fit: contain;
}

.text {
  margin: 0;
  font-size: 11.5px;
  line-height: 1.4;
  white-space: pre-wrap;
  word-break: break-word;
  overflow: hidden;
  display: -webkit-box;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 4;
  line-clamp: 4;
}

.text.link {
  color: var(--c-done);
  word-break: break-all;
}

.foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
  margin-top: 6px;
}

.meta {
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
  font-size: 10px;
  color: var(--text-dim);
}

.actions {
  flex: none;
  display: flex;
  gap: 4px;
}

.chip {
  padding: 3px 8px;
  border-radius: 999px;
  font-size: 10.5px;
  background: rgba(255, 255, 255, 0.08);
}

.chip:hover {
  background: rgba(255, 255, 255, 0.16);
}

.chip.on {
  background: color-mix(in srgb, var(--c-waiting) 25%, transparent);
  color: var(--c-waiting);
}

.chip.danger:hover {
  background: rgba(255, 90, 90, 0.2);
  color: #ff8a8a;
}
</style>
