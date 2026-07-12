<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { api } from "@/lib/api";
import { selectedApp } from "@/lib/app-scope";
import { channelTone, errMsg, exactTime, timeAgo } from "@/lib/ui";
import { confirm } from "@/composables/useConfirm";
import { toastErr, toastOk } from "@/composables/useToasts";
import type { BootstrappersResponse, ReleaseManifestFull, ReleasesResponse } from "@/lib/types";

const props = defineProps<{ channel: string }>();

const data = ref<ReleasesResponse | null>(null);
const boots = ref<BootstrappersResponse | null>(null);
const loading = ref(true);
const busySeq = ref<number | null>(null);
const busyBoot = ref<number | null>(null);
const error = ref<string | null>(null);

// Lazily-loaded full manifests for the expandable detail rows.
const openSeq = ref<number | null>(null);
const detail = ref<Record<number, ReleaseManifestFull>>({});
const detailLoading = ref<number | null>(null);

// The currently-expanded release's full manifest (only one row is open at a time).
const openDetail = computed<ReleaseManifestFull | null>(() =>
  openSeq.value != null ? detail.value[openSeq.value] ?? null : null,
);

async function load() {
  loading.value = true;
  error.value = null;
  try {
    const [rel, boot] = await Promise.all([
      api.get<ReleasesResponse>(`/admin/releases/${props.channel}`).then((r) => r.data),
      api.get<BootstrappersResponse>(`/admin/bootstrappers/${props.channel}`).then((r) => r.data).catch(() => null),
    ]);
    data.value = rel;
    boots.value = boot;
  } catch (e) {
    error.value = errMsg(e);
  } finally {
    loading.value = false;
  }
}

async function toggleDetail(seq: number) {
  if (openSeq.value === seq) {
    openSeq.value = null;
    return;
  }
  openSeq.value = seq;
  if (detail.value[seq]) return;
  detailLoading.value = seq;
  try {
    detail.value[seq] = (await api.get<ReleaseManifestFull>(`/admin/release/${props.channel}/${seq}`)).data;
  } catch (e) {
    error.value = errMsg(e);
  } finally {
    detailLoading.value = null;
  }
}

// Move the app pointer — the action this whole page exists for, so it confirms first.
async function move(seq: number) {
  const from = data.value?.current ?? 0;
  const forward = seq > from;
  const ok = await confirm({
    title: forward ? `Promote to seq ${seq}` : `Roll back to seq ${seq}`,
    body: forward
      ? "The channel pointer moves forward; clients pick up this release on their next launch."
      : "The channel pointer moves backward; clients on newer builds will downgrade on their next launch.",
    action: forward ? "Promote" : "Roll back",
    tone: forward ? "live" : "warn",
    details: [`app      ${selectedApp.value}`, `channel  ${props.channel}`, `pointer  seq ${from} → seq ${seq}`],
  });
  if (!ok) return;

  busySeq.value = seq;
  try {
    await api.post(`/${forward ? "promote" : "rollback"}`, { channel: props.channel, seq });
    toastOk(`${forward ? "Promoted" : "Rolled back"} ${props.channel} to seq ${seq}`);
    await load();
  } catch (e) {
    toastErr(errMsg(e));
  } finally {
    busySeq.value = null;
  }
}

// Move the bootstrapper pointer (independent track).
async function moveBoot(seq: number) {
  const from = boots.value?.current ?? 0;
  const forward = seq > from;
  const version = boots.value?.bootstrappers.find((b) => b.seq === seq)?.version ?? "?";
  const ok = await confirm({
    title: forward ? `Promote launcher to ${version}` : `Roll launcher back to ${version}`,
    body: "The launcher self-updates independently of app releases; clients replace their bootstrapper on next launch.",
    action: forward ? "Promote" : "Roll back",
    tone: forward ? "live" : "warn",
    details: [`app      ${selectedApp.value}`, `channel  ${props.channel}`, `launcher seq ${from} → seq ${seq} (${version})`],
  });
  if (!ok) return;

  busyBoot.value = seq;
  try {
    await api.post(`/${forward ? "promote" : "rollback"}`, { channel: props.channel, seq, kind: "boot" });
    toastOk(`Launcher ${forward ? "promoted" : "rolled back"} to ${version}`);
    await load();
  } catch (e) {
    toastErr(errMsg(e));
  } finally {
    busyBoot.value = null;
  }
}

onMounted(load);
</script>

<template>
  <header class="mb-6">
    <RouterLink to="/" class="text-xs text-ink-faint hover:text-ink font-mono">← overview</RouterLink>
    <div class="flex items-center gap-3 mt-2 flex-wrap">
      <h1 class="text-xl font-semibold tracking-tight font-mono" :style="{ color: channelTone(channel) }">{{ channel }}</h1>
      <span class="text-ink-faint text-[13px]">on <span class="font-mono">{{ selectedApp }}</span></span>
      <span v-if="data" class="chip">app seq {{ data.current ?? "—" }}</span>
      <span v-if="boots?.current != null" class="chip">launcher seq {{ boots.current }}</span>
    </div>
  </header>

  <div v-if="error" class="panel p-4 text-bad mb-4 text-[13px]">{{ error }}</div>
  <div v-if="loading" class="text-ink-dim text-[13px]">Loading…</div>

  <template v-else>
    <!-- ── App releases: the rail. Newest on top; the filled node is where clients are. ── -->
    <div class="silk mb-3">App releases</div>
    <section v-if="data && data.releases.length" class="panel py-2 px-3 mb-8">
      <div v-for="r in data.releases" :key="r.seq" class="rail-row border-b hairline last:border-0">
        <span class="rail-node" :class="r.current ? 'rail-node-live' : ''" />
        <div
          class="py-3 pr-2 cursor-pointer group"
          role="button"
          tabindex="0"
          :aria-expanded="openSeq === r.seq"
          @click="toggleDetail(r.seq)"
          @keydown.enter="toggleDetail(r.seq)"
        >
          <div class="flex items-center gap-3 flex-wrap">
            <span class="font-mono text-[13px] text-ink-faint w-14">seq {{ r.seq }}</span>
            <span class="font-mono text-[13px] group-hover:text-ink" :class="r.current ? '' : 'text-ink-dim'">{{ r.releaseId }}</span>
            <span v-if="r.current" class="flag-live">LIVE</span>
            <span class="ml-auto text-xs text-ink-faint" :title="exactTime(r.publishedAt)">{{ timeAgo(r.publishedAt) }}</span>
            <button
              v-if="!r.current"
              @click.stop="move(r.seq)"
              :disabled="busySeq === r.seq"
              class="btn btn-sm"
              :class="r.seq > (data.current ?? 0) ? 'btn-live' : 'btn-warn'"
            >{{ busySeq === r.seq ? "…" : r.seq > (data.current ?? 0) ? "Promote" : "Roll back" }}</button>
          </div>
          <div class="mt-2 flex flex-wrap gap-1.5">
            <span v-for="p in r.packages" :key="p" class="chip">{{ p }}</span>
            <span v-if="r.mac" class="chip chip-info">mac {{ r.mac }}</span>
            <span v-if="!r.packages.length && !r.mac" class="text-xs text-ink-faint">no payload</span>
          </div>

          <!-- Expanded manifest -->
          <div v-if="openSeq === r.seq" class="mt-3 rounded-[7px] border hairline bg-bg p-4 cursor-default" @click.stop>
            <div v-if="detailLoading === r.seq" class="text-ink-faint text-xs">Loading manifest…</div>
            <div v-else-if="openDetail" class="grid gap-4 md:grid-cols-2 text-xs">
              <div>
                <div class="lbl">Launch</div>
                <div class="font-mono">{{ openDetail.launch?.bin ?? "—" }}</div>
              </div>
              <div>
                <div class="lbl">Signature</div>
                <div class="font-mono">{{ openDetail.signature ? `${openDetail.signature.keyId} · ${openDetail.signature.alg}` : "unsigned" }}</div>
              </div>
              <div v-if="openDetail.packages.length" class="md:col-span-2">
                <div class="lbl mb-2">Packages</div>
                <table class="w-full">
                  <tbody>
                    <tr v-for="p in openDetail.packages" :key="p.name" class="border-t hairline first:border-0">
                      <td class="py-1.5 pr-4 font-mono whitespace-nowrap">{{ p.name }}@{{ p.version }}</td>
                      <td class="py-1.5 pr-4 text-ink-faint font-mono whitespace-nowrap">deps {{ p.deps.length ? p.deps.join(", ") : "—" }}</td>
                      <td class="py-1.5 text-ink-faint font-mono break-all">{{ p.manifest.key }}</td>
                    </tr>
                  </tbody>
                </table>
              </div>
              <div v-if="openDetail.macApp" class="md:col-span-2">
                <div class="lbl mb-1">macOS payload — whole app</div>
                <div class="font-mono">
                  {{ openDetail.macApp.bundleName }}@{{ openDetail.macApp.version }}
                  <span class="text-ink-faint">· launch {{ openDetail.macLaunch?.bin ?? "—" }}</span>
                </div>
                <div class="font-mono text-ink-faint break-all mt-1">{{ openDetail.macApp.artifact.key }}</div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </section>
    <section v-else class="panel p-8 text-center text-[13px] text-ink-faint mb-8">
      No app releases on this channel yet.
    </section>

    <!-- ── Launcher (bootstrapper) track: its own pointer, moved independently ── -->
    <div class="silk mb-3">Launcher</div>
    <section v-if="boots && boots.bootstrappers.length" class="panel py-2 px-3">
      <div v-for="b in boots.bootstrappers" :key="b.seq" class="rail-row border-b hairline last:border-0">
        <span class="rail-node" :class="b.current ? 'rail-node-live' : ''" />
        <div class="py-3 pr-2 flex items-center gap-3 flex-wrap">
          <span class="font-mono text-[13px] text-ink-faint w-14">seq {{ b.seq }}</span>
          <span class="font-mono text-[13px]" :class="b.current ? '' : 'text-ink-dim'">{{ b.version }}</span>
          <span v-if="b.win" class="chip">win</span>
          <span v-if="b.mac" class="chip">mac</span>
          <span v-if="b.current" class="flag-live">LIVE</span>
          <span class="ml-auto text-xs text-ink-faint" :title="exactTime(b.publishedAt)">{{ timeAgo(b.publishedAt) }}</span>
          <button
            v-if="!b.current"
            @click="moveBoot(b.seq)"
            :disabled="busyBoot === b.seq"
            class="btn btn-sm"
            :class="b.seq > (boots.current ?? 0) ? 'btn-live' : 'btn-warn'"
          >{{ busyBoot === b.seq ? "…" : b.seq > (boots.current ?? 0) ? "Promote" : "Roll back" }}</button>
        </div>
      </div>
    </section>
    <section v-else class="panel p-8 text-center text-[13px] text-ink-faint">
      No launcher published on this channel yet.
    </section>
  </template>
</template>
