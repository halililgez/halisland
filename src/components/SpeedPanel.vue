<script setup lang="ts">
/**
 * Hız sekmesi: Cloudflare ile internet hız testi (useSpeedTest). Test
 * App.vue'de tutulur; ada kapanınca yarıda kalmaz, kompakt adada canlı hız
 * görünür.
 */
import { computed } from "vue";
import Tile from "./Tile.vue";
import type { useSpeedTest, SpeedPhase } from "../composables/useSpeedTest";

const props = defineProps<{
  speed: ReturnType<typeof useSpeedTest>;
  now: Date;
}>();

const PHASE_LABEL: Record<SpeedPhase, string> = {
  idle: "Hazır",
  latency: "Gecikme ölçülüyor",
  download: "İndirme",
  upload: "Yükleme",
  done: "Tamamlandı",
  error: "Hata",
};

const PHASE_COLOR: Partial<Record<SpeedPhase, string>> = {
  latency: "var(--c-waiting)",
  download: "var(--c-done)",
  upload: "var(--c-focus)",
};

const phase = computed(() => props.speed.phase.value);
const running = computed(() => props.speed.running.value);
const result = computed(() => props.speed.result.value);

/** Göstergedeki sayı: test sürerken canlı değer, bitince indirme. */
const gaugeValue = computed(() => {
  if (running.value) return props.speed.live.value;
  if (phase.value === "done") return result.value.down ?? 0;
  return 0;
});
const gaugeUnit = computed(() => (phase.value === "latency" ? "ms" : "Mbps"));
const gaugeColor = computed(() => PHASE_COLOR[phase.value] ?? "var(--c-done)");

/** Logaritmik ölçek: 0–1000 Mbps. Gecikmede ilerleme gösterilir. */
const gaugeFrac = computed(() => {
  if (phase.value === "latency") return props.speed.progress.value;
  const v = Math.max(0, gaugeValue.value);
  return Math.min(1, Math.log10(1 + v) / Math.log10(1001));
});

function fmtSpeed(v: number | undefined): string {
  if (v === undefined) return "–";
  if (v >= 100) return Math.round(v).toString();
  if (v >= 10) return v.toFixed(1);
  return v.toFixed(2);
}

const fmtMs = (v: number | undefined) => (v === undefined ? "–" : Math.round(v).toString());

const metrics = computed(() => [
  { id: "download", label: "İndirme", icon: "↓", value: fmtSpeed(result.value.down), unit: "Mbps" },
  { id: "upload", label: "Yükleme", icon: "↑", value: fmtSpeed(result.value.up), unit: "Mbps" },
  { id: "latency", label: "Ping", icon: "◷", value: fmtMs(result.value.ping), unit: "ms" },
  { id: "jitter", label: "Titreşim", icon: "≈", value: fmtMs(result.value.jitter), unit: "ms" },
]);

function ago(at: number): string {
  const s = Math.max(0, (props.now.getTime() - at) / 1000);
  if (s < 60) return "şimdi";
  if (s < 3600) return `${Math.floor(s / 60)} dk`;
  if (s < 86400) return `${Math.floor(s / 3600)} sa`;
  return `${Math.floor(s / 86400)} gün`;
}

function onButton() {
  if (running.value) props.speed.cancel();
  else props.speed.start();
}
</script>

<template>
  <div class="speed">
    <Tile title="Hız testi" class="gauge-tile" :glow="running ? gaugeColor : undefined">
      <template #head>
        <span v-if="result.colo" class="colo mono" title="Cloudflare veri merkezi">{{ result.colo }}</span>
      </template>
      <div class="gauge-row">
        <div class="gauge" :style="{ '--g': gaugeColor }">
          <svg viewBox="0 0 100 56" aria-hidden="true">
            <path class="g-track" d="M8 50 A42 42 0 0 1 92 50" pathLength="100" />
            <path
              class="g-value"
              d="M8 50 A42 42 0 0 1 92 50"
              pathLength="100"
              :stroke-dasharray="`${gaugeFrac * 100} 100`"
            />
          </svg>
          <div class="g-num mono">
            {{ phase === "latency" ? fmtMs(gaugeValue) : running || phase === "done" ? fmtSpeed(gaugeValue) : "–" }}
            <small>{{ gaugeUnit }}</small>
          </div>
        </div>
        <div class="gauge-side">
          <span class="phase" :class="{ err: phase === 'error' }">
            {{ phase === "error" ? speed.error.value : PHASE_LABEL[phase] }}
          </span>
          <button class="chip start" :class="{ stop: running }" @click="onButton">
            {{ running ? "Durdur" : phase === "done" || phase === "error" ? "Yeniden" : "Başlat" }}
          </button>
        </div>
      </div>
      <div class="bar" :class="{ on: running }">
        <i :style="{ width: `${running ? speed.progress.value * 100 : 0}%`, background: gaugeColor }" />
      </div>
    </Tile>

    <Tile title="Sonuç" class="result-tile">
      <div class="metrics">
        <div
          v-for="m in metrics"
          :key="m.id"
          class="metric"
          :class="{ active: running && phase === m.id }"
          :style="{ '--m': PHASE_COLOR[m.id as SpeedPhase] ?? 'var(--c-waiting)' }"
        >
          <span class="m-label"><b>{{ m.icon }}</b>{{ m.label }}</span>
          <span class="m-value mono">{{ m.value }}<small>{{ m.unit }}</small></span>
        </div>
      </div>
    </Tile>

    <Tile title="Geçmiş" class="history-tile">
      <ul v-if="speed.history.value.length" class="history">
        <li v-for="h in speed.history.value" :key="h.at">
          <span class="h-when">{{ ago(h.at) }}</span>
          <span class="mono"><b>↓</b>{{ fmtSpeed(h.down) }}</span>
          <span class="mono"><b>↑</b>{{ fmtSpeed(h.up) }}</span>
          <span class="mono dim">{{ fmtMs(h.ping) }} ms</span>
        </li>
      </ul>
      <div v-else class="dim empty">Henüz test yok. Ölçüm Cloudflare üzerinden yapılır, birkaç yüz MB veri kullanabilir.</div>
    </Tile>
  </div>
</template>

<style scoped>
.speed {
  flex: 1;
  display: flex;
  gap: 10px;
  min-height: 0;
}

.gauge-tile {
  flex: 1.25;
}

.result-tile {
  flex: 1.1;
}

.colo {
  margin-left: auto;
  padding: 0 6px;
  border-radius: 999px;
  font-size: 9px;
  background: rgba(255, 255, 255, 0.08);
  color: var(--text-dim);
}

.gauge-row {
  flex: 1;
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 0;
}

.gauge {
  position: relative;
  width: 118px;
  flex: none;
}

.gauge svg {
  display: block;
  width: 100%;
}

.g-track,
.g-value {
  fill: none;
  stroke-width: 7;
  stroke-linecap: round;
}

.g-track {
  stroke: rgba(255, 255, 255, 0.08);
}

.g-value {
  stroke: var(--g);
  transition:
    stroke-dasharray 0.3s ease,
    stroke 0.3s ease;
  filter: drop-shadow(0 0 4px color-mix(in srgb, var(--g) 60%, transparent));
}

.g-num {
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  font-size: 20px;
  font-weight: 600;
  line-height: 1;
}

.g-num small {
  margin-top: 2px;
  font-size: 9px;
  font-weight: 400;
  color: var(--text-dim);
}

.gauge-side {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 8px;
}

.phase {
  font-size: 12px;
  color: var(--text-dim);
}

.phase.err {
  color: #ff8a8a;
}

.chip {
  padding: 3px 12px;
  border-radius: 999px;
  font-size: 11px;
  font-weight: 600;
  background: rgba(255, 255, 255, 0.1);
}

.chip:hover {
  background: rgba(255, 255, 255, 0.18);
}

.chip.start:not(.stop) {
  background: color-mix(in srgb, var(--c-done) 35%, transparent);
}

.chip.start:not(.stop):hover {
  background: color-mix(in srgb, var(--c-done) 50%, transparent);
}

.bar {
  height: 3px;
  border-radius: 2px;
  background: rgba(255, 255, 255, 0.06);
  overflow: hidden;
  opacity: 0;
  transition: opacity 0.3s ease;
}

.bar.on {
  opacity: 1;
}

.bar i {
  display: block;
  height: 100%;
  transition: width 0.25s linear;
}

.metrics {
  flex: 1;
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 6px;
  min-height: 0;
}

.metric {
  display: flex;
  flex-direction: column;
  justify-content: center;
  gap: 2px;
  padding: 4px 8px;
  border-radius: 10px;
  background: rgba(255, 255, 255, 0.04);
  border: 1px solid transparent;
  transition:
    border-color 0.3s ease,
    background 0.3s ease;
}

.metric.active {
  border-color: color-mix(in srgb, var(--m) 55%, transparent);
  background: color-mix(in srgb, var(--m) 12%, transparent);
}

.m-label {
  font-size: 10px;
  color: var(--text-dim);
}

.m-label b {
  margin-right: 4px;
  font-weight: 600;
}

.m-value {
  font-size: 16px;
  font-weight: 600;
  white-space: nowrap;
}

.m-value small {
  margin-left: 3px;
  font-size: 9px;
  font-weight: 400;
  color: var(--text-dim);
}

.history {
  margin: 0;
  padding: 0;
  list-style: none;
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: 11px;
}

.history li {
  display: grid;
  grid-template-columns: 36px 1fr 1fr auto;
  gap: 6px;
  align-items: baseline;
}

.history b {
  margin-right: 2px;
  font-weight: 600;
  color: var(--text-dim);
}

.h-when {
  color: var(--text-dim);
}

.dim {
  color: var(--text-dim);
}

.empty {
  font-size: 11px;
  line-height: 1.4;
}
</style>
