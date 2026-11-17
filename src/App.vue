<script setup lang="ts">
import { computed, ref } from "vue";
import { useGlass, type GlassMode } from "./composables/useGlass";
import Tile from "./components/Tile.vue";
import MusicTile from "./components/MusicTile.vue";
import ClaudeTile from "./components/ClaudeTile.vue";
import { useClock } from "./composables/useClock";
import { useFocusTimer } from "./composables/useFocusTimer";
import { useClaudeStatus, type ClaudeState } from "./composables/useClaudeStatus";
import { COMPACT, EXPANDED, useIsland } from "./composables/useIsland";
import { useMedia } from "./composables/useMedia";

const { time, date } = useClock();
const focus = useFocusTimer(25);
const claude = useClaudeStatus();
const island = useIsland(() => claude.markSeen());
const media = useMedia();

const islandEl = ref<HTMLElement | null>(null);
const glassMode = ref<GlassMode>("acrylic");
useGlass(islandEl, glassMode);

const CLAUDE_COLORS: Record<ClaudeState, string | undefined> = {
  idle: undefined,
  working: "var(--c-working)",
  waiting: "var(--c-waiting)",
  done: "var(--c-done)",
  seen: "var(--c-seen)",
};
const claudeColor = computed(() => CLAUDE_COLORS[claude.state.value]);

const focusActive = computed(() => focus.state.value !== "idle");
const focusColor = computed(() =>
  focus.state.value === "running" || focus.state.value === "finished"
    ? "var(--c-focus)"
    : undefined,
);

const islandStyle = computed(() => {
  const size = island.expanded.value ? EXPANDED : COMPACT;
  return {
    width: `${size.width}px`,
    height: `${size.height}px`,
    "--accent": media.accent.value ?? "transparent",
  };
});
</script>

<template>
  <div
    ref="islandEl"
    class="island"
    :class="{ expanded: island.expanded.value, tinted: !!media.accent.value }"
    :style="islandStyle"
    @mouseenter="island.hoverIn"
    @mouseleave="island.hoverOut"
  >
    <!-- Kompakt görünüm -->
    <div v-if="!island.expanded.value" class="compact" data-tauri-drag-region>
      <span class="left">
        <span
          class="dot"
          :class="{ on: !!claudeColor }"
          :style="claudeColor ? { background: claudeColor, color: claudeColor } : undefined"
        />
        <img v-if="media.state.value.active && media.artwork.value" class="mini-cover" :src="media.artwork.value" alt="" />
      </span>
      <span class="compact-time mono" data-tauri-drag-region>{{ time }}</span>
      <span v-if="focusActive" class="compact-focus mono">{{ focus.label.value }}</span>
      <span v-else-if="media.state.value.playing" class="eq" aria-hidden="true">
        <i /><i /><i /><i />
      </span>
      <span v-else class="compact-spacer" />
    </div>

    <!-- Genişletilmiş görünüm -->
    <div v-else class="expanded-view">
      <header class="header" data-tauri-drag-region>
        <span class="mono" data-tauri-drag-region>{{ date }}</span>
        <span class="brand" data-tauri-drag-region>Halisland</span>
        <span class="mono" data-tauri-drag-region>{{ time }}</span>
      </header>

      <div class="tiles">
        <Tile
          class="music-tile"
          title="Müzik"
          :glow="media.state.value.playing ? media.accent.value : undefined"
          :dim="!media.state.value.active"
        >
          <MusicTile
            :state="media.state.value"
            :artwork="media.artwork.value"
            :source="media.source.value"
            :position="media.position.value"
            :progress="media.progress.value"
            @control="media.control"
          />
        </Tile>

        <Tile title="Odak" :glow="focusColor" :pulse="focus.state.value === 'finished'">
          <button class="big mono" @click="focus.toggle()">{{ focus.label.value }}</button>
          <div class="row">
            <button class="chip" @click="focus.toggle()">
              {{ focus.state.value === "running" ? "Duraklat" : "Başlat" }}
            </button>
            <button v-if="focusActive" class="chip" @click="focus.reset()">Sıfırla</button>
          </div>
          <div class="bar"><div class="fill" :style="{ width: `${focus.progress.value * 100}%` }" /></div>
        </Tile>

        <Tile
          class="claude-tile"
          title="Claude"
          :glow="claudeColor"
          :pulse="claude.state.value === 'working' || claude.state.value === 'waiting'"
        >
          <ClaudeTile
            :state="claude.state.value"
            :info="claude.info.value"
            :label="claude.label.value"
            :detail="claude.detail.value"
            :elapsed="claude.elapsed.value"
            @focus="claude.focusTerminal"
          />
        </Tile>
      </div>
    </div>
  </div>
</template>

<style scoped>
.island {
  position: absolute;
  top: 0;
  left: 50%;
  transform: translateX(-50%);
  box-sizing: border-box;
  overflow: hidden;
  border-radius: 22px;
  background: var(--island-bg);
  border: 1px solid var(--island-border);
  transition:
    width 0.38s var(--ease),
    height 0.38s var(--ease),
    border-radius 0.38s var(--ease);
}

.island.expanded {
  border-radius: 28px;
}

/* Çalan şarkının kapak rengi adaya hafifçe siner. */
.island::before {
  content: "";
  position: absolute;
  inset: 0;
  pointer-events: none;
  background: radial-gradient(
    120% 140% at 0% 0%,
    color-mix(in srgb, var(--accent) 28%, transparent),
    transparent 65%
  );
  opacity: 0;
  transition: opacity 0.8s ease;
}

.island.tinted::before {
  opacity: 1;
}

.island > * {
  position: relative;
}

/* ---------- Kompakt ---------- */
.compact {
  height: 100%;
  display: grid;
  grid-template-columns: 1fr auto 1fr;
  align-items: center;
  padding: 0 16px;
  cursor: grab;
}

.left {
  display: flex;
  align-items: center;
  gap: 8px;
}

.mini-cover {
  width: 22px;
  height: 22px;
  border-radius: 6px;
  object-fit: cover;
}

.eq {
  justify-self: end;
  display: flex;
  align-items: flex-end;
  gap: 2px;
  height: 14px;
}

.eq i {
  width: 3px;
  border-radius: 2px;
  background: var(--accent, var(--text));
  animation: eq 0.9s ease-in-out infinite;
}

.island:not(.tinted) .eq i {
  background: var(--text);
}

.eq i:nth-child(2) {
  animation-delay: -0.3s;
}
.eq i:nth-child(3) {
  animation-delay: -0.6s;
}
.eq i:nth-child(4) {
  animation-delay: -0.15s;
}

@keyframes eq {
  0%,
  100% {
    height: 30%;
  }
  50% {
    height: 100%;
  }
}

.dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: rgba(255, 255, 255, 0.18);
  transition: background 0.4s ease;
}

.dot.on {
  box-shadow: 0 0 8px currentColor;
}

.compact-time {
  font-size: 14px;
  font-weight: 600;
}

.compact-focus {
  justify-self: end;
  font-size: 12px;
  color: var(--c-focus);
}

/* ---------- Genişletilmiş ---------- */
.expanded-view {
  height: 100%;
  box-sizing: border-box;
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px 14px 14px;
  animation: fade-in 0.25s ease 0.1s both;
}

.header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  font-size: 11px;
  color: var(--text-dim);
  cursor: grab;
}

.brand {
  font-weight: 700;
  letter-spacing: 0.04em;
  color: var(--text);
}

.music-tile {
  flex: 1.75;
}

.claude-tile {
  flex: 1.35;
}

.tiles {
  flex: 1;
  display: flex;
  gap: 10px;
  min-height: 0;
}

.big {
  font-size: 22px;
  font-weight: 600;
  line-height: 1.1;
  text-align: left;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.sub {
  margin-top: 4px;
  font-size: 11px;
  color: var(--text-dim);
}

.row {
  display: flex;
  gap: 6px;
  margin-top: 6px;
}

.chip {
  padding: 3px 9px;
  border-radius: 999px;
  font-size: 11px;
  background: rgba(255, 255, 255, 0.08);
}

.chip:hover {
  background: rgba(255, 255, 255, 0.16);
}

.bar {
  margin-top: 8px;
  height: 3px;
  border-radius: 2px;
  background: rgba(255, 255, 255, 0.08);
  overflow: hidden;
}

.fill {
  height: 100%;
  background: var(--c-focus);
  transition: width 0.25s linear;
}

@keyframes fade-in {
  from {
    opacity: 0;
  }
  to {
    opacity: 1;
  }
}
</style>
