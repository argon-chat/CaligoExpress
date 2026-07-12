<script setup lang="ts">
import { computed } from "vue";
import { useRoute } from "vue-router";
import AppShell from "@/components/AppShell.vue";
import ConfirmDialog from "@/components/ConfirmDialog.vue";
import Toasts from "@/components/Toasts.vue";
import { selectedApp } from "@/lib/app-scope";

const route = useRoute();
const bare = computed(() => route.meta.public === true);
</script>

<template>
  <router-view v-if="bare" />
  <AppShell v-else>
    <!-- Keyed by app: switching apps remounts the page, which re-fetches under the new scope. -->
    <router-view :key="selectedApp" />
  </AppShell>
  <ConfirmDialog />
  <Toasts />
</template>
