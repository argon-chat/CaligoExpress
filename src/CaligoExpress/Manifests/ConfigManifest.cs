namespace CaligoExpress.Manifests;

using System.Text.Json.Serialization;

/// <summary>
/// Bootstrap document, fetched from a hard-coded seed host list and signed.
/// Carries the dynamic mirror set, channel pointers, trust anchors and the minimum
/// bootstrapper version. Trust is anchored by the public key pinned in the binary.
/// </summary>
public sealed record ConfigManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("mirrors")] List<MirrorEntry> Mirrors,
    [property: JsonPropertyName("channels")] Dictionary<string, string> Channels,
    [property: JsonPropertyName("publicKeys")] List<PublicKeyEntry> PublicKeys,
    [property: JsonPropertyName("minBootstrapper")] string MinBootstrapper,
    [property: JsonPropertyName("signature")] SignatureEnvelope? Signature = null);
