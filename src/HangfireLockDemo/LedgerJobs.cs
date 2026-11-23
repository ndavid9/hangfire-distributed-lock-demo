using Hangfire;
using Hangfire.Server;

namespace HangfireLockDemo;

public static class LedgerRuntime
{
    public static string ConnectionString { get; set; } = "";
}

/// <summary>
/// One body, three entry points. The only intended difference between the overlapping modes is the lock filter.
/// AutomaticRetry is the same so a lock timeout is not "fixed" by disabling retries for real exceptions only
/// in one mode. The global AutomaticRetry (15 second minimum delay) is removed for the run so this delay is
/// the one that actually schedules the retry.
/// </summary>
public sealed class LedgerJobs
{
    [AutomaticRetry(Attempts = DemoTiming.RetryAttempts, DelaysInSeconds = new[] { 1 })]
    [DisableConcurrentExecution(DemoTiming.LockResource, DemoTiming.BrokenLockWaitSeconds)]
    public void PostBroken(PerformContext context) => Run(context);

    [AutomaticRetry(Attempts = DemoTiming.RetryAttempts, DelaysInSeconds = new[] { 1 })]
    [DisableConcurrentExecution(DemoTiming.LockResource, DemoTiming.InflatedLockWaitSeconds)]
    public void PostInflated(PerformContext context) => Run(context);

    [AutomaticRetry(Attempts = DemoTiming.RetryAttempts, DelaysInSeconds = new[] { 1 })]
    [SkipConcurrentExecution(DemoTiming.LockResource)]
    public void PostFixed(PerformContext context) => Run(context);

    public static void Run(PerformContext context)
    {
        var connectionString = LedgerRuntime.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("LedgerRuntime.ConnectionString was not set.");
        }

        var jobId = context.BackgroundJob.Id;
        LedgerStore.MarkStarted(connectionString, jobId);
        try
        {
            var until = DateTime.UtcNow + DemoTiming.WorkHold;
            while (DateTime.UtcNow < until)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(50);
            }
        }
        finally
        {
            LedgerStore.MarkFinished(connectionString, jobId);
        }
    }
}
