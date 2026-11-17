<script setup lang="ts">
/**
 * Bugün sekmesi: tarih, hafta şeridi ve hava durumu; halkalı odak sayacı;
 * günün yapılacakları.
 */
import { computed, nextTick, onBeforeUnmount, ref, toRef, watch } from "vue";
import { invoke } from "@tauri-apps/api/core";
import Tile from "./Tile.vue";
import WeatherGlyph from "./WeatherGlyph.vue";
import {
  DEVICE_NAME,
  describe,
  deviceLocation,
  searchCity,
  useWeather,
  type CityResult,
  type WeatherCity,
} from "../composables/useWeather";
import { useTodos } from "../composables/useTodos";
import { useReminders, clockTime, shortLeft } from "../composables/useReminders";
import type { FocusState } from "../composables/useFocusTimer";

const props = defineProps<{
  now: Date;
  active: boolean;
  city: WeatherCity | null;
  focusMinutes: number;
  focus: {
    state: FocusState;
    label: string;
    progress: number;
  };
}>();

const emit = defineEmits<{ focusToggle: []; focusReset: [] }>();

/* ---------- Tarih ve hafta ---------- */
const dayNum = computed(() => props.now.getDate());
const monthName = computed(() => props.now.toLocaleDateString("tr-TR", { month: "long" }));
const weekday = computed(() => props.now.toLocaleDateString("tr-TR", { weekday: "long" }));

const LETTERS = ["P", "S", "Ç", "P", "C", "C", "P"];
/** Pazartesiden başlayan bu hafta. */
const week = computed(() => {
  const d = new Date(props.now);
  const offset = (d.getDay() + 6) % 7;
  d.setDate(d.getDate() - offset);
  return LETTERS.map((letter, i) => {
    const day = new Date(d);
    day.setDate(d.getDate() + i);
    return { letter, date: day.getDate(), today: i === offset };
  });
});

/* ---------- Hava durumu ---------- */
const { weather, loading, error } = useWeather(toRef(props, "city"), toRef(props, "active"));
const desc = computed(() => (weather.value ? describe(weather.value.code, weather.value.isDay) : null));
const round = (n: number) => Math.round(n);

const editing = ref(false);
const query = ref("");
const results = ref<CityResult[]>([]);
const searching = ref(false);
const searchError = ref("");
const input = ref<HTMLInputElement | null>(null);
let debounce: number | undefined;

async function startEdit() {
  editing.value = true;
  query.value = "";
  results.value = [];
  searchError.value = "";
  await nextTick();
  input.value?.focus();
}

watch(query, (q) => {
  window.clearTimeout(debounce);
  searchError.value = "";
  if (q.trim().length < 2) {
    results.value = [];
    return;
  }
  debounce = window.setTimeout(async () => {
    searching.value = true;
    try {
      results.value = (await searchCity(q)).slice(0, 3);
      if (!results.value.length) searchError.value = "Bulunamadı";
    } catch {
      searchError.value = "Arama yapılamadı";
    } finally {
      searching.value = false;
    }
  }, 350);
});

/** Windows konum servisinden konumu al ve hava durumunu ona bağla. */
const locating = ref(false);
async function useDevice() {
  locating.value = true;
  searchError.value = "";
  try {
    const p = await deviceLocation();
    pick({ name: DEVICE_NAME, latitude: p.latitude, longitude: p.longitude, device: true });
  } catch (e) {
    searchError.value = String(e);
  } finally {
    locating.value = false;
  }
}

function pick(city: WeatherCity | null) {
  invoke("set_weather_city", { city }).catch((e) => console.error("set_weather_city", e));
  editing.value = false;
}

// Sekmeden çıkınca yarım kalan arama kapansın.
watch(
  () => props.active,
  (on) => {
    if (!on) editing.value = false;
  },
);

/* ---------- Odak ---------- */
const PRESETS = [15, 25, 45, 60];
const R = 30;
const C = 2 * Math.PI * R;
const focusColor = computed(() =>
  props.focus.state === "finished" ? "var(--c-seen)" : "var(--c-focus)",
);

function setMinutes(m: number) {
  invoke("settings_action", { id: `focus-${m}` }).catch((e) => console.error("settings_action", e));
}

/* ---------- Yapılacaklar ---------- */
const { todos, add, toggle, remove, clearDone } = useTodos();

/* Hatırlatıcılar: asistan kurar ("10 dakika sonra hatırlat"); burada görünür ve iptal edilir. */
const reminders = useReminders();
const draft = ref("");
const doneCount = computed(() => todos.value.filter((t) => t.done).length);
function submit() {
  add(draft.value);
  draft.value = "";
}

onBeforeUnmount(() => window.clearTimeout(debounce));
</script>

<template>
  <div class="today-panel">
    <!-- Tarih + hava -->
    <Tile title="Bugün" class="day-tile">
      <div class="date">
        <span class="num mono">{{ dayNum }}</span>
        <span class="names">
          <b>{{ monthName }}</b>
          <span>{{ weekday }}</span>
        </span>
        <!-- Şu anki sıcaklık tarihin sağında; tıklayınca şehir değişir. -->
        <button
          v-if="city && weather && desc && !editing"
          class="now-temp"
          :title="`${city.name} · değiştirmek için tıkla`"
          @click="startEdit"
        >
          <WeatherGlyph :icon="desc.icon" :size="20" />
          <span class="temp mono">{{ round(weather.temp) }}°</span>
        </button>
      </div>

      <template v-if="!editing">
        <div class="week" aria-label="Bu hafta">
          <span v-for="(d, i) in week" :key="i" class="wd" :class="{ today: d.today }">
            <small>{{ d.letter }}</small>
            <span class="mono">{{ d.date }}</span>
          </span>
        </div>

        <button v-if="!city" class="weather add" @click="startEdit">
          <WeatherGlyph icon="partly" :size="14" />
          <span>Hava durumu için konum ya da şehir seç</span>
        </button>
        <button v-else class="weather" :title="`${city.name} · değiştirmek için tıkla`" @click="startEdit">
          <span v-if="weather && desc" class="w-line">
            {{ desc.label }}
            <span class="mono">↑{{ round(weather.max) }}° ↓{{ round(weather.min) }}°</span>
            · {{ city.name }}
          </span>
          <span v-else class="w-line">{{ loading ? "Hava durumu alınıyor…" : error || city.name }}</span>
        </button>
      </template>

      <div v-else class="city-edit">
        <div class="city-row">
          <input
            ref="input"
            v-model="query"
            class="city-input"
            placeholder="Şehir ya da ilçe ara…"
            spellcheck="false"
            aria-label="Şehir ara"
            @keydown.esc="editing = false"
            @keydown.enter="results[0] && pick(results[0].city)"
          />
          <button v-if="city" class="mini-chip danger" title="Hava durumunu kaldır" @click="pick(null)">
            Kaldır
          </button>
          <button class="mini-chip" @click="editing = false">Vazgeç</button>
        </div>
        <ul class="results">
          <li v-if="!query.trim()">
            <button class="locate" :disabled="locating" @click="useDevice">
              <svg viewBox="0 0 24 24" width="11" height="11" aria-hidden="true">
                <path
                  d="M12 21s-6.5-5.6-6.5-11a6.5 6.5 0 0 1 13 0c0 5.4-6.5 11-6.5 11z"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2"
                  stroke-linejoin="round"
                />
                <circle cx="12" cy="10" r="2.2" fill="currentColor" />
              </svg>
              {{ locating ? "Konum alınıyor…" : "Konumumu kullan" }}
              <small v-if="city?.device">(şu an açık)</small>
            </button>
          </li>
          <li v-for="r in results" :key="`${r.city.latitude},${r.city.longitude}`">
            <button :title="r.label" @click="pick(r.city)">{{ r.label }}</button>
          </li>
          <li v-if="searching" class="dim">Aranıyor…</li>
          <li v-else-if="searchError" class="dim">{{ searchError }}</li>
        </ul>
      </div>
    </Tile>

    <!-- Halkalı odak sayacı -->
    <Tile
      title="Odak"
      class="focus-tile"
      :glow="focus.state === 'running' || focus.state === 'finished' ? focusColor : undefined"
      :pulse="focus.state === 'finished'"
    >
      <div class="focus">
        <button class="ring" :title="focus.state === 'running' ? 'Duraklat' : 'Başlat'" @click="emit('focusToggle')">
          <svg viewBox="0 0 72 72" width="76" height="76" aria-hidden="true">
            <circle cx="36" cy="36" :r="R" class="track" />
            <circle
              cx="36"
              cy="36"
              :r="R"
              class="value"
              :stroke="focusColor"
              :stroke-dasharray="C"
              :stroke-dashoffset="C * (1 - focus.progress)"
            />
          </svg>
          <span class="ring-label">
            <b class="mono">{{ focus.label }}</b>
            <small>{{ focus.state === "finished" ? "bitti" : focus.state === "running" ? "odak" : focus.state === "paused" ? "durdu" : "hazır" }}</small>
          </span>
        </button>
        <div class="focus-side">
          <div class="row">
            <button class="mini-chip strong" @click="emit('focusToggle')">
              {{ focus.state === "running" ? "Duraklat" : "Başlat" }}
            </button>
            <button v-if="focus.state !== 'idle'" class="mini-chip" @click="emit('focusReset')">Sıfırla</button>
          </div>
          <div class="presets" aria-label="Süre (dakika)">
            <button
              v-for="m in PRESETS"
              :key="m"
              class="mono"
              :class="{ active: focusMinutes === m }"
              :title="`${m} dakika`"
              @click="setMinutes(m)"
            >
              {{ m }}
            </button>
          </div>
          <small class="dim">Süre, sayaç sıfırlanınca uygulanır.</small>
        </div>
      </div>
    </Tile>

    <!-- Yapılacaklar -->
    <Tile title="Yapılacaklar" class="todo-tile">
      <template #head>
        <span class="todo-head">
          <span v-if="todos.length" class="mono dim">{{ doneCount }}/{{ todos.length }}</span>
          <button v-if="doneCount" class="mini-chip" title="Tamamlananları kaldır" @click="clearDone">Temizle</button>
        </span>
      </template>
      <input
        v-model="draft"
        class="todo-input"
        placeholder="Ekle ve Enter'a bas…"
        spellcheck="false"
        maxlength="200"
        aria-label="Yapılacak ekle"
        @keydown.enter="submit"
      />
      <div v-if="reminders.reminders.value.length" class="reminders">
        <span
          v-for="r in reminders.reminders.value"
          :key="r.id"
          class="reminder"
          :title="`${r.text}: ${clockTime(r.at)}`"
        >
          <span class="mono">{{ shortLeft(r.at, reminders.now.value) }}</span>
          <span class="r-text">{{ r.text }}</span>
          <button class="del" aria-label="Hatırlatıcıyı iptal et" @click="reminders.cancel(r.id)">✕</button>
        </span>
      </div>
      <ul v-if="todos.length" class="todos">
        <li v-for="t in todos" :key="t.id" :class="{ done: t.done }">
          <button class="check" :aria-pressed="t.done" :aria-label="t.done ? 'Geri al' : 'Tamamlandı'" @click="toggle(t.id)">
            <svg v-if="t.done" viewBox="0 0 24 24" width="10" height="10" aria-hidden="true">
              <path d="M5 12.5l4.5 4.5L19 7.5" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
          </button>
          <span class="t-text" :title="t.text">{{ t.text }}</span>
          <button class="del" aria-label="Sil" @click="remove(t.id)">✕</button>
        </li>
      </ul>
      <div v-else class="dim empty">Bugün için bir şey eklemedin.</div>
    </Tile>
  </div>
</template>

<style scoped>
.today-panel {
  flex: 1;
  display: flex;
  gap: 10px;
  min-height: 0;
}

.today-panel :deep(.tile-body) {
  justify-content: flex-start;
  gap: 7px;
  min-height: 0;
}

.day-tile {
  flex: 1.05;
}

.focus-tile {
  flex: 1;
}

.todo-tile {
  flex: 1.1;
}

.dim {
  color: var(--text-dim);
}

/* ---------- Tarih ---------- */
.date {
  display: flex;
  align-items: center;
  gap: 8px;
}

.date .num {
  font-size: 26px;
  font-weight: 700;
  line-height: 1;
}

.names {
  display: flex;
  flex-direction: column;
  line-height: 1.15;
  font-size: 11px;
  color: var(--text-dim);
}

.names b {
  font-size: 12px;
  color: var(--text);
  text-transform: capitalize;
}

.week {
  display: flex;
  justify-content: space-between;
}

.wd {
  display: flex;
  flex-direction: column;
  align-items: center;
  width: 22px;
  padding: 2px 0;
  border-radius: 8px;
  font-size: 10.5px;
}

.wd small {
  font-size: 8.5px;
  color: var(--text-dim);
}

.wd.today {
  background: var(--text);
  color: #111;
  font-weight: 700;
}

.wd.today small {
  color: rgba(0, 0, 0, 0.55);
}

/* ---------- Hava ---------- */
.weather {
  display: flex;
  align-items: center;
  gap: 7px;
  min-width: 0;
  margin: 0 -4px;
  padding: 2px 4px;
  border-radius: 8px;
  text-align: left;
  transition: background 0.2s ease;
}

.weather:hover {
  background: rgba(255, 255, 255, 0.07);
}

.weather.add,
.w-line {
  font-size: 10.5px;
  color: var(--text-dim);
}

.w-line {
  min-width: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.now-temp {
  margin-left: auto;
  display: flex;
  align-items: center;
  gap: 5px;
  padding: 2px 6px;
  border-radius: 10px;
  transition: background 0.2s ease;
}

.now-temp:hover {
  background: rgba(255, 255, 255, 0.07);
}

.temp {
  font-size: 18px;
  font-weight: 600;
}

.city-edit {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-height: 0;
}

.city-row {
  display: flex;
  gap: 4px;
  align-items: center;
}

.city-input,
.todo-input {
  flex: 1;
  min-width: 0;
  height: 22px;
  box-sizing: border-box;
  padding: 0 8px;
  border-radius: 999px;
  border: 1px solid rgba(255, 255, 255, 0.1);
  background: rgba(255, 255, 255, 0.06);
  color: var(--text);
  font: inherit;
  font-size: 11px;
  outline: none;
}

.city-input:focus,
.todo-input:focus {
  border-color: rgba(255, 255, 255, 0.25);
}

.city-input::placeholder,
.todo-input::placeholder {
  color: var(--text-dim);
}

.results {
  list-style: none;
  margin: 0;
  padding: 0;
  font-size: 11px;
}

.results button {
  width: 100%;
  padding: 2px 6px;
  border-radius: 6px;
  text-align: left;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.results button:hover {
  background: rgba(255, 255, 255, 0.1);
}

.results li.dim {
  padding: 2px 6px;
  /* İzin hatası iki satıra sığsın. */
  white-space: normal;
  display: -webkit-box;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 3;
  line-clamp: 3;
  overflow: hidden;
  font-size: 10px;
  line-height: 1.25;
}

.locate {
  display: flex;
  align-items: center;
  gap: 5px;
  color: var(--c-done);
}

.locate small {
  color: var(--text-dim);
}

.locate:disabled {
  opacity: 0.6;
  cursor: default;
}

/* ---------- Odak ---------- */
.focus {
  flex: 1;
  display: flex;
  align-items: center;
  gap: 10px;
}

.ring {
  position: relative;
  flex: none;
  border-radius: 50%;
}

.ring svg {
  display: block;
  transform: rotate(-90deg);
}

.ring circle {
  fill: none;
  stroke-width: 5;
}

.ring .track {
  stroke: rgba(255, 255, 255, 0.08);
}

.ring .value {
  stroke-linecap: round;
  transition:
    stroke-dashoffset 0.3s linear,
    stroke 0.4s ease;
}

.ring-label {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  line-height: 1.1;
}

.ring-label b {
  font-size: 15px;
}

.ring-label small {
  font-size: 9px;
  color: var(--text-dim);
}

.focus-side {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
}

.focus-side > small {
  font-size: 9px;
  line-height: 1.2;
}

.row {
  display: flex;
  gap: 4px;
}

.presets {
  display: flex;
  padding: 1px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.06);
}

.presets button {
  flex: 1;
  padding: 2px 0;
  border-radius: 999px;
  font-size: 10.5px;
  color: var(--text-dim);
}

.presets button.active {
  background: rgba(255, 255, 255, 0.16);
  color: var(--text);
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
}

.mini-chip:hover {
  background: rgba(255, 255, 255, 0.16);
}

.mini-chip.strong {
  background: color-mix(in srgb, var(--c-focus) 30%, transparent);
}

.mini-chip.strong:hover {
  background: color-mix(in srgb, var(--c-focus) 45%, transparent);
}

.mini-chip.danger:hover {
  background: rgba(255, 90, 90, 0.2);
  color: #ff8a8a;
}

/* ---------- Hatırlatıcılar ---------- */
.reminders {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.reminder {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-width: 100%;
  height: 18px;
  padding: 0 4px 0 7px;
  border-radius: 999px;
  background: rgba(255, 211, 77, 0.12);
  color: var(--c-waiting);
  font-size: 10.5px;
}

.reminder .r-text {
  min-width: 0;
  max-width: 90px;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
  color: var(--text);
}

.reminder .del {
  opacity: 0.6;
  font-size: 9px;
}

.reminder .del:hover {
  opacity: 1;
}

/* ---------- Yapılacaklar ---------- */
.todo-head {
  margin-left: auto;
  display: flex;
  align-items: center;
  gap: 6px;
  text-transform: none;
  letter-spacing: normal;
  font-weight: 500;
}

.todos {
  list-style: none;
  margin: 0 -4px;
  padding: 0;
  overflow-y: auto;
  min-height: 0;
  scrollbar-width: thin;
  scrollbar-color: rgba(255, 255, 255, 0.15) transparent;
}

.todos li {
  display: flex;
  align-items: center;
  gap: 7px;
  height: 22px;
  padding: 0 4px;
  border-radius: 7px;
  font-size: 11.5px;
}

.todos li:hover {
  background: rgba(255, 255, 255, 0.06);
}

.check {
  flex: none;
  display: grid;
  place-items: center;
  width: 14px;
  height: 14px;
  box-sizing: border-box;
  border-radius: 50%;
  border: 1.5px solid rgba(255, 255, 255, 0.35);
  color: #111;
  transition:
    background 0.2s ease,
    border-color 0.2s ease;
}

.done .check {
  background: var(--c-seen);
  border-color: var(--c-seen);
}

.t-text {
  flex: 1;
  min-width: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  transition: color 0.2s ease;
}

.done .t-text {
  color: var(--text-dim);
  text-decoration: line-through;
}

.del {
  flex: none;
  font-size: 10px;
  color: var(--text-dim);
  opacity: 0;
  transition: opacity 0.15s ease;
}

.todos li:hover .del {
  opacity: 1;
}

.del:hover {
  color: #ff8a8a;
}

.empty {
  font-size: 11px;
}
</style>
