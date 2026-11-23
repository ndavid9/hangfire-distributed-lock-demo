using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;

namespace HangfireLockDemo;

/// <summary>
/// Mutual exclusion through the same storage lock as <see cref="Hangfire.DisableConcurrentExecutionAttribute"/>,
/// without turning contention into a failed job.
///
/// Hangfire.SqlServer 1.8.25 acquires that lock with sp_getapplock (exclusive, session owner) and slices each
/// wait to at most one second. A non-zero timeout blocks the worker and its dedicated SQL connection until the
/// lock is granted or the timeout elapses. On timeout the stock attribute throws
/// <see cref="DistributedLockTimeoutException"/>. The worker stores a Failed state, and AutomaticRetry schedules
/// another attempt, so the occurrence runs once the holder releases the lock.
///
/// A zero timeout makes sp_getapplock return immediately. When the lock is already held, this filter sets
/// <see cref="PerformingContext.Canceled"/>. Worker.PerformJob in Hangfire 1.8.25 turns that into
/// DeletedState ("Canceled by filter '...'") and does not call AutomaticRetry. The holder keeps the lock until
/// OnPerformed, so a second server still cannot enter the method. A later tick, after release, acquires the lock
/// and runs normally.
///
/// Tradeoff: the skipped tick does not run later. Serialize-behind requires a wait, which is the inflated-timeout
/// mode, and that wait occupies a worker plus a SQL connection.
/// </summary>
public sealed class SkipConcurrentExecutionAttribute : JobFilterAttribute, IServerFilter
{
    private readonly string _resource;

    public SkipConcurrentExecutionAttribute(string resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            throw new ArgumentException("A lock resource name is required.", nameof(resource));
        }

        // DisableConcurrentExecution lowercases an explicit resource. Match it so both modes lock the same key.
        _resource = resource.ToLowerInvariant();
    }

    public void OnPerforming(PerformingContext context)
    {
        try
        {
            var handle = context.Connection.AcquireDistributedLock(_resource, TimeSpan.Zero);
            context.Items["DistributedLock"] = handle;
        }
        catch (DistributedLockTimeoutException)
        {
            // Do not throw. A thrown lock timeout becomes a failed attempt and is retried.
            context.Canceled = true;
        }
    }

    public void OnPerformed(PerformedContext context)
    {
        if (context.Items.TryGetValue("DistributedLock", out var value) && value is IDisposable handle)
        {
            handle.Dispose();
        }
    }
}
