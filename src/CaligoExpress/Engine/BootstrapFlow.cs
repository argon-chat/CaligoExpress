namespace CaligoExpress.Engine;

using CaligoExpress.Abstractions;
using CaligoExpress.Locking;
using CaligoExpress.Manifests;
using CaligoExpress.State;
using CaligoExpress.Transaction;
using CaligoExpress.Versioning;
using Serilog;

public enum BootstrapOutcome
{
    /// <summary>The bootstrapper updated itself and relaunched; the current process must now exit.</summary>
    SelfUpdateRelaunching,

    /// <summary>Packages were updated.</summary>
    Updated,

    /// <summary>Nothing to do.</summary>
    UpToDate,
}

public sealed record BootstrapOptions
{
    public bool AllowRollback { get; init; }
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The entry-point flow the bootstrapper runs before launching the app: take the single-instance
/// lock, recover any interrupted transaction, clean up a prior self-update, update the bootstrapper
/// itself first if needed (then relaunch), and finally apply package updates. The caller passes a
/// release whose signature has already been verified against the pinned trust anchors.
/// </summary>
public sealed class BootstrapFlow
{
    // Mac Catalyst reports as the iOS family, NOT OSX, so IsOSPlatform(OSX) is FALSE there — which would
    // (wrongly) run the chunked Windows-package path on macOS. IsMacCatalyst() covers the bootstrapper's
    // actual runtime; IsMacOS() covers a plain-Cocoa host.
    private static readonly bool IsMac = OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS();

    private readonly InstallPaths _paths;
    private readonly IContentSource _content;
    private readonly StateStore _state;
    private readonly RecoveryService _recovery;
    private readonly SelfUpdater _selfUpdater;
    private readonly IProcessRunner _runner;
    private readonly IArchiveExtractor _extractor;
    private readonly ILogger _log;
    private readonly IClock _clock;
    private readonly Publishing.CdnKeyScheme _keys;

    public BootstrapFlow(
        InstallPaths paths,
        IContentSource content,
        StateStore state,
        RecoveryService recovery,
        SelfUpdater selfUpdater,
        IProcessRunner runner,
        ILogger? log = null,
        IClock? clock = null,
        IArchiveExtractor? extractor = null,
        Publishing.CdnKeyScheme? keys = null)
    {
        _paths = paths;
        _content = content;
        _state = state;
        _recovery = recovery;
        _selfUpdater = selfUpdater;
        _runner = runner;
        _extractor = extractor ?? DittoArchiveExtractor.Instance;
        _log = log ?? Serilog.Core.Logger.None;
        _clock = clock ?? SystemClock.Instance;
        _keys = keys ?? Publishing.CdnKeyScheme.Root;
    }

    public async Task<BootstrapOutcome> RunAsync(
        ReleaseManifest release,
        BootstrapperManifest? bootstrapper,
        IReadOnlyList<string> relaunchArgs,
        BootstrapOptions? options = null,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new BootstrapOptions();

        using var _ = InstallLock.Acquire(_paths, options.LockTimeout);

        _recovery.RecoverPending();
        _selfUpdater.CleanupOld();

        var current = _state.Read();

        // The bootstrapper is its own signed pointer now (may be absent on a fresh channel). Pick THIS
        // platform's binary: Windows uses `full` (the .exe), macOS uses `macFull` (the .app ditto-zip) — a
        // Windows client must never download the mac bundle or vice versa. If our platform's binary isn't
        // published yet (null), skip self-update and proceed to the package/app update.
        var bootArtifact = IsMac ? bootstrapper?.MacFull : bootstrapper?.Full;
        if (bootstrapper is not null && bootArtifact is not null
            && NeedsBootstrapperUpdate(current.BootstrapperVersion, bootstrapper.Version))
        {
            _log.Information("Bootstrapper update {From} -> {To}; self-updating",
                current.BootstrapperVersion, bootstrapper.Version);

            await _selfUpdater.PrepareAsync(bootArtifact, progress, ct);
            _selfUpdater.Swap();

            // Advance the persisted bootstrapper version BEFORE relaunch so the fresh process sees the
            // job as done and proceeds to packages instead of self-updating again (loop guard).
            var s = _state.Read();
            s.BootstrapperVersion = bootstrapper.Version;
            await _state.WriteAsync(s, ct);

            _selfUpdater.Relaunch(_runner, relaunchArgs);
            return BootstrapOutcome.SelfUpdateRelaunching;
        }

        // macOS delivers the app as one signed bundle (the chunked path cannot reassemble a notarized
        // .app without breaking its signature), so it runs the whole-app installer instead.
        UpdateOutcome outcome;
        if (IsMac)
        {
            var monolith = new MonolithInstaller(_paths, _content, _state, _recovery, _extractor, _log, _clock);
            outcome = await monolith.UpdateAsync(
                release, new UpdateOptions { AllowRollback = options.AllowRollback }, progress, ct);
        }
        else
        {
            var engine = new UpdateEngine(_paths, _content, _state, _recovery, _log, _clock, _keys);
            var result = await engine.UpdateAsync(
                release, new UpdateOptions { AllowRollback = options.AllowRollback }, progress, ct);
            outcome = result.Outcome;
        }

        // Keep the recorded bootstrapper version in sync once everything matches.
        if (bootstrapper is not null)
        {
            var after = _state.Read();
            if (!string.Equals(after.BootstrapperVersion, bootstrapper.Version, StringComparison.OrdinalIgnoreCase))
            {
                after.BootstrapperVersion = bootstrapper.Version;
                await _state.WriteAsync(after, ct);
            }
        }

        return outcome == UpdateOutcome.Updated ? BootstrapOutcome.Updated : BootstrapOutcome.UpToDate;
    }

    /// <summary>True when the target bootstrapper is a different, non-older version than what is installed.</summary>
    public static bool NeedsBootstrapperUpdate(string installed, string target)
    {
        if (string.Equals(installed, target, StringComparison.OrdinalIgnoreCase))
            return false;

        // Block downgrades when both versions parse as SemVer; otherwise any difference triggers an update.
        if (PackageVersion.TryParse(installed, out var iv) && PackageVersion.TryParse(target, out var tv))
            return tv! > iv!;

        return true;
    }
}
