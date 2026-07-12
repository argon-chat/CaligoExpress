<script setup lang="ts">
import { ref } from "vue";
import { ensureAuthenticated } from "@/composables/useOAuth";

const busy = ref(false);

async function signIn() {
  busy.value = true;
  await ensureAuthenticated(); // redirects to Aegis SSO (PKCE)
}
</script>

<template>
  <div class="min-h-screen grid place-items-center p-6">
    <div class="panel p-10 w-full max-w-sm">
      <div class="font-mono font-medium tracking-[0.24em] text-lg">CALIGO</div>
      <div class="font-mono text-[10px] tracking-[0.14em] uppercase text-ink-faint mt-1 mb-8">release console</div>
      <button
        @click="signIn"
        :disabled="busy"
        class="btn w-full py-2.5! font-medium"
      >
        {{ busy ? "Redirecting…" : "Sign in with SSO" }}
      </button>
      <p class="text-[11px] text-ink-faint mt-5">Operator access only. You'll be redirected to your identity provider.</p>
    </div>
  </div>
</template>
