import { computed, onBeforeUnmount, ref } from "vue";

export function useClock() {
  const now = ref(new Date());
  const timer = window.setInterval(() => (now.value = new Date()), 1000);
  onBeforeUnmount(() => window.clearInterval(timer));

  const time = computed(() =>
    now.value.toLocaleTimeString("tr-TR", { hour: "2-digit", minute: "2-digit" }),
  );
  const date = computed(() =>
    now.value
      .toLocaleDateString("tr-TR", { weekday: "short", day: "numeric", month: "short" })
      .toLocaleUpperCase("tr-TR"),
  );

  return { now, time, date };
}
