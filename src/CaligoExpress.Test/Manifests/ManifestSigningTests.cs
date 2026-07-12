namespace CaligoExpress.Test.Manifests;

using CaligoExpress.Crypto;
using CaligoExpress.Manifests;

[TestFixture]
public class ManifestSigningTests
{
    private static ReleaseManifest SampleRelease(SignatureEnvelope? sig = null) => new(
        SchemaVersion: 1,
        Channel: "beta",
        ReleaseSeq: 42,
        ReleaseId: "2026.6.3-abcdef",
        CreatedAt: "2026-06-03T00:00:00Z",
        Packages:
        [
            new PackageRef("core", "3.4.1", true, [],
                new ArtifactRef("core/3.4.1/package.json", "bb", 10))
        ],
        Signature: sig);

    [Test]
    public void Release_RoundTripsThroughJson()
    {
        var json = CaligoJson.Serialize(SampleRelease());
        var back = CaligoJson.Deserialize<ReleaseManifest>(json);

        Assert.That(back.ReleaseSeq, Is.EqualTo(42));
        Assert.That(back.Packages[0].Name, Is.EqualTo("core"));
        Assert.That(back.Packages[0].Manifest.Key, Is.EqualTo("core/3.4.1/package.json"));
        // WhenWritingNull: an unsigned manifest must not emit the signature property.
        Assert.That(json, Does.Not.Contain("signature"));
    }

    [Test]
    public void SignThenVerify_Succeeds_AndDetectsTampering()
    {
        var (priv, pub) = Ed25519.GenerateKeyPair();
        var keys = new Dictionary<string, byte[]> { ["k1"] = pub };

        // Producer: serialize unsigned, sign, reattach, serialize signed.
        var unsignedJson = CaligoJson.Serialize(SampleRelease());
        var envelope = ManifestSigning.Sign(unsignedJson, "k1", priv);
        var signedJson = CaligoJson.Serialize(SampleRelease(envelope));

        // Consumer: verify the signed JSON against the trust anchors.
        var parsed = CaligoJson.Deserialize<ReleaseManifest>(signedJson);
        Assert.That(ManifestSigning.Verify(signedJson, parsed.Signature, keys), Is.True);

        // Flip a byte of meaningful content -> verification must fail.
        var tampered = signedJson.Replace("\"releaseSeq\":42", "\"releaseSeq\":43");
        Assert.That(ManifestSigning.Verify(tampered, parsed.Signature, keys), Is.False);
    }

    [Test]
    public void Verify_FailsForUnknownKeyOrMissingSignature()
    {
        var (priv, _) = Ed25519.GenerateKeyPair();
        var (_, otherPub) = Ed25519.GenerateKeyPair();
        var json = CaligoJson.Serialize(SampleRelease());
        var envelope = ManifestSigning.Sign(json, "k1", priv);

        Assert.That(ManifestSigning.Verify(json, envelope, new Dictionary<string, byte[]> { ["k1"] = otherPub }), Is.False);
        Assert.That(ManifestSigning.Verify(json, null, new Dictionary<string, byte[]> { ["k1"] = otherPub }), Is.False);
    }

    [Test]
    public void ToKeyMap_ParsesEd25519AnchorsFromConfig()
    {
        var (_, pub) = Ed25519.GenerateKeyPair();
        var entries = new[] { new PublicKeyEntry("k1", "ed25519", Convert.ToBase64String(pub)) };
        var map = ManifestSigning.ToKeyMap(entries);
        Assert.That(map["k1"], Is.EqualTo(pub));
    }
}
