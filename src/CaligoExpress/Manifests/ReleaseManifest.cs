namespace CaligoExpress.Manifests;

using System.Text.Json.Serialization;

/// <summary>
/// One package pinned by a release. <see cref="Deps"/> is the dependency DAG used for install
/// ordering and grouping. <see cref="Manifest"/> points at the package.json listing, which carries
/// every file's content-defined chunk list; the client downloads only the chunks it is missing (no
/// per-version delta artifacts and no full zip — those are obsolete under content-defined chunking).
/// </summary>
public sealed record PackageRef(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("deps")] List<string> Deps,
    [property: JsonPropertyName("manifest")] ArtifactRef Manifest);

/// <summary>
/// The self-updating bootstrapper as its OWN signed pointer, decoupled from app releases: a channel's
/// bootstrapper version advances independently of which package release is rolled out. Delivered whole
/// (one binary, content-addressed by <see cref="Full"/>); not chunked because the running exe self-updates
/// by rename. <see cref="Seq"/> is the bootstrapper track's monotonic sequence for the control plane.
/// </summary>
public sealed record BootstrapperManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("full")] ArtifactRef Full,
    [property: JsonPropertyName("signature")] SignatureEnvelope? Signature = null,
    // The macOS bootstrapper is a DIFFERENT binary (a .app bundle ditto-zip) than the Windows .exe, so the
    // two platforms cannot share one `full` pointer — that would make a Windows client download the mac
    // bundle (and vice versa). They live side by side on one signed bootstrapper.json: `full` = Windows,
    // `macFull` = macOS. Null when that platform hasn't published yet. Self-updater picks its own per-OS.
    [property: JsonPropertyName("macFull")] ArtifactRef? MacFull = null,
    // App slug for multi-app workers; inside the signed payload so a manifest can't be replayed into
    // another app's namespace. Null (omitted) on legacy default-app manifests — bytes unchanged.
    [property: JsonPropertyName("app")] string? App = null);

/// <summary>
/// How the bootstrapper launches the application once it is up to date. <see cref="Bin"/> is relative
/// to the install root (e.g. "app/Argon.exe", or on macOS the bundle "app/ArgonChat.app" which is handed
/// to <c>open</c>); <see cref="WorkingDir"/> defaults to the binary's folder.
/// </summary>
public sealed record LaunchSpec(
    [property: JsonPropertyName("bin")] string Bin,
    [property: JsonPropertyName("args")] List<string> Args,
    [property: JsonPropertyName("workingDir")] string? WorkingDir = null);

/// <summary>
/// A whole-application artifact delivered as a single content-addressed blob rather than a chunked
/// package. This is the macOS delivery model: a signed-and-notarized <c>.app</c> bundle cannot be
/// reassembled from chunks without breaking its code signature (symlinks, exec bits, and the stapled
/// ticket would not survive), so the client downloads the entire bundle as one
/// <c>ditto -c -k --keepParent</c> zip and atomically swaps it into place. <see cref="BundleName"/> is
/// the directory name the archive expands to (e.g. "ArgonChat.app") so the installer can locate the
/// extracted bundle. <see cref="Version"/> drives the "do I already have this?" check independently of
/// the release sequence.
/// </summary>
public sealed record MonolithRef(
    [property: JsonPropertyName("artifact")] ArtifactRef Artifact,
    [property: JsonPropertyName("bundleName")] string BundleName,
    [property: JsonPropertyName("version")] string Version);

/// <summary>
/// The pinned, consistent set of package versions for a channel at a point in time. The bootstrapper is
/// NOT part of this — it has its own <see cref="BootstrapperManifest"/> pointer, so operators roll out app
/// versions without touching the launcher. <see cref="ReleaseSeq"/> is a monotonic integer issued by the
/// control plane: it drives update direction and downgrade protection independent of SemVer parsing. Signed.
/// </summary>
public sealed record ReleaseManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("releaseSeq")] long ReleaseSeq,
    [property: JsonPropertyName("releaseId")] string ReleaseId,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("packages")] List<PackageRef> Packages,
    [property: JsonPropertyName("launch")] LaunchSpec? Launch = null,
    [property: JsonPropertyName("bootstrapper")] LegacyBootstrapperRef? Bootstrapper = null,
    [property: JsonPropertyName("signature")] SignatureEnvelope? Signature = null,
    // macOS whole-app delivery. Null on Windows releases, so existing Windows clients and manifests are
    // untouched; a mac client reads MacApp/MacLaunch and ignores Packages (the chunked path).
    [property: JsonPropertyName("macApp")] MonolithRef? MacApp = null,
    [property: JsonPropertyName("macLaunch")] LaunchSpec? MacLaunch = null,
    // Linux whole-app delivery: one AppImage that replaces itself in place (there is no Linux
    // bootstrapper, so no launch spec). BundleName is the image's file name. Null when Linux hasn't
    // published; Windows and mac clients ignore it.
    [property: JsonPropertyName("linuxApp")] MonolithRef? LinuxApp = null,
    // App slug for multi-app workers; inside the signed payload so a manifest can't be replayed into
    // another app's namespace. Null (omitted) on legacy default-app manifests — bytes unchanged.
    [property: JsonPropertyName("app")] string? App = null);

/// <summary>
/// LEGACY backward-compat shim. Pre-decoupling clients require a <c>bootstrapper</c> field inside
/// release.json (and crash without it). The publisher fills it with the channel's current bootstrapper so
/// those clients keep working and self-update to the new launcher, which then reads the standalone
/// bootstrapper.json pointer. New clients ignore this field. Remove once no client predates the split.
/// </summary>
public sealed record LegacyBootstrapperRef(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("full")] ArtifactRef Full);
