namespace CaligoExpress.Test.Support;

using CaligoExpress.Abstractions;

/// <summary>
/// A scriptable stand-in for <c>ditto</c> so the whole-app swap is testable on any OS. Instead of reading
/// the (opaque) archive bytes, it materializes a predetermined tree of relative paths into the destination
/// directory — letting tests drive the bundle contents per version. Counts calls so tests can assert that
/// an up-to-date install does not re-extract.
/// </summary>
public sealed class FakeArchiveExtractor : IArchiveExtractor
{
    /// <summary>Relative path (e.g. "ArgonChat.app/Contents/Info.plist") → bytes. Set before each call.</summary>
    public Dictionary<string, byte[]> Tree { get; set; } = new(StringComparer.Ordinal);
    public int ExtractCalls { get; private set; }

    public Task ExtractAsync(string archivePath, string destinationDir, CancellationToken ct = default)
    {
        ExtractCalls++;
        Directory.CreateDirectory(destinationDir);
        foreach (var (rel, bytes) in Tree)
        {
            var full = Path.Combine(destinationDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }
        return Task.CompletedTask;
    }
}
