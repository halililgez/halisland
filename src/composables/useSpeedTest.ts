import { computed, onBeforeUnmount, ref } from "vue";

/**
 * İnternet hız testi (Cloudflare). Anahtar gerekmez; uçlar CORS'a açıktır:
 *  - `GET  https://speed.cloudflare.com/__down?bytes=N`: N bayt indirir.
 *  - `POST https://speed.cloudflare.com/__up`: gövdeyi alıp atar.
 * Ölçüm yalnızca kullanıcı başlatınca yapılır. Sırayla: gecikme (küçük
 * istekler), indirme, yükleme. Bağlantı hızlansın diye boyutlar giderek
 * büyür; sonuç, en az 250 ms süren isteklerin %90'lık dilimidir (kısa
 * istekler bağlantı kurulumu yüzünden hızı düşük gösterir). Veri kullanımını
 * sınırlamak için her aşamanın süre ve bayt sınırı vardır.
 */

const BASE = "https://speed.cloudflare.com";

export type SpeedPhase = "idle" | "latency" | "download" | "upload" | "done" | "error";

export interface SpeedResult {
  /** ms */
  ping: number;
  /** ms */
  jitter: number;
  /** Mbit/sn */
  down: number;
  /** Mbit/sn */
  up: number;
  /** Cloudflare veri merkezi (IATA kodu, ör. IST). */
  colo: string | null;
  at: number;
}

const LATENCY_COUNT = 12;
const DOWN = { budgetMs: 8000, hardMs: 12000, maxBytes: 250e6, maxSize: 100e6 };
const UP = { budgetMs: 6000, hardMs: 10000, maxBytes: 100e6, maxSize: 25e6 };
/** İlk istekler küçük; sonra boyut ölçülen hıza göre ayarlanır. */
const FIRST_SIZE = 1e5;
const WARMUP = 2;
/** Bir isteğin hedef süresi: yavaş hatta büyük istek bütçeyi aşmasın. */
const TARGET_MS = 1000;
const MIN_SAMPLE_MS = 250;
const HISTORY_KEY = "halisland.speedHistory";
const HISTORY_MAX = 5;

const mbps = (bytes: number, ms: number) => (bytes * 8) / (ms / 1000) / 1e6;
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

function percentile(values: number[], p: number): number {
  if (!values.length) return 0;
  const s = [...values].sort((a, b) => a - b);
  const i = Math.min(s.length - 1, Math.max(0, Math.round((p / 100) * (s.length - 1))));
  return s[i];
}

/** Cloudflare'in bildirdiği sunucu süresi (ms); gecikmeden düşülür. */
function serverMs(res: Response): number {
  const header = res.headers.get("server-timing") ?? "";
  const durs = [...header.matchAll(/cfSpeed\w*;dur=([\d.]+)/g)].map((m) => parseFloat(m[1]));
  return durs.length ? Math.max(...durs) : 0;
}

function loadHistory(): SpeedResult[] {
  try {
    const raw = localStorage.getItem(HISTORY_KEY);
    const list = raw ? (JSON.parse(raw) as SpeedResult[]) : [];
    return Array.isArray(list) ? list.slice(0, HISTORY_MAX) : [];
  } catch {
    return [];
  }
}

function saveHistory(list: SpeedResult[]) {
  try {
    localStorage.setItem(HISTORY_KEY, JSON.stringify(list));
  } catch {
    /* gizli mod vb. */
  }
}

/**
 * Sonraki isteğin boyutu: son isteğin hızıyla ~1 sn sürecek kadar. Birden
 * fazla büyümesin (en fazla 4 kat), kalan süreye de sığsın.
 */
function nextSize(index: number, lastMs: number, lastSize: number, max: number, leftMs: number): number {
  if (index < WARMUP || lastMs <= 0) return FIRST_SIZE;
  const perMs = lastSize / lastMs;
  const want = perMs * Math.min(TARGET_MS, Math.max(250, leftMs));
  return Math.round(Math.min(max, lastSize * 4, Math.max(FIRST_SIZE, want)));
}

export function useSpeedTest() {
  const phase = ref<SpeedPhase>("idle");
  /** Şu anki aşamanın canlı hızı (Mbit/sn) ya da gecikme (ms). */
  const live = ref(0);
  /** Aşamanın ilerlemesi (0–1), süre bütçesine göre. */
  const progress = ref(0);
  const result = ref<Partial<SpeedResult>>({});
  const error = ref<string | null>(null);
  const history = ref<SpeedResult[]>(loadHistory());
  let controller: AbortController | null = null;

  const running = computed(
    () => phase.value === "latency" || phase.value === "download" || phase.value === "upload",
  );

  async function measureLatency(signal: AbortSignal) {
    const times: number[] = [];
    // İlk istek bağlantıyı (DNS, TLS) kurar ve gecikmeye sayılmaz. Onu
    // Cloudflare'in standart `/cdn-cgi/trace` sayfasına yaparız: yanıttaki
    // `colo=IST` satırı hangi veri merkezine ölçtüğümüzü söyler. Başka alan
    // okunmaz ve saklanmaz.
    const trace = await fetch(`${BASE}/cdn-cgi/trace`, { cache: "no-store", signal });
    const colo = (await trace.text()).match(/^colo=(\w+)$/m)?.[1] ?? null;
    progress.value = 1 / LATENCY_COUNT;
    for (let i = 1; i < LATENCY_COUNT; i++) {
      const t0 = performance.now();
      const res = await fetch(`${BASE}/__down?bytes=0&r=${Math.random()}`, {
        cache: "no-store",
        signal,
      });
      await res.arrayBuffer();
      const ms = Math.max(0, performance.now() - t0 - serverMs(res));
      times.push(ms);
      live.value = ms;
      progress.value = (i + 1) / LATENCY_COUNT;
    }
    const ping = percentile(times, 50);
    const diffs = times.slice(1).map((t, i) => Math.abs(t - times[i]));
    const jitter = diffs.length ? diffs.reduce((a, b) => a + b, 0) / diffs.length : 0;
    result.value = { ...result.value, ping, jitter, colo };
  }

  async function measureDownload(signal: AbortSignal) {
    const start = performance.now();
    const samples: number[] = [];
    let total = 0;
    // Canlı hız: son ~1 sn'de gelen bayt.
    const window: { t: number; bytes: number }[] = [];
    let size = 0;
    let lastMs = 0;
    for (let i = 0; ; i++) {
      const elapsed = performance.now() - start;
      if (elapsed > DOWN.budgetMs || total > DOWN.maxBytes) break;
      size = nextSize(i, lastMs, size, DOWN.maxSize, DOWN.budgetMs - elapsed);
      const t0 = performance.now();
      const res = await fetch(`${BASE}/__down?bytes=${size}&r=${Math.random()}`, {
        cache: "no-store",
        signal,
      });
      const reader = res.body?.getReader();
      let got = 0;
      if (reader) {
        for (;;) {
          const { done, value } = await reader.read();
          if (done) break;
          got += value.byteLength;
          total += value.byteLength;
          const now = performance.now();
          window.push({ t: now, bytes: total });
          while (window.length > 1 && now - window[0].t > 1000) window.shift();
          if (window.length > 1) {
            const w0 = window[0];
            live.value = mbps(total - w0.bytes, now - w0.t);
          }
          progress.value = Math.min(1, (now - start) / DOWN.budgetMs);
          // Yavaş hatta büyük istek bütçeyi aşmasın.
          if (now - start > DOWN.hardMs) {
            await reader.cancel();
            break;
          }
        }
      } else {
        got = (await res.arrayBuffer()).byteLength;
        total += got;
      }
      lastMs = performance.now() - t0;
      if (lastMs >= MIN_SAMPLE_MS) samples.push(mbps(got, lastMs));
      if (performance.now() - start > DOWN.hardMs) break;
    }
    const all = mbps(total, performance.now() - start);
    result.value = { ...result.value, down: samples.length ? percentile(samples, 90) : all };
  }

  async function measureUpload(signal: AbortSignal) {
    const start = performance.now();
    const samples: number[] = [];
    let total = 0;
    let size = 0;
    let lastMs = 0;
    for (let i = 0; ; i++) {
      const elapsed = performance.now() - start;
      if (elapsed > UP.budgetMs || total > UP.maxBytes) break;
      size = nextSize(i, lastMs, size, UP.maxSize, UP.budgetMs - elapsed);
      const body = new Uint8Array(size);
      // Kalan süreyi aşan isteği kes (yavaş hatta 25 MB dakikalar sürer).
      const timeout = AbortSignal.timeout(Math.max(1000, UP.hardMs - elapsed));
      const t0 = performance.now();
      try {
        const res = await fetch(`${BASE}/__up?r=${Math.random()}`, {
          method: "POST",
          body,
          cache: "no-store",
          signal: AbortSignal.any([signal, timeout]),
        });
        await res.arrayBuffer();
      } catch (e) {
        if (signal.aborted) throw e;
        break; // süre doldu
      }
      lastMs = performance.now() - t0;
      total += size;
      if (lastMs >= MIN_SAMPLE_MS) samples.push(mbps(size, lastMs));
      live.value = mbps(size, lastMs);
      progress.value = Math.min(1, (performance.now() - start) / UP.budgetMs);
    }
    const all = mbps(total, performance.now() - start);
    result.value = { ...result.value, up: samples.length ? percentile(samples, 90) : all };
  }

  async function start() {
    if (running.value) return;
    controller?.abort();
    controller = new AbortController();
    const { signal } = controller;
    result.value = {};
    error.value = null;
    try {
      phase.value = "latency";
      live.value = 0;
      progress.value = 0;
      await measureLatency(signal);

      phase.value = "download";
      live.value = 0;
      progress.value = 0;
      await measureDownload(signal);
      await sleep(300);

      phase.value = "upload";
      live.value = 0;
      progress.value = 0;
      await measureUpload(signal);

      const r = result.value;
      const done: SpeedResult = {
        ping: r.ping ?? 0,
        jitter: r.jitter ?? 0,
        down: r.down ?? 0,
        up: r.up ?? 0,
        colo: r.colo ?? null,
        at: Date.now(),
      };
      result.value = done;
      history.value = [done, ...history.value].slice(0, HISTORY_MAX);
      saveHistory(history.value);
      phase.value = "done";
    } catch (e) {
      if (signal.aborted) {
        phase.value = "idle";
        return;
      }
      console.error("speed test", e);
      error.value = navigator.onLine ? "Sunucuya ulaşılamadı" : "İnternet bağlantısı yok";
      phase.value = "error";
    }
  }

  function cancel() {
    controller?.abort();
    controller = null;
    phase.value = "idle";
    result.value = {};
  }

  onBeforeUnmount(() => controller?.abort());

  return { phase, live, progress, result, error, history, running, start, cancel };
}
