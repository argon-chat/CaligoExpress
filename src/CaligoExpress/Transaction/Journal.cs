namespace CaligoExpress.Transaction;

using System.Text.Json;
using System.Text.Json.Serialization;
using CaligoExpress.Manifests;
using CaligoExpress.State;

public enum TxnOpKind
{
    /// <summary>Overwrite an existing installed file (old content is backed up first).</summary>
    Replace,

    /// <summary>Create a new installed file that did not exist before.</summary>
    Add,

    /// <summary>Remove an installed file (old content is backed up first).</summary>
    Delete,

    /// <summary>
    /// Replace a whole directory tree atomically — the macOS <c>.app</c> bundle swap. The staged tree
    /// (already fully materialized) is moved into place; any existing tree is moved to the backup first
    /// so a rollback restores it wholesale. Used by the whole-app (monolith) install path.
    /// </summary>
    ReplaceTree,
}

public enum TxnStatus
{
    Prepared,
    Committing,
    Committed,
}

/// <summary>A single planned filesystem mutation. Paths are absolute.</summary>
public sealed class TxnOp
{
    public TxnOpKind Kind { get; set; }
    public string TargetPath { get; set; } = "";

    /// <summary>Where the new content waits during prepare (Replace/Add).</summary>
    public string? StagedPath { get; set; }

    /// <summary>Where the old content is moved during commit so a rollback can restore it (Replace/Delete).</summary>
    public string? BackupPath { get; set; }

    public string? OldHash { get; set; }
    public string? NewHash { get; set; }
}

/// <summary>
/// The write-ahead journal for one in-place update transaction. Written once during prepare; the
/// commit point is the atomic write of <see cref="NewState"/>. Recovery decides commit-vs-rollback by
/// comparing this target against the persisted state, not by trusting a half-written status flag.
/// </summary>
public sealed class TransactionJournal
{
    public string Id { get; set; } = "";
    public TxnStatus Status { get; set; } = TxnStatus.Prepared;
    public long TargetReleaseSeq { get; set; }
    public string TargetReleaseId { get; set; } = "";
    public List<TxnOp> Operations { get; set; } = [];

    /// <summary>The state to persist at the commit point.</summary>
    public LocalInstallState NewState { get; set; } = new();
}

/// <summary>Reads/writes transaction journals atomically (small, infrequent — reflection JSON is fine).</summary>
public static class Journals
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Write(string journalFile, TransactionJournal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journalFile)!);
        var tmp = journalFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(journal, Options));
        if (File.Exists(journalFile))
            File.Replace(tmp, journalFile, null, ignoreMetadataErrors: true);
        else
            File.Move(tmp, journalFile);
    }

    public static TransactionJournal Read(string journalFile)
        => JsonSerializer.Deserialize<TransactionJournal>(File.ReadAllText(journalFile), Options)
           ?? throw new InvalidOperationException($"unreadable journal: {journalFile}");
}
