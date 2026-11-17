<script setup lang="ts">
/** Yüzde halkası: ortasında değer, altında etiket. */
import { computed } from "vue";

const props = defineProps<{
  /** 0–100 */
  value: number;
  label: string;
  color: string;
}>();

const R = 22;
const C = 2 * Math.PI * R;
const clamped = computed(() => Math.min(100, Math.max(0, props.value)));
/** %85'in üstünde halka uyarı rengine döner. */
const stroke = computed(() => (clamped.value >= 85 ? "var(--c-hot)" : props.color));
</script>

<template>
  <div class="ring">
    <svg viewBox="0 0 54 54" width="54" height="54" aria-hidden="true">
      <circle cx="27" cy="27" :r="R" class="track" />
      <circle
        cx="27"
        cy="27"
        :r="R"
        class="value"
        :stroke="stroke"
        :stroke-dasharray="C"
        :stroke-dashoffset="C * (1 - clamped / 100)"
      />
    </svg>
    <span class="num mono">{{ Math.round(clamped) }}<small>%</small></span>
    <span class="label">{{ label }}</span>
  </div>
</template>

<style scoped>
.ring {
  position: relative;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
}

svg {
  transform: rotate(-90deg);
}

circle {
  fill: none;
  stroke-width: 5;
}

.track {
  stroke: rgba(255, 255, 255, 0.08);
}

.value {
  stroke-linecap: round;
  transition:
    stroke-dashoffset 0.8s var(--ease),
    stroke 0.4s ease;
}

.num {
  position: absolute;
  top: 0;
  width: 54px;
  height: 54px;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 14px;
  font-weight: 600;
}

.num small {
  font-size: 9px;
  font-weight: 500;
  color: var(--text-dim);
}

.label {
  font-size: 10px;
  color: var(--text-dim);
  white-space: nowrap;
}
</style>
