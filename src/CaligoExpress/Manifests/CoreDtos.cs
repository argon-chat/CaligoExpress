namespace CaligoExpress.Manifests;

using System.Text.Json.Serialization;

/// <summary>
/// Pointer to a binary artifact stored on a mirror/CDN. <see cref="Key"/> is the
/// mirror-relative path (e.g. "core/3.4.1/core.zip"); <see cref="Hash"/> is the
/// lowercase SHA-256 hex of the artifact bytes and anchors trust (mirrors are untrusted).
/// </summary>
public sealed record ArtifactRef(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("size")] long Size);

/// <summary>Detached Ed25519 signature over the canonical bytes of a manifest (sans this field).</summary>
public sealed record SignatureEnvelope(
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("alg")] string Alg,
    [property: JsonPropertyName("sig")] string Sig);

/// <summary>A trust anchor. <see cref="Key"/> is base64 of the raw 32-byte Ed25519 public key.</summary>
public sealed record PublicKeyEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("alg")] string Alg,
    [property: JsonPropertyName("key")] string Key);

/// <summary>A content mirror. Many can be listed; the client races/fails over between them.</summary>
public sealed record MirrorEntry(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("regions")] List<string>? Regions = null,
    [property: JsonPropertyName("priority")] int Priority = 0,
    [property: JsonPropertyName("scheme")] string Scheme = "https");
