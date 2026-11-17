<script setup lang="ts">
/**
 * Kontrol sekmesi:
 * - Ses: kaydırıcı, sessiz, hızlı seviyeler, çıkış aygıtı seçici.
 * - Ekran: monitör başına parlaklık (DDC/CI ya da dizüstü WMI), gece ışığı.
 * - Bağlantılar: Wi-Fi, Bluetooth, bağlı ağ.
 * Durumlar yalnızca sekme görünürken sorulur (`active`).
 */
import { computed, ref, toRef, watch } from "vue";
import Tile from "./Tile.vue";
import { useControls, type RadioStatus } from "../composables/useControls";
import { useDisplay } from "../composables/useDisplay";

const props = defineProps<{ active: boolean }>();
const active = toRef(props, "active");

const c = useControls(active);
const d = useDisplay(active);

/* ---------------- Ses ---------------- */
const LEVELS = [0, 25, 50, 75, 100];
const percent = computed(() => Math.round((c.audio.value?.volume ?? 0) * 100));
const silent = computed(() => !c.audio.value || c.audio.value.muted || percent.value === 0);
const waves = computed(() => {
  if (silent.value) return 0;
  return percent.value < 34 ? 1 : percent.value < 67 ? 2 : 3;
});

/** Sürükleme bitince (fare kaydırıcının dışında bırakılsa da) ölçümler yeniden işlensin. */
function onRelease(end: () => void) {
  const stop = () => {
    end();
    window.removeEventListener("pointerup", stop);
    window.removeEventListener("pointercancel", stop);
  };
  window.addEventListener("pointerup", stop);
  window.addEventListener("pointercancel", stop);
}

function startVolumeDrag() {
  c.dragging.value = true;
  onRelease(() => (c.dragging.value = false));
}

function onVolumeWheel(e: WheelEvent) {
  if (!c.audio.value) return;
  const step = e.deltaY < 0 ? 0.02 : -0.02;
  c.setVolume(Math.round((c.audio.value.volume + step) * 50) / 50);
}

/** Çıkış aygıtı seçici (Ses kutucuğunun içinde açılır). */
const picking = ref(false);
function togglePicker() {
  picking.value = !picking.value;
  if (picking.value) c.loadDevices();
}
function pickDevice(id: string) {
  c.setDevice(id);
  picking.value = false;
}
watch(active, (on) => {
  if (!on) picking.value = false;
});

/* ---------------- Ekran ---------------- */
function startBrightnessDrag(id: string) {
  d.dragging.value = id;
  onRelease(() => (d.dragging.value = null));
}

function onBrightnessWheel(e: WheelEvent, id: string, current: number) {
  d.setBrightness(id, current + (e.deltaY < 0 ? 5 : -5));
}

/* ---------------- Bağlantılar ---------------- */
const RADIOS: { id: "wifi" | "bluetooth"; label: string }[] = [
  { id: "wifi", label: "Wi-Fi" },
  { id: "bluetooth", label: "Bluetooth" },
];

function radioText(id: "wifi" | "bluetooth", s: RadioStatus | undefined): string {
  const r = c.radios.value;
  switch (s) {
    case "on":
      return id === "wifi" && r?.wlan && r.network ? r.network : "Açık";
    case "off":
      return "Kapalı";
    case "disabled":
      return "Uçak modu ya da donanım anahtarı kapalı";
    case "missing":
      return "Bu bilgisayarda yok";
    default:
      return "…";
  }
}

const networkText = computed(() => {
  const r = c.radios.value;
  if (!r) return "";
  if (!r.network) return "İnternet bağlantısı yok";
  return r.wlan ? `Wi-Fi: ${r.network}` : `Kablolu: ${r.network}`;
});
</script>

<template>
  <div class="controls">
    <!-- Ses -->
    <Tile title="Ses" class="sound-tile" :dim="!c.audio.value && !!c.audioError.value">
      <template #head>
        <button
          v-if="c.audio.value"
          class="device"
          :class="{ open: picking }"
          :title="`${c.audio.value.device} · çıkış aygıtını değiştir`"
          @click="togglePicker"
        >
          <span>{{ c.audio.value.device }}</span>
          <svg viewBox="0 0 24 24" width="9" height="9" aria-hidden="true">
            <path d="M6 9l6 6 6-6" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round" />
          </svg>
        </button>
      </template>

      <ul v-if="picking" class="devices" @keydown.esc="picking = false">
        <li v-for="dev in c.devices.value" :key="dev.id">
          <button :class="{ current: dev.default }" :disabled="c.switching.value" @click="pickDevice(dev.id)">
            <span class="dot" aria-hidden="true" />
            <span class="dev-name" :title="dev.name">{{ dev.name }}</span>
          </button>
        </li>
        <li v-if="!c.devices.value.length" class="dim msg">Aygıtlar okunuyor…</li>
      </ul>

      <template v-else-if="c.audio.value">
        <div class="volume" @wheel.prevent="onVolumeWheel">
          <button
            class="speaker"
            :class="{ muted: silent }"
            :title="c.audio.value.muted ? 'Sesi aç' : 'Sessiz'"
            :aria-label="c.audio.value.muted ? 'Sesi aç' : 'Sessiz'"
            :aria-pressed="c.audio.value.muted"
            @click="c.toggleMute"
          >
            <svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">
              <path d="M4 9.5h3.5L12 5.5v13l-4.5-4H4z" fill="currentColor" />
              <g fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round">
                <path v-if="waves >= 1" d="M15 9.5a3.5 3.5 0 0 1 0 5" />
                <path v-if="waves >= 2" d="M17.3 7.5a6.5 6.5 0 0 1 0 9" />
                <path v-if="waves >= 3" d="M19.6 5.5a9.5 9.5 0 0 1 0 13" />
                <path v-if="waves === 0" d="M16 9.5l5 5M21 9.5l-5 5" />
              </g>
            </svg>
          </button>
          <input
            class="slider"
            type="range"
            min="0"
            max="100"
            step="1"
            :value="percent"
            :style="{ '--p': `${percent}%` }"
            :class="{ muted: c.audio.value.muted }"
            aria-label="Ses seviyesi"
            @input="c.setVolume(Number(($event.target as HTMLInputElement).value) / 100)"
            @pointerdown="startVolumeDrag"
          />
          <span class="pct mono">{{ c.audio.value.muted ? "–" : percent }}</span>
        </div>
        <div class="levels">
          <button
            v-for="l in LEVELS"
            :key="l"
            class="mono"
            :class="{ active: !c.audio.value.muted && percent === l }"
            @click="c.setVolume(l / 100)"
          >
            {{ l }}
          </button>
        </div>
      </template>
      <div v-else class="dim msg">{{ c.audioError.value || "Ses okunuyor…" }}</div>
    </Tile>

    <!-- Ekran -->
    <Tile title="Ekran" class="screen-tile">
      <template v-if="d.state.value">
        <div class="screens">
          <div
            v-for="s in d.state.value.screens"
            :key="s.id"
            class="screen-row"
            @wheel.prevent="onBrightnessWheel($event, s.id, s.brightness)"
          >
            <span class="s-name" :title="s.name">{{ s.name }}</span>
            <input
              class="slider small"
              type="range"
              min="0"
              max="100"
              step="1"
              :value="s.brightness"
              :style="{ '--p': `${s.brightness}%` }"
              :aria-label="`${s.name} parlaklığı`"
              @input="d.setBrightness(s.id, Number(($event.target as HTMLInputElement).value))"
              @pointerdown="startBrightnessDrag(s.id)"
            />
            <span class="s-pct mono">{{ s.brightness }}</span>
          </div>
          <div v-if="!d.state.value.screens.length" class="dim msg">
            Parlaklık ayarlanamıyor. Monitörün menüsünden DDC/CI'yi açmayı dene.
          </div>
        </div>

        <button
          v-if="d.state.value.nightLight !== null"
          class="night-row"
          :class="{ on: d.state.value.nightLight }"
          :disabled="d.nightBusy.value"
          @click="d.toggleNight"
        >
          <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
            <path d="M19 14.5A7.5 7.5 0 0 1 9.5 5a7.5 7.5 0 1 0 9.5 9.5z" fill="currentColor" />
          </svg>
          <span>Gece ışığı</span>
          <span class="switch warm" :class="{ on: d.state.value.nightLight }" />
        </button>
        <button v-else class="night-row" title="Windows ayarlarında aç" @click="d.openNightSettings">
          <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
            <path d="M19 14.5A7.5 7.5 0 0 1 9.5 5a7.5 7.5 0 1 0 9.5 9.5z" fill="currentColor" />
          </svg>
          <span>Gece ışığı ayarları</span>
          <span class="dim">↗</span>
        </button>
        <div v-if="d.error.value" class="err msg">{{ d.error.value }}</div>
      </template>
      <div v-else class="dim msg">{{ d.error.value || "Ekranlar okunuyor…" }}</div>
    </Tile>

    <!-- Bağlantılar -->
    <Tile title="Bağlantılar" class="net-tile">
      <template v-if="c.radios.value">
        <button
          v-for="r in RADIOS"
          :key="r.id"
          class="radio-row"
          :class="[c.radios.value[r.id], { busy: c.busy.value === r.id }]"
          :disabled="c.radios.value[r.id] === 'missing' || c.radios.value[r.id] === 'disabled' || !!c.busy.value"
          @click="c.toggleRadio(r.id)"
        >
          <span class="r-icon" aria-hidden="true">
            <svg v-if="r.id === 'wifi'" viewBox="0 0 24 24" width="15" height="15">
              <g fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
                <path d="M3 9a13 13 0 0 1 18 0" />
                <path d="M6.5 12.5a8 8 0 0 1 11 0" />
                <path d="M9.8 16a3.4 3.4 0 0 1 4.4 0" />
              </g>
              <circle cx="12" cy="19" r="1.4" fill="currentColor" />
            </svg>
            <svg v-else viewBox="0 0 24 24" width="15" height="15">
              <path
                d="M7 7.5l10 9-5 4.5v-18l5 4.5-10 9"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"
              />
            </svg>
          </span>
          <span class="r-text">
            <b>{{ r.label }}</b>
            <small>{{ radioText(r.id, c.radios.value[r.id]) }}</small>
          </span>
          <span
            v-if="c.radios.value[r.id] === 'on' || c.radios.value[r.id] === 'off'"
            class="switch"
            :class="{ on: c.radios.value[r.id] === 'on' }"
          />
        </button>
        <div class="net dim">
          <span>{{ networkText }}</span>
          <span v-if="c.radioError.value" class="err">{{ c.radioError.value }}</span>
        </div>
      </template>
      <div v-else class="dim msg">{{ c.radioError.value || "Bağlantılar okunuyor…" }}</div>
    </Tile>
  </div>
</template>

<style scoped>
.controls {
  flex: 1;
  display: flex;
  gap: 10px;
  min-height: 0;
}

.sound-tile {
  flex: 1.25;
}

.screen-tile {
  flex: 1;
}

.net-tile {
  flex: 0.95;
}

.controls :deep(.tile-body) {
  justify-content: flex-start;
  gap: 8px;
  min-height: 0;
}

.dim {
  color: var(--text-dim);
}

.msg {
  font-size: 10.5px;
  line-height: 1.3;
}

.err {
  color: #ff9a9a;
}

/* ---------- Aygıt seçici ---------- */
.device {
  margin-left: auto;
  min-width: 0;
  max-width: 78%;
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 1px 6px;
  border-radius: 999px;
  text-transform: none;
  letter-spacing: normal;
  font-weight: 500;
  color: var(--text-dim);
  transition:
    background 0.2s ease,
    color 0.2s ease;
}

.device span {
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.device svg {
  flex: none;
  transition: transform 0.25s var(--ease);
}

.device:hover,
.device.open {
  background: rgba(255, 255, 255, 0.1);
  color: var(--text);
}

.device.open svg {
  transform: rotate(180deg);
}

.devices {
  list-style: none;
  margin: 0 -6px;
  padding: 0;
  overflow-y: auto;
  min-height: 0;
  scrollbar-width: thin;
  scrollbar-color: rgba(255, 255, 255, 0.15) transparent;
}

.devices button {
  width: 100%;
  display: flex;
  align-items: center;
  gap: 8px;
  height: 23px;
  padding: 0 6px;
  border-radius: 8px;
  font-size: 11.5px;
  text-align: left;
}

.devices button:hover:not(:disabled) {
  background: rgba(255, 255, 255, 0.08);
}

.dot {
  flex: none;
  width: 10px;
  height: 10px;
  box-sizing: border-box;
  border-radius: 50%;
  border: 1.5px solid rgba(255, 255, 255, 0.35);
}

.current .dot {
  border: 3px solid var(--c-done);
}

.dev-name {
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.current .dev-name {
  font-weight: 600;
}

/* ---------- Ses ---------- */
.volume {
  display: flex;
  align-items: center;
  gap: 9px;
}

.speaker {
  flex: none;
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  border-radius: 50%;
  background: rgba(255, 255, 255, 0.1);
  transition:
    background 0.2s ease,
    color 0.2s ease;
}

.speaker:hover {
  background: rgba(255, 255, 255, 0.18);
}

.speaker.muted {
  color: var(--c-hot);
  background: color-mix(in srgb, var(--c-hot) 15%, transparent);
}

.slider {
  --p: 0%;
  --fill: var(--text);
  --track: 8px;
  --thumb: 16px;
  flex: 1;
  min-width: 0;
  height: 22px;
  margin: 0;
  background: transparent;
  appearance: none;
  -webkit-appearance: none;
  cursor: pointer;
}

.slider.small {
  --track: 6px;
  --thumb: 13px;
  height: 18px;
}

.slider.muted {
  --fill: rgba(255, 255, 255, 0.3);
}

.slider::-webkit-slider-runnable-track {
  height: var(--track);
  border-radius: 999px;
  background: linear-gradient(to right, var(--fill) var(--p), rgba(255, 255, 255, 0.1) var(--p));
}

.slider::-webkit-slider-thumb {
  -webkit-appearance: none;
  appearance: none;
  width: var(--thumb);
  height: var(--thumb);
  margin-top: calc((var(--track) - var(--thumb)) / 2);
  border-radius: 50%;
  background: #fff;
  box-shadow: 0 1px 6px rgba(0, 0, 0, 0.45);
  transition: transform 0.15s ease;
}

.slider:active::-webkit-slider-thumb {
  transform: scale(1.15);
}

.slider::-moz-range-track {
  height: var(--track);
  border-radius: 999px;
  background: linear-gradient(to right, var(--fill) var(--p), rgba(255, 255, 255, 0.1) var(--p));
}

.slider::-moz-range-thumb {
  width: var(--thumb);
  height: var(--thumb);
  border: none;
  border-radius: 50%;
  background: #fff;
}

.pct {
  flex: none;
  width: 28px;
  text-align: right;
  font-size: 15px;
  font-weight: 600;
}

.levels {
  display: flex;
  padding: 2px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid rgba(255, 255, 255, 0.06);
}

.levels button {
  flex: 1;
  padding: 3px 0;
  border-radius: 999px;
  font-size: 11px;
  color: var(--text-dim);
  transition:
    background 0.2s ease,
    color 0.2s ease;
}

.levels button:hover {
  color: var(--text);
}

.levels button.active {
  background: rgba(255, 255, 255, 0.16);
  color: var(--text);
}

/* ---------- Ekran ---------- */
.screens {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-height: 0;
  overflow-y: auto;
  scrollbar-width: none;
}

.screen-row {
  display: flex;
  align-items: center;
  gap: 7px;
}

.s-name {
  flex: none;
  width: 52px;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
  font-size: 10.5px;
  color: var(--text-dim);
}

.screen-row .slider {
  --fill: var(--c-waiting);
}

.s-pct {
  flex: none;
  width: 22px;
  text-align: right;
  font-size: 11px;
}

.night-row {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: auto -6px 0;
  padding: 3px 6px;
  border-radius: 10px;
  font-size: 12px;
  text-align: left;
  transition: background 0.2s ease;
}

.night-row span:first-of-type {
  flex: 1;
}

.night-row svg {
  color: var(--text-dim);
  transition: color 0.25s ease;
}

.night-row.on svg {
  color: #ffb45c;
}

.night-row:hover:not(:disabled) {
  background: rgba(255, 255, 255, 0.07);
}

.night-row:disabled {
  opacity: 0.7;
  cursor: default;
}

/* ---------- Bağlantılar ---------- */
.radio-row {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0 -6px;
  padding: 3px 6px;
  border-radius: 10px;
  text-align: left;
  transition:
    background 0.2s ease,
    opacity 0.2s ease;
}

.radio-row:hover:not(:disabled) {
  background: rgba(255, 255, 255, 0.07);
}

.radio-row:disabled {
  cursor: default;
}

.radio-row.missing,
.radio-row.disabled {
  opacity: 0.5;
}

.radio-row.busy {
  opacity: 0.7;
}

.r-icon {
  flex: none;
  display: grid;
  place-items: center;
  width: 26px;
  height: 26px;
  border-radius: 50%;
  background: rgba(255, 255, 255, 0.08);
  color: var(--text-dim);
  transition:
    background 0.25s ease,
    color 0.25s ease;
}

.radio-row.on .r-icon {
  background: var(--c-done);
  color: #fff;
}

.r-text {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  line-height: 1.2;
}

.r-text b {
  font-size: 12px;
  font-weight: 600;
}

.r-text small {
  font-size: 10px;
  color: var(--text-dim);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.switch {
  position: relative;
  flex: none;
  width: 30px;
  height: 18px;
  box-sizing: border-box;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.1);
  border: 1px solid rgba(255, 255, 255, 0.1);
  transition: background 0.25s ease;
}

.switch::after {
  content: "";
  position: absolute;
  top: 2px;
  left: 2px;
  width: 12px;
  height: 12px;
  border-radius: 50%;
  background: var(--text);
  transition: transform 0.25s var(--ease);
}

.switch.on {
  background: var(--c-done);
  border-color: transparent;
}

.switch.warm.on {
  background: #f59a3c;
}

.switch.on::after {
  transform: translateX(12px);
}

.net {
  display: flex;
  flex-direction: column;
  gap: 2px;
  margin-top: auto;
  font-size: 10px;
}

.net span {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
</style>
