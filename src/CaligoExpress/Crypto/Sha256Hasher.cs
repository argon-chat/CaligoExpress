namespace CaligoExpress.Crypto;

using System.Security.Cryptography;

/// <summary>SHA-256 helpers returning lowercase hex, used as the content-address for every artifact and file.</summary>
public static class Sha256Hasher
{
    public static string HashHex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static async Task<string> HashFileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
    }

    public static async Task<string> HashStreamAsync(Stream stream, CancellationToken ct = default)
        => Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));

    /// <summary>Constant-time comparison of two hex digests (case-insensitive).</summary>
    public static bool HashesEqual(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(a), Convert.FromHexString(b));
}
