import { onBeforeUnmount, ref, watch, type Ref } from "vue";
import { invoke } from "@tauri-apps/api/core";

export interface AppUsage {
  name: string;
  memory: number;
}

export interface SystemStats {
  cpu: number;
  memoryUsed: number;
  memoryTotal: number;
  topApps: AppUsage[];
  diskName: string;
  diskUsed: number;
  diskTotal: number;
  netDown: number;
  netUp: number;
  uptime: number;
}

const INTERVAL_MS = 2000;

/**
 * Sistem ölçümleri (src-tauri/src/system.rs). Yalnızca `active` doğruyken,
 * yani Sistem sekmesi görünürken iki saniyede bir sorulur.
 */
export function useSystemStats(active: Ref<boolean>) {
  const stats = ref<SystemStats | null>(null);
  let timer: number | undefined;

  async function poll() {
    try {
      stats.value = await invoke<SystemStats>("system_stats");
    } catch (e) {
      console.error("system_stats", e);
    }
  }

  function stop() {
    if (timer !== undefined) window.clearInterval(timer);
    timer = undefined;
  }

  watch(
    active,
    (on) => {
      stop();
      if (!on) return;
      poll();
      timer = window.setInterval(poll, INTERVAL_MS);
    },
    { immediate: true },
  );

  onBeforeUnmount(stop);
  return stats;
}

const num = new Intl.NumberFormat("tr-TR", { maximumFractionDigits: 1 });

/** 1536 → "1,5 KB" */
export function formatBytes(bytes: number): string {
  const units = ["B", "KB", "MB", "GB", "TB"];
  let v = bytes;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  const n = i >= 3 ? num.format(v) : Math.round(v).toString();
  return `${n} ${units[i]}`;
}

/** 340878 → "3 gün 22 sa" */
export function formatUptime(seconds: number): string {
  const d = Math.floor(seconds / 86400);
  const h = Math.floor((seconds % 86400) / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  if (d > 0) return `${d} gün ${h} sa`;
  if (h > 0) return `${h} sa ${m} dk`;
  return `${m} dk`;
}
