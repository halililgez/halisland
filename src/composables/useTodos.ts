import { ref, watch, type Ref } from "vue";

export interface Todo {
  id: number;
  text: string;
  done: boolean;
  /** Tamamlandığı gün (YYYY-MM-DD); ertesi gün listeden düşer. */
  doneOn?: string;
}

const KEY = "halisland.todos";
const MAX_TEXT = 200;

function today(d = new Date()) {
  const p = (n: number) => n.toString().padStart(2, "0");
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

function load(): Todo[] {
  try {
    const raw = window.localStorage.getItem(KEY);
    const list = raw ? (JSON.parse(raw) as Todo[]) : [];
    // Önceki günlerde tamamlananlar artık gösterilmez.
    const t = today();
    return Array.isArray(list) ? list.filter((x) => !x.done || x.doneOn === t) : [];
  } catch {
    return [];
  }
}

/** Tek liste: Bugün sekmesi ve asistan (yapilacak aracı) aynı durumu kullanır. */
let shared: Ref<Todo[]> | null = null;

function state(): Ref<Todo[]> {
  if (shared) return shared;
  const todos = ref<Todo[]>(load());
  watch(
    todos,
    (list) => {
      try {
        window.localStorage.setItem(KEY, JSON.stringify(list));
      } catch {
        /* depo kapalıysa yalnızca bu oturumda kalır */
      }
    },
    { deep: true },
  );
  shared = todos;
  return todos;
}

/**
 * Bugünün yapılacakları. WebView'in yerel deposunda (localStorage) tutulur;
 * tamamlananlar ertesi gün kendiliğinden temizlenir.
 */
export function useTodos() {
  const todos = state();

  function add(text: string): Todo | null {
    const t = text.trim().slice(0, MAX_TEXT);
    if (!t) return null;
    // Aynı milisaniyede iki ekleme (asistan) kimlik çakıştırmasın.
    const id = Math.max(Date.now(), ...todos.value.map((x) => x.id + 1));
    const item = { id, text: t, done: false };
    todos.value.push(item);
    return item;
  }

  function toggle(id: number) {
    const item = todos.value.find((x) => x.id === id);
    if (!item) return;
    item.done = !item.done;
    item.doneOn = item.done ? today() : undefined;
  }

  function remove(id: number) {
    todos.value = todos.value.filter((x) => x.id !== id);
  }

  function clearDone() {
    todos.value = todos.value.filter((x) => !x.done);
  }

  return { todos, add, toggle, remove, clearDone };
}
