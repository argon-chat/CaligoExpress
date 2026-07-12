import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
import tailwindcss from "@tailwindcss/vite";
import { fileURLToPath, URL } from "node:url";

export default defineConfig({
  plugins: [vue(), tailwindcss()],
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
      // @argon/core lives in the monorepo; for a standalone build we shim the bits this app uses.
      "@argon/core": fileURLToPath(new URL("./src/lib/argon-core.ts", import.meta.url)),
    },
  },
  server: { port: 5173 },
});
