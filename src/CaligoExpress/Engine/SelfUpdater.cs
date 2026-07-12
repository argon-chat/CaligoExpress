namespace CaligoExpress.Engine;

using CaligoExpress.Abstractions;
using CaligoExpress.Fs;
using CaligoExpress.Manifests;
using CaligoExpress.State;
using Serilog;

/// <summary>
/// Replaces the running bootstrapper with a new version. On Windows this uses the rename-self trick — no
/// PowerShell, no external script (the legacy approach was a base64 PowerShell blob with a path bug). A
/// running .exe cannot be overwritten, but it CAN be renamed: move the live exe aside to <c>.old</c>,
/// move the verified <c>.new</c> into its place, relaunch, and exit. The fresh process deletes the
/// leftover <c>.old</c> on startup.
///
/// On macOS the bootstrapper is a <c>.app</c> bundle (a directory), and the artifact is a <c>ditto</c>
/// zip of that bundle. macOS lets you replace a running app's on-disk bundle (the kernel keeps the
/// running executable's inode), so the swap is: expand the zip, move the live bundle aside to
/// <c>.old</c>, move the new bundle into place, then relaunch with <c>open</c> (the Sparkle pattern).
/// </summary>
public sealed class SelfUpdater
{
    // Mac Catalyst reports as the iOS family, NOT OSX (IsOSPlatform(OSX) is FALSE there) — use the
    // OperatingSystem helpers so the bootstrapper self-update takes the macOS bundle path on Catalyst.
    private static readonly bool IsMac = OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS();

    private readonly InstallPaths _paths;
    private readonly IContentSource _content;
    private readonly IArchiveExtractor? _extractor;
    private readonly ILogger _log;

    public SelfUpdater(InstallPaths paths, IContentSource content, ILogger? log = null, IArchiveExtractor? extractor = null)
    {
        _paths = paths;
        _content = content;
        _extractor = extractor;
        _log = log ?? Serilog.Core.Logger.None;
    }

    /// <summary>Removes the previous bootstrapper left behind by an earlier self-update. Idempotent.</summary>
    public void CleanupOld()
    {
        if (IsMac)
        {
            if (Directory.Exists(_paths.BootstrapperOld))
            {
                AtomicFile.DeleteDirectory(_paths.BootstrapperOld);
                _log.Information("Removed stale bootstrapper backup");
            }
            return;
        }

        if (File.Exists(_paths.BootstrapperOld))
        {
            AtomicFile.Delete(_paths.BootstrapperOld);
            _log.Information("Removed stale bootstrapper backup");
        }
    }

    /// <summary>Downloads and verifies the new bootstrapper into the staging slot, reporting download
    /// progress as a "launcher" fetch so the UI moves the bar during the self-update instead of sitting
    /// on a silent "Preparing…" (this download was the one phase that emitted no progress).</summary>
    public Task PrepareAsync(ArtifactRef full, IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
    {
        _paths.EnsureControlDirs();

        // Flip the UI off "Preparing…" immediately, before any bytes arrive, with the known total size.
        progress?.Report(new UpdateProgress(UpdateProgress.Download, "launcher", 0, full.Size, 0));

        var inner = progress is null
            ? null
            : new ContentProgressAdapter(progress, full.Size);

        return _content.DownloadToFileAsync(full.Key, _paths.BootstrapperNew, full.Hash, inner, ct);
    }

    /// <summary>Maps per-artifact <see cref="ContentProgress"/> onto the engine's coarse
    /// <see cref="UpdateProgress"/>, tagging it as the launcher download. Forwards synchronously — the
    /// outer reporter (a MAUI <c>Progress</c>) already marshals to the UI thread.</summary>
    private sealed class ContentProgressAdapter(IProgress<UpdateProgress> outer, long fallbackTotal)
        : IProgress<ContentProgress>
    {
        public void Report(ContentProgress p)
        {
            long? total = p.TotalBytes is > 0 ? p.TotalBytes : (fallbackTotal > 0 ? fallbackTotal : null);
            double? fraction = total is > 0 ? (double)p.BytesReceived / total.Value : null;
            outer.Report(new UpdateProgress(UpdateProgress.Download, "launcher", p.BytesReceived, total, fraction));
        }
    }

    /// <summary>The pure swap: live bootstrapper → <c>.old</c>, staged new → live. Crash-tolerant and idempotent-ish.</summary>
    public void Swap()
    {
        if (IsMac)
        {
            SwapMacBundle();
            return;
        }

        if (!File.Exists(_paths.BootstrapperNew))
            throw new CaligoException("no staged bootstrapper to swap in");

        if (File.Exists(_paths.BootstrapperOld))
            AtomicFile.Delete(_paths.BootstrapperOld);

        if (File.Exists(_paths.BootstrapperExe))
            AtomicFile.Move(_paths.BootstrapperExe, _paths.BootstrapperOld, overwrite: true);

        AtomicFile.Move(_paths.BootstrapperNew, _paths.BootstrapperExe, overwrite: true);
        _log.Information("Swapped in new bootstrapper executable");
    }

    /// <summary>macOS bundle swap: expand the staged ditto zip, move the live <c>.app</c> aside to
    /// <c>.old</c>, and move the fresh bundle into place. The running process keeps its executable inode.</summary>
    private void SwapMacBundle()
    {
        if (!File.Exists(_paths.BootstrapperNew))
            throw new CaligoException("no staged bootstrapper to swap in");
        if (_extractor is null)
            throw new CaligoException("macOS bootstrapper self-update requires an archive extractor");

        // Expand the downloaded bundle zip next to the staging slot, then locate the .app inside it.
        var stageDir = _paths.BootstrapperNew + ".stage";
        AtomicFile.DeleteDirectory(stageDir);
        _extractor.ExtractAsync(_paths.BootstrapperNew, stageDir).GetAwaiter().GetResult();

        var stagedBundle = ResolveStagedBundle(stageDir, _paths.BootstrapperFileName);

        if (Directory.Exists(_paths.BootstrapperOld))
            AtomicFile.DeleteDirectory(_paths.BootstrapperOld);

        if (Directory.Exists(_paths.BootstrapperExe))
            AtomicFile.MoveDirectory(_paths.BootstrapperExe, _paths.BootstrapperOld, overwrite: true);

        AtomicFile.MoveDirectory(stagedBundle, _paths.BootstrapperExe, overwrite: true);
        AtomicFile.DeleteDirectory(stageDir);
        AtomicFile.Delete(_paths.BootstrapperNew);
        _log.Information("Swapped in new bootstrapper bundle");
    }

    /// <summary>Locates the <c>.app</c> bundle inside an expanded bootstrapper archive. Don't trust the
    /// bundle's internal name: the mac publish pipeline names it from MAUI's <c>&lt;ApplicationTitle&gt;</c>
    /// (e.g. "Argon.Installer.app"), which need not match <paramref name="expectedName"/> ("ArgonChat.app").
    /// The swap destination (<see cref="InstallPaths.BootstrapperExe"/>) is the already-correctly-named live
    /// bundle, so any single top-level <c>.app</c> is the new bootstrapper — prefer the expected name,
    /// otherwise take the lone <c>.app</c>.</summary>
    public static string ResolveStagedBundle(string stageDir, string expectedName)
    {
        var expected = Path.Combine(stageDir, expectedName);
        if (Directory.Exists(expected))
            return expected;

        var apps = Directory.GetDirectories(stageDir, "*.app");
        if (apps.Length == 0)
            throw new CaligoException(
                $"staged bootstrapper archive contained no .app bundle (expected '{expectedName}')");
        if (apps.Length > 1)
            throw new CaligoException(
                $"staged bootstrapper archive contained {apps.Length} .app bundles; expected exactly one");
        return apps[0];
    }

    /// <summary>Starts the (already swapped-in) bootstrapper. The current process should exit right after.
    /// On macOS the bootstrapper is a <c>.app</c> bundle, so it is relaunched via <c>open</c>.</summary>
    public int Relaunch(IProcessRunner runner, IReadOnlyList<string> relaunchArgs)
    {
        if (IsMac)
        {
            var args = new List<string> { "-n", _paths.BootstrapperExe };
            if (relaunchArgs.Count > 0)
            {
                args.Add("--args");
                args.AddRange(relaunchArgs);
            }
            return runner.Start(new ProcessStartSpec("/usr/bin/open", args));
        }

        return runner.Start(new ProcessStartSpec(
            _paths.BootstrapperExe, relaunchArgs, Path.GetDirectoryName(_paths.BootstrapperExe)));
    }

    /// <summary>Convenience for callers that swap and relaunch in one step.</summary>
    public int SwapAndRelaunch(IProcessRunner runner, IReadOnlyList<string> relaunchArgs)
    {
        Swap();
        return Relaunch(runner, relaunchArgs);
    }
}
