<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { invoke } from "@tauri-apps/api/core";
import { useGlass } from "./composables/useGlass";
import { useSettings } from "./composables/useSettings";
import Tile from "./components/Tile.vue";
import MusicTile from "./components/MusicTile.vue";
import ClaudeTile from "./components/ClaudeTile.vue";
import SettingsPanel from "./components/SettingsPanel.vue";
import SystemPanel from "./components/SystemPanel.vue";
import PowerPanel from "./components/PowerPanel.vue";
import ClipboardPanel from "./components/ClipboardPanel.vue";
import { useClipboard } from "./composables/useClipboard";
import { useSystemStats } from "./composables/useSystemStats";
import { useClock } from "./composables/useClock";
import { useFocusTimer } from "./composables/useFocusTimer";
import { useClaudeStatus, type ClaudeState } from "./composables/useClaudeStatus";
import { COMPACT, EXPANDED, useIsland } from "./composables/useIsland";
import { useMedia } from "./composables/useMedia";

const settings = useSettings();
const { time, date } = useClock();
const focus = useFocusTimer(computed(() => settings.value.focusMinutes));
const claude = useClaudeStatus();
const islandEl = ref<HTMLElement | null>(null);
const island = useIsland(() => claude.markSeen());
const media = useMedia();

useGlass(
  islandEl,
  computed(() => settings.value.glass),
);

/** Kapak rengi; ayarlardan kapatılabilir. */
const accent = computed(() => (settings.value.tint ? media.accent.value : undefined));

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

/** Açık adanın sekmeleri. Ayarlar sekme değil, dişliyle açılan ayrı sayfa. */
type Tab = "main" | "clipboard" | "system" | "power";
const TABS: { id: Tab; label: string }[] = [
  { id: "main", label: "Ana" },
  { id: "clipboard", label: "Pano" },
  { id: "system", label: "Sistem" },
  { id: "power", label: "Güç" },
];
const tab = ref<Tab>("main");
const settingsOpen = ref(false);
/** Kayan şeritteki sıra: sekmeler, en sağda ayarlar. */
const pageIndex = computed(() =>
  settingsOpen.value ? TABS.length : TABS.findIndex((t) => t.id === tab.value),
);
const isActive = (t: Tab) => !settingsOpen.value && tab.value === t;

// Ada kapanınca bir dahaki açılışta ana sekmeyle başlasın.
watch(island.expanded, (open) => {
  if (!open) {
    settingsOpen.value = false;
    tab.value = "main";
  }
});

/** Sistem ölçümleri yalnızca sekme görünürken yapılır. */
const systemStats = useSystemStats(
  computed(() => island.expanded.value && isActive("system")),
);

/** Pano geçmişi; liste yalnızca sekme görünürken yenilenir. */
const clip = useClipboard(computed(() => island.expanded.value && isActive("clipboard")));

/** Sağ tık: ayarları adanın içinde açar. Dişli: ayarlar ↔ sekmeler. */
async function openSettings() {
  await island.expand();
  settingsOpen.value = true;
}

function toggleSettings() {
  settingsOpen.value = !settingsOpen.value;
}

function settingsAction(id: string) {
  invoke("settings_action", { id }).catch((e) => console.error("settings_action", e));
}

/** Konum kilitliyken sürükleme alanları kaldırılır, ada yerinden oynamaz. */
const drag = computed(() =>
  settings.value.locked ? {} : { "data-tauri-drag-region": "" },
);

const islandStyle = computed(() => {
  const size = island.expanded.value ? EXPANDED : COMPACT;
  return {
    width: `${size.width}px`,
    height: `${size.height}px`,
    "--accent": accent.value ?? "transparent",
  };
});
</script>

<template>
  <div
    ref="islandEl"
    class="island"
    :class="{ expanded: island.expanded.value, tinted: !!accent, locked: settings.locked }"
    :style="islandStyle"
    @mouseenter="island.hoverIn"
    @mouseleave="island.hoverOut"
    @contextmenu.prevent="openSettings"
  >
    <!-- Kompakt görünüm -->
    <div v-if="!island.expanded.value" class="compact" v-bind="drag">
      <span class="left">
        <span
          class="dot"
          :class="{ on: !!claudeColor }"
          :style="claudeColor ? { background: claudeColor, color: claudeColor } : undefined"
        />
        <img v-if="media.state.value.active && media.artwork.value" class="mini-cover" :src="media.artwork.value" alt="" />
      </span>
      <span class="compact-time mono" v-bind="drag">{{ time }}</span>
      <span v-if="focusActive" class="compact-focus mono">{{ focus.label.value }}</span>
      <span v-else-if="media.state.value.playing" class="eq" aria-hidden="true">
        <i /><i /><i /><i />
      </span>
      <span v-else class="compact-spacer" />
    </div>

    <!-- Genişletilmiş görünüm -->
    <div v-else class="expanded-view">
      <header class="header" v-bind="drag">
        <button v-if="settingsOpen" class="back" @click="settingsOpen = false">
          <svg viewBox="0 0 24 24" width="12" height="12" aria-hidden="true">
            <path
              d="M15 5l-7 7 7 7"
              fill="none"
              stroke="currentColor"
              stroke-width="2.4"
              stroke-linecap="round"
              stroke-linejoin="round"
            />
          </svg>
          Ayarlar
        </button>
        <span v-else class="mono header-left" v-bind="drag">{{ date }}</span>
        <span v-if="settingsOpen" class="brand" v-bind="drag">Halisland</span>
        <nav
          v-else
          class="tabs"
          role="tablist"
          :style="{ '--tab-index': pageIndex, '--tab-count': TABS.length }"
        >
          <span class="tab-pill" aria-hidden="true" />
          <button
            v-for="t in TABS"
            :key="t.id"
            role="tab"
            :aria-selected="tab === t.id"
            :class="{ active: tab === t.id }"
            @click="tab = t.id"
          >
            {{ t.label }}
          </button>
        </nav>
        <span class="header-right" v-bind="drag">
          <template v-if="settingsOpen">
            <button class="chip" @click="settingsAction('reset-position')">Konumu sıfırla</button>
            <button class="chip danger" @click="settingsAction('quit')">Çıkış</button>
          </template>
          <span v-else class="mono" v-bind="drag">{{ time }}</span>
          <!-- Konum kilidi: hızlı erişim için dişlinin hemen yanında. -->
          <button
            class="icon-btn"
            :class="{ active: settings.locked }"
            :title="settings.locked ? 'Konum kilitli; sürüklemek için kilidi aç' : 'Konumu kilitle'"
            :aria-label="settings.locked ? 'Konum kilidini aç' : 'Konumu kilitle'"
            :aria-pressed="settings.locked"
            @click="settingsAction('lock')"
          >
            <svg viewBox="0 0 24 24" width="12" height="12" aria-hidden="true">
              <rect x="5" y="11" width="14" height="10" rx="2.5" fill="currentColor" />
              <path
                :d="settings.locked ? 'M8 11V8a4 4 0 0 1 8 0v3' : 'M8 11V8a4 4 0 0 1 7.6-1.7'"
                fill="none"
                stroke="currentColor"
                stroke-width="2.2"
                stroke-linecap="round"
              />
            </svg>
          </button>
          <button
            class="gear icon-btn"
            :class="{ active: settingsOpen }"
            title="Ayarlar"
            aria-label="Ayarlar"
            @click="toggleSettings"
          >
            <svg viewBox="0 0 24 24" width="13" height="13" aria-hidden="true">
              <path
                fill="currentColor"
                d="M19.4 13a7.6 7.6 0 0 0 0-2l2.1-1.6-2-3.5-2.5 1a7.4 7.4 0 0 0-1.7-1L15 3.3h-4l-.4 2.6a7.4 7.4 0 0 0-1.7 1l-2.5-1-2 3.5L6.6 11a7.6 7.6 0 0 0 0 2l-2.1 1.6 2 3.5 2.5-1a7.4 7.4 0 0 0 1.7 1l.4 2.6h4l.4-2.6a7.4 7.4 0 0 0 1.7-1l2.5 1 2-3.5zM13 15.5a3.5 3.5 0 1 1 0-7 3.5 3.5 0 0 1 0 7z"
                transform="translate(-1 0)"
              />
            </svg>
          </button>
        </span>
      </header>

      <!-- Kayan sayfalar: Ana · Sistem · Güç · (Ayarlar) -->
      <div class="pages">
      <div class="track" :style="{ '--page-index': pageIndex }">
      <div class="tiles page" :inert="!isActive('main')">
        <Tile
          class="music-tile"
          title="Müzik"
          :glow="media.state.value.playing ? accent : undefined"
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
            :connected="claude.connected.value"
            @focus="claude.focusTerminal"
          />
        </Tile>
      </div>

      <ClipboardPanel
        class="page"
        :inert="!isActive('clipboard')"
        :items="clip.items.value"
        :enabled="settings.clipboard"
        @copy="clip.copy"
        @remove="clip.remove"
        @pin="clip.pin"
        @clear="clip.clear"
      />

      <SystemPanel class="page" :inert="!isActive('system')" :stats="systemStats" />

      <PowerPanel
        class="page"
        :inert="!isActive('power')"
        :active="island.expanded.value && isActive('power')"
      />

      <SettingsPanel
        class="page"
        :inert="!settingsOpen"
        :settings="settings"
        :claude-connected="claude.connected.value"
      />
      </div>
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

/* Ortadaki sekme şeridi tam ortada kalsın diye iki yan eşit genişlikte. */
.header {
  display: grid;
  grid-template-columns: 1fr auto 1fr;
  align-items: center;
  font-size: 11px;
  color: var(--text-dim);
  cursor: grab;
}

.header-left,
.back {
  justify-self: start;
}

.header-right {
  justify-self: end;
  display: flex;
  align-items: center;
  gap: 8px;
}

/* Başlıktaki yuvarlak simge düğmeleri: kilit ve dişli. */
.icon-btn {
  display: grid;
  place-items: center;
  width: 20px;
  height: 20px;
  border-radius: 50%;
  color: var(--text-dim);
  cursor: pointer;
  transition:
    background 0.2s ease,
    color 0.2s ease;
}

.icon-btn + .icon-btn {
  margin-left: -4px;
}

.icon-btn:hover,
.icon-btn.active {
  background: rgba(255, 255, 255, 0.12);
  color: var(--text);
}

.gear svg {
  transition: transform 0.4s var(--ease);
}

.gear.active svg {
  transform: rotate(60deg);
}

/* Kayan sayfalar. Kutucukların dış parıltısı kırpılmasın diye alan adanın
   iç boşluğuna taşar; sayfalar da adanın kenarına kadar kayar. */
.pages {
  --page-gap: 28px;
  flex: 1;
  min-height: 0;
  margin: -10px -14px -14px;
  padding: 10px 14px 14px;
  /* `hidden` kutu odak/scrollIntoView ile yana kaydırılabiliyor ve sayfalar
     kayıyor; `clip` hiç kaydırılamaz. */
  overflow: clip;
}

.track {
  height: 100%;
  display: flex;
  gap: var(--page-gap);
  transition: transform 0.45s var(--ease);
}

.track {
  transform: translateX(calc(var(--page-index, 0) * (-100% - var(--page-gap))));
}

/* Sekme şeridi: eşit genişlikte düğmeler, altlarında kayan bir hap. */
.tabs {
  --tab-w: 58px;
  position: relative;
  display: flex;
  padding: 2px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid rgba(255, 255, 255, 0.06);
}

.tabs button {
  position: relative;
  width: var(--tab-w);
  padding: 2px 0;
  border-radius: 999px;
  font-size: 11px;
  font-weight: 600;
  color: var(--text-dim);
  transition: color 0.2s ease;
}

.tabs button:hover,
.tabs button.active {
  color: var(--text);
}

.tab-pill {
  position: absolute;
  top: 2px;
  bottom: 2px;
  left: 2px;
  width: var(--tab-w);
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.16);
  box-shadow: 0 1px 4px rgba(0, 0, 0, 0.25);
  transform: translateX(calc(var(--tab-index) * var(--tab-w)));
  transition: transform 0.35s var(--ease);
}

.track > .page {
  flex: 0 0 100%;
  /* İçerik (uzun uygulama adları) sayfayı genişletip kaymayı bozmasın. */
  min-width: 0;
  height: 100%;
  transition:
    opacity 0.35s ease,
    filter 0.35s ease;
}

.track > .page[inert] {
  opacity: 0;
  filter: blur(4px);
}

.back {
  display: flex;
  align-items: center;
  gap: 4px;
  margin-left: -4px;
  padding: 2px 8px 2px 4px;
  border-radius: 999px;
  font-size: 11px;
  font-weight: 600;
  color: var(--text);
}

.back:hover {
  background: rgba(255, 255, 255, 0.1);
}

.header .chip {
  padding: 2px 9px;
  color: var(--text-dim);
}

.header .chip:hover {
  color: var(--text);
}

/* Kilitliyken sürükleme yok; el imleci de gösterilmez. */
.island.locked .compact,
.island.locked .header {
  cursor: default;
}

.header .chip.danger:hover {
  background: rgba(255, 90, 90, 0.2);
  color: #ff8a8a;
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
