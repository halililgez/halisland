<script setup lang="ts">
/**
 * Genişletilmiş adadaki kutucuk. `glow` verilirse o renkte parlar,
 * `pulse` ile nabız gibi atar (ör. Claude çalışırken).
 */
defineProps<{
  title: string;
  glow?: string;
  pulse?: boolean;
  dim?: boolean;
}>();
</script>

<template>
  <div
    class="tile"
    :class="{ glowing: !!glow, pulse, dim }"
    :style="glow ? { '--glow': glow } : undefined"
  >
    <div class="tile-title">{{ title }}</div>
    <div class="tile-body"><slot /></div>
  </div>
</template>

<style scoped>
.tile {
  --glow: transparent;
  position: relative;
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px 12px;
  border-radius: 16px;
  background: var(--tile-bg);
  border: 1px solid var(--tile-border);
  transition:
    box-shadow 0.6s ease,
    border-color 0.6s ease,
    background 0.6s ease,
    opacity 0.4s ease;
}

.tile.glowing {
  border-color: color-mix(in srgb, var(--glow) 55%, transparent);
  background: color-mix(in srgb, var(--glow) 12%, var(--tile-bg));
  box-shadow:
    inset 0 0 18px color-mix(in srgb, var(--glow) 25%, transparent),
    0 0 14px color-mix(in srgb, var(--glow) 30%, transparent);
}

.tile.pulse {
  animation: pulse 1.8s ease-in-out infinite;
}

.tile.dim {
  opacity: 0.55;
}

.tile-title {
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--text-dim);
}

.tile-body {
  flex: 1;
  display: flex;
  flex-direction: column;
  justify-content: center;
  min-height: 0;
}

@keyframes pulse {
  0%,
  100% {
    box-shadow:
      inset 0 0 14px color-mix(in srgb, var(--glow) 18%, transparent),
      0 0 8px color-mix(in srgb, var(--glow) 20%, transparent);
  }
  50% {
    box-shadow:
      inset 0 0 24px color-mix(in srgb, var(--glow) 38%, transparent),
      0 0 20px color-mix(in srgb, var(--glow) 45%, transparent);
  }
}
</style>
