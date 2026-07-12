namespace CaligoExpress.State;

using CaligoExpress.Fs;

/// <summary>
/// The on-disk layout of an installation. Everything the updater owns lives under a single
/// <c>.caligo</c> control directory next to the installed application, so the app tree itself stays
/// clean. All installed-file paths are resolved through <see cref="SafePath"/> to defeat traversal.
/// </summary>
public sealed class InstallPaths
{
    private readonly string? _controlDirOverride;
    private readonly string? _bootstrapperPathOverride;

    public string Root { get; }
    public string BootstrapperFileName { get; }

    /// <param name="root">The install root that package <c>targetRoot</c>s and the launch bin resolve under
    /// (Windows: <c>%AppData%\.argon-chat</c>; macOS: <c>~/Library/Application Support/Argon</c>).</param>
    /// <param name="bootstrapperFileName">The bootstrapper's on-disk name (a <c>.exe</c> on Windows, a
    /// <c>.app</c> bundle directory on macOS).</param>
    /// <param name="controlDir">Where the updater keeps its private state/journal/cache. Defaults to
    /// <c>Root/.caligo</c>; pass an explicit path to keep it out of the bundle on platforms where the
    /// bootstrapper does not live under <paramref name="root"/>.</param>
    /// <param name="bootstrapperPath">The bootstrapper's own location. Defaults to
    /// <c>Root/bootstrapperFileName</c>; on macOS the bootstrapper bundle lives in <c>~/Applications</c>,
    /// decoupled from the payload root, so it is passed explicitly.</param>
    public InstallPaths(
        string root,
        string bootstrapperFileName = "bootstrapper.exe",
        string? controlDir = null,
        string? bootstrapperPath = null)
    {
        Root = Path.GetFullPath(root);
        BootstrapperFileName = bootstrapperFileName;
        _controlDirOverride = controlDir is null ? null : Path.GetFullPath(controlDir);
        _bootstrapperPathOverride = bootstrapperPath is null ? null : Path.GetFullPath(bootstrapperPath);
    }

    public string ControlDir => _controlDirOverride ?? Path.Combine(Root, ".caligo");
    public string StateFile => Path.Combine(ControlDir, "state.json");
    public string StateBackupFile => StateFile + ".bak";
    public string JournalDir => Path.Combine(ControlDir, "journal");
    public string CacheDir => Path.Combine(ControlDir, "cache");
    public string LockFile => Path.Combine(ControlDir, "lock");

    public string BootstrapperExe => _bootstrapperPathOverride ?? Path.Combine(Root, BootstrapperFileName);
    public string BootstrapperNew => Path.Combine(ControlDir, BootstrapperFileName + ".new");
    public string BootstrapperOld => Path.Combine(ControlDir, BootstrapperFileName + ".old");

    public string TxnDir(string txnId) => Path.Combine(JournalDir, txnId);
    public string TxnJournalFile(string txnId) => Path.Combine(TxnDir(txnId), "txn.json");
    public string TxnStagedDir(string txnId) => Path.Combine(TxnDir(txnId), "staged");
    public string TxnBackupDir(string txnId) => Path.Combine(TxnDir(txnId), "backup");

    public void EnsureControlDirs()
    {
        Directory.CreateDirectory(ControlDir);
        Directory.CreateDirectory(JournalDir);
        Directory.CreateDirectory(CacheDir);
    }

    /// <summary>Resolves the absolute install path for a package file, validated to stay under the root.</summary>
    public string ResolveInstalledFile(string targetRoot, string relativePath)
    {
        var combined = SafePath.NormalizeRelative(targetRoot) + "/" + SafePath.NormalizeRelative(relativePath);
        return SafePath.ResolveUnder(Root, combined);
    }

    /// <summary>Resolves a single root-relative path (e.g. a launch bin like "app/ArgonChat.app") to its
    /// absolute location, validated to stay under the root.</summary>
    public string ResolveUnderRoot(string relativePath) => SafePath.ResolveUnder(Root, relativePath);
}
