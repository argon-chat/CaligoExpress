namespace CaligoExpress.State;

using CaligoExpress.Fs;
using CaligoExpress.Manifests;

/// <summary>
/// Reads/writes the local install state. Writes are atomic and keep a <c>.bak</c>; reads fall back to
/// the backup and finally to a fresh empty state if both are unreadable, so a torn write never bricks
/// the installation (worst case the next run treats it as a repair/fresh install).
/// </summary>
public sealed class StateStore
{
    private readonly InstallPaths _paths;

    public StateStore(InstallPaths paths) => _paths = paths;

    public LocalInstallState Read()
    {
        if (TryRead(_paths.StateFile, out var state))
            return state!;
        if (TryRead(_paths.StateBackupFile, out var backup))
            return backup!;
        return new LocalInstallState();
    }

    public async Task WriteAsync(LocalInstallState state, CancellationToken ct = default)
    {
        _paths.EnsureControlDirs();
        await AtomicFile.WriteAllTextAsync(_paths.StateFile, CaligoJson.SerializePretty(state), keepBackup: true, ct);
    }

    private static bool TryRead(string path, out LocalInstallState? state)
    {
        state = null;
        try
        {
            if (!File.Exists(path))
                return false;
            state = CaligoJson.Deserialize<LocalInstallState>(File.ReadAllText(path));
            return state is not null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
