namespace CaligoExpress.Transaction;

using CaligoExpress.State;

/// <summary>
/// Owns the per-transaction <c>staged/</c> and <c>backup/</c> directories. New content is fully
/// materialized into <c>staged/</c> during prepare (never touching the live tree), so the commit is
/// just a sequence of cheap, atomic renames. Tokens are opaque so file paths never leak into the
/// staging namespace.
/// </summary>
public sealed class StagingArea
{
    public string StagedDir { get; }
    public string BackupDir { get; }

    public StagingArea(InstallPaths paths, string txnId)
    {
        StagedDir = paths.TxnStagedDir(txnId);
        BackupDir = paths.TxnBackupDir(txnId);
        Directory.CreateDirectory(StagedDir);
        Directory.CreateDirectory(BackupDir);
    }

    public string StagePathFor(string token) => Path.Combine(StagedDir, token);
    public string BackupPathFor(string token) => Path.Combine(BackupDir, token);

    public async Task WriteStagedAsync(string token, byte[] bytes, CancellationToken ct = default)
    {
        var path = StagePathFor(token);
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await fs.WriteAsync(bytes, ct);
        await fs.FlushAsync(ct);
        fs.Flush(flushToDisk: true);
    }

    public void WriteStaged(string token, byte[] bytes)
    {
        var path = StagePathFor(token);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>Opens a staged file for streaming writes (e.g. extracting a zip entry directly into it).</summary>
    public FileStream OpenStagedWrite(string token)
        => new(StagePathFor(token), FileMode.Create, FileAccess.Write, FileShare.None);
}
