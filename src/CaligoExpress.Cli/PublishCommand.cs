namespace CaligoExpress.Cli;

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CaligoExpress.Chunking;
using CaligoExpress.Crypto;
using CaligoExpress.Manifests;
using CaligoExpress.Publishing;

/// <summary>
/// CI-friendly "publish to the worker" primitive. The bootstrapper and the app are now INDEPENDENT tracks
/// with their own signed pointers, so one invocation publishes whichever it was given:
///   --boot-bin/--boot-version  → a bootstrapper.json (POST /publish-bootstrapper), and/or
///   --core-src/--core-version  → an app release.json with packages only (POST /publish).
/// The core package is content-defined-chunked (only chunks the mirrors lack are uploaded; the previous
/// build is never needed). Artifacts upload via presigned URLs so S3 creds stay in the worker.
///
///   caligo publish --worker URL --token TOK --channel beta --key k1.key --key-id k1 \
///       [--app my-app] \
///       [--core-src DIR --core-version V --launch-bin app/ArgonChat.exe --name core] \
///       [--boot-bin FILE --boot-version V [--boot-public-key KEY]] [--force]
/// </summary>
internal static class PublishCommand
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // Multi-app scope for this invocation, set once in RunAsync: null/Root = the worker's default app
    // (legacy root layout); an app slug moves every route under /apps/<app> and every key under apps/<app>/.
    private static string? AppId;
    private static CdnKeyScheme Keys = CdnKeyScheme.Root;

    public static async Task<int> RunAsync(Args a)
    {
        // Secrets come from env in CI (CALIGO_WORKER/CALIGO_PUBLISH_TOKEN/CALIGO_K1_KEY) so they never
        // appear in process arguments / build logs; flags override for local use.
        var worker = (a.Get("worker") ?? Env("CALIGO_WORKER")
            ?? throw new ArgumentException("worker required (--worker or CALIGO_WORKER)")).TrimEnd('/');
        var token = a.Get("token") ?? Env("CALIGO_PUBLISH_TOKEN")
            ?? throw new ArgumentException("token required (--token or CALIGO_PUBLISH_TOKEN)");
        var priv = a.Get("key-b64") is { } kb ? Convert.FromBase64String(kb.Trim())
            : Env("CALIGO_K1_KEY") is { } ek ? Convert.FromBase64String(ek.Trim())
            : Convert.FromBase64String(File.ReadAllText(a.Req("key")).Trim());

        AppId = a.Get("app") ?? Env("CALIGO_APP_ID");
        Keys = CdnKeyScheme.ForApp(AppId);
        if (AppId is not null)
            worker = $"{worker}/apps/{AppId}"; // every worker route re-exists under /apps/<app>

        var channel = a.Req("channel");
        var keyId = a.Get("key-id", "k1");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        // A separate client with NO default auth header for the S3 uploads: a presigned URL authenticates
        // via query params, and sending an Authorization header alongside makes S3 reject it (400).
        using var s3Http = new HttpClient();

        Log($"worker {worker}  channel {channel}  key-id {keyId}" + (AppId is null ? "" : $"  app {AppId}"));

        var ok = true;
        if (a.Get("boot-bin") is { } bootBin)
            ok &= await PublishBootstrapperAsync(http, s3Http, worker, channel, keyId, priv, a, bootBin, mac: false);
        if (a.Get("mac-boot-bin") is { } macBootBin)
            ok &= await PublishBootstrapperAsync(http, s3Http, worker, channel, keyId, priv, a, macBootBin, mac: true);
        if (a.Get("core-src") is { } coreSrc)
            ok &= await PublishCoreAsync(http, s3Http, worker, channel, keyId, priv, a, coreSrc);
        if (a.Get("mac-app") is { } macZip)
            ok &= await PublishMacAppAsync(http, s3Http, worker, channel, keyId, priv, a, macZip);
        if (a.Get("linux-app") is { } appImage)
            ok &= await PublishLinuxAppAsync(http, s3Http, worker, channel, keyId, priv, a, appImage);
        // Generic public download: upload an arbitrary file to a mirror-root key (e.g. the human-facing
        // first-install ArgonChat.dmg / ArgonChat_Setup.exe). Root keys aren't GC-prunable, so they persist.
        if (a.Get("upload-file") is { } upFile && a.Get("upload-key") is { } upKey)
            ok &= await PublishPublicFileAsync(http, s3Http, worker, upFile, upKey);

        if (a.Get("boot-bin") is null && a.Get("mac-boot-bin") is null && a.Get("core-src") is null
            && a.Get("mac-app") is null && a.Get("linux-app") is null && a.Get("upload-file") is null)
        {
            Log("nothing to publish — pass --boot-bin, --mac-boot-bin, --core-src, --mac-app, --linux-app, and/or --upload-file");
            return 1;
        }
        return ok ? 0 : 1;
    }

    // ── bootstrapper track (per-OS binaries on one signed pointer) ────────────────
    // Windows (.exe) and macOS (.app ditto-zip) are different binaries, so they live as `full` and
    // `macFull` side by side. Each publish sets ITS field and PRESERVES the other (fetched from the current
    // bootstrapper.json) — otherwise a mac publish would clobber the Windows binary and a Windows client
    // would download the mac bundle (and vice versa).
    private static async Task<bool> PublishBootstrapperAsync(
        HttpClient http, HttpClient s3, string worker, string channel, string keyId, byte[] priv, Args a, string bootBin, bool mac)
    {
        var version = a.Req("boot-version");
        var bytes = await File.ReadAllBytesAsync(bootBin);
        var key = mac ? Keys.BootstrapperMac(version) : Keys.Bootstrapper(version);
        var seq = await NextSeqAsync(http, $"{worker}/admin/bootstrappers/{channel}", "bootstrappers");

        var current = await FetchCurrentBootstrapperAsync(http, worker, channel);
        var newArtifact = Artifact(key, bytes);

        ArtifactRef full;
        ArtifactRef? macFull;
        if (mac)
        {
            macFull = newArtifact;
            full = current?.Full
                ?? throw new InvalidOperationException(
                    "publish the Windows bootstrapper (--boot-bin) on this channel before the macOS one — bootstrapper.json needs a `full`");
        }
        else
        {
            full = newArtifact;
            macFull = current?.MacFull; // keep the mac binary if one is already published
        }

        Log($"bootstrapper [{(mac ? "mac" : "win")}]: {version} ({Size(bytes.Length)}) <- {bootBin}, seq {seq}"
            + (mac ? "  (preserving existing windows `full`)" : current?.MacFull is not null ? "  (preserving existing `macFull`)" : ""));

        var docs = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [key] = bytes };
        if (a.Get("boot-public-key") is { } publicKey)
        {
            docs[publicKey] = bytes;
            Log($"  + public download key: {publicKey}");
        }

        var boot = new BootstrapperManifest(
            1, channel, seq, version, DateTimeOffset.UtcNow.ToString("o"), full, Signature: null, MacFull: macFull,
            App: AppId);
        var signed = ManifestTrust.Sign(boot, keyId, priv);

        Log($"uploading bootstrapper binary ...");
        await PresignUploadAsync(http, s3, worker, docs, skipExisting: false, logEachKey: true);

        var json = CaligoJson.Serialize(signed);
        var body = a.Flag("force") ? $"{{\"bootstrapper\":{json},\"force\":true}}" : json;
        return await PostAsync(http, $"{worker}/publish-bootstrapper", body, $"bootstrapper {version} (seq {seq})");
    }

    /// <summary>Reads the channel's current bootstrapper.json (full manifest) so a per-OS publish can preserve
    /// the other platform's binary. Returns null if the channel has no bootstrapper yet.</summary>
    private static async Task<BootstrapperManifest?> FetchCurrentBootstrapperAsync(HttpClient http, string worker, string channel)
    {
        var resp = await http.GetAsync($"{worker}/{channel}/bootstrapper.json");
        if (!resp.IsSuccessStatusCode)
            return null;
        return CaligoJson.Deserialize<BootstrapperManifest>(await resp.Content.ReadAsStringAsync());
    }

    // ── app release track ────────────────────────────────────────────────────────
    private static async Task<bool> PublishCoreAsync(
        HttpClient http, HttpClient s3, string worker, string channel, string keyId, byte[] priv, Args a, string coreSrc)
    {
        var name = a.Get("name", "core");
        var targetRoot = a.Get("target-root", "app");
        var version = a.Req("core-version");

        var current = await FetchCurrentReleaseAsync(http, worker, channel);
        var seq = await NextSeqAsync(http, $"{worker}/admin/releases/{channel}", "releases");
        Log(current is null
            ? $"app release: first on this channel, seq {seq}"
            : $"app release: replacing {name}, reuse {current.Packages.Count} packages, seq {seq}");

        Log($"core: chunking {name} {version} from {coreSrc} ...");
        var packed = Packager.Pack(coreSrc, name, version, targetRoot);
        var chunkBytes = packed.Chunks.Values.Sum(b => (long)b.Length);
        Log($"  {packed.Manifest.Files.Count} files, {packed.Chunks.Count} unique chunks ({Size(chunkBytes)})");

        var manifestKey = Keys.PackageManifest(name, version);
        var manifestBytes = Encoding.UTF8.GetBytes(CaligoJson.Serialize(packed.Manifest));
        var docs = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [manifestKey] = manifestBytes };
        var chunks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (hash, b) in packed.Chunks)
            chunks[Keys.Chunk(hash)] = b;

        var coreRef = new PackageRef(name, version, true, [], Artifact(manifestKey, manifestBytes));
        var packages = current?.Packages.ToList() ?? [];
        packages.RemoveAll(p => p.Name == name);
        packages.Add(coreRef);

        var launch = a.Get("launch-bin") is { } lb ? new LaunchSpec(lb, []) : current?.Launch;
        if (launch is null)
            Log("WARNING: release has no launch spec — set --launch-bin or have one on the current release");

        // Backward-compat shim: embed the channel's current bootstrapper so pre-decoupling clients (which
        // require a `bootstrapper` field in release.json) keep working and migrate to the new launcher.
        // Prefer the standalone bootstrapper.json; if it doesn't exist yet (e.g. core publishes before the
        // installer workflow on the very first migration), carry the previous release's bootstrapper forward
        // so the field is never empty — order between the two CI workflows then doesn't matter.
        var bootShim = await FetchBootstrapperShimAsync(http, worker, channel) ?? current?.Bootstrapper;
        if (bootShim is not null)
            Log($"  + legacy bootstrapper shim -> {bootShim.Version}");
        else
            Log("  (no bootstrapper shim — fresh channel with no prior release; fine if no old clients)");

        var releaseId = $"{channel}-{version}-{seq}";
        // Preserve any macOS whole-app pointer already on the channel — Windows and mac publish to the same
        // release.json from separate CI jobs, so a core publish must not clobber the mac fields (and vice versa).
        var release = new ReleaseManifest(
            1, channel, seq, releaseId, DateTimeOffset.UtcNow.ToString("o"), packages, launch, bootShim,
            Signature: null, MacApp: current?.MacApp, MacLaunch: current?.MacLaunch, LinuxApp: current?.LinuxApp,
            App: AppId);
        var signed = ManifestTrust.Sign(release, keyId, priv);
        Log($"release {releaseId}: packages [{string.Join(", ", packages.Select(p => $"{p.Name}@{p.Version}"))}]");

        if (chunks.Count > 0)
        {
            Log("uploading chunks (skipping ones already on mirrors) ...");
            await UploadChunksAsync(http, s3, worker, chunks);
        }
        Log($"uploading {docs.Count} document(s) (package manifest) ...");
        await PresignUploadAsync(http, s3, worker, docs, skipExisting: false, logEachKey: true);

        var json = CaligoJson.Serialize(signed);
        var body = a.Flag("force") ? $"{{\"release\":{json},\"force\":true}}" : json;
        return await PostAsync(http, $"{worker}/publish", body, $"{releaseId} live on {channel}");
    }

    // ── macOS whole-app track ────────────────────────────────────────────────────
    // Publishes a notarized .app bundle (delivered as one ditto zip) as the channel's macOS payload. Unlike
    // core, this is NOT chunked — a signed/notarized bundle must arrive byte-identical, so it ships whole and
    // the client atomically swaps it (see MonolithInstaller). Windows packages on the same release are
    // preserved so the two platforms can share one signed release.json published from separate CI jobs.
    //   caligo publish --worker URL --token TOK --channel live --key k1.key --key-id k1 \
    //       --mac-app ArgonChat.zip --mac-version V [--mac-bundle-name ArgonChat.app] [--mac-launch-bin app/ArgonChat.app] [--force]
    private static async Task<bool> PublishMacAppAsync(
        HttpClient http, HttpClient s3, string worker, string channel, string keyId, byte[] priv, Args a, string macZip)
    {
        var version = a.Req("mac-version");
        var bundleName = a.Get("mac-bundle-name", "ArgonChat.app");
        var launchBin = a.Get("mac-launch-bin", "app/ArgonChat.app");

        var bytes = await File.ReadAllBytesAsync(macZip);
        var key = Keys.MacApp(version);

        var current = await FetchCurrentReleaseAsync(http, worker, channel);
        var seq = await NextSeqAsync(http, $"{worker}/admin/releases/{channel}", "releases");
        Log($"mac app: {version} ({Size(bytes.Length)}) <- {macZip}, bundle {bundleName}, seq {seq}");

        var macApp = new MonolithRef(Artifact(key, bytes), bundleName, version);
        var macLaunch = new LaunchSpec(launchBin, []);

        // Preserve the channel's existing Windows packages/launch/bootstrapper shim so a mac publish does not
        // drop them (mirrors how a core publish preserves the mac fields).
        var packages = current?.Packages.ToList() ?? [];
        var bootShim = await FetchBootstrapperShimAsync(http, worker, channel) ?? current?.Bootstrapper;

        var releaseId = $"{channel}-mac-{version}-{seq}";
        var release = new ReleaseManifest(
            1, channel, seq, releaseId, DateTimeOffset.UtcNow.ToString("o"), packages, current?.Launch, bootShim,
            Signature: null, MacApp: macApp, MacLaunch: macLaunch, LinuxApp: current?.LinuxApp, App: AppId);
        var signed = ManifestTrust.Sign(release, keyId, priv);

        Log("uploading mac app bundle artifact ...");
        var docs = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [key] = bytes };
        await PresignUploadAsync(http, s3, worker, docs, skipExisting: false, logEachKey: true);

        var json = CaligoJson.Serialize(signed);
        var body = a.Flag("force") ? $"{{\"release\":{json},\"force\":true}}" : json;
        return await PostAsync(http, $"{worker}/publish", body, $"{releaseId} live on {channel} (mac)");
    }

    // ── Linux whole-app track ────────────────────────────────────────────────────
    // Publishes an AppImage as the channel's Linux payload. Like the mac bundle it ships whole: the running
    // AppImage downloads it, checks its hash and renames it over itself (there is no Linux bootstrapper).
    // The Windows packages and the mac fields on the release are preserved.
    //   caligo publish --worker URL --token TOK --channel live --key k1.key --key-id k1 \
    //       --linux-app App-x86_64.AppImage --linux-version V [--force]
    private static async Task<bool> PublishLinuxAppAsync(
        HttpClient http, HttpClient s3, string worker, string channel, string keyId, byte[] priv, Args a, string appImage)
    {
        var version = a.Req("linux-version");
        var bytes = await File.ReadAllBytesAsync(appImage);
        var key = Keys.LinuxApp(version);

        var current = await FetchCurrentReleaseAsync(http, worker, channel);
        var seq = await NextSeqAsync(http, $"{worker}/admin/releases/{channel}", "releases");
        Log($"linux app: {version} ({Size(bytes.Length)}) <- {appImage}, seq {seq}");

        var linuxApp = new MonolithRef(Artifact(key, bytes), Path.GetFileName(appImage), version);
        var packages = current?.Packages.ToList() ?? [];
        var bootShim = await FetchBootstrapperShimAsync(http, worker, channel) ?? current?.Bootstrapper;

        var releaseId = $"{channel}-linux-{version}-{seq}";
        var release = new ReleaseManifest(
            1, channel, seq, releaseId, DateTimeOffset.UtcNow.ToString("o"), packages, current?.Launch, bootShim,
            Signature: null, MacApp: current?.MacApp, MacLaunch: current?.MacLaunch, LinuxApp: linuxApp, App: AppId);
        var signed = ManifestTrust.Sign(release, keyId, priv);

        Log("uploading linux AppImage artifact ...");
        var docs = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [key] = bytes };
        await PresignUploadAsync(http, s3, worker, docs, skipExisting: false, logEachKey: true);

        var json = CaligoJson.Serialize(signed);
        var body = a.Flag("force") ? $"{{\"release\":{json},\"force\":true}}" : json;
        return await PostAsync(http, $"{worker}/publish", body, $"{releaseId} live on {channel} (linux)");
    }

    // ── generic public-file upload ────────────────────────────────────────────────
    // Uploads any file to a mirror-root key for human-facing first-install downloads (ArgonChat.dmg,
    // ArgonChat_Setup.exe). Not referenced by any manifest, but root keys are excluded from GC pruning so
    // the public download stays put.
    private static async Task<bool> PublishPublicFileAsync(HttpClient http, HttpClient s3, string worker, string file, string key)
    {
        var bytes = await File.ReadAllBytesAsync(file);
        Log($"public upload: {key} ({Size(bytes.Length)}) <- {file}");
        var docs = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [key] = bytes };
        await PresignUploadAsync(http, s3, worker, docs, skipExisting: false, logEachKey: true);
        return true;
    }

    private static async Task<bool> PostAsync(HttpClient http, string url, string body, string okMsg)
    {
        var resp = await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
        var respText = await resp.Content.ReadAsStringAsync();
        Log($"POST {url} -> {(int)resp.StatusCode} {resp.ReasonPhrase}");
        Log($"  {respText}");
        if (resp.IsSuccessStatusCode)
            Log($"DONE: {okMsg}");
        return resp.IsSuccessStatusCode;
    }

    private static void Log(string message) => Console.WriteLine($"[caligo publish] {message}");

    private static string Size(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / 1024.0 / 1024.0:0.0} MB" : $"{bytes / 1024.0:0.0} KB";

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static ArtifactRef Artifact(string key, byte[] bytes) => new(key, Sha256Hasher.HashHex(bytes), bytes.Length);

    /// <summary>Reads the channel's current bootstrapper.json to embed as a legacy compat shim in release.json.</summary>
    private static async Task<LegacyBootstrapperRef?> FetchBootstrapperShimAsync(HttpClient http, string worker, string channel)
    {
        var resp = await http.GetAsync($"{worker}/{channel}/bootstrapper.json");
        if (!resp.IsSuccessStatusCode)
            return null;
        var boot = CaligoJson.Deserialize<BootstrapperManifest>(await resp.Content.ReadAsStringAsync());
        return new LegacyBootstrapperRef(boot.Version, boot.Full);
    }

    private static async Task<ReleaseManifest?> FetchCurrentReleaseAsync(HttpClient http, string worker, string channel)
    {
        var resp = await http.GetAsync($"{worker}/{channel}/release.json");
        if (!resp.IsSuccessStatusCode)
        {
            Log($"GET /{channel}/release.json -> {(int)resp.StatusCode} (treating as no current release)");
            return null;
        }
        return CaligoJson.Deserialize<ReleaseManifest>(await resp.Content.ReadAsStringAsync());
    }

    /// <summary>Next monotonic seq for a track = current max + 1 (the listing returns seqs descending).</summary>
    private static async Task<long> NextSeqAsync(HttpClient http, string url, string field)
    {
        var resp = await http.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
            return 1;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty(field, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return 1;
        long max = 0;
        foreach (var item in arr.EnumerateArray())
            if (item.TryGetProperty("seq", out var s) && s.TryGetInt64(out var v) && v > max)
                max = v;
        return max + 1;
    }

    /// <summary>Presigns the chunk keys (skipping ones already on every mirror) and PUTs the missing ones
    /// concurrently (≈ half the cores), logging throttled progress.</summary>
    private static async Task UploadChunksAsync(HttpClient worker, HttpClient s3, string workerUrl, Dictionary<string, byte[]> chunks)
    {
        var req = new { kind = "content", keys = chunks.Keys.ToArray(), skipExisting = true };
        var presignResp = await worker.PostAsJsonAsync($"{workerUrl}/admin/presign", req, Web);
        if (!presignResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"presign -> {(int)presignResp.StatusCode}\n{await presignResp.Content.ReadAsStringAsync()}");
        var presign = await presignResp.Content.ReadFromJsonAsync<PresignResp>(Web)
                      ?? throw new InvalidOperationException("empty presign response");

        var toUpload = presign.Items.Where(i => i.Targets.Length > 0).ToList();
        var totalRaw = toUpload.Sum(i => (long)chunks[i.Key].Length);
        if (toUpload.Count == 0)
        {
            Log($"  chunks: all {chunks.Count} already present on mirrors");
            return;
        }
        Log($"  chunks: {toUpload.Count} to upload ({Size(totalRaw)} raw), {chunks.Count - toUpload.Count} already present");

        var parallelism = Math.Max(4, Environment.ProcessorCount / 2);
        using var gate = new SemaphoreSlim(parallelism);
        var sw = Stopwatch.StartNew();
        var logLock = new object();
        long sentBytes = 0;
        long doneRaw = 0;
        var doneCount = 0;
        var step = Math.Max(1, toUpload.Count / 20);

        var tasks = toUpload.Select(async item =>
        {
            await gate.WaitAsync();
            try
            {
                var raw = chunks[item.Key];
                var stored = ChunkCodec.Encode(raw);
                foreach (var target in item.Targets)
                {
                    var put = await s3.PutAsync(target.Url, new ByteArrayContent(stored));
                    if (!put.IsSuccessStatusCode)
                    {
                        var errBody = await put.Content.ReadAsStringAsync();
                        throw new InvalidOperationException($"upload {item.Key} -> {target.Mirror} failed: {(int)put.StatusCode}\n{errBody}");
                    }
                }

                var c = Interlocked.Increment(ref doneCount);
                var sent = Interlocked.Add(ref sentBytes, stored.Length);
                var rawDone = Interlocked.Add(ref doneRaw, raw.Length);
                if (c % step == 0 || c == toUpload.Count)
                {
                    var pct = totalRaw > 0 ? rawDone * 100.0 / totalRaw : 100;
                    var mbps = sent / 1024.0 / 1024.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    lock (logLock)
                        Log($"  chunks {c}/{toUpload.Count}  {pct:0}%  {Size(sent)} sent  {mbps:0.0} MB/s");
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        var saved = totalRaw > 0 ? (1 - (double)sentBytes / totalRaw) * 100 : 0;
        Log($"  chunks: uploaded {toUpload.Count}: {Size(totalRaw)} raw -> {Size(sentBytes)} sent ({saved:0}% smaller) in {sw.Elapsed.TotalSeconds:0.0}s");
    }

    private static async Task PresignUploadAsync(
        HttpClient worker, HttpClient s3, string workerUrl, Dictionary<string, byte[]> uploads,
        bool skipExisting, bool logEachKey = false)
    {
        var req = new { kind = "content", keys = uploads.Keys.ToArray(), skipExisting };
        var presignResp = await worker.PostAsJsonAsync($"{workerUrl}/admin/presign", req, Web);
        if (!presignResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"presign -> {(int)presignResp.StatusCode}\n{await presignResp.Content.ReadAsStringAsync()}");
        var presign = await presignResp.Content.ReadFromJsonAsync<PresignResp>(Web)
                      ?? throw new InvalidOperationException("empty presign response");

        foreach (var item in presign.Items)
        {
            if (item.Targets.Length == 0)
                continue;
            var bytes = uploads[item.Key];
            foreach (var target in item.Targets)
            {
                var put = await s3.PutAsync(target.Url, new ByteArrayContent(bytes));
                if (!put.IsSuccessStatusCode)
                {
                    var errBody = await put.Content.ReadAsStringAsync();
                    throw new InvalidOperationException($"upload {item.Key} -> {target.Mirror} failed: {(int)put.StatusCode}\n{errBody}");
                }
            }
            if (logEachKey)
                Log($"  uploaded {item.Key} ({Size(bytes.Length)}) -> {string.Join(", ", item.Targets.Select(t => t.Mirror))}");
        }
    }

    private sealed record PresignResp(PresignItem[] Items);
    private sealed record PresignItem(string Key, PresignTarget[] Targets);
    private sealed record PresignTarget(string Mirror, string Url);
}
