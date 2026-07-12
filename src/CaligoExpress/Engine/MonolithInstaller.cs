namespace CaligoExpress.Engine;

using CaligoExpress.Abstractions;
using CaligoExpress.Fs;
using CaligoExpress.Manifests;
using CaligoExpress.State;
using CaligoExpress.Transaction;
using Serilog;

/// <summary>
/// The macOS whole-app ("monolith") update path, used instead of the chunked <see cref="UpdateEngine"/>.
/// A signed-and-notarized <c>.app</c> bundle cannot be reassembled from content-defined chunks without
/// breaking its code signature (symlinks, exec bits, and the stapled ticket would not survive), so the
/// client downloads the entire bundle as one content-addressed <c>ditto</c> zip, expands it with
/// <c>ditto</c> (preserving every byte), and atomically swaps it into the payload directory inside a
/// single journaled, crash-safe transaction — the same <see cref="TransactionRunner"/> and
/// <see cref="RecoveryService"/> that back the chunked path, via a directory-level
/// <see cref="TxnOpKind.ReplaceTree"/> op.
///
/// The caller hands in a release manifest whose signature is already verified against the pinned trust
/// anchors; the artifact's SHA-256 then chains that trust down to every byte (mirrors are untrusted).
/// </summary>
public sealed class MonolithInstaller
{
    /// <summary>The synthetic package slot under which the installed whole-app version is recorded in state.</summary>
    public const string MacAppPackage = "@macapp";

    private readonly InstallPaths _paths;
    private readonly IContentSource _content;
    private readonly StateStore _state;
    private readonly RecoveryService _recovery;
    private readonly IArchiveExtractor _extractor;
    private readonly ILogger _log;
    private readonly IClock _clock;

    public MonolithInstaller(
        InstallPaths paths,
        IContentSource content,
        StateStore state,
        RecoveryService recovery,
        IArchiveExtractor extractor,
        ILogger? log = null,
        IClock? clock = null)
    {
        _paths = paths;
        _content = content;
        _state = state;
        _recovery = recovery;
        _extractor = extractor;
        _log = log ?? Serilog.Core.Logger.None;
        _clock = clock ?? SystemClock.Instance;
    }

    public async Task<UpdateOutcome> UpdateAsync(
        ReleaseManifest target,
        UpdateOptions? options = null,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new UpdateOptions();

        _recovery.RecoverPending();
        var current = _state.Read();
        progress?.Report(new UpdateProgress(UpdateProgress.Plan));

        var mac = target.MacApp
            ?? throw new CaligoException($"release {target.ReleaseId} has no macApp artifact");
        var launch = target.MacLaunch
            ?? throw new CaligoException($"release {target.ReleaseId} has no macLaunch spec");

        // Release sequences are per-channel; switching channels is deliberate, not a downgrade.
        if (string.Equals(target.Channel, current.Channel, StringComparison.OrdinalIgnoreCase)
            && target.ReleaseSeq < current.ReleaseSeq && !options.AllowRollback)
            throw new CaligoException(
                $"downgrade blocked: target release {target.ReleaseSeq} is older than installed {current.ReleaseSeq} on channel {current.Channel}");

        var payloadAppDir = _paths.ResolveUnderRoot(launch.Bin);

        if (IsAlreadyInstalled(current, mac, payloadAppDir))
        {
            _log.Information("Mac app already at {Version} ({Hash}); nothing to do", mac.Version, Short(mac.Artifact.Hash));
            return UpdateOutcome.UpToDate;
        }

        _paths.EnsureControlDirs();
        var txnId = $"{_clock.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..28];
        var staging = new StagingArea(_paths, txnId);

        // 1) Download the whole-app artifact (hash-verified, mirror failover + resume handled by the source).
        var zipPath = Path.Combine(staging.StagedDir, "download.zip");
        _log.Information("Downloading mac app {Version} ({Bytes} bytes)", mac.Version, mac.Artifact.Size);
        progress?.Report(new UpdateProgress(UpdateProgress.Download, "app", 0, mac.Artifact.Size));
        await _content.DownloadToFileAsync(
            mac.Artifact.Key, zipPath, mac.Artifact.Hash,
            new DownloadProgressAdapter(progress, mac.Artifact.Size), ct);

        // 2) Expand with ditto so symlinks/exec-bits/stapled ticket survive (System.IO.Compression would not).
        progress?.Report(new UpdateProgress(UpdateProgress.Install, "app"));
        var extractDir = Path.Combine(staging.StagedDir, "extract");
        await _extractor.ExtractAsync(zipPath, extractDir, ct);

        var extractedBundle = Path.Combine(extractDir, mac.BundleName);
        if (!Directory.Exists(extractedBundle))
            throw new CaligoException(
                $"archive for release {target.ReleaseId} did not contain expected bundle '{mac.BundleName}'");

        // 3) Atomic, journaled swap of the live bundle (one ReplaceTree op → crash-safe via RecoveryService).
        var op = new TxnOp
        {
            Kind = TxnOpKind.ReplaceTree,
            TargetPath = payloadAppDir,
            StagedPath = extractedBundle,
            BackupPath = staging.BackupPathFor("app"),
            NewHash = mac.Artifact.Hash,
        };

        var newState = new LocalInstallState
        {
            SchemaVersion = 1,
            Channel = target.Channel,
            ReleaseSeq = target.ReleaseSeq,
            ReleaseId = target.ReleaseId,
            BootstrapperVersion = current.BootstrapperVersion, // bootstrapper updates via SelfUpdater
            Packages = new Dictionary<string, InstalledPackage>(StringComparer.Ordinal)
            {
                [MacAppPackage] = new InstalledPackage(MacAppPackage, mac.Version, mac.Artifact.Hash),
            },
        };

        var journal = new TransactionJournal
        {
            Id = txnId,
            TargetReleaseSeq = target.ReleaseSeq,
            TargetReleaseId = target.ReleaseId,
            Operations = [op],
            NewState = newState,
        };

        _log.Information("Committing whole-app swap for release {Release}", target.ReleaseId);
        progress?.Report(new UpdateProgress(UpdateProgress.Install, "app"));
        await new TransactionRunner(_paths, _state).CommitAsync(journal, ct);

        progress?.Report(new UpdateProgress(UpdateProgress.Done));
        return UpdateOutcome.Updated;
    }

    /// <summary>True when the recorded whole-app version and artifact hash match the target and the bundle
    /// is actually present on disk — so we neither re-download nor re-swap an identical app.</summary>
    private static bool IsAlreadyInstalled(LocalInstallState current, MonolithRef mac, string payloadAppDir)
        => current.Packages.TryGetValue(MacAppPackage, out var inst)
           && string.Equals(inst.Version, mac.Version, StringComparison.OrdinalIgnoreCase)
           && string.Equals(inst.ManifestHash, mac.Artifact.Hash, StringComparison.OrdinalIgnoreCase)
           && Directory.Exists(payloadAppDir);

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;

    /// <summary>Maps the per-artifact <see cref="ContentProgress"/> onto the engine's coarse download phase.</summary>
    private sealed class DownloadProgressAdapter(IProgress<UpdateProgress>? outer, long fallbackTotal)
        : IProgress<ContentProgress>
    {
        public void Report(ContentProgress p)
        {
            if (outer is null)
                return;
            long? total = p.TotalBytes is > 0 ? p.TotalBytes : (fallbackTotal > 0 ? fallbackTotal : null);
            double? fraction = total is > 0 ? (double)p.BytesReceived / total.Value : null;
            outer.Report(new UpdateProgress(UpdateProgress.Download, "app", p.BytesReceived, total, fraction));
        }
    }
}
