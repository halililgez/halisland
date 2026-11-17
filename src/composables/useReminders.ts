import { computed, ref, watch } from "vue";

export interface Reminder {
  id: number;
  /** Çalacağı an (ms). */
  at: number;
  text: string;
}

const KEY = "halisland.reminders";
const MAX = 20;
const MAX_TEXT = 120;

function load(): Reminder[] {
  try {
    const raw = window.localStorage.getItem(KEY);
    const list = raw ? (JSON.parse(raw) as Reminder[]) : [];
    return Array.isArray(list) ? list.filter((r) => typeof r.at === "number" && typeof r.text === "string") : [];
  } catch {
    return [];
  }
}

/** Kurulu hatırlatıcılar (en yakını başta). */
const reminders = ref<Reminder[]>([]);
/** Süresi dolmuş, kullanıcının henüz kapatmadığı hatırlatıcılar. */
const fired = ref<Reminder[]>([]);
const now = ref(Date.now());
let started = false;

function persist() {
  try {
    window.localStorage.setItem(KEY, JSON.stringify(reminders.value));
  } catch {
    /* depo kapalıysa yalnızca bu oturumda kalır */
  }
}

/** Kısa, yumuşak bir "ding ding" (ses dosyası gerekmesin). */
function chime() {
  try {
    const ctx = new AudioContext();
    for (const [i, f] of [880, 1175, 880, 1175].entries()) {
      const o = ctx.createOscillator();
      const g = ctx.createGain();
      o.frequency.value = f;
      o.type = "sine";
      const t = ctx.currentTime + i * 0.22;
      g.gain.setValueAtTime(0.0001, t);
      g.gain.exponentialRampToValueAtTime(0.18, t + 0.02);
      g.gain.exponentialRampToValueAtTime(0.0001, t + 0.2);
      o.connect(g).connect(ctx.destination);
      o.start(t);
      o.stop(t + 0.21);
    }
    window.setTimeout(() => ctx.close(), 1500);
  } catch {
    /* ses yoksa yalnızca görsel uyarı */
  }
}

function tick() {
  now.value = Date.now();
  const due = reminders.value.filter((r) => r.at <= now.value);
  if (due.length === 0) return;
  reminders.value = reminders.value.filter((r) => r.at > now.value);
  fired.value = [...fired.value, ...due];
  chime();
}

function start() {
  if (started) return;
  started = true;
  reminders.value = load().sort((a, b) => a.at - b.at);
  watch(reminders, persist, { deep: true });
  // Uygulama kapalıyken süresi dolanlar açılışta hemen gösterilir.
  tick();
  window.setInterval(tick, 1000);
}

/**
 * Zamanlayıcılar ve hatırlatıcılar ("10 dakika sonra çayı hatırlat").
 * Yerel depoda tutulur; süresi dolunca ada uyarır ve kısa bir ses çalar.
 * Tek durum: ada, Bugün sekmesi ve asistan aynı listeyi kullanır.
 */
export function useReminders() {
  start();

  function add(at: number, text: string): Reminder | string {
    const t = text.trim().slice(0, MAX_TEXT) || "Zamanlayıcı";
    if (!Number.isFinite(at) || at <= Date.now()) return "Zaman geçmişte ya da geçersiz.";
    if (reminders.value.length >= MAX) return `En fazla ${MAX} hatırlatıcı kurulabilir.`;
    const id = Math.max(Date.now(), ...reminders.value.map((r) => r.id + 1));
    const r = { id, at, text: t };
    reminders.value = [...reminders.value, r].sort((a, b) => a.at - b.at);
    return r;
  }

  function cancel(id: number) {
    reminders.value = reminders.value.filter((r) => r.id !== id);
  }

  function dismiss(id?: number) {
    fired.value = id === undefined ? [] : fired.value.filter((r) => r.id !== id);
  }

  const next = computed(() => reminders.value[0] ?? null);

  return { reminders, fired, now, next, add, cancel, dismiss };
}

/**
 * "10 dk çay", "1,5 saat fırın", "30 sn yumurta" ya da "15:30 toplantı" →
 * hatırlatıcı (an ve metin). Uymazsa null: metin yapılacak olarak eklenir.
 */
export function parseReminder(input: string, now = Date.now()): { at: number; text: string } | null {
  const s = input.trim();
  const rel = s.match(/^(\d+(?:[.,]\d+)?)\s*(sn|saniye|dk|dakika|sa|saat)\.?\s+(.+)$/i);
  if (rel) {
    const n = Number(rel[1].replace(",", "."));
    const unit = rel[2].toLocaleLowerCase("tr");
    const ms = unit.startsWith("s") && unit !== "sa" && unit !== "saat" ? 1000 : unit === "sa" || unit === "saat" ? 3600_000 : 60_000;
    return n > 0 ? { at: now + n * ms, text: rel[3].trim() } : null;
  }
  const abs = s.match(/^(\d{1,2})[:.](\d{2})\s+(.+)$/);
  if (abs) {
    const h = Number(abs[1]);
    const m = Number(abs[2]);
    if (h > 23 || m > 59) return null;
    const d = new Date(now);
    d.setHours(h, m, 0, 0);
    if (d.getTime() <= now) d.setDate(d.getDate() + 1);
    return { at: d.getTime(), text: abs[3].trim() };
  }
  return null;
}

/** Kalan süre: "4:59", "1:05:00". */
export function countdown(ms: number): string {
  const total = Math.max(0, Math.ceil(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = (total % 60).toString().padStart(2, "0");
  return h > 0 ? `${h}:${m.toString().padStart(2, "0")}:${s}` : `${m}:${s}`;
}

/** Kısa kalan süre: "45 sn", "19 dk"; bir saatten uzunsa saat ("14:30"). */
export function shortLeft(at: number, now = Date.now()): string {
  const ms = at - now;
  if (ms < 60_000) return `${Math.max(0, Math.ceil(ms / 1000))} sn`;
  if (ms < 3600_000) return `${Math.ceil(ms / 60_000)} dk`;
  return clockTime(at);
}

/** Saat olarak: "14:30". */
export function clockTime(ms: number): string {
  return new Date(ms).toLocaleTimeString("tr-TR", { hour: "2-digit", minute: "2-digit" });
}
