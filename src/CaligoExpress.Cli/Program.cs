using CaligoExpress.Content;
using CaligoExpress.Crypto;
using CaligoExpress.Engine;
using CaligoExpress.Manifests;
using CaligoExpress.Publishing;
using CaligoExpress.State;
using CaligoExpress.Transaction;
using CaligoExpress.Cli;
using DeltaQ.SuffixSorting.SAIS;

if (args.Length == 0)
{
    Cli.PrintUsage();
    return 1;
}

try
{
    var rest = new Args(args);
    switch (args[0])
    {
        case "init": return await Commands.InitAsync(rest);
        case "keygen": return Commands.Keygen(rest);
        case "pack": return Commands.Pack(rest);
        case "diffsize": return Commands.DiffSize(rest);
        case "release": return Commands.Release(rest);
        case "verify": return await Commands.VerifyAsync(rest);
        case "simulate": return await Commands.SimulateAsync(rest);
        case "publish": return await PublishCommand.RunAsync(rest);
        case "canon": return Commands.Canon(rest);
        case "-h" or "--help" or "help": Cli.PrintUsage(); return 0;
        default:
            Console.Error.WriteLine($"unknown command '{args[0]}'");
            Cli.PrintUsage();
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

namespace CaligoExpress.Cli
{
    /// <summary>Minimal, dependency-free flag parser: collects repeated <c>--key value</c> pairs.</summary>
    internal sealed class Args
    {
        private readonly Dictionary<string, List<string>> _map = new(StringComparer.Ordinal);
        private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

        public Args(string[] argv)
        {
            for (var i = 1; i < argv.Length; i++)
            {
                if (!argv[i].StartsWith("--"))
                    continue;
                var key = argv[i][2..];
                if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--"))
                {
                    (_map.TryGetValue(key, out var list) ? list : _map[key] = []).Add(argv[++i]);
                }
                else
                {
                    _flags.Add(key);
                }
            }
        }

        public string? Get(string key) => _map.TryGetValue(key, out var v) ? v[^1] : null;
        public string Get(string key, string fallback) => Get(key) ?? fallback;
        public string Req(string key) => Get(key) ?? throw new ArgumentException($"--{key} is required");
        public IReadOnlyList<string> All(string key) => _map.TryGetValue(key, out var v) ? v : [];
        public bool Flag(string key) => _flags.Contains(key);
    }

    internal static class Cli
    {
        public static void PrintUsage() => Console.WriteLine(
            """
            caligo — update package publisher / simulator

              init     --config caligo.json [--keygen] [--keys-dir keys]
                       [--installer-config <path>] [--config-out <cdn>/config.json]
                       [--worker-url <url> --publish-token <tok>]
              keygen   --out <dir> [--id k1]
              pack     --src <dir> --name <n> --version <v> [--target-root app] --cdn <dir>
              diffsize --old <dir> --new <dir>   (measures binary-delta size per changed file)
              release  --cdn <dir> --channel <c> --seq <n> --id <id> --key <privFile> --key-id <kid>
                       --boot-version <v> --package name=version [--package ...] [--deps name=dep1,dep2 ...]
                       [--launch-bin app/Argon.exe] [--launch-arg <a> ...]
              verify   --cdn <dir> --channel <c> --pub <pubFile> --key-id <kid>
              simulate --cdn <dir> --channel <c> --install <dir> --pub <pubFile> --key-id <kid> [--allow-rollback]
              publish  --worker <url> --token <tok> --channel <c> --key <privFile> --key-id <kid>
                       [--app <slug>]  (multi-app worker: routes /apps/<slug>, keys apps/<slug>/)
                       [--core-src <dir> --core-version <v>]
                       [--boot-bin <file> --boot-version <v>] [--launch-bin app/ArgonChat.exe] [--force]
            """);
    }

    internal static class Commands
    {
        // One command to configure everything: scaffold/load caligo.json, (optionally) generate the
        // signing key, then emit the signed config.json and the installer's embedded runtime config from it.
        public static async Task<int> InitAsync(Args a)
        {
            var configPath = a.Get("config", "caligo.json");

            CaligoProjectConfig project;
            if (File.Exists(configPath))
            {
                project = CaligoJson.Deserialize<CaligoProjectConfig>(File.ReadAllText(configPath));
            }
            else
            {
                project = Scaffold();
                File.WriteAllText(configPath, CaligoJson.SerializePretty(project));
                Console.WriteLine($"scaffolded {configPath} — fill in mirrors/seedHosts/launch, then re-run init");
            }

            var keysDir = a.Get("keys-dir", "keys");
            var keyFile = a.Get("key", Path.Combine(keysDir, $"{project.KeyId}.key"));
            var forceNewKey = a.Flag("keygen");

            // From-scratch setup: ensure a signing key exists. Reuse the private key on disk if present
            // (just re-derive the public key); otherwise generate one. --keygen forces a fresh key (rotation).
            if (forceNewKey || string.IsNullOrEmpty(project.PublicKey))
            {
                Directory.CreateDirectory(keysDir);
                byte[] pub;
                if (!forceNewKey && File.Exists(keyFile))
                {
                    var priv = Convert.FromBase64String(File.ReadAllText(keyFile).Trim());
                    pub = Ed25519.PublicKeyFromPrivate(priv);
                    Console.WriteLine($"using existing signing key '{project.KeyId}' from {keysDir}");
                }
                else
                {
                    var (priv, generated) = Ed25519.GenerateKeyPair();
                    File.WriteAllText(keyFile, Convert.ToBase64String(priv));
                    pub = generated;
                    Console.WriteLine($"generated signing key '{project.KeyId}' in {keysDir} (keep {project.KeyId}.key secret)");
                }

                File.WriteAllText(Path.Combine(keysDir, $"{project.KeyId}.pub"), Convert.ToBase64String(pub));
                project.PublicKey = Convert.ToBase64String(pub);
                File.WriteAllText(configPath, CaligoJson.SerializePretty(project));
            }

            if (a.Get("installer-config") is { } installerOut)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(installerOut))!);
                File.WriteAllText(installerOut, CaligoJson.SerializePretty(ConfigBuilder.BuildInstallerRuntime(project)));
                Console.WriteLine($"wrote installer runtime config -> {installerOut}");
            }

            string? signedConfigJson = null;
            if (a.Get("config-out") is { } configOut)
            {
                var priv = Convert.FromBase64String(File.ReadAllText(keyFile).Trim());
                var signed = ManifestTrust.Sign(ConfigBuilder.BuildConfig(project), project.KeyId, priv);
                signedConfigJson = CaligoJson.Serialize(signed);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(configOut))!);
                File.WriteAllText(configOut, signedConfigJson);
                Console.WriteLine($"wrote signed config.json -> {configOut}");
            }

            if (a.Get("worker-url") is { } workerUrl)
            {
                signedConfigJson ??= CaligoJson.Serialize(
                    ManifestTrust.Sign(ConfigBuilder.BuildConfig(project), project.KeyId,
                        Convert.FromBase64String(File.ReadAllText(keyFile).Trim())));
                using var http = new HttpClient();
                // On a multi-app worker, a non-default app's control plane lives under /apps/<appId>.
                var workerBase = workerUrl.TrimEnd('/')
                    + (string.IsNullOrEmpty(project.AppId) ? "" : $"/apps/{project.AppId}");
                var req = new HttpRequestMessage(HttpMethod.Post, $"{workerBase}/config")
                {
                    Content = new StringContent(signedConfigJson, System.Text.Encoding.UTF8, "application/json"),
                };
                if (a.Get("publish-token") is { } token)
                    req.Headers.Add("Authorization", $"Bearer {token}");
                var resp = await http.SendAsync(req);
                var respBody = await resp.Content.ReadAsStringAsync();
                Console.WriteLine($"pushed config to worker -> {(int)resp.StatusCode} {resp.ReasonPhrase}");
                if (!string.IsNullOrWhiteSpace(respBody))
                    Console.WriteLine($"  {respBody}");
            }

            Console.WriteLine("init done.");
            return 0;

            static CaligoProjectConfig Scaffold() => new()
            {
                AppName = "My App",
                BootstrapperFileName = "MyAppSetup.exe",
                DefaultChannel = "live",
                SeedHosts = ["manifest.example.com"],
                MinBootstrapper = "1.0.0",
                Mirrors = [new MirrorEntry("updates.example.com", ["*"], 0)],
                Launch = new LaunchSpec("app/MyApp.exe", []),
                WorkerUrl = "https://caligo-worker.example.com",
            };
        }

        public static int Keygen(Args a)
        {
            var outDir = a.Req("out");
            var id = a.Get("id", "k1");
            Directory.CreateDirectory(outDir);

            var (priv, pub) = Ed25519.GenerateKeyPair();
            File.WriteAllText(Path.Combine(outDir, $"{id}.key"), Convert.ToBase64String(priv));
            File.WriteAllText(Path.Combine(outDir, $"{id}.pub"), Convert.ToBase64String(pub));

            Console.WriteLine($"key pair '{id}' written to {outDir}");
            Console.WriteLine($"  public key: {Convert.ToBase64String(pub)}");
            return 0;
        }

        public static int Pack(Args a)
        {
            var packed = Packager.Pack(a.Req("src"), a.Req("name"), a.Req("version"), a.Get("target-root", "app"));
            var manifest = new CdnPublisher(a.Req("cdn")).PublishPackage(packed);
            var chunkBytes = packed.Chunks.Values.Sum(b => (long)b.Length);
            Console.WriteLine($"packed {packed.Manifest.Name} {packed.Manifest.Version}: {packed.Manifest.Files.Count} files, {packed.Chunks.Count} unique chunks ({chunkBytes} bytes)");
            Console.WriteLine($"  manifest {manifest.Key}");
            return 0;
        }

        // Diagnostic: for every file that changed between two build dirs, compute the binary delta
        // (bsdiff old->new) and report it against the full size — i.e. exactly how much an update would
        // transfer WITH per-file deltas vs WITHOUT (whole changed chunks). This answers "would chunk/binary
        // deltas actually help our big binaries, or are they different end-to-end?".
        public static int DiffSize(Args a)
        {
            var oldDir = Path.GetFullPath(a.Req("old"));
            var newDir = Path.GetFullPath(a.Req("new"));
            var sorter = new SAIS();

            var oldFiles = Directory.EnumerateFiles(oldDir, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(oldDir, f).Replace('\\', '/'), f => f, StringComparer.Ordinal);

            long fullChanged = 0, deltaChanged = 0, unchangedBytes = 0;
            int changed = 0, added = 0, unchanged = 0;

            Console.WriteLine($"{"file",-52}{"new",11}{"delta",11}  ratio");
            foreach (var f in Directory.EnumerateFiles(newDir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(x => new FileInfo(x).Length))
            {
                var rel = Path.GetRelativePath(newDir, f).Replace('\\', '/');
                var newBytes = File.ReadAllBytes(f);

                if (!oldFiles.TryGetValue(rel, out var oldPath))
                {
                    fullChanged += newBytes.Length;
                    deltaChanged += newBytes.Length;
                    added++;
                    Console.WriteLine($"{Trunc(rel, 52),-52}{Size(newBytes.Length),11}{"(new)",11}");
                    continue;
                }

                var oldBytes = File.ReadAllBytes(oldPath);
                if (oldBytes.AsSpan().SequenceEqual(newBytes))
                {
                    unchangedBytes += newBytes.Length;
                    unchanged++;
                    continue; // byte-identical -> already on disk, 0 transfer
                }

                using var ms = new MemoryStream();
                DeltaQ.BsDiff.Diff.Create(oldBytes, newBytes, ms, sorter);
                fullChanged += newBytes.Length;
                deltaChanged += ms.Length;
                changed++;
                Console.WriteLine($"{Trunc(rel, 52),-52}{Size(newBytes.Length),11}{Size(ms.Length),11}  {(double)ms.Length / Math.Max(1, newBytes.Length):P0}");
            }

            Console.WriteLine();
            Console.WriteLine($"unchanged (skipped, already on disk): {unchanged} files, {Size(unchangedBytes)}");
            Console.WriteLine($"changed: {changed}   added: {added}");
            Console.WriteLine($"  transfer WITHOUT deltas (full changed+new): {Size(fullChanged)}");
            Console.WriteLine($"  transfer WITH binary deltas:                {Size(deltaChanged)}  ({(double)deltaChanged / Math.Max(1, fullChanged):P0} of full)");
            Console.WriteLine();
            Console.WriteLine(deltaChanged < fullChanged * 0.5
                ? "=> deltas would help a lot — worth building the chunk/file-delta layer."
                : "=> deltas barely help — the changed files differ throughout (rebuild is the lever, not deltas).");
            return 0;
        }

        private static string Size(long b) =>
            b >= 1 << 20 ? $"{b / 1024.0 / 1024.0:0.0} MB" : $"{b / 1024.0:0.0} KB";

        private static string Trunc(string s, int n) => s.Length <= n ? s : "…" + s[^(n - 1)..];

        public static int Release(Args a)
        {
            var cdn = a.Req("cdn");
            var index = new CdnIndex(cdn);
            var deps = ParseDeps(a.All("deps"));
            var channel = a.Req("channel");
            var seq = long.Parse(a.Req("seq"));
            var keyId = a.Req("key-id");
            var priv = Convert.FromBase64String(File.ReadAllText(a.Req("key")).Trim());
            var publisher = new CdnPublisher(cdn);

            var packages = new List<PackageRef>();
            foreach (var spec in a.All("package"))
            {
                var (name, version) = SplitPair(spec);
                packages.Add(new PackageRef(
                    name, version, true,
                    deps.GetValueOrDefault(name, []),
                    index.RefFor(CdnKeys.PackageManifest(name, version))));
            }

            var launch = a.Get("launch-bin") is { } bin
                ? new LaunchSpec(bin, a.All("launch-arg").ToList())
                : null;

            var release = new ReleaseManifest(
                1, channel, seq, a.Req("id"), DateTimeOffset.UtcNow.ToString("o"), packages, launch);
            publisher.PublishRelease(ManifestTrust.Sign(release, keyId, priv));
            Console.WriteLine($"published release {release.ReleaseId} (seq {seq}) on {channel}: {packages.Count} packages");

            // The bootstrapper is its own signed pointer now — publish it alongside if a version was given.
            if (a.Get("boot-version") is { } bootVersion)
            {
                var boot = new BootstrapperManifest(
                    1, channel, seq, bootVersion, DateTimeOffset.UtcNow.ToString("o"),
                    index.RefFor(CdnKeys.Bootstrapper(bootVersion)));
                publisher.PublishBootstrapperManifest(ManifestTrust.Sign(boot, keyId, priv));
                Console.WriteLine($"published bootstrapper {bootVersion} (seq {seq}) on {channel}");
            }
            return 0;
        }

        public static async Task<int> VerifyAsync(Args a)
        {
            var cdn = a.Req("cdn");
            var content = new LocalContentSource(cdn);
            var keys = LoadPub(a);

            var channel = a.Req("channel");
            var release = ManifestTrust.LoadVerifiedRelease(await content.GetBytesAsync(CdnKeys.Release(channel)), keys);
            Console.WriteLine($"signature OK: release {release.ReleaseId} seq {release.ReleaseSeq}");

            var index = new CdnIndex(cdn);
            var problems = 0;

            // The bootstrapper is a separate signed pointer; verify it if the channel has one.
            if (index.Exists(CdnKeys.BootstrapperManifest(channel)))
            {
                var boot = ManifestTrust.LoadVerifiedBootstrapper(await content.GetBytesAsync(CdnKeys.BootstrapperManifest(channel)), keys);
                problems += CheckArtifact(index, boot.Full);
                Console.WriteLine($"bootstrapper {boot.Version}: signature OK");
            }

            foreach (var p in release.Packages)
            {
                problems += CheckArtifact(index, p.Manifest);
                if (!index.Exists(p.Manifest.Key))
                    continue;

                // The package.json lists every chunk object; verify each is present on the CDN.
                var mf = CaligoJson.Deserialize<PackageManifest>(
                    System.Text.Encoding.UTF8.GetString(await content.GetBytesAsync(p.Manifest.Key)));
                var missing = mf.Chunks.Count(c => !index.Exists(CdnKeys.Chunk(c.Hash)));
                if (missing > 0)
                {
                    Console.Error.WriteLine($"  {p.Name}: {missing} missing chunk(s)");
                    problems += missing;
                }
                else
                {
                    Console.WriteLine($"  {p.Name} {p.Version}: {mf.Chunks.Count} chunks present");
                }
            }

            Console.WriteLine(problems == 0
                ? $"all artifacts verified for {release.Packages.Count} packages"
                : $"{problems} artifact problem(s) found");
            return problems == 0 ? 0 : 1;
        }

        public static async Task<int> SimulateAsync(Args a)
        {
            var content = new LocalContentSource(a.Req("cdn"));
            var paths = new InstallPaths(a.Req("install"));
            var state = new StateStore(paths);
            var engine = new UpdateEngine(paths, content, state, new RecoveryService(paths, state));

            var release = ManifestTrust.LoadVerifiedRelease(
                await content.GetBytesAsync(CdnKeys.Release(a.Req("channel"))), LoadPub(a));

            var result = await engine.UpdateAsync(release, new UpdateOptions { AllowRollback = a.Flag("allow-rollback") });

            Console.WriteLine($"outcome: {result.Outcome} ({result.FileOperations} file ops)");
            foreach (var p in result.Plan.Packages)
                Console.WriteLine($"  {p.Name,-16} {p.Action}");
            Console.WriteLine($"installed release: {state.Read().ReleaseId}");
            return 0;
        }

        // Debug: print the canonical signing bytes for a manifest file, to compare cross-language.
        public static int Canon(Args a)
        {
            var json = File.ReadAllText(a.Req("file"));
            var bytes = CaligoExpress.Crypto.JsonCanonicalizer.CanonicalizeForSigning(json);
            Console.Out.Write(System.Text.Encoding.UTF8.GetString(bytes));
            return 0;
        }

        private static int CheckArtifact(CdnIndex index, ArtifactRef expected)
        {
            if (!index.Exists(expected.Key))
            {
                Console.Error.WriteLine($"  MISSING {expected.Key}");
                return 1;
            }
            var actual = index.RefFor(expected.Key);
            if (!string.Equals(actual.Hash, expected.Hash, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"  HASH MISMATCH {expected.Key}");
                return 1;
            }
            return 0;
        }

        private static Dictionary<string, byte[]> LoadPub(Args a)
            => new(StringComparer.Ordinal) { [a.Req("key-id")] = Convert.FromBase64String(File.ReadAllText(a.Req("pub")).Trim()) };

        private static Dictionary<string, List<string>> ParseDeps(IReadOnlyList<string> specs)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var spec in specs)
            {
                var (name, csv) = SplitPair(spec);
                map[name] = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            return map;
        }

        private static (string, string) SplitPair(string spec)
        {
            var eq = spec.IndexOf('=');
            if (eq < 0)
                throw new ArgumentException($"expected name=value, got '{spec}'");
            return (spec[..eq], spec[(eq + 1)..]);
        }
    }
}
