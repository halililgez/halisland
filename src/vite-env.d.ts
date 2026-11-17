/// <reference types="vite/client" />

/** features.json'daki "assistant": false ise asistan sekmesi ve anahtarı yok. */
declare const __ASSISTANT__: boolean;

declare module "*.vue" {
  import type { DefineComponent } from "vue";
  const component: DefineComponent<{}, {}, any>;
  export default component;
}
