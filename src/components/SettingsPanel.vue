<script setup lang="ts">
/**
 * Açık adanın içindeki ayar paneli (sağ tık ya da dişli düğmesi).
 * Değişiklikler Rust'a `settings_action` ile gider; kimlikler tepsi
 * menüsündekilerle aynıdır (src-tauri/src/tray.rs). Yeni değerler
 * `settings-changed` / `claude-hooks-changed` olaylarıyla geri gelir.
 */
import { ref } from "vue";
import { invoke } from "@tauri-apps/api/core";
import Tile from "./Tile.vue";
import type { Settings } from "../composables/useSettings";
import type { GlassMode } from "../composables/useGlass";
import type { Edge } from "../composables/useIsland";

defineProps<{
  settings: Settings;
  claudeConnected: boolean;
  /** Asistan yalnızca Windows'ta. */
  hasAssistant: boolean;
}>();

const FOCUS_CHOICES = [15, 25, 30, 45, 60];
/** Yön tuşları: 3x3 ızgarada yerleri (satır, sütun). */
const EDGE_KEYS: { edge: Edge; label: string; area: string; rotate: number }[] = [
  { edge: "top", label: "Üst kenar", area: "1 / 2", rotate: 0 },
  { edge: "left", label: "Sol kenar", area: "2 / 1", rotate: -90 },
  { edge: "right", label: "Sağ kenar", area: "2 / 3", rotate: 90 },
  { edge: "bottom", label: "Alt kenar", area: "3 / 2", rotate: 180 },
];

const GLASS_CHOICES: { mode: GlassMode; label: string }[] = [
  { mode: "acrylic", label: "Buzlu" },
  { mode: "blur", label: "Sade" },
  { mode: "off", label: "Kapalı" },
];

const glassSupported = ref(true);
invoke<boolean>("glass_supported")
  .then((v) => (glassSupported.value = v))
  .catch(() => (glassSupported.value = false));

function act(id: string) {
  invoke("settings_action", { id }).catch((e) => console.error("settings_action", e));
}
</script>

<template>
  <div class="tiles">
    <Tile title="Görünüm">
      <div class="field">
        <span class="label">
          Cam efekti
          <small v-if="!glassSupported">bu sistemde yok</small>
        </span>
        <div class="segmented" :class="{ disabled: !glassSupported }">
          <button
            v-for="c in GLASS_CHOICES"
            :key="c.mode"
            :class="{ active: settings.glass === c.mode }"
            :disabled="!glassSupported"
            @click="act(`glass-${c.mode}`)"
          >
            {{ c.label }}
          </button>
        </div>
      </div>
      <button class="switch-row" @click="act('tint')">
        <span>Kapak rengini al</span>
        <span class="switch" :class="{ on: settings.tint }" />
      </button>
    </Tile>

    <Tile class="edge-tile" title="Kenar">
      <div class="dpad" role="radiogroup" aria-label="Adanın yapışacağı kenar">
        <button
          v-for="k in EDGE_KEYS"
          :key="k.edge"
          role="radio"
          :aria-checked="settings.edge === k.edge"
          :aria-label="k.label"
          :title="k.label"
          :class="{ active: settings.edge === k.edge }"
          :style="{ gridArea: k.area }"
          @click="act(`edge-${k.edge}`)"
        >
          <svg viewBox="0 0 24 24" width="12" height="12" aria-hidden="true" :style="{ transform: `rotate(${k.rotate}deg)` }">
            <path d="M6 15l6-6 6 6" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round" />
          </svg>
        </button>
        <!-- Ortada küçük ekran: seçili kenar vurgulu. -->
        <span class="screen" :class="`on-${settings.edge}`" aria-hidden="true" />
      </div>
    </Tile>

    <Tile title="Odak süresi">
      <div class="focus-value mono">
        {{ settings.focusMinutes }}<span>dakika</span>
      </div>
      <div class="segmented">
        <button
          v-for="m in FOCUS_CHOICES"
          :key="m"
          class="mono"
          :class="{ active: settings.focusMinutes === m }"
          @click="act(`focus-${m}`)"
        >
          {{ m }}
        </button>
      </div>
    </Tile>

    <Tile class="system-tile" title="Sistem">
      <button class="switch-row" @click="act('claude-hooks')">
        <span>Claude Code'u bağla</span>
        <span class="switch" :class="{ on: claudeConnected }" />
      </button>
      <button
        v-if="hasAssistant"
        class="switch-row"
        title="Yerel dil modeliyle sohbet (Ollama). Güçlü bir ekran kartı ister."
        @click="act('assistant')"
      >
        <span>Asistan</span>
        <span class="switch" :class="{ on: settings.assistant }" />
      </button>
      <button
        class="switch-row"
        title="Kapatınca geçmiş silinir"
        @click="act('clipboard')"
      >
        <span>Pano geçmişi</span>
        <span class="switch" :class="{ on: settings.clipboard }" />
      </button>
      <button class="switch-row" @click="act('autostart')">
        <span>Oturum açılınca başlat</span>
        <span class="switch" :class="{ on: settings.autostart }" />
      </button>
    </Tile>
  </div>
</template>

<style scoped>
.tiles {
  flex: 1;
  display: flex;
  gap: 10px;
  min-height: 0;
}

.tiles :deep(.tile-body) {
  gap: 8px;
}

/* Dört anahtar (Asistan ile) 200 px'lik sayfaya sığsın. */
.system-tile :deep(.tile-body) {
  gap: 2px;
}

.edge-tile {
  flex: 0 0 auto;
}

.dpad {
  display: grid;
  grid-template-columns: repeat(3, 24px);
  grid-template-rows: repeat(3, 24px);
  gap: 3px;
  margin: auto;
}

.dpad button {
  display: grid;
  place-items: center;
  border-radius: 8px;
  background: rgba(255, 255, 255, 0.06);
  color: var(--text-dim);
  transition:
    background 0.2s ease,
    color 0.2s ease;
}

.dpad button:hover {
  background: rgba(255, 255, 255, 0.12);
  color: var(--text);
}

.dpad button.active {
  background: rgba(255, 255, 255, 0.2);
  color: var(--text);
  box-shadow: 0 1px 4px rgba(0, 0, 0, 0.25);
}

.dpad svg {
  transition: transform 0.2s ease;
}

.screen {
  grid-area: 2 / 2;
  place-self: center;
  position: relative;
  width: 18px;
  height: 14px;
  box-sizing: border-box;
  border-radius: 3px;
  border: 1.5px solid rgba(255, 255, 255, 0.3);
}

/* Seçili kenarda küçük bir ada. */
.screen::after {
  content: "";
  position: absolute;
  border-radius: 2px;
  background: var(--text);
  transition: all 0.25s var(--ease);
}

.screen.on-top::after,
.screen.on-bottom::after {
  left: 4px;
  right: 4px;
  height: 2px;
}

.screen.on-top::after {
  top: 1px;
}

.screen.on-bottom::after {
  bottom: 1px;
}

.screen.on-left::after,
.screen.on-right::after {
  top: 3px;
  bottom: 3px;
  width: 2px;
}

.screen.on-left::after {
  left: 1px;
}

.screen.on-right::after {
  right: 1px;
}

.field {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.label {
  font-size: 12px;
}

.label small {
  margin-left: 4px;
  font-size: 10px;
  color: var(--text-dim);
}

/* iOS tarzı bölümlü seçici */
.segmented {
  display: flex;
  padding: 2px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid rgba(255, 255, 255, 0.06);
}

.segmented button {
  flex: 1;
  min-width: 0;
  padding: 4px 0;
  border-radius: 999px;
  font-size: 11px;
  color: var(--text-dim);
  transition:
    background 0.2s ease,
    color 0.2s ease;
}

.segmented button:hover:not(:disabled) {
  color: var(--text);
}

.segmented button.active {
  background: rgba(255, 255, 255, 0.16);
  color: var(--text);
  box-shadow: 0 1px 4px rgba(0, 0, 0, 0.25);
}

.segmented.disabled {
  opacity: 0.4;
}

.segmented button:disabled {
  cursor: default;
}

.focus-value {
  font-size: 22px;
  font-weight: 600;
  line-height: 1.1;
}

.focus-value span {
  margin-left: 5px;
  font-size: 11px;
  font-weight: 400;
  color: var(--text-dim);
}

.switch-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 3px 0;
  font-size: 12px;
  text-align: left;
}

.switch-row:hover .switch {
  border-color: rgba(255, 255, 255, 0.22);
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
  transition:
    background 0.25s ease,
    border-color 0.25s ease;
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
  background: var(--c-seen);
  border-color: transparent;
}

.switch.on::after {
  transform: translateX(12px);
}
</style>
