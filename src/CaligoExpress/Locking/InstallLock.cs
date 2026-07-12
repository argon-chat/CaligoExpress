namespace CaligoExpress.Locking;

using CaligoExpress.Abstractions;
using CaligoExpress.State;

/// <summary>
/// A cross-process lock scoped to one installation root, so two updater instances can never mutate the
/// same tree at once. Backed by an exclusively-opened lock file (<see cref="FileShare.None"/>), which —
/// unlike a named mutex — is not thread-affine and so survives the engine's async continuations safely.
/// The file is deleted on release; if a holder crashes, the OS closes its handle and the next acquirer
/// gets in.
/// </summary>
public sealed class InstallLock : IDisposable
{
    private readonly FileStream _stream;

    private InstallLock(FileStream stream) => _stream = stream;

    public static InstallLock Acquire(InstallPaths paths, TimeSpan timeout)
    {
        paths.EnsureControlDirs();
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            try
            {
                var stream = new FileStream(
                    paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);
                return new InstallLock(stream);
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new CaligoException("another updater is already running for this installation");
                Thread.Sleep(50);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
