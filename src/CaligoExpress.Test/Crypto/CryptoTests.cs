namespace CaligoExpress.Test.Crypto;

using System.Text;
using System.Text.Json.Nodes;
using CaligoExpress.Crypto;

[TestFixture]
public class CryptoTests
{
    [Test]
    public void Canonicalize_IsStableAcrossKeyOrderAndWhitespace()
    {
        const string a = """{ "b": 1, "a": 2, "nested": { "y": [3,2,1], "x": true } }""";
        const string b = "{\"nested\":{\"x\":true,\"y\":[3,2,1]},\"a\":2,\"b\":1}";

        var ca = JsonCanonicalizer.Canonicalize(JsonNode.Parse(a));
        var cb = JsonCanonicalizer.Canonicalize(JsonNode.Parse(b));

        Assert.That(ca, Is.EqualTo(cb));
        // Arrays are order-sensitive and keys are sorted ordinally.
        Assert.That(Encoding.UTF8.GetString(ca), Is.EqualTo("{\"a\":2,\"b\":1,\"nested\":{\"x\":true,\"y\":[3,2,1]}}"));
    }

    [Test]
    public void CanonicalizeForSigning_DropsSignatureProperty()
    {
        const string signed = """{ "data": 1, "signature": { "sig": "zzz" } }""";
        const string unsigned = """{ "data": 1 }""";
        Assert.That(
            JsonCanonicalizer.CanonicalizeForSigning(signed),
            Is.EqualTo(JsonCanonicalizer.CanonicalizeForSigning(unsigned)));
    }

    [Test]
    public void Sha256_KnownVector()
        => Assert.That(Sha256Hasher.HashHex(Encoding.UTF8.GetBytes("abc")),
            Is.EqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));

    [Test]
    public void Ed25519_SignVerifyRoundtrip()
    {
        var (priv, pub) = Ed25519.GenerateKeyPair();
        var msg = Encoding.UTF8.GetBytes("the quick brown fox");

        var sig = Ed25519.Sign(msg, priv);

        Assert.That(Ed25519.Verify(msg, sig, pub), Is.True);
        Assert.That(Ed25519.PublicKeyFromPrivate(priv), Is.EqualTo(pub));
    }

    [Test]
    public void Ed25519_RejectsTamperedMessage()
    {
        var (priv, pub) = Ed25519.GenerateKeyPair();
        var sig = Ed25519.Sign(Encoding.UTF8.GetBytes("original"), priv);
        Assert.That(Ed25519.Verify(Encoding.UTF8.GetBytes("tampered"), sig, pub), Is.False);
    }

    [Test]
    public void Ed25519_RejectsWrongKey()
    {
        var (priv, _) = Ed25519.GenerateKeyPair();
        var (_, otherPub) = Ed25519.GenerateKeyPair();
        var msg = Encoding.UTF8.GetBytes("payload");
        var sig = Ed25519.Sign(msg, priv);
        Assert.That(Ed25519.Verify(msg, sig, otherPub), Is.False);
    }
}
