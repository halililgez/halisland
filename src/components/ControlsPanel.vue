<script setup lang="ts">
/**
 * Kontrol sekmesi: ses (kaydırıcı, sessiz, hızlı seviyeler) ve bağlantılar
 * (Wi-Fi, Bluetooth, bağlı ağ).
 */
import { computed } from "vue";
import Tile from "./Tile.vue";
import type { AudioState, RadioStatus, RadiosState } from "../composables/useControls";

const props = defineProps<{
  audio: AudioState | null;
  radios: RadiosState | null;
  audioError: string;
  radioError: string;
  busy: "wifi" | "bluetooth" | null;
}>();

const emit = defineEmits<{
  volume: [value: number];
  mute: [];
  radio: [kind: "wifi" | "bluetooth"];
  drag: [active: boolean];
}>();

const LEVELS = [0, 25, 50, 75, 100];

const percent = computed(() => Math.round((props.audio?.volume ?? 0) * 100));
const silent = computed(() => !props.audio || props.audio.muted || percent.value === 0);

/** Hoparlör simgesindeki dalga sayısı: 0 (sessiz) – 3. */
const waves = computed(() => {
  if (silent.value) return 0;
  return percent.value < 34 ? 1 : percent.value < 67 ? 2 : 3;
});

function onInput(e: Event) {
  emit("volume", Number((e.target as HTMLInputElement).value) / 100);
}

/** Sürükleme bitince (fare kaydırıcının dışında bırakılsa da) ölçümler yeniden işlensin. */
function startDrag() {
  emit("drag", true);
  const end = () => {
    emit("drag", false);
    window.removeEventListener("pointerup", end);
    window.removeEventListener("pointercancel", end);
  };
  window.addEventListener("pointerup", end);
  window.addEventListener("pointercancel", end);
}

/** Fare tekerleği: 2'şer adım. */
function onWheel(e: WheelEvent) {
  if (!props.audio) return;
  const step = e.deltaY < 0 ? 0.02 : -0.02;
  emit("volume", Math.round((props.audio.volume + step) * 50) / 50);
}

const RADIOS: { id: "wifi" | "bluetooth"; label: string }[] = [
  { id: "wifi", label: "Wi-Fi" },
  { id: "bluetooth", label: "Bluetooth" },
];

function radioText(id: "wifi" | "bluetooth", s: RadioStatus | undefined): string {
  switch (s) {
    case "on":
      return id === "wifi" && props.radios?.wlan && props.radios.network
        ? props.radios.network
        : "Açık";
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
  const r = props.radios;
  if (!r) return "";
  if (!r.network) return "İnternet bağlantısı yok";
  return r.wlan ? `Wi-Fi: ${r.network}` : `Kablolu: ${r.network}`;
});
</script>

<template>
  <div class="controls">
    <Tile title="Ses" class="sound-tile" :dim="!audio && !!audioError">
      <template #head>
        <span class="head" :title="audio?.device">{{ audio?.device }}</span>
      </template>

      <template v-if="audio">
        <div class="volume" @wheel.prevent="onWheel">
          <button
            class="speaker"
            :class="{ muted: silent }"
            :title="audio.muted ? 'Sesi aç' : 'Sessiz'"
            :aria-label="audio.muted ? 'Sesi aç' : 'Sessiz'"
            :aria-pressed="audio.muted"
            @click="emit('mute')"
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
            :class="{ muted: audio.muted }"
            aria-label="Ses seviyesi"
            @input="onInput"
            @pointerdown="startDrag"
          />
          <span class="pct mono">{{ audio.muted ? "–" : percent }}</span>
        </div>
        <div class="levels">
          <button
            v-for="l in LEVELS"
            :key="l"
            class="mono"
            :class="{ active: !audio.muted && percent === l }"
            @click="emit('volume', l / 100)"
          >
            {{ l }}
          </button>
        </div>
      </template>
      <div v-else class="dim msg">{{ audioError || "Ses okunuyor…" }}</div>
    </Tile>

    <Tile title="Bağlantılar" class="net-tile">
      <template v-if="radios">
        <button
          v-for="r in RADIOS"
          :key="r.id"
          class="radio-row"
          :class="[radios[r.id], { busy: busy === r.id }]"
          :disabled="radios[r.id] === 'missing' || radios[r.id] === 'disabled' || !!busy"
          @click="emit('radio', r.id)"
        >
          <span class="r-icon" aria-hidden="true">
            <svg v-if="r.id === 'wifi'" viewBox="0 0 24 24" width="16" height="16">
              <g fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
                <path d="M3 9a13 13 0 0 1 18 0" />
                <path d="M6.5 12.5a8 8 0 0 1 11 0" />
                <path d="M9.8 16a3.4 3.4 0 0 1 4.4 0" />
              </g>
              <circle cx="12" cy="19" r="1.4" fill="currentColor" />
            </svg>
            <svg v-else viewBox="0 0 24 24" width="16" height="16">
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
            <small>{{ radioText(r.id, radios[r.id]) }}</small>
          </span>
          <span
            v-if="radios[r.id] === 'on' || radios[r.id] === 'off'"
            class="switch"
            :class="{ on: radios[r.id] === 'on' }"
          />
        </button>
        <div class="net dim">
          <span>{{ networkText }}</span>
          <span v-if="radioError" class="err">{{ radioError }}</span>
        </div>
      </template>
      <div v-else class="dim msg">{{ radioError || "Bağlantılar okunuyor…" }}</div>
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
  flex: 1.35;
}

.net-tile {
  flex: 1;
}

.controls :deep(.tile-body) {
  gap: 8px;
  min-height: 0;
}

.dim {
  color: var(--text-dim);
}

.msg {
  font-size: 11px;
}

.head {
  margin-left: auto;
  min-width: 0;
  max-width: 70%;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
  text-transform: none;
  letter-spacing: normal;
  font-weight: 500;
}

/* ---------- Ses ---------- */
.volume {
  display: flex;
  align-items: center;
  gap: 10px;
}

.speaker {
  flex: none;
  display: grid;
  place-items: center;
  width: 36px;
  height: 36px;
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
  flex: 1;
  min-width: 0;
  height: 22px;
  margin: 0;
  background: transparent;
  appearance: none;
  -webkit-appearance: none;
  cursor: pointer;
}

.slider.muted {
  --fill: rgba(255, 255, 255, 0.3);
}

.slider::-webkit-slider-runnable-track {
  height: 8px;
  border-radius: 999px;
  background: linear-gradient(to right, var(--fill) var(--p), rgba(255, 255, 255, 0.1) var(--p));
}

.slider::-webkit-slider-thumb {
  -webkit-appearance: none;
  appearance: none;
  width: 16px;
  height: 16px;
  margin-top: -4px;
  border-radius: 50%;
  background: #fff;
  box-shadow: 0 1px 6px rgba(0, 0, 0, 0.45);
  transition: transform 0.15s ease;
}

.slider:active::-webkit-slider-thumb {
  transform: scale(1.15);
}

.slider::-moz-range-track {
  height: 8px;
  border-radius: 999px;
  background: linear-gradient(to right, var(--fill) var(--p), rgba(255, 255, 255, 0.1) var(--p));
}

.slider::-moz-range-thumb {
  width: 16px;
  height: 16px;
  border: none;
  border-radius: 50%;
  background: #fff;
}

.pct {
  flex: none;
  width: 30px;
  text-align: right;
  font-size: 16px;
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

/* ---------- Bağlantılar ---------- */
.radio-row {
  display: flex;
  align-items: center;
  gap: 9px;
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
  width: 28px;
  height: 28px;
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

.err {
  color: #ff9a9a;
}
</style>
