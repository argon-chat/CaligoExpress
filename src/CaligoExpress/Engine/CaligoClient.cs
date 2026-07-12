namespace CaligoExpress.Engine;

using System.Text;
using CaligoExpress.Abstractions;
using CaligoExpress.Content;
using CaligoExpress.Crypto;
using CaligoExpress.Manifests;
using CaligoExpress.Publishing;
using Serilog;

public sealed record CaligoClientOptions
{
    /// <summary>Hard-coded seed hosts used to fetch the (signed) config before the mirror list is known.</summary>
    public required IReadOnlyList<string> SeedHosts { get; init; }

    /// <summary>Trust anchors pinned in the binary: keyId -> raw Ed25519 public key bytes.</summary>
    public required IReadOnlyDictionary<string, byte[]> PinnedKeys { get; init; }

    public string Scheme { get; init; } = "https";
    public string? Region { get; init; }

    /// <summary>App slug on a multi-app worker/bucket: manifests live under <c>apps/&lt;appId&gt;/</c>.
    /// Null for the default app (Argon), which keeps the legacy root layout.</summary>
    public string? AppId { get; init; }

    /// <summary>When false, signatures are parsed but not enforced (dev only). Production must keep this true.</summary>
    public bool RequireSignatures { get; init; } = true;
}

/// <summary>Everything the bootstrapper needs to run an update: a mirror-backed content source, the verified
/// app release, and the verified bootstrapper pointer (separate signed doc; null if a channel has none yet).</summary>
public sealed record ResolvedRelease(
    IContentSource Content, ConfigManifest Config, ReleaseManifest Release, BootstrapperManifest? Bootstrapper);

/// <summary>
/// Resolves the signed config and channel release for the client, with the regional failover that the
/// legacy system never actually performed. There are two endpoint classes: the small signed manifests
/// (config.json + release.json) come from the seed/manifest hosts, while the large artifacts come from
/// the content mirrors listed in the config (region-ordered).
///   1. fetch config.json by racing the seed (manifest) hosts, verify against the pinned keys;
///   2. fetch the channel's release.json from the same manifest hosts, verify against the config's keys;
///   3. build a region-ordered content source from the config's mirror list for artifact downloads.
/// Trust chains pinned key -> config -> release -> per-artifact hashes.
/// </summary>
public sealed class CaligoClient
{
    private readonly CaligoClientOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger _log;

    public CaligoClient(CaligoClientOptions options, HttpClient http, ILogger? log = null)
    {
        _options = options;
        _http = http;
        _log = log ?? Serilog.Core.Logger.None;
    }

    public async Task<ResolvedRelease> ResolveAsync(string channel, CancellationToken ct = default)
    {
        // Manifest endpoints: the seed hosts serve the small signed config + release documents.
        var manifests = new MirrorContentSource(
            _options.SeedHosts.Select(IContentSource (h) => new HttpContentSource(h, _http, _options.Scheme)).ToList(),
            _log);

        var keys = CdnKeyScheme.ForApp(_options.AppId);
        var configBytes = await manifests.GetBytesAsync(keys.Config, ct);
        var config = _options.RequireSignatures
            ? ManifestTrust.LoadVerifiedConfig(configBytes, _options.PinnedKeys)
            : CaligoJson.Deserialize<ConfigManifest>(Encoding.UTF8.GetString(configBytes));

        var releaseKey = config.Channels.TryGetValue(channel, out var key) ? key : keys.Release(channel);
        var releaseBytes = await manifests.GetBytesAsync(releaseKey, ct);
        var release = _options.RequireSignatures
            ? ManifestTrust.LoadVerifiedRelease(releaseBytes, ManifestSigning.ToKeyMap(config.PublicKeys))
            : CaligoJson.Deserialize<ReleaseManifest>(Encoding.UTF8.GetString(releaseBytes));

        // The bootstrapper is its own signed pointer, advanced independently of app releases. A channel may
        // not have one yet (then there is simply no self-update to apply).
        BootstrapperManifest? bootstrapper = null;
        try
        {
            var bootBytes = await manifests.GetBytesAsync(keys.BootstrapperManifest(channel), ct);
            bootstrapper = _options.RequireSignatures
                ? ManifestTrust.LoadVerifiedBootstrapper(bootBytes, ManifestSigning.ToKeyMap(config.PublicKeys))
                : CaligoJson.Deserialize<BootstrapperManifest>(Encoding.UTF8.GetString(bootBytes));
        }
        catch (ContentNotFoundException)
        {
            _log.Information("No bootstrapper.json for channel {Channel}; self-update skipped", channel);
        }

        // Content endpoints: artifacts (chunks/bootstrapper binary) come from the region-ordered mirrors.
        var content = MirrorResolver.Build(config, _http, _options.Region, _log);

        return new ResolvedRelease(content, config, release, bootstrapper);
    }
}
