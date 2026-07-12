namespace CaligoExpress.Test.Support;

using System.IO.Compression;
using CaligoExpress.Abstractions;

/// <summary>
/// A cross-platform <see cref="IArchiveExtractor"/> backed by <see cref="System.IO.Compression"/>, used so
/// the publish→download→extract→swap loop is testable on Windows/Linux CI where <c>ditto</c> does not
/// exist. NOTE: real macOS installs must use <see cref="DittoArchiveExtractor"/> — a plain zip reader drops
/// symlinks/exec-bits/xattrs and would break the bundle's signature. This stand-in only exercises the
/// engine's orchestration, not Apple's metadata preservation (which mac CI verifies separately).
/// </summary>
public sealed class ZipArchiveExtractor : IArchiveExtractor
{
    public Task ExtractAsync(string archivePath, string destinationDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationDir);
        ZipFile.ExtractToDirectory(archivePath, destinationDir, overwriteFiles: true);
        return Task.CompletedTask;
    }
}
