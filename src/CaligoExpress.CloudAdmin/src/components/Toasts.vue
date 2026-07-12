<script setup lang="ts">
import { toasts } from "@/composables/useToasts";
</script>

<template>
  <div class="fixed bottom-4 right-4 z-[60] flex flex-col gap-2 items-end pointer-events-none" aria-live="polite">
    <TransitionGroup name="toast">
      <div
        v-for="t in toasts"
        :key="t.id"
        class="panel pointer-events-auto px-4 py-2.5 text-[13px] max-w-md flex items-start gap-2.5"
        :class="t.kind === 'err' ? 'border-[color-mix(in_srgb,var(--color-bad)_50%,transparent)]' : ''"
      >
        <span
          class="mt-1 size-1.5 rounded-full flex-none"
          :class="t.kind === 'ok' ? 'bg-live' : 'bg-bad'"
        />
        <span :class="t.kind === 'err' ? 'text-bad' : ''">{{ t.text }}</span>
      </div>
    </TransitionGroup>
  </div>
</template>
