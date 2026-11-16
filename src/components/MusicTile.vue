<script setup lang="ts">
import { formatMs, type MediaState } from "../composables/useMedia";

defineProps<{
  state: MediaState;
  artwork: string;
  source: string;
  position: number;
  progress: number;
}>();

const emit = defineEmits<{ control: ["playPause" | "next" | "previous"] }>();
</script>

<template>
  <div v-if="state.active" class="music">
    <div class="cover" :class="{ empty: !artwork }">
      <img v-if="artwork" :src="artwork" alt="" />
      <svg v-else viewBox="0 0 24 24" aria-hidden="true">
        <path
          fill="currentColor"
          d="M12 3v10.55A4 4 0 1 0 14 17V7h4V3h-6Z"
        />
      </svg>
    </div>

    <div class="info">
      <div class="title" :title="state.title">{{ state.title || "Bilinmeyen parça" }}</div>
      <div class="artist" :title="state.artist">{{ state.artist || source }}</div>

      <div class="controls">
        <button aria-label="Önceki" @click="emit('control', 'previous')">
          <svg viewBox="0 0 24 24"><path fill="currentColor" d="M6 6h2v12H6zm3.5 6 8.5 6V6z" /></svg>
        </button>
        <button class="main" :aria-label="state.playing ? 'Duraklat' : 'Oynat'" @click="emit('control', 'playPause')">
          <svg v-if="state.playing" viewBox="0 0 24 24">
            <path fill="currentColor" d="M6 5h4v14H6zm8 0h4v14h-4z" />
          </svg>
          <svg v-else viewBox="0 0 24 24"><path fill="currentColor" d="M8 5v14l11-7z" /></svg>
        </button>
        <button aria-label="Sonraki" @click="emit('control', 'next')">
          <svg viewBox="0 0 24 24"><path fill="currentColor" d="m6 18 8.5-6L6 6zm10-12h2v12h-2z" /></svg>
        </button>
        <span class="src">{{ source }}</span>
      </div>

      <div v-if="state.durationMs > 0" class="timeline mono">
        <span>{{ formatMs(position) }}</span>
        <div class="bar"><div class="fill" :style="{ width: `${progress * 100}%` }" /></div>
        <span>{{ formatMs(state.durationMs) }}</span>
      </div>
    </div>
  </div>

  <div v-else class="idle">
    <div class="big">Sessiz</div>
    <div class="sub">Bir şey çalınca burada görünür</div>
  </div>
</template>

<style scoped>
.music {
  display: flex;
  gap: 12px;
  align-items: center;
  min-width: 0;
}

.cover {
  flex: none;
  width: 72px;
  height: 72px;
  border-radius: 12px;
  overflow: hidden;
  background: rgba(255, 255, 255, 0.06);
  display: grid;
  place-items: center;
  box-shadow: 0 4px 14px rgba(0, 0, 0, 0.35);
}

.cover img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.cover svg {
  width: 28px;
  height: 28px;
  color: var(--text-dim);
}

.info {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.title,
.artist {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.title {
  font-size: 15px;
  font-weight: 600;
}

.artist {
  font-size: 12px;
  color: var(--text-dim);
}

.controls {
  display: flex;
  align-items: center;
  gap: 4px;
  margin-top: 4px;
}

.controls button {
  width: 26px;
  height: 26px;
  border-radius: 50%;
  display: grid;
  place-items: center;
  transition: background 0.15s ease;
}

.controls button:hover {
  background: rgba(255, 255, 255, 0.12);
}

.controls button.main {
  width: 30px;
  height: 30px;
  background: rgba(255, 255, 255, 0.1);
}

.controls svg {
  width: 16px;
  height: 16px;
}

.src {
  margin-left: auto;
  font-size: 10px;
  color: var(--text-dim);
  text-transform: capitalize;
}

.timeline {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 4px;
  font-size: 10px;
  color: var(--text-dim);
}

.timeline .bar {
  flex: 1;
  height: 3px;
  border-radius: 2px;
  background: rgba(255, 255, 255, 0.1);
  overflow: hidden;
}

.timeline .fill {
  height: 100%;
  background: var(--accent, var(--text));
  transition: width 0.25s linear;
}

.idle .big {
  font-size: 22px;
  font-weight: 600;
}

.idle .sub {
  margin-top: 4px;
  font-size: 11px;
  color: var(--text-dim);
}
</style>
