namespace CaligoExpress.Crypto;

using CaligoExpress.Manifests;

/// <summary>
/// Binds canonical JSON + Ed25519 + <see cref="SignatureEnvelope"/> together.
///
/// Producer flow: serialize the manifest with no signature, sign the canonical bytes, attach the
/// returned envelope, then serialize again to publish.
/// Consumer flow: deserialize, read the envelope, and verify against the same JSON with the
/// signature property stripped. Any mismatch must be treated as fatal (fail closed).
/// </summary>
public static class ManifestSigning
{
    public const string Algorithm = "ed25519";

    public static SignatureEnvelope Sign(string json, string keyId, byte[] privateKey)
    {
        var payload = JsonCanonicalizer.CanonicalizeForSigning(json);
        var sig = Ed25519.Sign(payload, privateKey);
        return new SignatureEnvelope(keyId, Algorithm, Convert.ToBase64String(sig));
    }

    /// <summary>
    /// Verifies <paramref name="json"/> against <paramref name="signature"/> using the trust
    /// anchors in <paramref name="keysById"/> (id -> raw public key bytes). Returns false on any
    /// problem (unknown key, wrong algorithm, bad signature) rather than throwing.
    /// </summary>
    public static bool Verify(string json, SignatureEnvelope? signature, IReadOnlyDictionary<string, byte[]> keysById)
    {
        if (signature is null || signature.Alg != Algorithm)
            return false;
        if (!keysById.TryGetValue(signature.KeyId, out var publicKey))
            return false;

        byte[] sigBytes;
        try
        {
            sigBytes = Convert.FromBase64String(signature.Sig);
        }
        catch (FormatException)
        {
            return false;
        }

        var payload = JsonCanonicalizer.CanonicalizeForSigning(json);
        return Ed25519.Verify(payload, sigBytes, publicKey);
    }

    /// <summary>Builds the id -> raw-public-key map from a config's <see cref="PublicKeyEntry"/> list.</summary>
    public static Dictionary<string, byte[]> ToKeyMap(IEnumerable<PublicKeyEntry> keys)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var k in keys)
        {
            if (k.Alg == Algorithm)
                map[k.Id] = Convert.FromBase64String(k.Key);
        }
        return map;
    }
}
