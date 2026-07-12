<script setup lang="ts">
import { watch, nextTick, useTemplateRef } from "vue";
import { pending, settle } from "@/composables/useConfirm";

const confirmBtn = useTemplateRef<HTMLButtonElement>("confirmBtn");

// Focus the confirming button when the dialog opens; Escape cancels.
watch(pending, async (p) => {
  if (p) {
    await nextTick();
    confirmBtn.value?.focus();
  }
});

function onKeydown(e: KeyboardEvent) {
  if (e.key === "Escape") settle(false);
}
</script>

<template>
  <div v-if="pending" class="overlay" @keydown="onKeydown" @click.self="settle(false)">
    <div class="panel w-full max-w-sm p-6" role="alertdialog" aria-modal="true" :aria-label="pending.title">
      <h2 class="font-semibold text-[15px] mb-2">{{ pending.title }}</h2>
      <p class="text-[13px] text-ink-dim leading-relaxed">{{ pending.body }}</p>

      <div v-if="pending.details?.length" class="mt-4 rounded-[7px] border hairline bg-bg px-3.5 py-2.5 font-mono text-xs text-ink-dim">
        <div v-for="(line, i) in pending.details" :key="i" class="py-0.5">{{ line }}</div>
      </div>

      <div class="mt-6 flex justify-end gap-2.5">
        <button class="btn" @click="settle(false)">Cancel</button>
        <button
          ref="confirmBtn"
          class="btn"
          :class="pending.tone === 'live' ? 'btn-live' : pending.tone === 'warn' ? 'btn-warn' : 'btn-bad'"
          @click="settle(true)"
        >{{ pending.action }}</button>
      </div>
    </div>
  </div>
</template>
