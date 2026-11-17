import { ref, watch, type Ref } from "vue";
import { invoke } from "@tauri-apps/api/core";

/** Rust'taki `WeatherCity` ile aynı (src-tauri/src/settings.rs). */
export interface WeatherCity {
  name: string;
  latitude: number;
  longitude: number;
  /** Cihaz konumundan mı? Öyleyse her yenilemede konum da tazelenir. */
  device?: boolean;
}

/** Cihaz konumunu kullanırken gösterilen ad. */
export const DEVICE_NAME = "Konumum";

/** Windows konum servisinden (yaklaşık 1 km'ye yuvarlanmış) konum. */
export function deviceLocation() {
  return invoke<{ latitude: number; longitude: number }>("device_location");
}

export type WeatherIcon = "sun" | "moon" | "partly" | "cloud" | "fog" | "rain" | "snow" | "storm";

export interface Weather {
  temp: number;
  min: number;
  max: number;
  code: number;
  isDay: boolean;
  /** Ölçüm zamanı (ms) */
  fetchedAt: number;
}

/** Bu süreden eski veri, sekme yeniden açılınca yenilenir. */
const STALE_MS = 30 * 60_000;

/**
 * Hava durumu (Open-Meteo, anahtarsız). Yalnızca Bugün sekmesi görünürken
 * ve veri eskiyse ya da şehir değiştiyse istenir.
 */
export function useWeather(city: Ref<WeatherCity | null>, active: Ref<boolean>) {
  const weather = ref<Weather | null>(null);
  const loading = ref(false);
  const error = ref("");
  let key = "";

  async function load() {
    let c = city.value;
    if (!c) {
      weather.value = null;
      return;
    }
    loading.value = true;
    error.value = "";
    // Cihaz konumu: önce konumu tazele. Alınamazsa son bilinen konumla devam.
    if (c.device) {
      try {
        const p = await deviceLocation();
        if (p.latitude !== c.latitude || p.longitude !== c.longitude) {
          c = { ...c, latitude: p.latitude, longitude: p.longitude };
          // Kaydedilen konum değişince izleyici yeniden yüklemesin.
          key = cityKey(c);
          invoke("set_weather_city", { city: c }).catch((e) => console.error("set_weather_city", e));
        }
      } catch (e) {
        console.warn("device_location", e);
      }
    }
    try {
      const url =
        "https://api.open-meteo.com/v1/forecast" +
        `?latitude=${c.latitude}&longitude=${c.longitude}` +
        "&current=temperature_2m,weather_code,is_day" +
        "&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto";
      const res = await fetch(url);
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const d = await res.json();
      weather.value = {
        temp: d.current.temperature_2m,
        code: d.current.weather_code,
        isDay: d.current.is_day === 1,
        min: d.daily.temperature_2m_min[0],
        max: d.daily.temperature_2m_max[0],
        fetchedAt: Date.now(),
      };
      key = cityKey(c);
    } catch (e) {
      console.error("weather", e);
      error.value = "Hava durumu alınamadı";
    } finally {
      loading.value = false;
    }
  }

  watch(
    [active, city],
    () => {
      if (!active.value) return;
      const c = city.value;
      if (!c) {
        weather.value = null;
        key = "";
        return;
      }
      // Konum tazelenirken kaydedilen yeni koordinat için ikinci istek atma.
      if (loading.value && key === cityKey(c)) return;
      const old = !weather.value || Date.now() - weather.value.fetchedAt > STALE_MS;
      if (old || key !== cityKey(c)) load();
    },
    { immediate: true },
  );

  return { weather, loading, error, reload: load };
}

function cityKey(c: WeatherCity) {
  return `${c.latitude},${c.longitude}`;
}

export interface CityResult {
  city: WeatherCity;
  /** Listede gösterilen uzun ad (ülkeyle birlikte, aynı adlı yerler ayırt edilsin). */
  label: string;
}

/** Şehir arama (Open-Meteo konum araması). Kaydedilen ad kısa: "Kadıköy, İstanbul". */
export async function searchCity(query: string): Promise<CityResult[]> {
  const q = query.trim();
  if (q.length < 2) return [];
  const url =
    "https://geocoding-api.open-meteo.com/v1/search" +
    `?name=${encodeURIComponent(q)}&count=5&language=tr&format=json`;
  const res = await fetch(url);
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  const d = await res.json();
  return (d.results ?? []).map(
    (r: { name: string; admin1?: string; country?: string; latitude: number; longitude: number }) => {
      const region = r.admin1 && r.admin1 !== r.name ? r.admin1 : "";
      const name = [r.name, region].filter(Boolean).join(", ");
      return {
        city: { name, latitude: r.latitude, longitude: r.longitude },
        label: [name, r.country].filter(Boolean).join(" · "),
      };
    },
  );
}

/** WMO hava kodu → Türkçe açıklama ve simge. */
export function describe(code: number, isDay: boolean): { label: string; icon: WeatherIcon } {
  if (code === 0) return { label: isDay ? "Güneşli" : "Açık", icon: isDay ? "sun" : "moon" };
  if (code === 1) return { label: "Az bulutlu", icon: isDay ? "partly" : "moon" };
  if (code === 2) return { label: "Parçalı bulutlu", icon: "partly" };
  if (code === 3) return { label: "Kapalı", icon: "cloud" };
  if (code === 45 || code === 48) return { label: "Sisli", icon: "fog" };
  if (code >= 51 && code <= 57) return { label: "Çisenti", icon: "rain" };
  if (code >= 61 && code <= 67) return { label: "Yağmurlu", icon: "rain" };
  if (code >= 71 && code <= 77) return { label: "Karlı", icon: "snow" };
  if (code >= 80 && code <= 82) return { label: "Sağanak", icon: "rain" };
  if (code === 85 || code === 86) return { label: "Kar sağanağı", icon: "snow" };
  if (code >= 95) return { label: "Gök gürültülü", icon: "storm" };
  return { label: "—", icon: "cloud" };
}
