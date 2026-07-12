/// <reference types="vite/client" />

declare module "*.vue" {
  import type { DefineComponent } from "vue";
  const component: DefineComponent<Record<string, unknown>, Record<string, unknown>, unknown>;
  export default component;
}

interface ImportMetaEnv {
  readonly VITE_WORKER_URL?: string;
  readonly VITE_OAUTH_AUTHORIZE_URL?: string;
  readonly VITE_OAUTH_TOKEN_URL?: string;
  readonly VITE_OAUTH_CLIENT_ID?: string;
}
interface ImportMeta {
  readonly env: ImportMetaEnv;
}
