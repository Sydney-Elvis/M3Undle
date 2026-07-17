namespace M3Undle.Web.Application;

/// <summary>
/// Ensures only one destructive database operation (backup, restore, encryption key rotation)
/// runs at a time. M3Undle is a single-process Docker deployment, so an in-process lock is
/// sufficient — no distributed locking is needed. Registered as a singleton; acquired from
/// scoped services that perform the actual work.
/// </summary>
public sealed class DestructiveOperationLock
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private string? _currentOperation;

    /// <summary>Name of the operation currently holding the lock, or null if free.</summary>
    public string? CurrentOperation => _currentOperation;

    /// <summary>
    /// Attempts to acquire the lock without waiting. Returns false immediately if another
    /// destructive operation already holds it — callers should surface a conflict rather than
    /// queue, since these operations should never silently stack up behind one another.
    /// </summary>
    public bool TryAcquire(string operationName, out IDisposable? handle)
    {
        if (!_semaphore.Wait(0))
        {
            handle = null;
            return false;
        }

        _currentOperation = operationName;
        handle = new Releaser(this);
        return true;
    }

    private sealed class Releaser(DestructiveOperationLock owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            owner._currentOperation = null;
            owner._semaphore.Release();
        }
    }
}
