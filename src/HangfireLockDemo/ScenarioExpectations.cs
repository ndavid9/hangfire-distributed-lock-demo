namespace HangfireLockDemo;

public static class ScenarioExpectations
{
    public static IReadOnlyList<string> Evaluate(ScenarioReport report)
    {
        var problems = new List<string>();
        if (report.TimedOut)
        {
            problems.Add("The scenario timed out before every condition was met.");
        }

        if (!report.GlobalRetryRemoved)
        {
            problems.Add("The built-in global AutomaticRetry filter was still registered, so retry delay is not controlled by the lab.");
        }

        if (report.QueuePollInterval != TimeSpan.Zero)
        {
            problems.Add("QueuePollInterval was changed. Fetch polling is not the execution lock.");
        }

        if (report.SlidingInvisibilityTimeout != DemoTiming.SlidingInvisibilityTimeout)
        {
            problems.Add("SlidingInvisibilityTimeout was changed. That window is not the execution lock.");
        }

        if (!report.UseRecommendedIsolationLevel)
        {
            problems.Add("UseRecommendedIsolationLevel was turned off.");
        }

        // Fresh schema is version 9. TryAutoDetectSchemaDependentOptions then sets DisableGlobalLocks
        // for schema >= 6. That removes the legacy transaction applock around entire Hash/Set/List
        // writes. It does not disable AcquireDistributedLock. Forcing it back on is not the fix.
        if (!report.DisableGlobalLocks)
        {
            problems.Add("DisableGlobalLocks was off. Auto-detect should enable it on schema >= 6; the execution lock does not use this switch.");
        }

        if (report.UseFineGrainedLocks)
        {
            problems.Add("UseFineGrainedLocks was enabled. That adds per-key transaction applocks and is not the change under test.");
        }

        RequireProbe(report, problems);
        RequireNoOverlap(report, problems);

        switch (report.Mode)
        {
            case DemoMode.Broken:
                RequireBroken(report, problems);
                break;
            case DemoMode.InflatedTimeout:
                RequireInflated(report, problems);
                break;
            case DemoMode.Fixed:
                RequireFixed(report, problems);
                break;
            case DemoMode.SingleWorker:
                RequireSingleWorker(report, problems);
                break;
            default:
                problems.Add($"Unhandled mode {report.Mode}.");
                break;
        }

        return problems;
    }

    private static void RequireProbe(ScenarioReport report, List<string> problems)
    {
        var probe = report.Probe;
        if (!probe.SawOpenWork)
        {
            problems.Add("No in-flight work row was observed, so the applock probe never ran.");
        }

        if (!probe.ExecutionLockDeniedWhileOpen)
        {
            problems.Add($"sp_getapplock on '{DemoTiming.ExecutionLockResource}' was not denied while work was in flight. The execution lock was not held.");
        }

        if (probe.ExecutionLockGrantedWhileOpen)
        {
            problems.Add("sp_getapplock granted the execution resource while a work row was still open. Another session was able to take the lock.");
        }

        if (!probe.SchedulerBatchLockAcquiredWhileOpen)
        {
            problems.Add($"Could not acquire '{DemoTiming.RecurringBatchLockResource}' while work was in flight. The recurring scheduler lock was held for the whole job, which is not the execution-lock failure.");
        }
    }

    private static void RequireNoOverlap(ScenarioReport report, List<string> problems)
    {
        var finished = report.WorkLog.Where(row => row.FinishedAt.HasValue).ToList();
        for (var i = 0; i < finished.Count; i++)
        {
            for (var j = i + 1; j < finished.Count; j++)
            {
                var left = finished[i];
                var right = finished[j];
                var leftEnd = left.FinishedAt!.Value;
                var rightEnd = right.FinishedAt!.Value;
                if (left.StartedAt < rightEnd && right.StartedAt < leftEnd)
                {
                    problems.Add($"Work rows {left.Id} and {right.Id} overlap. The SQL lock did not enforce exclusion.");
                }
            }
        }
    }

    private static void RequireBroken(ScenarioReport report, List<string> problems)
    {
        var match = report.LockFailures.FirstOrDefault(failure =>
            failure.ExceptionType == DemoTiming.ExpectedExceptionType
            && failure.ExceptionMessage == DemoTiming.ExpectedLockTimeoutMessage
            && failure.ExceptionDetails.Contains("DisableConcurrentExecutionAttribute.OnPerforming", StringComparison.Ordinal)
            && failure.ExceptionDetails.Contains("SqlServerDistributedLock.Acquire", StringComparison.Ordinal));

        if (match is null)
        {
            problems.Add("No Failed state contained the SqlServer applock timeout from DisableConcurrentExecution for resource HangFire:ledger-post.");
            return;
        }

        if (report.LockFailures.Any(failure => failure.ExceptionMessage.Contains("recurring-jobs:lock", StringComparison.Ordinal)
            || failure.ExceptionMessage.Contains("locks:schedulepoller", StringComparison.Ordinal)
            || failure.ExceptionMessage.Contains("lock:recurring-job:", StringComparison.Ordinal)))
        {
            problems.Add("A job failed on a scheduler lock. Those timeouts are a different resource from the execution lock.");
        }

        var retried = report.WorkLog.Any(row => row.FinishedAt.HasValue && row.HangfireJobId == match.JobId);
        if (!retried)
        {
            problems.Add($"Job {match.JobId} failed with the lock timeout but never finished the method. AutomaticRetry did not re-enter the body.");
        }
    }

    private static void RequireInflated(ScenarioReport report, List<string> problems)
    {
        if (report.LockFailures.Count > 0)
        {
            problems.Add("Inflated timeout still recorded a distributed lock timeout. The wait was not long enough, or a different lock failed.");
        }

        if (report.States.Any(state => state.Name == "Deleted" && (state.Reason?.Contains("SkipConcurrentExecutionAttribute", StringComparison.Ordinal) ?? false)))
        {
            problems.Add("Inflated mode deleted a job via the skip filter. That filter should not be installed.");
        }

        var first = report.WorkLog.Where(row => row.FinishedAt.HasValue).OrderBy(row => row.StartedAt).FirstOrDefault();
        if (first is null)
        {
            problems.Add("Inflated mode did not finish any work.");
            return;
        }

        var overlapRan = report.States
            .Where(state => state.JobCreatedAt < first.FinishedAt!.Value && state.JobId != first.HangfireJobId)
            .Select(state => state.JobId)
            .Distinct()
            .Any(jobId => report.WorkLog.Any(row => row.HangfireJobId == jobId && row.FinishedAt.HasValue));

        if (!overlapRan)
        {
            problems.Add("No occurrence created while the first execution was still running later executed the body. Inflating the timeout did not serialize an overlap, or no overlap was scheduled.");
        }
    }

    private static void RequireFixed(ScenarioReport report, List<string> problems)
    {
        if (report.LockFailures.Count > 0)
        {
            problems.Add("Fixed mode still stored DistributedLockTimeoutException. The skip filter threw or the stock attribute was still applied.");
        }

        var skipped = report.States
            .Where(state => state.Name == "Deleted" && (state.Reason?.Contains("SkipConcurrentExecutionAttribute", StringComparison.Ordinal) ?? false))
            .Select(state => state.JobId)
            .Distinct()
            .ToList();

        if (skipped.Count == 0)
        {
            problems.Add("No overlapping occurrence was deleted by SkipConcurrentExecutionAttribute.");
        }

        foreach (var jobId in skipped)
        {
            if (report.WorkLog.Any(row => row.HangfireJobId == jobId))
            {
                problems.Add($"Skipped job {jobId} still wrote a work row.");
            }
        }

        var finished = report.WorkLog.Where(row => row.FinishedAt.HasValue).OrderBy(row => row.StartedAt).ToList();
        if (finished.Count < 2)
        {
            problems.Add("Fixed mode did not run a later occurrence after the holder finished. The schedule looks disabled.");
            return;
        }

        if (finished[1].StartedAt < finished[0].FinishedAt!.Value)
        {
            problems.Add("The second execution started before the first finished.");
        }
    }

    private static void RequireSingleWorker(ScenarioReport report, List<string> problems)
    {
        if (report.ServerCount != 1 || report.WorkersPerServer != 1)
        {
            problems.Add("Single-worker mode did not actually use one server and one worker.");
        }

        if (report.LockFailures.Count > 0)
        {
            problems.Add("A single worker recorded a lock timeout. The second occurrence should have stayed queued until the holder released the lock.");
        }

        var finished = report.WorkLog.Where(row => row.FinishedAt.HasValue).ToList();
        if (finished.Count < 2)
        {
            problems.Add("Single-worker mode did not complete two queued occurrences.");
        }
    }
}
