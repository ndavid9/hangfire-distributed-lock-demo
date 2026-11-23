namespace HangfireLockDemo;

public static class DemoTiming
{
    /// <summary>
    /// Six-field cron (seconds included). Hangfire's recurring scheduler only honors this when
    /// <see cref="SchedulePollingInterval"/> is shorter than the default 15 seconds.
    /// A one-minute production cron hits the same overlap once the job runs longer than its interval.
    /// </summary>
    public const string Cron = "*/2 * * * * *";

    public static readonly TimeSpan WorkHold = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan SchedulePollingInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Wait used by the stock attribute in the broken mode. Shorter than <see cref="WorkHold"/>.
    /// </summary>
    public const int BrokenLockWaitSeconds = 1;

    /// <summary>
    /// Longer than <see cref="WorkHold"/>, so the waiter acquires the lock when the holder finishes
    /// and then runs the body. Not the fix.
    /// </summary>
    public const int InflatedLockWaitSeconds = 45;

    public static readonly int[] RetryDelaysSeconds = [1];

    public const int RetryAttempts = 8;

    /// <summary>
    /// Hangfire.SqlServer 1.8.25 has no storage-level distributed lock timeout.
    /// Scheduler waits are hard-coded to one minute. This invisibility window is unrelated and
    /// stays at the product default in every mode.
    /// </summary>
    public static readonly TimeSpan SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5);

    public const string SchemaName = "HangFire";
    public const string LockResource = "ledger-post";
    public const string RecurringJobId = "ledger-post";

    public static string ExecutionLockResource => $"{SchemaName}:{LockResource}";
    public static string RecurringBatchLockResource => $"{SchemaName}:recurring-jobs:lock";
    public static string RecurringJobLockResource => $"{SchemaName}:lock:recurring-job:{RecurringJobId}";
    public static string SchedulePollerLockResource => $"{SchemaName}:locks:schedulepoller";

    public const string ExpectedLockTimeoutMessage =
        "Timeout expired. The timeout elapsed prior to obtaining a distributed lock on the 'HangFire:ledger-post' resource.";

    public const string ExpectedExceptionType = "Hangfire.Storage.DistributedLockTimeoutException";
}
