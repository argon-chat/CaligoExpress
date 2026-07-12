namespace CaligoExpress.Transaction;

using CaligoExpress.Fs;
using CaligoExpress.State;

/// <summary>
/// Executes the commit phase of an in-place update: back up each live file being replaced/removed,
/// move staged content into place, then — as the single atomic commit point — write the new state.
/// Backups are kept until the very end so a crash before the state write is fully recoverable.
///
/// <see cref="CrashHook"/> lets tests deterministically abort at any labelled point to prove the
/// system always lands on either the old or the new state, never a broken mix.
/// </summary>
public sealed class TransactionRunner
{
    private readonly InstallPaths _paths;
    private readonly StateStore _state;

    public TransactionRunner(InstallPaths paths, StateStore state)
    {
        _paths = paths;
        _state = state;
    }

    /// <summary>Invoked at labelled points during commit; throw from it to simulate a crash.</summary>
    public Action<string>? CrashHook { get; set; }

    public async Task CommitAsync(TransactionJournal journal, CancellationToken ct = default)
    {
        var journalFile = _paths.TxnJournalFile(journal.Id);

        journal.Status = TxnStatus.Committing;
        Journals.Write(journalFile, journal);
        CrashHook?.Invoke("after-committing-status");

        foreach (var op in journal.Operations)
        {
            ApplyOp(op);
            CrashHook?.Invoke($"after-op:{op.TargetPath}");
        }

        CrashHook?.Invoke("before-state-write");

        // ── Commit point: a single atomic file write flips the installation to the new release. ──
        await _state.WriteAsync(journal.NewState, ct);
        CrashHook?.Invoke("after-state-write");

        journal.Status = TxnStatus.Committed;
        Journals.Write(journalFile, journal);

        CleanupTxn(journal.Id);
    }

    private void ApplyOp(TxnOp op)
    {
        switch (op.Kind)
        {
            case TxnOpKind.Replace:
                EnsureParent(op.TargetPath);
                if (File.Exists(op.TargetPath))
                    AtomicFile.Move(op.TargetPath, RequireBackup(op), overwrite: true);
                AtomicFile.Move(RequireStaged(op), op.TargetPath, overwrite: true);
                break;

            case TxnOpKind.Add:
                EnsureParent(op.TargetPath);
                AtomicFile.Move(RequireStaged(op), op.TargetPath, overwrite: true);
                break;

            case TxnOpKind.Delete:
                if (File.Exists(op.TargetPath))
                    AtomicFile.Move(op.TargetPath, RequireBackup(op), overwrite: true);
                break;

            case TxnOpKind.ReplaceTree:
                EnsureParent(op.TargetPath);
                // Back up the live bundle (if any) before swapping the new one in, so a crash between the
                // two renames is recoverable. Both moves are same-volume rename(2) calls → atomic.
                if (Directory.Exists(op.TargetPath))
                    AtomicFile.MoveDirectory(op.TargetPath, RequireBackup(op), overwrite: true);
                AtomicFile.MoveDirectory(RequireStaged(op), op.TargetPath, overwrite: true);
                break;
        }
    }

    private void CleanupTxn(string txnId)
    {
        var dir = _paths.TxnDir(txnId);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void EnsureParent(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);
    }

    private static string RequireStaged(TxnOp op)
        => op.StagedPath ?? throw new InvalidOperationException($"op for '{op.TargetPath}' has no staged content");

    private static string RequireBackup(TxnOp op)
    {
        if (op.BackupPath is null)
            throw new InvalidOperationException($"op for '{op.TargetPath}' has no backup path");
        Directory.CreateDirectory(Path.GetDirectoryName(op.BackupPath)!);
        return op.BackupPath;
    }
}
