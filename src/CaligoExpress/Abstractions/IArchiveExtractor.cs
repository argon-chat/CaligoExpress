namespace CaligoExpress.Abstractions;

using System.Diagnostics;

/// <summary>
/// Expands a whole-app archive (the macOS monolith zip) into a destination directory. Kept behind an
/// interface so the swap logic is unit-testable on any OS with a fake extractor, and so the real
/// extraction tool is swappable per platform.
///
/// CRITICAL on macOS: the bundle must be expanded with <c>ditto</c>, not a generic zip reader.
/// <c>System.IO.Compression</c> drops symlinks, executable bits, and extended attributes, which would
/// invalidate the code signature and fail notarization/Gatekeeper. The artifact is produced with
/// <c>ditto -c -k --keepParent</c> and must be expanded with <c>ditto -x -k</c> so every byte — including
/// the stapled ticket — round-trips intact.
/// </summary>
public interface IArchiveExtractor
{
    /// <summary>
    /// Extracts <paramref name="archivePath"/> into <paramref name="destinationDir"/> (created if needed).
    /// Throws on a non-zero exit / extraction failure.
    /// </summary>
    Task ExtractAsync(string archivePath, string destinationDir, CancellationToken ct = default);
}

/// <summary>Expands a macOS bundle archive with <c>ditto</c>, preserving symlinks, perms, and xattrs.</summary>
public sealed class DittoArchiveExtractor : IArchiveExtractor
{
    public static readonly DittoArchiveExtractor Instance = new();

    public async Task ExtractAsync(string archivePath, string destinationDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationDir);

        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/ditto",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // `ditto -x -k <zip> <dest>` expands an Apple-style PKZip preserving HFS metadata.
        psi.ArgumentList.Add("-x");
        psi.ArgumentList.Add("-k");
        psi.ArgumentList.Add(archivePath);
        psi.ArgumentList.Add(destinationDir);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start ditto");
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"ditto failed ({proc.ExitCode}) extracting '{archivePath}': {stderr.Trim()}");
    }
}
