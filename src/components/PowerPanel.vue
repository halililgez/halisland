<script setup lang="ts">
/**
 * Güç sekmesi. Kilitle ve uyku hemen çalışır; oturumu kapat, yeniden başlat
 * ve kapat geri alınamadığı için önce "Emin misin?" der, ikinci tıklamada
 * çalışır. Onay birkaç saniye içinde verilmezse düğme eski hâline döner.
 */
import { onBeforeUnmount, ref } from "vue";
import { invoke } from "@tauri-apps/api/core";

type Action = "lock" | "sleep" | "logout" | "restart" | "shutdown";

interface PowerButton {
  id: Action;
  label: string;
  confirm: boolean;
  /** SVG yolları (24x24, çizgi) */
  icon: string[];
}

const BUTTONS: PowerButton[] = [
  {
    id: "lock",
    label: "Kilitle",
    confirm: false,
    icon: ["M6 11h12v9H6z", "M8.5 11V8a3.5 3.5 0 0 1 7 0v3"],
  },
  {
    id: "sleep",
    label: "Uyku",
    confirm: false,
    icon: ["M19 14.5A7.5 7.5 0 0 1 9.5 5a7.5 7.5 0 1 0 9.5 9.5z"],
  },
  {
    id: "logout",
    label: "Oturumu kapat",
    confirm: true,
    icon: ["M14 4H6v16h8", "M10 12h10", "M17 8.5l3.5 3.5-3.5 3.5"],
  },
  {
    id: "restart",
    label: "Yeniden başlat",
    confirm: true,
    icon: ["M19 12a7 7 0 1 1-2.05-4.95", "M19 4v4h-4"],
  },
  {
    id: "shutdown",
    label: "Kapat",
    confirm: true,
    icon: ["M12 3.5v8", "M7.05 6.6a7 7 0 1 0 9.9 0"],
  },
];

const CONFIRM_MS = 4000;
const armed = ref<Action | null>(null);
const error = ref("");
let disarm: number | undefined;

function clearArm() {
  if (disarm !== undefined) window.clearTimeout(disarm);
  disarm = undefined;
  armed.value = null;
}

async function press(b: PowerButton) {
  error.value = "";
  if (b.confirm && armed.value !== b.id) {
    clearArm();
    armed.value = b.id;
    disarm = window.setTimeout(clearArm, CONFIRM_MS);
    return;
  }
  clearArm();
  try {
    await invoke("power_action", { action: b.id });
  } catch (e) {
    error.value = String(e);
  }
}

onBeforeUnmount(clearArm);
defineExpose({ reset: clearArm });
</script>

<template>
  <div class="power">
    <button
      v-for="b in BUTTONS"
      :key="b.id"
      class="power-btn"
      :class="{ danger: b.confirm, armed: armed === b.id }"
      @click="press(b)"
    >
      <span class="icon">
        <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
          <path
            v-for="(d, i) in b.icon"
            :key="i"
            :d="d"
            fill="none"
            stroke="currentColor"
            stroke-width="1.9"
            stroke-linecap="round"
            stroke-linejoin="round"
          />
        </svg>
      </span>
      <span class="label">{{ armed === b.id ? "Emin misin?" : b.label }}</span>
      <span class="hint">{{ armed === b.id ? "Onay için tekrar bas" : "" }}</span>
    </button>
    <div v-if="error" class="error" role="alert">{{ error }}</div>
  </div>
</template>

<style scoped>
.power {
  position: relative;
  flex: 1;
  display: flex;
  gap: 10px;
  min-height: 0;
}

.power-btn {
  --tone: var(--text);
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  border-radius: 16px;
  background: var(--tile-bg);
  border: 1px solid var(--tile-border);
  transition:
    background 0.25s ease,
    border-color 0.25s ease,
    box-shadow 0.25s ease,
    transform 0.15s ease;
}

.power-btn:hover {
  background: rgba(255, 255, 255, 0.1);
}

.power-btn:active {
  transform: scale(0.97);
}

.icon {
  display: grid;
  place-items: center;
  width: 42px;
  height: 42px;
  border-radius: 50%;
  background: rgba(255, 255, 255, 0.08);
  color: var(--tone);
  transition:
    background 0.25s ease,
    color 0.25s ease;
}

.power-btn.danger:hover .icon {
  color: var(--c-hot);
}

.power-btn.armed {
  --tone: var(--c-hot);
  border-color: color-mix(in srgb, var(--c-hot) 55%, transparent);
  background: color-mix(in srgb, var(--c-hot) 14%, var(--tile-bg));
  box-shadow: 0 0 14px color-mix(in srgb, var(--c-hot) 30%, transparent);
  animation: armed 1s ease-in-out infinite;
}

.power-btn.armed .icon {
  background: color-mix(in srgb, var(--c-hot) 22%, transparent);
}

.label {
  font-size: 12px;
  font-weight: 600;
  white-space: nowrap;
}

.hint {
  min-height: 12px;
  margin-top: -4px;
  font-size: 9.5px;
  color: var(--text-dim);
}

.error {
  position: absolute;
  left: 50%;
  bottom: 2px;
  transform: translateX(-50%);
  padding: 2px 10px;
  border-radius: 999px;
  font-size: 10px;
  background: rgba(255, 90, 90, 0.2);
  color: #ffb0b0;
}

@keyframes armed {
  50% {
    box-shadow: 0 0 22px color-mix(in srgb, var(--c-hot) 45%, transparent);
  }
}
</style>
