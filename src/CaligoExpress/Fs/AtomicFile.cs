namespace CaligoExpress.Fs;

/// <summary>
/// Crash-safe file writes and moves. Writes go to a temp sibling that is flushed to disk and then
/// atomically swapped in (<see cref="File.Replace"/>/<see cref="File.Move"/>). Destructive moves
/// retry briefly to ride out transient AV/indexer locks common on Windows.
/// </summary>
public static class AtomicFile
{
    private const int RetryCount = 8;
    private static readonly int[] BackoffMs = [10, 25, 50, 100, 150, 200, 300, 500];

    public static async Task WriteAllBytesAsync(string path, byte[] bytes, bool keepBackup = false, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);

        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await fs.WriteAsync(bytes, ct);
            await fs.FlushAsync(ct);
            fs.Flush(flushToDisk: true);
        }

        Swap(tmp, path, keepBackup);
    }

    public static Task WriteAllTextAsync(string path, string text, bool keepBackup = false, CancellationToken ct = default)
        => WriteAllBytesAsync(path, System.Text.Encoding.UTF8.GetBytes(text), keepBackup, ct);

    private static void Swap(string tmp, string path, bool keepBackup)
    {
        if (File.Exists(path))
        {
            ClearReadOnly(path);
            var backup = keepBackup ? path + ".bak" : null;
            if (backup is not null)
                ClearReadOnly(backup);
            Retry(() => File.Replace(tmp, path, backup, ignoreMetadataErrors: true));
        }
        else
        {
            Retry(() => File.Move(tmp, path));
        }
    }

    /// <summary>Move that tolerates a pre-existing destination and transient locks.</summary>
    public static void Move(string source, string destination, bool overwrite = true)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (dir is not null)
            Directory.CreateDirectory(dir);
        if (overwrite && File.Exists(destination))
            ClearReadOnly(destination);
        Retry(() => File.Move(source, destination, overwrite));
    }

    public static void Delete(string path)
    {
        if (!File.Exists(path))
            return;
        ClearReadOnly(path);
        Retry(() => File.Delete(path));
    }

    /// <summary>
    /// Moves a whole directory (e.g. a macOS <c>.app</c> bundle) into place. On the same volume this is a
    /// single <c>rename(2)</c>, so the swap is atomic; cross-volume callers must stage onto the target
    /// volume first. When <paramref name="overwrite"/> the existing destination is removed beforehand
    /// (<see cref="Directory.Move"/> refuses a pre-existing target). Tolerates transient locks like the
    /// file moves do.
    /// </summary>
    public static void MoveDirectory(string source, string destination, bool overwrite = true)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (parent is not null)
            Directory.CreateDirectory(parent);
        if (overwrite && Directory.Exists(destination))
            DeleteDirectory(destination);
        Retry(() => Directory.Move(source, destination));
    }

    /// <summary>Recursively deletes a directory, riding out the same transient locks as the file ops.</summary>
    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        Retry(() => Directory.Delete(path, recursive: true));
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < RetryCount)
            {
                Thread.Sleep(BackoffMs[attempt]);
            }
            catch (UnauthorizedAccessException) when (attempt < RetryCount)
            {
                Thread.Sleep(BackoffMs[attempt]);
            }
        }
    }
}
