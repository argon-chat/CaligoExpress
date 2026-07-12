namespace CaligoExpress.Abstractions;

/// <summary>Base type for every recoverable update-system failure.</summary>
public class CaligoException : Exception
{
    public CaligoException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Content did not hash to the value the manifest promised. Always fatal (fail closed).</summary>
public sealed class HashMismatchException : CaligoException
{
    public string Key { get; }
    public string Expected { get; }
    public string Actual { get; }

    public HashMismatchException(string key, string expected, string actual)
        : base($"hash mismatch for '{key}': expected {expected}, got {actual}")
    {
        Key = key;
        Expected = expected;
        Actual = actual;
    }
}

/// <summary>No mirror could supply the requested key.</summary>
public sealed class ContentNotFoundException : CaligoException
{
    public string Key { get; }
    public ContentNotFoundException(string key, Exception? inner = null)
        : base($"content not found on any mirror: '{key}'", inner) => Key = key;
}

/// <summary>A manifest's signature failed to verify against the pinned trust anchors.</summary>
public sealed class SignatureVerificationException : CaligoException
{
    public SignatureVerificationException(string what)
        : base($"signature verification failed for {what}") { }
}

/// <summary>A manifest declared a path that escapes its target root (zip-slip / traversal).</summary>
public sealed class UnsafePathException : CaligoException
{
    public UnsafePathException(string path)
        : base($"unsafe path rejected: '{path}'") { }
}
