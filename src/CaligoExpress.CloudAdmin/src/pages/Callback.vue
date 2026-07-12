<script setup lang="ts">
import { onMounted, ref } from "vue";
import { useRouter } from "vue-router";
import { exchangeCodeForToken, startTokenAutoRefresh } from "@/composables/useOAuth";

const router = useRouter();
const error = ref<string | null>(null);

onMounted(async () => {
  const code = new URLSearchParams(location.search).get("code");
  if (!code) {
    error.value = "The sign-in redirect is missing its authorization code. Start again from the sign-in page.";
    return;
  }
  try {
    await exchangeCodeForToken(code);
    startTokenAutoRefresh();
    router.replace("/");
  } catch (e) {
    error.value = e instanceof Error ? e.message : "Sign-in failed";
  }
});
</script>

<template>
  <div class="min-h-screen grid place-items-center p-6">
    <div class="panel p-10 text-center min-w-72">
      <template v-if="!error">
        <div class="size-7 mx-auto mb-4 rounded-full border-2 border-line-strong border-t-ink-dim animate-spin" />
        <div class="text-ink-dim text-[13px]">Completing sign-in…</div>
      </template>
      <template v-else>
        <div class="text-bad text-[13px] mb-5">{{ error }}</div>
        <RouterLink to="/login" class="btn btn-sm">Back to sign-in</RouterLink>
      </template>
    </div>
  </div>
</template>
