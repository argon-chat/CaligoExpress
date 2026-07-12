<script setup lang="ts">
import { onMounted, ref } from "vue";
import { api } from "@/lib/api";
import { apps, defaultApp, loadApps, selectedApp } from "@/lib/app-scope";
import { errMsg } from "@/lib/ui";
import { confirm } from "@/composables/useConfirm";
import { toastErr, toastOk } from "@/composables/useToasts";
import type { AdminSettings, MirrorConfigInput, MirrorRedacted, MirrorRow } from "@/lib/types";
import MirrorEditor from "@/components/MirrorEditor.vue";

const loading = ref(true);
const savingSection = ref<string | null>(null);

const manifest = ref<MirrorRow[]>([]);
const content = ref<MirrorRow[]>([]);
const keep = ref(5);
const gcDryRun = ref(false);
const pinned = ref<{ id: string; value: string; stored: boolean }[]>([]);

// Per-app CI publish tokens (worker-global management; master/SSO only). Values are write-only.
const tokenDrafts = ref<Record<string, string>>({});
const tokenBusy = ref<string | null>(null);

function toRow(m: MirrorRedacted): MirrorRow {
  return {
    name: m.name, endpoint: m.endpoint, bucket: m.bucket, region: m.region,
    publicBaseUrl: m.publicBaseUrl ?? "", accessKeyId: "", secretAccessKey: "",
    hasSecret: m.hasSecret, accessKeyHint: m.accessKeyId,
  };
}

function toConfig(r: MirrorRow): MirrorConfigInput {
  return {
    name: r.name.trim(), endpoint: r.endpoint.trim(), bucket: r.bucket.trim(), region: r.region.trim() || "auto",
    publicBaseUrl: r.publicBaseUrl.trim() || undefined,
    accessKeyId: r.accessKeyId, secretAccessKey: r.secretAccessKey,
  };
}

async function load() {
  loading.value = true;
  try {
    const s = (await api.get<AdminSettings>("/admin/settings")).data;
    manifest.value = s.manifestMirrors.map(toRow);
    content.value = s.contentMirrors.map(toRow);
    keep.value = s.keepPerChannel;
    gcDryRun.value = s.gcDryRun ?? false;
    // Pinned keys are public — show their actual values so it's obvious they persisted.
    pinned.value = Object.entries(s.pinnedKeys ?? {}).map(([id, value]) => ({ id, value, stored: true }));
    await loadApps().catch(() => undefined);
  } catch (e) {
    toastErr(errMsg(e));
  } finally {
    loading.value = false;
  }
}

// Each section saves its own endpoint, so a failure in one never blocks (or falsely confirms) another.
async function save(section: "mirrors" | "retention" | "keys", fn: () => Promise<unknown>, okText: string) {
  savingSection.value = section;
  try {
    await fn();
    toastOk(okText);
    await load();
  } catch (e) {
    toastErr(errMsg(e));
  } finally {
    savingSection.value = null;
  }
}

const saveMirrors = () =>
  save("mirrors", () =>
    api.put("/admin/settings/mirrors", {
      manifest: manifest.value.filter((m) => m.name.trim()).map(toConfig),
      content: content.value.filter((m) => m.name.trim()).map(toConfig),
    }), "Mirrors saved");

const saveRetention = () =>
  save("retention", async () => {
    await api.put("/admin/settings/keep", { keepPerChannel: Number(keep.value) });
    await api.put("/admin/settings/gc", { dryRun: gcDryRun.value });
  }, "Retention & GC saved");

const savePinned = () =>
  save("keys", () => {
    const keys: Record<string, string> = {};
    for (const k of pinned.value) if (k.id.trim()) keys[k.id.trim()] = k.value.trim();
    return api.put("/admin/settings/pinned-keys", { keys });
  }, `Trusted keys saved for ${selectedApp.value}`);

async function saveToken(app: string, token: string) {
  const value = token.trim();
  if (!value) {
    const ok = await confirm({
      title: `Remove ${app}'s publish token`,
      body: "CI for this app stops being able to publish until a new token is set here.",
      action: "Remove token",
      tone: "bad",
      details: [`app  ${app}`],
    });
    if (!ok) return;
  }
  tokenBusy.value = app;
  try {
    await api.put(`/admin/apps/${app}/token`, { token: value });
    tokenDrafts.value[app] = "";
    await loadApps();
    toastOk(value ? `Token set for ${app}` : `Token removed for ${app}`);
  } catch (e) {
    toastErr(errMsg(e));
  } finally {
    tokenBusy.value = null;
  }
}

function addPinned() {
  pinned.value.push({ id: "", value: "", stored: false });
}
function removePinned(i: number) {
  pinned.value.splice(i, 1);
}

onMounted(load);
</script>

<template>
  <header class="mb-6">
    <div class="silk mb-1.5">Settings</div>
    <h1 class="text-xl font-semibold tracking-tight">Runtime configuration</h1>
    <p class="text-[13px] text-ink-dim mt-1">Stored in the worker's database; changes apply immediately.</p>
  </header>

  <div v-if="loading" class="text-ink-dim text-[13px]">Loading…</div>

  <div v-else class="grid gap-8">
    <!-- ════ Zone: this app only ════ -->
    <div>
      <div class="silk mb-3">This app — <span class="text-ink-dim normal-case tracking-normal">{{ selectedApp }}</span></div>
      <section class="panel p-6">
        <div class="flex items-center justify-between mb-1">
          <h2 class="font-semibold text-[15px]">Trusted signing keys</h2>
          <div class="flex gap-2">
            <button @click="addPinned" class="btn btn-sm">+ Add key</button>
            <button @click="savePinned" :disabled="savingSection === 'keys'" class="btn btn-sm">
              {{ savingSection === "keys" ? "Saving…" : "Save keys" }}
            </button>
          </div>
        </div>
        <p class="text-xs text-ink-faint mb-4">
          A publish to <span class="font-mono">{{ selectedApp }}</span> must be signed by one of these Ed25519 keys.
          Other apps have their own list — switch apps in the sidebar to edit theirs.
        </p>

        <div v-if="!pinned.length" class="text-[13px] text-ink-faint py-8 text-center border border-dashed hairline rounded-lg">
          No trusted keys — publishes are accepted unsigned. Add the key from <code class="font-mono">caligo init --keygen</code>.
        </div>

        <div v-for="(k, i) in pinned" :key="i" class="rounded-lg border hairline bg-bg p-4 mb-3">
          <div class="flex items-center justify-between mb-3">
            <span class="text-xs font-mono text-ink-dim">{{ k.id || "(new key)" }}</span>
            <button @click="removePinned(i)" class="text-xs text-ink-faint hover:text-bad">Remove</button>
          </div>
          <div class="grid gap-3 sm:grid-cols-[9rem_1fr]">
            <label><span class="lbl">Key ID</span><input v-model="k.id" class="inp font-mono" placeholder="k1" /></label>
            <label>
              <span class="lbl">Public key (base64)</span>
              <input v-model="k.value" class="inp font-mono" autocomplete="off" placeholder="base64 public key" />
            </label>
          </div>
        </div>
      </section>
    </div>

    <!-- ════ Zone: whole worker (every app) ════ -->
    <div>
      <div class="silk mb-3">Worker-wide — all apps</div>
      <div class="grid gap-4">
        <MirrorEditor
          v-model="manifest"
          accent="var(--color-info)"
          title="Manifest mirrors"
          help="S3 buckets behind the *-manifest hosts. The worker replicates every app's config.json and release pointers here."
        >
          <button @click="saveMirrors" :disabled="savingSection === 'mirrors'" class="btn btn-sm">
            {{ savingSection === "mirrors" ? "Saving…" : "Save mirrors" }}
          </button>
        </MirrorEditor>
        <MirrorEditor
          v-model="content"
          accent="var(--color-live)"
          title="Content mirrors"
          help="S3 buckets behind the *-updates hosts. Artifacts and chunks upload here; GC prunes stale ones per app namespace."
        >
          <button @click="saveMirrors" :disabled="savingSection === 'mirrors'" class="btn btn-sm">
            {{ savingSection === "mirrors" ? "Saving…" : "Save mirrors" }}
          </button>
        </MirrorEditor>

        <section class="panel p-6">
          <div class="flex items-center justify-between mb-1">
            <h2 class="font-semibold text-[15px]">Retention & GC</h2>
            <button @click="saveRetention" :disabled="savingSection === 'retention'" class="btn btn-sm">
              {{ savingSection === "retention" ? "Saving…" : "Save" }}
            </button>
          </div>
          <p class="text-xs text-ink-faint mb-4">Applies to every app and channel on this worker.</p>

          <div class="grid gap-5 sm:grid-cols-2">
            <label class="block">
              <span class="lbl">Releases kept per channel</span>
              <input v-model.number="keep" type="number" min="1" class="inp w-24" />
              <span class="block text-xs text-ink-faint mt-1.5">Older releases and their artifacts become GC-eligible; the live pointer is always kept.</span>
            </label>
            <div>
              <span class="lbl">GC dry-run mode</span>
              <label class="flex items-center gap-2.5 text-[13px] mt-1.5 cursor-pointer">
                <input v-model="gcDryRun" type="checkbox" class="accent-warn" />
                <span>Scheduled GC only reports, never deletes</span>
              </label>
              <span class="block text-xs text-ink-faint mt-1.5">The safety switch while rolling out storage-layout changes. Manual runs from the overview choose per run.</span>
            </div>
          </div>
        </section>

        <section class="panel p-6">
          <h2 class="font-semibold text-[15px] mb-1">App publish tokens</h2>
          <p class="text-xs text-ink-faint mb-4">
            Each app's CI publishes with its own token, valid only for that app's routes and storage namespace.
            Values are write-only: type a new value to rotate, apply an empty field to remove.
          </p>
          <div v-for="a in apps" :key="a.app" class="flex items-center gap-3 py-2.5 border-t hairline first:border-0 flex-wrap">
            <span class="font-mono text-[13px] w-40 truncate">{{ a.app }}<span v-if="a.isDefault" class="text-ink-faint"> · default</span></span>
            <span class="chip" :class="a.hasToken ? 'chip-live' : ''">{{ a.hasToken ? "token set" : "no token" }}</span>
            <input
              v-model="tokenDrafts[a.app]"
              class="inp font-mono flex-1 min-w-40 text-xs!"
              type="password"
              autocomplete="new-password"
              :placeholder="a.hasToken ? 'new value to rotate · empty to remove' : 'new token value'"
            />
            <button
              @click="saveToken(a.app, tokenDrafts[a.app] ?? '')"
              :disabled="tokenBusy === a.app"
              class="btn btn-sm"
            >{{ tokenBusy === a.app ? "Saving…" : "Apply" }}</button>
          </div>
          <p v-if="defaultApp" class="text-xs text-ink-faint mt-3">
            The master <code class="font-mono">PUBLISH_TOKEN</code> (worker secret) always works for every app and is managed with <code class="font-mono">wrangler secret</code>, not here.
          </p>
        </section>
      </div>
    </div>
  </div>
</template>
