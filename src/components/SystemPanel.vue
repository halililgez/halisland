<script setup lang="ts">
/** Sistem sekmesi: işlemci ve bellek halkaları, en çok bellek kullananlar, disk ve ağ. */
import { computed } from "vue";
import Tile from "./Tile.vue";
import Ring from "./Ring.vue";
import { formatBytes, formatUptime, type SystemStats } from "../composables/useSystemStats";

const props = defineProps<{ stats: SystemStats | null }>();

const memPct = computed(() =>
  props.stats && props.stats.memoryTotal ? (props.stats.memoryUsed / props.stats.memoryTotal) * 100 : 0,
);
const diskPct = computed(() =>
  props.stats && props.stats.diskTotal ? (props.stats.diskUsed / props.stats.diskTotal) * 100 : 0,
);
/** Satır çubukları en büyük uygulamaya göre ölçeklenir. */
const topMax = computed(() => props.stats?.topApps[0]?.memory || 1);
</script>

<template>
  <div class="tiles">
    <Tile title="Kaynaklar" class="res">
      <div class="rings">
        <Ring :value="stats?.cpu ?? 0" label="İşlemci" color="var(--c-done)" />
        <Ring :value="memPct" label="Bellek" color="var(--c-focus)" />
      </div>
      <div class="sub mono">
        {{ stats ? `${formatBytes(stats.memoryUsed)} / ${formatBytes(stats.memoryTotal)}` : "Ölçülüyor…" }}
      </div>
    </Tile>

    <Tile title="En çok bellek" class="apps">
      <ul v-if="stats?.topApps.length" class="app-list">
        <li v-for="a in stats.topApps" :key="a.name">
          <div class="app-row">
            <span class="app-name" :title="a.name">{{ a.name }}</span>
            <span class="mono dim">{{ formatBytes(a.memory) }}</span>
          </div>
          <div class="mini-bar"><i :style="{ width: `${(a.memory / topMax) * 100}%` }" /></div>
        </li>
      </ul>
      <div v-else class="dim">Ölçülüyor…</div>
    </Tile>

    <Tile title="Disk ve ağ">
      <div class="app-row">
        <span>{{ stats?.diskName || "Disk" }}</span>
        <span class="mono dim">
          {{ stats ? `${formatBytes(stats.diskUsed)} / ${formatBytes(stats.diskTotal)}` : "–" }}
        </span>
      </div>
      <div class="mini-bar disk" :class="{ hot: diskPct >= 90 }">
        <i :style="{ width: `${diskPct}%` }" />
      </div>
      <div class="net mono">
        <span title="İndirme"><b>↓</b>{{ stats ? `${formatBytes(stats.netDown)}/sn` : "–" }}</span>
        <span title="Yükleme"><b>↑</b>{{ stats ? `${formatBytes(stats.netUp)}/sn` : "–" }}</span>
      </div>
      <div class="sub">Açık kalma: {{ stats ? formatUptime(stats.uptime) : "–" }}</div>
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

.res {
  flex: 0.9;
}

.apps {
  flex: 1.3;
}

.tiles :deep(.tile-body) {
  gap: 6px;
}

.rings {
  display: flex;
  justify-content: space-around;
}

.sub {
  font-size: 10px;
  color: var(--text-dim);
  text-align: center;
}

.dim {
  color: var(--text-dim);
}

.app-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.app-row {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  font-size: 11px;
}

.app-name {
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.app-row .mono {
  flex: none;
}

.mini-bar {
  margin-top: 2px;
  height: 3px;
  border-radius: 2px;
  background: rgba(255, 255, 255, 0.07);
  overflow: hidden;
}

.mini-bar i {
  display: block;
  height: 100%;
  border-radius: 2px;
  background: var(--c-focus);
  transition: width 0.8s var(--ease);
}

.mini-bar.disk {
  height: 5px;
}

.mini-bar.disk i {
  background: var(--c-seen);
}

.mini-bar.disk.hot i {
  background: var(--c-hot);
}

.net {
  display: flex;
  justify-content: space-between;
  font-size: 11px;
  margin-top: 4px;
}

.net b {
  margin-right: 3px;
  color: var(--text-dim);
  font-weight: 600;
}

.tiles > :last-child .sub {
  text-align: left;
}
</style>
