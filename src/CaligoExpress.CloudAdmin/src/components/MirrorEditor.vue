<script setup lang="ts">
import type { MirrorRow } from "@/lib/types";

const model = defineModel<MirrorRow[]>({ required: true });
defineProps<{ title: string; help: string; accent: string }>();

function blank(): MirrorRow {
  return {
    name: "", endpoint: "", bucket: "", region: "auto", publicBaseUrl: "",
    accessKeyId: "", secretAccessKey: "", hasSecret: false, accessKeyHint: "",
  };
}

function add() {
  model.value = [...model.value, blank()];
}
function remove(i: number) {
  model.value = model.value.filter((_, idx) => idx !== i);
}
</script>

<template>
  <section class="panel p-6">
    <div class="flex items-center justify-between mb-1">
      <h2 class="font-semibold text-[15px] flex items-center gap-2">
        <span class="size-2 rounded-xs" :style="{ background: accent }" />{{ title }}
      </h2>
      <div class="flex gap-2">
        <button @click="add" class="btn btn-sm">+ Add mirror</button>
        <slot />
      </div>
    </div>
    <p class="text-xs text-ink-faint mb-4">{{ help }}</p>

    <div v-if="!model.length" class="text-[13px] text-ink-faint py-8 text-center border border-dashed hairline rounded-lg">
      No mirrors yet — add one.
    </div>

    <div v-for="(m, i) in model" :key="i" class="rounded-lg border hairline bg-bg p-4 mb-3 last:mb-0">
      <div class="flex items-center justify-between mb-3">
        <span class="text-xs font-mono text-ink-dim">{{ m.name || "(unnamed)" }}</span>
        <button @click="remove(i)" class="text-xs text-ink-faint hover:text-bad">Remove</button>
      </div>
      <div class="grid sm:grid-cols-2 gap-3">
        <label><span class="lbl">Name</span><input v-model="m.name" class="inp" placeholder="r2-eu" /></label>
        <label><span class="lbl">Region</span><input v-model="m.region" class="inp" placeholder="auto" /></label>
        <label class="sm:col-span-2">
          <span class="lbl">Endpoint</span>
          <input v-model="m.endpoint" class="inp font-mono" placeholder="https://<acct>.r2.cloudflarestorage.com" />
        </label>
        <label><span class="lbl">Bucket</span><input v-model="m.bucket" class="inp font-mono" placeholder="argon-cdn" /></label>
        <label>
          <span class="lbl">Public base URL</span>
          <input v-model="m.publicBaseUrl" class="inp font-mono" placeholder="https://updates.example.com" />
        </label>
        <label>
          <span class="lbl">Access key ID</span>
          <input
            v-model="m.accessKeyId"
            class="inp font-mono"
            autocomplete="off"
            :placeholder="m.accessKeyHint || 'AKIA…'"
          />
        </label>
        <label>
          <span class="lbl">Secret access key</span>
          <input
            v-model="m.secretAccessKey"
            type="password"
            class="inp font-mono"
            autocomplete="off"
            :placeholder="m.hasSecret ? '•••••••• (unchanged)' : 'secret'"
          />
        </label>
      </div>
      <p v-if="m.hasSecret" class="text-[11px] text-ink-faint mt-2">
        Credentials are stored — leave the key fields blank to keep them.
      </p>
    </div>
  </section>
</template>
