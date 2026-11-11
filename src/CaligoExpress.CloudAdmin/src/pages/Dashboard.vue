<script setup lang="ts">
import { onMounted, ref } from "vue";
import { api } from "@/lib/api";
import { selectedApp } from "@/lib/app-scope";
import { channelTone, errMsg, timeAgo, exactTime } from "@/lib/ui";
import { confirm } from "@/composables/useConfirm";
import { toastErr, toastOk } from "@/composables/useToasts";
import type { LiveConfig, OverviewChannel } from "@/lib/types";

interface GcReport {
  apps?: string[];
  keptReleases: number;
  keptBootstrappers: number;
  keepPerChannel: number;
  referencedKeys: number;
  replicated: number;
  listed: number;
  skippedChunks: number;
  unknownAppKeys?: number;
  deleted: number;
  dbDeleted: number;
  dryRun?: boolean;
  wouldDeleteTotal?: number;
  wouldDelete?: string[];
  errors: string[];
}

const channels = ref<OverviewChannel[]>([]);
const config = ref<LiveConfig | null>(null);
const configError = ref<string | null>(null);
const showRaw = ref(false);
const loading = ref(true);
const gcBusy = ref(false);
const error = ref<string | null>(null);
const gc = ref<GcReport | null>(null);

async function load() {
  loading.value = true;
  error.value = null;
  try {
    channels.value = (await api.get<{ channels: OverviewChannel[] }>("/admin/overview")).data.channels;
  } catch (e) {
    error.value = errMsg(e);
  } finally {
    loading.value = false;
  }
  // The signed config.json clients actually receive (mirrors / channels / pinned keys / minBootstrapper).
  try {
    config.value = (await api.get<LiveConfig>("/config.json")).data;
    configError.value = null;
  } catch (e) {
    config.value = null;
    configError.value = errMsg(e);
  }
}

// GC sweeps ALL apps on this worker (it heals mirrors + prunes the shared bucket by app namespace).
async function runGc(dryRun: boolean) {
  if (!dryRun) {
    const ok = await confirm({
      title: "Run garbage collection",
      body: "Prunes artifacts no kept release references, across every app on this worker, and re-uploads current manifests to all mirrors. Deletion is permanent.",
      action: "Run GC",
      tone: "bad",
      details: ["scope   all apps on this worker"],
    });
    if (!ok) return;
  }
  gcBusy.value = true;
  try {
    gc.value = (await api.post<GcReport>("/admin/gc", { dryRun })).data;
    if (gc.value.errors?.length) toastErr(`GC finished with ${gc.value.errors.length} error(s)`);
    else toastOk(dryRun ? "Dry run complete — nothing was deleted" : "GC complete");
  } catch (e) {
    toastErr(errMsg(e));
  } finally {
    gcBusy.value = false;
  }
}

onMounted(load);
</script>

<template>
  <header class="flex items-end justify-between mb-6 gap-4 flex-wrap">
    <div>
      <div class="silk mb-1.5">Overview</div>
      <h1 class="text-xl font-semibold tracking-tight">
        <span class="font-mono">{{ selectedApp }}</span>
        <span class="text-ink-faint font-normal text-[15px] ml-2" v-if="channels.length">{{ channels.length }} channel{{ channels.length === 1 ? "" : "s" }}</span>
      </h1>
    </div>
    <div class="flex items-center gap-2">
      <button @click="runGc(true)" :disabled="gcBusy" class="btn" title="Report what GC would delete, without deleting">
        Dry-run GC
      </button>
      <button @click="runGc(false)" :disabled="gcBusy" class="btn">
        {{ gcBusy ? "Running…" : "Run GC" }}
      </button>
    </div>
  </header>

  <div v-if="error" class="panel p-4 text-bad mb-4 text-[13px]">{{ error }}</div>

  <!-- ── GC report: everything the sweep did (or would do), in the UI instead of the console ── -->
  <section v-if="gc" class="panel p-5 mb-6">
    <div class="flex items-center justify-between mb-4">
      <div class="silk">GC report</div>
      <span v-if="gc.dryRun" class="chip chip-warn">dry run — nothing deleted</span>
      <button class="text-ink-faint hover:text-ink text-xs" @click="gc = null">Dismiss</button>
    </div>
    <div class="grid grid-cols-2 sm:grid-cols-4 gap-x-6 gap-y-4 text-[13px]">
      <div><div class="lbl">Apps swept</div><span class="font-mono">{{ gc.apps?.join(", ") ?? "—" }}</span></div>
      <div><div class="lbl">Kept</div><span class="font-mono">{{ gc.keptReleases }} releases · {{ gc.keptBootstrappers }} boots</span></div>
      <div><div class="lbl">Keys listed / referenced</div><span class="font-mono">{{ gc.listed }} / {{ gc.referencedKeys }}</span></div>
      <div>
        <div class="lbl">{{ gc.dryRun ? "Would delete" : "Deleted" }}</div>
        <span class="font-mono" :class="(gc.dryRun ? gc.wouldDeleteTotal : gc.deleted) ? 'text-warn' : 'text-live'">
          {{ gc.dryRun ? gc.wouldDeleteTotal : `${gc.deleted} keys · ${gc.dbDeleted} db rows` }}
        </span>
      </div>
    </div>
    <div v-if="gc.skippedChunks || gc.unknownAppKeys" class="mt-3 text-xs text-ink-faint">
      <span v-if="gc.skippedChunks">{{ gc.skippedChunks }} chunks held back (keep set incomplete — fail-safe). </span>
      <span v-if="gc.unknownAppKeys">{{ gc.unknownAppKeys }} keys under unknown app namespaces were left untouched.</span>
    </div>
    <div v-if="gc.wouldDelete?.length" class="mt-4">
      <div class="lbl">Keys that would be deleted ({{ gc.wouldDeleteTotal }} total, first {{ gc.wouldDelete.length }})</div>
      <div class="rounded-[7px] border hairline bg-bg p-3 max-h-48 overflow-auto font-mono text-[11px] text-ink-dim leading-relaxed">
        <div v-for="k in gc.wouldDelete" :key="k">{{ k }}</div>
      </div>
    </div>
    <div v-if="gc.errors.length" class="mt-4">
      <div class="lbl text-bad!">Errors</div>
      <div class="rounded-[7px] border border-[color-mix(in_srgb,var(--color-bad)_40%,transparent)] bg-bg p-3 font-mono text-[11px] text-bad leading-relaxed">
        <div v-for="(e, i) in gc.errors" :key="i">{{ e }}</div>
      </div>
    </div>
  </section>

  <div v-if="loading" class="text-ink-dim text-[13px]">Loading…</div>

  <!-- ── Channels: one card per channel, its hue on the edge, the live release inside ── -->
  <div v-else-if="channels.length" class="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
    <RouterLink
      v-for="c in channels"
      :key="c.channel"
      :to="`/channels/${c.channel}`"
      class="panel panel-hover p-5 block relative overflow-hidden"
    >
      <span class="absolute left-0 top-0 bottom-0 w-0.75" :style="{ background: channelTone(c.channel) }" />
      <div class="flex items-center justify-between">
        <span class="font-mono text-[13px] font-medium" :style="{ color: channelTone(c.channel) }">{{ c.channel }}</span>
        <span class="chip">seq {{ c.currentSeq }}</span>
      </div>
      <template v-if="c.release">
        <div class="mt-3 font-mono text-[13px] truncate" :title="c.release.releaseId">{{ c.release.releaseId }}</div>
        <div class="text-xs text-ink-faint mt-1" :title="exactTime(c.release.createdAt)">
          {{ timeAgo(c.release.createdAt) }} · launcher {{ c.bootstrapper ?? "—" }}
        </div>
        <div class="mt-3 flex flex-wrap gap-1.5">
          <span v-for="p in c.release.packages" :key="p.name" class="chip">{{ p.name }}@{{ p.version }}</span>
          <span v-if="c.release.mac" class="chip chip-info">mac {{ c.release.mac }}</span>
          <span v-if="c.release.linux" class="chip chip-info">linux {{ c.release.linux }}</span>
        </div>
      </template>
      <div v-else class="mt-3 text-[13px] text-ink-faint">Nothing published yet</div>
    </RouterLink>
  </div>

  <div v-else class="panel p-10 text-center">
    <div class="text-[13px] text-ink-dim mb-2">No channels for <span class="font-mono">{{ selectedApp }}</span> yet.</div>
    <div class="text-xs text-ink-faint">
      Publish the first release from CI:
      <code class="font-mono text-ink-dim">caligo publish --channel beta …</code>
    </div>
  </div>

  <!-- ── Live config.json: exactly what clients receive (signed offline) ── -->
  <section class="mt-8">
    <div class="flex items-center justify-between mb-3">
      <div class="silk">Live config.json</div>
      <button v-if="config" @click="showRaw = !showRaw" class="btn btn-sm">
        {{ showRaw ? "Hide raw" : "Raw JSON" }}
      </button>
    </div>

    <div v-if="configError" class="panel p-4 text-[13px] text-ink-faint">
      No config published for this app yet ({{ configError }}). Run
      <code class="font-mono text-ink-dim">caligo init --config-out … --worker-url …</code> to sign and push one.
    </div>

    <div v-else-if="config" class="panel p-5 text-[13px]">
      <div class="grid gap-6 md:grid-cols-2">
        <div>
          <div class="lbl">minBootstrapper</div>
          <div class="font-mono">{{ config.minBootstrapper }}</div>
          <div class="lbl mt-4">Signed by</div>
          <div class="font-mono">{{ config.signature?.keyId ?? "unsigned" }}<template v-if="config.signature"> · {{ config.signature.alg }}</template></div>
          <div class="lbl mt-4">Channels</div>
          <div class="flex flex-wrap gap-1.5">
            <span v-for="(_, ch) in config.channels" :key="ch" class="chip" :style="{ color: channelTone(String(ch)) }">{{ ch }}</span>
          </div>
          <div class="lbl mt-4">Trusted keys</div>
          <div class="flex flex-wrap gap-1.5">
            <span v-for="k in config.publicKeys" :key="k.id" class="chip">{{ k.id }} · {{ k.alg }}</span>
          </div>
        </div>
        <div>
          <div class="lbl mb-2">Mirrors — client failover order</div>
          <table class="w-full text-xs">
            <tbody>
              <tr v-for="m in config.mirrors" :key="m.host" class="border-t hairline first:border-0">
                <td class="py-1.5 pr-3 text-ink-faint font-mono w-8">p{{ m.priority ?? 0 }}</td>
                <td class="py-1.5 font-mono">{{ m.scheme ?? "https" }}://{{ m.host }}</td>
                <td class="py-1.5 text-ink-faint font-mono text-right">{{ (m.regions ?? ["*"]).join(",") }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <pre v-if="showRaw" class="mt-5 p-4 rounded-[7px] border hairline bg-bg overflow-auto text-[11px] font-mono text-ink-dim">{{ JSON.stringify(config, null, 2) }}</pre>
    </div>
  </section>
</template>
