<script setup lang="ts">
import { onMounted, ref } from "vue";
import { api } from "@/lib/api";
import { apps, loadApps, selectedApp, setSelectedApp } from "@/lib/app-scope";
import { clearTokens } from "@/composables/useOAuth";
import type { Me } from "@/lib/types";

const me = ref<Me | null>(null);

// The worker this UI is pointed at — shown so an operator always knows which control plane this is.
const workerHost = new URL(import.meta.env.VITE_WORKER_URL ?? "http://localhost:8787").host;

const nav = [
  { to: "/", label: "Overview" },
  { to: "/settings", label: "Settings" },
];

onMounted(async () => {
  try {
    me.value = (await api.get<Me>("/me")).data;
  } catch {
    /* token may still be settling; ignore */
  }
  try {
    await loadApps();
  } catch {
    /* pre-multi-app worker or transient error — the default app still works */
  }
});

function switchApp(e: Event) {
  setSelectedApp((e.target as HTMLSelectElement).value); // router-view is keyed by app → pages re-fetch
}

function signOut() {
  clearTokens();
  location.href = "/login";
}

function initial(): string {
  const s = me.value?.name ?? me.value?.preferredUsername ?? "?";
  return s.slice(0, 1).toUpperCase();
}
</script>

<template>
  <div class="min-h-screen flex">
    <aside class="w-60 shrink-0 hidden md:flex flex-col border-r hairline bg-panel">
      <!-- Brand -->
      <div class="px-5 pt-5 pb-4">
        <div class="font-mono font-medium tracking-[0.22em] text-[15px]">CALIGO</div>
        <div class="font-mono text-[10px] tracking-[0.14em] uppercase text-ink-faint mt-0.5">release console</div>
      </div>

      <!-- App scope: the one fact every action below depends on. -->
      <div class="px-3 pb-3">
        <div class="rounded-lg border hairline bg-bg p-3">
          <div class="lbl mb-1.5!">Application</div>
          <select v-if="apps.length > 1" :value="selectedApp" @change="switchApp" class="inp font-mono text-[13px]!">
            <option v-for="a in apps" :key="a.app" :value="a.app">
              {{ a.app }}{{ a.isDefault ? "  · default" : "" }}
            </option>
          </select>
          <div v-else class="font-mono text-[13px] px-0.5">{{ selectedApp || "argon" }}</div>
        </div>
      </div>

      <nav class="px-3 flex flex-col gap-0.5">
        <RouterLink
          v-for="n in nav"
          :key="n.to"
          :to="n.to"
          class="px-3 py-2 rounded-[7px] text-[13px] text-ink-dim hover:text-ink hover:bg-raised transition-colors"
          active-class="!text-ink bg-raised"
        >{{ n.label }}</RouterLink>
      </nav>

      <div class="mt-auto">
        <div class="px-5 py-3 border-t hairline">
          <div class="lbl mb-0.5!">Worker</div>
          <div class="font-mono text-[11px] text-ink-dim truncate" :title="workerHost">{{ workerHost }}</div>
        </div>
        <div class="px-4 py-3 border-t hairline flex items-center gap-2.5">
          <div class="size-8 rounded-[7px] bg-raised border hairline grid place-items-center font-mono text-xs">
            {{ initial() }}
          </div>
          <div class="min-w-0">
            <div class="text-[13px] truncate leading-tight">{{ me?.name ?? me?.preferredUsername ?? "Signed in" }}</div>
            <div class="text-[11px] text-ink-faint truncate">{{ me?.email ?? me?.sub }}</div>
          </div>
          <button @click="signOut" class="ml-auto text-ink-faint hover:text-bad text-sm" title="Sign out">⏻</button>
        </div>
      </div>
    </aside>

    <main class="flex-1 min-w-0 p-4 md:p-8 max-w-6xl mx-auto w-full">
      <!-- Mobile bar (sidebar hidden): brand, nav, app scope -->
      <div class="md:hidden mb-4 flex items-center gap-3 flex-wrap">
        <span class="font-mono tracking-[0.2em] text-sm">CALIGO</span>
        <RouterLink
          v-for="n in nav"
          :key="n.to"
          :to="n.to"
          class="text-[13px] text-ink-dim"
          active-class="text-ink!"
        >{{ n.label }}</RouterLink>
        <select v-if="apps.length > 1" :value="selectedApp" @change="switchApp" class="inp font-mono w-auto! ml-auto">
          <option v-for="a in apps" :key="a.app" :value="a.app">{{ a.app }}</option>
        </select>
      </div>
      <slot />
    </main>
  </div>
</template>
