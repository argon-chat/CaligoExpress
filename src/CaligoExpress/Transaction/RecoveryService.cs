namespace CaligoExpress.Transaction;

using CaligoExpress.Fs;
using CaligoExpress.State;
using Serilog;

public sealed record RecoveryReport(int Completed, int RolledBack, int Discarded)
{
    public bool DidAnything => Completed + RolledBack + Discarded > 0;
}

/// <summary>
/// Run at the very start of every bootstrap, before any new work. For each leftover transaction it
/// decides — by comparing the journal's target release against the persisted state — whether the
/// commit happened. If it did, it finishes cleanup; if not, it rolls the live tree back to the old
/// content using the backups. The decision is filesystem-driven and idempotent, so re-running after a
/// second crash is safe.
/// </summary>
public sealed class RecoveryService
{
    private readonly InstallPaths _paths;
    private readonly StateStore _state;
    private readonly ILogger _log;

    public RecoveryService(InstallPaths paths, StateStore state, ILogger? log = null)
    {
        _paths = paths;
        _state = state;
        _log = log ?? Serilog.Core.Logger.None;
    }

    public RecoveryReport RecoverPending()
    {
        if (!Directory.Exists(_paths.JournalDir))
            return new RecoveryReport(0, 0, 0);

        int completed = 0, rolledBack = 0, discarded = 0;

        foreach (var txnDir in Directory.GetDirectories(_paths.JournalDir))
        {
            var journalFile = Path.Combine(txnDir, "txn.json");
            if (!File.Exists(journalFile))
            {
                // Half-created transaction with no intent recorded: nothing was applied to the live
                // tree yet, so it is safe to discard.
                SafeDeleteDir(txnDir);
                discarded++;
                continue;
            }

            TransactionJournal journal;
            try
            {
                journal = Journals.Read(journalFile);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Unreadable journal {Dir}; rolling back conservatively", txnDir);
                journal = new TransactionJournal { Id = Path.GetFileName(txnDir) };
                SafeDeleteDir(txnDir);
                discarded++;
                continue;
            }

            var current = _state.Read();
            var committed = journal.TargetReleaseSeq == current.ReleaseSeq &&
                            string.Equals(journal.TargetReleaseId, current.ReleaseId, StringComparison.Ordinal) &&
                            !string.IsNullOrEmpty(journal.TargetReleaseId);

            if (committed)
            {
                _log.Information("Recovery: transaction {Id} already committed; cleaning up", journal.Id);
                SafeDeleteDir(txnDir);
                completed++;
            }
            else
            {
                _log.Warning("Recovery: rolling back interrupted transaction {Id}", journal.Id);
                Rollback(journal);
                SafeDeleteDir(txnDir);
                rolledBack++;
            }
        }

        return new RecoveryReport(completed, rolledBack, discarded);
    }

    private static void Rollback(TransactionJournal journal)
    {
        // Reverse order so dependents are undone before dependencies.
        for (var i = journal.Operations.Count - 1; i >= 0; i--)
        {
            var op = journal.Operations[i];
            switch (op.Kind)
            {
                case TxnOpKind.Replace:
                case TxnOpKind.Delete:
                    // If we backed the old file up, the live file was (or was about to be) changed:
                    // restore the backup over whatever is there now.
                    if (op.BackupPath is not null && File.Exists(op.BackupPath))
                        AtomicFile.Move(op.BackupPath, op.TargetPath, overwrite: true);
                    break;

                case TxnOpKind.Add:
                    // The added file existed nowhere before. If the staged copy is gone, we already
                    // moved it into place, so remove it; otherwise nothing was applied.
                    if (op.StagedPath is not null && !File.Exists(op.StagedPath) && File.Exists(op.TargetPath))
                        AtomicFile.Delete(op.TargetPath);
                    break;

                case TxnOpKind.ReplaceTree:
                    if (op.BackupPath is not null && Directory.Exists(op.BackupPath))
                    {
                        // The old bundle was moved aside → restore it over whatever is there now.
                        if (Directory.Exists(op.TargetPath))
                            AtomicFile.DeleteDirectory(op.TargetPath);
                        AtomicFile.MoveDirectory(op.BackupPath, op.TargetPath, overwrite: false);
                    }
                    else if (op.StagedPath is not null && !Directory.Exists(op.StagedPath) && Directory.Exists(op.TargetPath))
                    {
                        // First install (no prior bundle to back up) that was already applied → remove it.
                        AtomicFile.DeleteDirectory(op.TargetPath);
                    }
                    break;
            }
        }
    }

    private static void SafeDeleteDir(string dir)
    {
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
}
