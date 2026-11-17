<script setup lang="ts">
/**
 * Güç sekmesi. Kilitle ve uyku hemen çalışır. Oturumu kapat, yeniden başlat
 * ve kapat geri alınamadığı için önce sayfanın üstüne bir onay şeridi açılır:
 * "… mı?" [Vazgeç] [İşlem]. Aynı yere hızlıca iki kez tıklayıp farkında
 * olmadan onaylamamak için onay düğmesi kısa bir süre pasif kalır.
 * Sekmeden çıkınca, ada kapanınca ya da Esc'e basınca onay iptal olur.
 */
import { onBeforeUnmount, ref, watch } from "vue";
import { invoke } from "@tauri-apps/api/core";

type Action = "lock" | "sleep" | "logout" | "restart" | "shutdown";

interface PowerButton {
  id: Action;
  label: string;
  /** Onay şeridindeki soru; yoksa onaysız çalışır. */
  question?: string;
  /** SVG yolları (24x24, çizgi) */
  icon: string[];
}

const BUTTONS: PowerButton[] = [
  {
    id: "lock",
    label: "Kilitle",
    icon: ["M6 11h12v9H6z", "M8.5 11V8a3.5 3.5 0 0 1 7 0v3"],
  },
  {
    id: "sleep",
    label: "Uyku",
    icon: ["M19 14.5A7.5 7.5 0 0 1 9.5 5a7.5 7.5 0 1 0 9.5 9.5z"],
  },
  {
    id: "logout",
    label: "Oturumu kapat",
    question: "Oturum kapatılsın mı?",
    icon: ["M14 4H6v16h8", "M10 12h10", "M17 8.5l3.5 3.5-3.5 3.5"],
  },
  {
    id: "restart",
    label: "Yeniden başlat",
    question: "Bilgisayar yeniden başlatılsın mı?",
    icon: ["M19 12a7 7 0 1 1-2.05-4.95", "M19 4v4h-4"],
  },
  {
    id: "shutdown",
    label: "Kapat",
    question: "Bilgisayar kapatılsın mı?",
    icon: ["M12 3.5v8", "M7.05 6.6a7 7 0 1 0 9.9 0"],
  },
];

/** Onay düğmesi bu süre boyunca tıklanamaz (çift tıklama koruması). */
const ARM_DELAY_MS = 600;

const props = defineProps<{ active: boolean }>();

const pending = ref<PowerButton | null>(null);
const ready = ref(false);
const error = ref("");
let readyTimer: number | undefined;

function cancel() {
  if (readyTimer !== undefined) window.clearTimeout(readyTimer);
  readyTimer = undefined;
  pending.value = null;
  ready.value = false;
}

async function run(id: Action) {
  try {
    await invoke("power_action", { action: id });
  } catch (e) {
    error.value = String(e);
  }
}

function press(b: PowerButton) {
  error.value = "";
  if (!b.question) {
    cancel();
    run(b.id);
    return;
  }
  cancel();
  pending.value = b;
  readyTimer = window.setTimeout(() => (ready.value = true), ARM_DELAY_MS);
}

function confirm() {
  const b = pending.value;
  if (!b || !ready.value) return;
  cancel();
  run(b.id);
}

function onKey(e: KeyboardEvent) {
  if (e.key === "Escape" && pending.value) cancel();
}

watch(
  () => props.active,
  (on) => {
    if (!on) cancel();
  },
);

window.addEventListener("keydown", onKey);
onBeforeUnmount(() => {
  cancel();
  window.removeEventListener("keydown", onKey);
});
</script>

<template>
  <div class="power">
    <div class="buttons" :inert="!!pending">
      <button
        v-for="b in BUTTONS"
        :key="b.id"
        class="power-btn"
        :class="{ danger: !!b.question, chosen: pending?.id === b.id }"
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
        <span class="label">{{ b.label }}</span>
      </button>
    </div>

    <Transition name="confirm">
      <div v-if="pending" class="confirm" role="alertdialog" :aria-label="pending.question">
        <div class="question">
          <strong>{{ pending.question }}</strong>
          <span>Açık uygulamalardaki kaydedilmemiş işler kaybolabilir.</span>
        </div>
        <div class="actions">
          <button class="btn cancel" @click="cancel">Vazgeç</button>
          <button class="btn go" :disabled="!ready" @click="confirm">{{ pending.label }}</button>
        </div>
      </div>
    </Transition>

    <div v-if="error" class="error" role="alert">{{ error }}</div>
  </div>
</template>

<style scoped>
.power {
  position: relative;
  flex: 1;
  display: flex;
  min-height: 0;
}

.buttons {
  flex: 1;
  display: flex;
  gap: 10px;
  transition:
    filter 0.3s ease,
    opacity 0.3s ease;
}

/* Onay açıkken düğmeler geride bulanıklaşır. */
.buttons[inert] {
  filter: blur(3px);
  opacity: 0.35;
}

.power-btn {
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
  transition: color 0.25s ease;
}

.power-btn.danger:hover .icon,
.power-btn.chosen .icon {
  color: var(--c-hot);
}

.label {
  font-size: 12px;
  font-weight: 600;
  white-space: nowrap;
}

/* ---------- Onay şeridi ---------- */
.confirm {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 0 18px 0 22px;
  border-radius: 16px;
  background: color-mix(in srgb, var(--c-hot) 10%, rgba(20, 20, 24, 0.82));
  border: 1px solid color-mix(in srgb, var(--c-hot) 40%, transparent);
  box-shadow: 0 0 18px color-mix(in srgb, var(--c-hot) 22%, transparent);
}

.question {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.question strong {
  font-size: 15px;
  font-weight: 600;
}

.question span {
  font-size: 11px;
  color: var(--text-dim);
}

.actions {
  display: flex;
  gap: 8px;
  flex: none;
}

.btn {
  padding: 7px 16px;
  border-radius: 999px;
  font-size: 12px;
  font-weight: 600;
  transition:
    background 0.2s ease,
    opacity 0.2s ease;
}

.cancel {
  background: rgba(255, 255, 255, 0.12);
}

.cancel:hover {
  background: rgba(255, 255, 255, 0.2);
}

.go {
  background: var(--c-hot);
  color: #fff;
}

.go:hover:not(:disabled) {
  background: color-mix(in srgb, var(--c-hot) 85%, #fff);
}

.go:disabled {
  opacity: 0.4;
  cursor: default;
}

.confirm-enter-active,
.confirm-leave-active {
  transition:
    opacity 0.25s ease,
    transform 0.3s var(--ease);
}

.confirm-enter-from,
.confirm-leave-to {
  opacity: 0;
  transform: scale(0.97);
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
</style>
