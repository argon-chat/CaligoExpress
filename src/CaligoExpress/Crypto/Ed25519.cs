namespace CaligoExpress.Crypto;

using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

/// <summary>
/// Ed25519 sign/verify over raw 32-byte keys. Signing happens offline in CI/CLI with the private
/// key; the engine/bootstrapper only ever verifies against a public key pinned in the binary.
/// Backed by BouncyCastle (pure-managed, no native dependency).
/// </summary>
public static class Ed25519
{
    /// <summary>Generates a fresh key pair. Returns the raw 32-byte private and public keys.</summary>
    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        var priv = new Ed25519PrivateKeyParameters(new SecureRandom());
        var pub = priv.GeneratePublicKey();
        return (priv.GetEncoded(), pub.GetEncoded());
    }

    public static byte[] PublicKeyFromPrivate(byte[] privateKey)
        => new Ed25519PrivateKeyParameters(privateKey, 0).GeneratePublicKey().GetEncoded();

    public static byte[] Sign(ReadOnlySpan<byte> message, byte[] privateKey)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(privateKey, 0));
        signer.BlockUpdate(message.ToArray(), 0, message.Length);
        return signer.GenerateSignature();
    }

    public static bool Verify(ReadOnlySpan<byte> message, byte[] signature, byte[] publicKey)
    {
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
        verifier.BlockUpdate(message.ToArray(), 0, message.Length);
        return verifier.VerifySignature(signature);
    }
}
