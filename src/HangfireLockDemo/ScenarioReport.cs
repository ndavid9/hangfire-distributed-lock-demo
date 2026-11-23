using System.Text;

namespace HangfireLockDemo;

public sealed record WorkLogRow(int Id, string HangfireJobId, DateTime StartedAt, DateTime? FinishedAt);

public sealed record StateRow(
    string JobId,
    DateTime JobCreatedAt,
    string Name,
    string? Reason,
    string? Data,
    DateTime StateCreatedAt);

public sealed record LockFailureView(
    string JobId,
    string ExceptionType,
    string ExceptionMessage,
    string ExceptionDetails);

public sealed class LockProbeEvidence
{
    public bool SawOpenWork { get; set; }
    public bool ExecutionLockDeniedWhileOpen { get; set; }
    public bool ExecutionLockGrantedWhileOpen { get; set; }
    public int? ExecutionLockReturnCode { get; set; }
    public bool SchedulerBatchLockAcquiredWhileOpen { get; set; }
    public bool RecurringJobLockAcquiredWhileOpen { get; set; }
    public bool SchedulePollerLockAcquiredWhileOpen { get; set; }
    public int? ApplicationLockGrantCount { get; set; }
    public string? DmvError { get; set; }
}

public sealed class ScenarioReport
{
    public required DemoMode Mode { get; init; }
    public required string DatabaseName { get; init; }
    public required int ServerCount { get; init; }
    public required int WorkersPerServer { get; init; }
    public required TimeSpan QueuePollInterval { get; init; }
    public required TimeSpan? SlidingInvisibilityTimeout { get; init; }
    public required bool DisableGlobalLocks { get; init; }
    public required bool UseFineGrainedLocks { get; init; }
    public required bool UseRecommendedIsolationLevel { get; init; }
    public required bool GlobalRetryRemoved { get; init; }
    public required IReadOnlyList<WorkLogRow> WorkLog { get; init; }
    public required IReadOnlyList<StateRow> States { get; init; }
    public required IReadOnlyList<LockFailureView> LockFailures { get; init; }
    public required LockProbeEvidence Probe { get; init; }
    public required IReadOnlyList<string> Trace { get; init; }
    public required bool TimedOut { get; init; }

    public string Format()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"mode={Mode} database={DatabaseName} servers={ServerCount} workersPerServer={WorkersPerServer}");
        builder.AppendLine(
            $"queuePoll={QueuePollInterval} slidingInvisibility={SlidingInvisibilityTimeout} disableGlobalLocks={DisableGlobalLocks} fineGrainedLocks={UseFineGrainedLocks} recommendedIsolation={UseRecommendedIsolationLevel} globalRetryRemoved={GlobalRetryRemoved} timedOut={TimedOut}");
        builder.AppendLine("work:");
        foreach (var row in WorkLog)
        {
            builder.AppendLine($"  id={row.Id} job={row.HangfireJobId} start={row.StartedAt:O} finish={(row.FinishedAt.HasValue ? row.FinishedAt.Value.ToString("O") : "null")}");
        }

        builder.AppendLine("states:");
        var printedFailedData = false;
        foreach (var state in States)
        {
            builder.AppendLine($"  job={state.JobId} jobCreated={state.JobCreatedAt:O} state={state.Name} at={state.StateCreatedAt:O} reason={state.Reason}");
            if (!printedFailedData && state.Name == "Failed" && !string.IsNullOrEmpty(state.Data))
            {
                builder.AppendLine("  failedData:");
                builder.AppendLine(state.Data);
                printedFailedData = true;
            }
        }

        builder.AppendLine("lockFailures:");
        var firstStack = true;
        foreach (var failure in LockFailures)
        {
            builder.AppendLine($"  job={failure.JobId} type={failure.ExceptionType}");
            builder.AppendLine($"  message={failure.ExceptionMessage}");
            if (firstStack)
            {
                builder.AppendLine("  details:");
                builder.AppendLine(failure.ExceptionDetails);
                firstStack = false;
            }
        }

        builder.AppendLine(
            $"probe sawOpen={Probe.SawOpenWork} executionDenied={Probe.ExecutionLockDeniedWhileOpen} executionGranted={Probe.ExecutionLockGrantedWhileOpen} executionCode={Probe.ExecutionLockReturnCode} batchLock={Probe.SchedulerBatchLockAcquiredWhileOpen} recurringJobLock={Probe.RecurringJobLockAcquiredWhileOpen} schedulePoller={Probe.SchedulePollerLockAcquiredWhileOpen} applicationGrants={Probe.ApplicationLockGrantCount} dmvError={Probe.DmvError}");
        builder.AppendLine("trace:");
        foreach (var line in Trace)
        {
            builder.AppendLine($"  {line}");
        }

        return builder.ToString();
    }
}
