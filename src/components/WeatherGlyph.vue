<script setup lang="ts">
/** Hava durumu simgesi (çizgi, 24x24). */
import type { WeatherIcon } from "../composables/useWeather";

defineProps<{ icon: WeatherIcon; size?: number }>();

const CLOUD = "M7 18h10a4 4 0 0 0 .6-7.96A5.5 5.5 0 0 0 7 10.5 3.75 3.75 0 0 0 7 18z";
</script>

<template>
  <svg
    :width="size ?? 22"
    :height="size ?? 22"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="1.7"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
    :class="icon"
  >
    <template v-if="icon === 'sun'">
      <circle cx="12" cy="12" r="4" class="warm" />
      <path
        class="warm"
        d="M12 2.5v2M12 19.5v2M2.5 12h2M19.5 12h2M5.3 5.3l1.4 1.4M17.3 17.3l1.4 1.4M5.3 18.7l1.4-1.4M17.3 6.7l1.4-1.4"
      />
    </template>
    <path v-else-if="icon === 'moon'" d="M19 14.5A7.5 7.5 0 0 1 9.5 5a7.5 7.5 0 1 0 9.5 9.5z" />
    <template v-else-if="icon === 'partly'">
      <path class="warm" d="M8 3v1.5M3.5 8H5M4.8 4.8l1 1M11.2 4.8l-1 1" />
      <path class="warm" d="M5.6 10.6A3 3 0 0 1 10.9 7.4" />
      <path :d="CLOUD" transform="translate(1.5 1)" />
    </template>
    <path v-else-if="icon === 'cloud'" :d="CLOUD" />
    <template v-else-if="icon === 'fog'">
      <path :d="CLOUD" transform="translate(0 -3)" />
      <path d="M5 19h14M8 22h8" />
    </template>
    <template v-else-if="icon === 'rain'">
      <path :d="CLOUD" transform="translate(0 -3)" />
      <path class="cool" d="M9 18l-1 3M13 18l-1 3M17 18l-1 3" />
    </template>
    <template v-else-if="icon === 'snow'">
      <path :d="CLOUD" transform="translate(0 -3)" />
      <path class="cool" d="M9 19.5h.01M13 21h.01M17 19.5h.01M11 22.5h.01M15 22.5h.01" stroke-width="2.4" />
    </template>
    <template v-else>
      <path :d="CLOUD" transform="translate(0 -3)" />
      <path class="warm" d="M12.5 16l-2 3.5h3l-2 3.5" />
    </template>
  </svg>
</template>

<style scoped>
.warm {
  stroke: var(--c-waiting);
}

.cool {
  stroke: var(--c-done);
}
</style>
