namespace HangfireLockDemo;

public enum DemoMode
{
    /// <summary>
    /// Several workers, stock DisableConcurrentExecution with a 1 second wait, short AutomaticRetry.
    /// Overlap fails with DistributedLockTimeoutException and a retry then runs the body.
    /// </summary>
    Broken,

    /// <summary>
    /// Same filter as <see cref="Broken"/> but the wait is longer than the job. The exception disappears
    /// and the overlapping occurrence still runs. This is the timeout inflation that does not preserve skip semantics.
    /// </summary>
    InflatedTimeout,

    /// <summary>
    /// Same schedule and the same SQL application lock, acquired with a zero wait. Overlap is deleted
    /// instead of failed, and a later tick still runs after the holder finishes.
    /// </summary>
    Fixed,

    /// <summary>
    /// Same filters as <see cref="Broken"/> with one worker. The second occurrence stays queued, so the
    /// lock wait is never started. Shows a normal hold apart from a timeout failure.
    /// </summary>
    SingleWorker
}

public static class DemoModeParser
{
    public static bool TryParse(string text, out DemoMode mode)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "broken":
                mode = DemoMode.Broken;
                return true;
            case "inflated":
            case "inflated-timeout":
                mode = DemoMode.InflatedTimeout;
                return true;
            case "fixed":
                mode = DemoMode.Fixed;
                return true;
            case "single-worker":
                mode = DemoMode.SingleWorker;
                return true;
            default:
                mode = default;
                return false;
        }
    }
}
