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

defineProps<{
  settings: Settings;
  claudeConnected: boolean;
}>();

const FOCUS_CHOICES = [15, 25, 30, 45, 60];
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

    <Tile title="Sistem">
      <button class="switch-row" @click="act('claude-hooks')">
        <span>Claude Code'u bağla</span>
        <span class="switch" :class="{ on: claudeConnected }" />
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
