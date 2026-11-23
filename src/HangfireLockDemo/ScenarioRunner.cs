using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;

namespace HangfireLockDemo;

public static class ScenarioRunner
{
    public static ScenarioReport Run(
        DemoMode mode,
        string serverConnectionString,
        string databaseName,
        TimeSpan? timeout = null)
    {
        SqlNames.RequireDatabaseName(databaseName);
        var budget = timeout ?? TimeSpan.FromSeconds(100);
        var (serverCount, workersPerServer) = Topology(mode);
        DatabaseAdmin.Recreate(serverConnectionString, databaseName);
        var databaseConnectionString = LocalConnections.ForDatabase(serverConnectionString, databaseName);
        LedgerStore.EnsureSchema(databaseConnectionString);
        LedgerRuntime.ConnectionString = databaseConnectionString;

        using var restoreRetry = GlobalRetryScope.RemoveBuiltInRetry();
        if (!restoreRetry.Removed)
        {
            throw new InvalidOperationException("The built-in AutomaticRetry filter was not removed, so retry delay is not controlled by the lab.");
        }
        var options = new SqlServerStorageOptions
        {
            SchemaName = DemoTiming.SchemaName,
            PrepareSchemaIfNecessary = true,
            QueuePollInterval = TimeSpan.Zero,
            SlidingInvisibilityTimeout = DemoTiming.SlidingInvisibilityTimeout,
            UseRecommendedIsolationLevel = true,
            TryAutoDetectSchemaDependentOptions = true
        };

        var storage = new SqlServerStorage(databaseConnectionString, options);
        JobStorage.Current = storage;
        using (storage.GetConnection())
        {
            // Touch the storage so schema installation finishes before the scheduler runs.
        }

        var probe = new LockProbeEvidence();
        var trace = new List<string>();
        var servers = new List<BackgroundJobServer>(serverCount);
        try
        {
            for (var index = 0; index < serverCount; index++)
            {
                var serverOptions = new BackgroundJobServerOptions
                {
                    ServerName = $"lock-demo-{mode.ToString().ToLowerInvariant()}-{index + 1}",
                    WorkerCount = workersPerServer,
                    Queues = ["default"],
                    SchedulePollingInterval = DemoTiming.SchedulePollingInterval,
                    ShutdownTimeout = TimeSpan.FromSeconds(15),
                    StopTimeout = TimeSpan.FromSeconds(5),
                    HeartbeatInterval = TimeSpan.FromSeconds(15),
                    ServerCheckInterval = TimeSpan.FromSeconds(15),
                    ServerTimeout = TimeSpan.FromMinutes(5)
                };
                servers.Add(new BackgroundJobServer(serverOptions, storage));
            }

            var manager = new RecurringJobManager(storage);
            manager.AddOrUpdate(
                DemoTiming.RecurringJobId,
                JobFor(mode),
                DemoTiming.Cron,
                new RecurringJobOptions
                {
                    TimeZone = TimeZoneInfo.Utc,
                    MisfireHandling = MisfireHandlingMode.Relaxed
                });

            trace.Add($"{DateTime.UtcNow:O} servers started, recurring job {DemoTiming.RecurringJobId} cron '{DemoTiming.Cron}'");
            var deadline = DateTime.UtcNow + budget;
            ScenarioReport? last = null;
            while (DateTime.UtcNow < deadline)
            {
                ObserveLocks(databaseConnectionString, probe, trace);
                var (work, states) = HangfireSnapshot.Read(databaseConnectionString);
                var failures = HangfireSnapshot.ReadLockFailures(states);
                last = Build(mode, databaseName, serverCount, workersPerServer, options, restoreRetry.Removed, work, states, failures, probe, trace, timedOut: false);
                var problems = ScenarioExpectations.Evaluate(last);
                if (problems.Count == 0)
                {
                    trace.Add($"{DateTime.UtcNow:O} expectations met");
                    return last;
                }

                Thread.Sleep(250);
            }

            trace.Add($"{DateTime.UtcNow:O} timed out");
            var finalWork = last?.WorkLog ?? [];
            var finalStates = last?.States ?? [];
            try
            {
                var (work, states) = HangfireSnapshot.Read(databaseConnectionString);
                finalWork = work;
                finalStates = states;
            }
            catch (SqlException ex)
            {
                trace.Add("final read failed: " + ex.Message);
            }

            return Build(
                mode,
                databaseName,
                serverCount,
                workersPerServer,
                options,
                restoreRetry.Removed,
                finalWork,
                finalStates,
                HangfireSnapshot.ReadLockFailures(finalStates),
                probe,
                trace,
                timedOut: true);
        }
        finally
        {
            foreach (var server in servers)
            {
                server.Dispose();
            }

            LedgerRuntime.ConnectionString = "";
            SqlConnection.ClearAllPools();
        }
    }

    private static void ObserveLocks(string connectionString, LockProbeEvidence probe, List<string> trace)
    {
        if (!HangfireSnapshot.HasOpenWork(connectionString))
        {
            return;
        }

        probe.SawOpenWork = true;
        var probeConnectionString = new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
        using var connection = new SqlConnection(probeConnectionString);
        connection.Open();

        if (!probe.ExecutionLockDeniedWhileOpen || !probe.ExecutionLockGrantedWhileOpen)
        {
            var code = LockProbe.TryExclusive(connection, DemoTiming.ExecutionLockResource);
            probe.ExecutionLockReturnCode = code;
            var stillOpen = HangfireSnapshot.HasOpenWork(connectionString);
            if (stillOpen && code < 0)
            {
                probe.ExecutionLockDeniedWhileOpen = true;
            }

            if (stillOpen && code >= 0)
            {
                probe.ExecutionLockGrantedWhileOpen = true;
                trace.Add($"{DateTime.UtcNow:O} execution lock unexpectedly granted ({code}) while work was open");
            }
        }

        TryAcquireSchedulerLock(connection, connectionString, DemoTiming.RecurringBatchLockResource, probe, trace, static (evidence, value) => evidence.SchedulerBatchLockAcquiredWhileOpen = value, static evidence => evidence.SchedulerBatchLockAcquiredWhileOpen);
        TryAcquireSchedulerLock(connection, connectionString, DemoTiming.RecurringJobLockResource, probe, trace, static (evidence, value) => evidence.RecurringJobLockAcquiredWhileOpen = value, static evidence => evidence.RecurringJobLockAcquiredWhileOpen);
        TryAcquireSchedulerLock(connection, connectionString, DemoTiming.SchedulePollerLockResource, probe, trace, static (evidence, value) => evidence.SchedulePollerLockAcquiredWhileOpen = value, static evidence => evidence.SchedulePollerLockAcquiredWhileOpen);

        if (probe.ApplicationLockGrantCount is null && probe.DmvError is null)
        {
            probe.ApplicationLockGrantCount = LockProbe.TryCountApplicationGrants(connection, out var error);
            probe.DmvError = error;
        }
    }

    private static void TryAcquireSchedulerLock(
        SqlConnection connection,
        string workConnectionString,
        string resource,
        LockProbeEvidence probe,
        List<string> trace,
        Action<LockProbeEvidence, bool> set,
        Func<LockProbeEvidence, bool> get)
    {
        if (get(probe))
        {
            return;
        }

        var code = LockProbe.TryExclusive(connection, resource);
        if (code >= 0 && HangfireSnapshot.HasOpenWork(workConnectionString))
        {
            set(probe, true);
            trace.Add($"{DateTime.UtcNow:O} acquired scheduler lock '{resource}' while work was open (sp_getapplock={code})");
        }
    }

    private static ScenarioReport Build(
        DemoMode mode,
        string databaseName,
        int serverCount,
        int workersPerServer,
        SqlServerStorageOptions options,
        bool globalRetryRemoved,
        IReadOnlyList<WorkLogRow> work,
        IReadOnlyList<StateRow> states,
        IReadOnlyList<LockFailureView> failures,
        LockProbeEvidence probe,
        List<string> trace,
        bool timedOut)
    {
        return new ScenarioReport
        {
            Mode = mode,
            DatabaseName = databaseName,
            ServerCount = serverCount,
            WorkersPerServer = workersPerServer,
            QueuePollInterval = options.QueuePollInterval,
            SlidingInvisibilityTimeout = options.SlidingInvisibilityTimeout,
            DisableGlobalLocks = options.DisableGlobalLocks,
            UseFineGrainedLocks = options.UseFineGrainedLocks,
            UseRecommendedIsolationLevel = options.UseRecommendedIsolationLevel,
            GlobalRetryRemoved = globalRetryRemoved,
            WorkLog = work,
            States = states,
            LockFailures = failures,
            Probe = probe,
            Trace = trace.ToList(),
            TimedOut = timedOut
        };
    }

    private static (int Servers, int Workers) Topology(DemoMode mode)
    {
        return mode == DemoMode.SingleWorker ? (1, 1) : (2, 2);
    }

    private static Job JobFor(DemoMode mode)
    {
        return mode switch
        {
            DemoMode.Broken => Job.FromExpression<LedgerJobs>(job => job.PostBroken(null!)),
            DemoMode.SingleWorker => Job.FromExpression<LedgerJobs>(job => job.PostBroken(null!)),
            DemoMode.InflatedTimeout => Job.FromExpression<LedgerJobs>(job => job.PostInflated(null!)),
            DemoMode.Fixed => Job.FromExpression<LedgerJobs>(job => job.PostFixed(null!)),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown mode.")
        };
    }

    private sealed class GlobalRetryScope : IDisposable
    {
        private readonly List<object> _removed = [];
        public bool Removed { get; private set; }

        public static GlobalRetryScope RemoveBuiltInRetry()
        {
            var scope = new GlobalRetryScope();
            foreach (var filter in GlobalJobFilters.Filters)
            {
                if (filter.Instance is AutomaticRetryAttribute)
                {
                    scope._removed.Add(filter.Instance);
                }
            }

            foreach (var instance in scope._removed)
            {
                GlobalJobFilters.Filters.Remove(instance);
            }

            scope.Removed = scope._removed.Count > 0
                && GlobalJobFilters.Filters.All(filter => filter.Instance is not AutomaticRetryAttribute);
            return scope;
        }

        public void Dispose()
        {
            foreach (var instance in _removed)
            {
                if (GlobalJobFilters.Filters.All(filter => !ReferenceEquals(filter.Instance, instance)))
                {
                    GlobalJobFilters.Filters.Add(instance);
                }
            }
        }
    }
}
