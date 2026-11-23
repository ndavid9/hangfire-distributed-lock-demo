# Hangfire SQL distributed-lock lab

A small .NET 10 console app that reproduces one **documented** Hangfire.SqlServer failure mode with a recurring job, proves it against a local SQL Server, and checks a fix. It does **not** diagnose an incident whose exception text, storage, and code were not provided. If your stack trace names a different resource or provider, use the recognition table below instead of copying this change.

## Assumption

The failure under test is:

`Hangfire.Storage.DistributedLockTimeoutException` from `DisableConcurrentExecutionAttribute.OnPerforming`, resource `HangFire:ledger-post`, raised by `Hangfire.SqlServer.SqlServerDistributedLock.Acquire` after `sp_getapplock` does not grant an exclusive **session** application lock.

That is the execution lock. It is not the recurring-job scheduler lock and not a connectivity failure. Packages are the current stable pair **Hangfire.Core 1.8.25** and **Hangfire.SqlServer 1.8.25** (verified SDK **10.0.403**). SQL Server is **2022 CU16** in Docker, bound to `127.0.0.1:14333` only. The `sa` password `LocalOnly_hf_Passw0rd` is a local demo default.

## What is going wrong

A recurring job is registered with a six-field cron (`*/2 * * * * *`) and `SchedulePollingInterval` of one second, only so the overlap happens quickly. The same shape appears in production when a job runs longer than its real interval.

Hangfire 1.8.25 uses three different locks before a method runs:

| Resource suffix | Who takes it | How long | If the wait exceeds the timeout |
| --- | --- | --- | --- |
| `recurring-jobs:lock` | `RecurringJobScheduler` batch poll | The scheduling pass, then released | Caught inside the scheduler. Another server is polling. Not a failed job. |
| `lock:recurring-job:{id}` | Per recurring job, around enqueue | The enqueue transaction | Scheduling or `AddOrUpdate` for that id did not run. The job method did not fail. |
| `locks:schedulepoller` | `DelayedJobScheduler` | The delayed-job pass | Caught inside the scheduler. Not a failed job. |
| The `DisableConcurrentExecution` resource (`ledger-post` here) | The worker, in `OnPerforming`, **before** the method | The whole method, on a dedicated SQL connection | **Failed job.** `AutomaticRetry` will run the method later. |

The full `sp_getapplock` resource is `{SchemaName}:{suffix}`. This lab sets the schema to `HangFire`, so the execution resource is `HangFire:ledger-post`.

`DisableConcurrentExecution(timeoutInSeconds)` is the wait for that execution lock, not a cap on the running job. Hangfire.SqlServer 1.8.25 has **no** `DistributedLockTimeout` storage option. The scheduler timeouts above are hard-coded to one minute inside `RecurringJobScheduler` and `DelayedJobScheduler`. `SlidingInvisibilityTimeout` (left at the default 5 minutes) is how long a fetched job stays invisible if the worker dies. It is not this lock.

`TryAutoDetectSchemaDependentOptions` is left on. A new database is schema version 9, so startup sets `DisableGlobalLocks` to true (the switch flips at schema 6). That skips the legacy **transaction** applock on the whole `Hash`, `Set`, or `List` key space (`HangFire:Hash:Lock` and its siblings) during storage writes. `UseFineGrainedLocks` stays false, so those writes do not take a replacement applock either. Session locks from `AcquireDistributedLock` — the scheduler locks and the execution lock — are unchanged. Turning global locks back on is not the repair, and this lab does not do it.

With two servers and two workers each, the next tick is **dequeued while the first tick still holds the lock**. `sp_getapplock` returns no grant. Hangfire 1.8.25 retries the procedure in slices of at most one second (the 1.6.15 starvation fix). Return codes `-1` (timeout), `-2` (canceled), and `-3` (deadlock victim) are not separate exception types: when the caller's wait is exhausted they all become `DistributedLockTimeoutException`. A dead connection is different: `SqlException` escapes `ExecuteNonQuery` and is not turned into that timeout.

The broken mode then does what the stock filters do. The worker records **Failed**. `AutomaticRetry` (here, a 1 second delay, after the built-in 15 second global delay is removed) runs the **same job id** once the lock is free. The body runs twice. The rows in `demo.WorkLog` do not overlap, because the lock still serializes them. Exclusion worked. Skip-if-still-running did not.

A single worker usually hides this. The second occurrence stays in the queue until the only worker finishes and releases the lock, so nobody calls `sp_getapplock` and loses. The `single-worker` mode checks that: the execution applock is denied to a probe session during the run, and no job fails with the timeout.

## What was changed

Only the lock filter. Schedule, queue poll, invisibility timeout, schema auto-detect, worker count (except the diagnostic mode), and `AutomaticRetry` stay the same.

Broken entry point, `LedgerJobs.PostBroken`:

```csharp
[AutomaticRetry(Attempts = 8, DelaysInSeconds = new[] { 1 })]
[DisableConcurrentExecution("ledger-post", 1)]
public void PostBroken(PerformContext context) => Run(context);
```

Fixed entry point, `LedgerJobs.PostFixed`, and `SkipConcurrentExecutionAttribute`:

```csharp
[AutomaticRetry(Attempts = 8, DelaysInSeconds = new[] { 1 })]
[SkipConcurrentExecution("ledger-post")]
public void PostFixed(PerformContext context) => Run(context);
```

The skip filter calls `AcquireDistributedLock(resource, TimeSpan.Zero)`. SqlClient issues one `sp_getapplock` with `@LockTimeout = 0`. If the lock is held, the filter catches `DistributedLockTimeoutException` and sets `PerformingContext.Canceled`. It does not throw. `Worker.PerformJob` in Hangfire 1.8.25 then writes **Deleted** with reason `Canceled by filter 'SkipConcurrentExecutionAttribute'` and does not consult `AutomaticRetry`. The holder still disposes the lock in `OnPerformed`. The next tick after release acquires the lock and runs the method.

`inflated-timeout` keeps the stock attribute and raises the wait to 45 seconds. The exception goes away. The overlapping job still enters the method as soon as the holder exits, and it occupied a worker and a dedicated SQL connection for the whole wait. That is the behavior you get from "just increase the timeout". It is measured so it is not mistaken for the fix.

## Tradeoff

Skipped ticks do not run later. Use that when an in-flight run makes the overlapping tick useless (a scan, a sync, a poll). If every tick must run as soon as the previous one finishes, you want a wait or a one-worker queue, and the timeout has to cover the remaining runtime. You pay a blocked worker and a checked-out SQL connection for that wait. Making the timeout very large, across many workers, is how people get back to connection-pool timeouts (`max pool size was reached`), which then look like lock failures.

The skip filter does not stop the scheduler from inserting the overlapping job. A worker still dequeues it and deletes it. Sergey Odinokov's `SkipWhenPreviousJobIsRunningAttribute` cancels at creation time with a `Running` hash flag under `lock:recurring-job:{id}`. That flag's read is not locked, and a hard crash can leave `Running=yes` until the in-flight job reaches a final state. This lab uses the execution applock instead, because that is the lock the failing stack already names, and SQL Server drops a session applock when the connection dies.

Limits of what was verified:

- One database, two `BackgroundJobServer` instances in one process. The lock is still `sp_getapplock` in SQL Server, not an in-process mutex. This is not a multi-host partition test.
- Sub-minute cron is only the clock. It is not the repair. Do not lower `SchedulePollingInterval` to "fix" a lock timeout.
- `-1`, `-2`, and `-3` from `sp_getapplock` look identical in this Hangfire version.
- `sp_getapplock` resource names are 255 characters. A long `{Schema}:{Type}.{Method}` can collide or fail before this filter matters. Pass a short explicit resource.
- The sample job writes its ledger on a second connection. A crash after the insert and before `FinishedAt` can leave a partial row. The applock does not enlist that insert.
- PostgreSQL and Redis storages keep lock rows or tokens with their own expiry. Restarting the process does not clear them the way a dead SQL session does. This project does not exercise those providers.
- No dashboard, auth, or hosted deployment is included.

## How to tell which lock failed

Read the resource inside the message and the first Hangfire frame in the stack.

- Message resource ends with `ledger-post` (or your attribute resource) and the stack contains `DisableConcurrentExecutionAttribute.OnPerforming` and `SqlServerDistributedLock.Acquire`: execution / concurrency lock. This lab.
- Stack contains `RecurringJobScheduler` or `UseConnectionDistributedLock` and the resource ends with `recurring-jobs:lock`: batch scheduler lock. Usually debug-logged and swallowed.
- Stack contains `AcquireDistributedRecurringJobLock` or `RecurringJobManager.AddOrUpdate` and the resource contains `lock:recurring-job:`: that recurring id could not be scheduled or updated.
- Stack contains `DelayedJobScheduler` and the resource ends with `locks:schedulepoller`: delayed-job poller. Usually swallowed.
- `SqlException`, especially a connection timeout or "obtaining a connection from the pool": the client never got a session, or the server rejected the command. Not an applock result code.
- `SqlServerDistributedLockException` mentioning error `-999`: `sp_getapplock` rejected the arguments (often the resource). Not a wait timeout.
- Provider types under `Hangfire.PostgreSql` or `Hangfire.Pro.Redis`: different lock implementation. Do not apply this SQL-specific filter and expect the same release behavior.

`sys.dm_tran_locks` shows `resource_type = 'APPLICATION'` while a lock is held. `resource_description` is a hash, not the Hangfire name. The name is in the exception text and in `HangFire.State.Data`.

## What to check in your code

1. The exception's resource and stack, against the table above. Confirm the storage is Hangfire.SqlServer and not an in-memory stand-in.
2. `DisableConcurrentExecution` timeout versus the job's runtime and the schedule interval. A timeout of `0` still throws; it does not skip.
3. `AutomaticRetry` on that method. A lock failure is a normal failure unless `ExceptOn` includes `DistributedLockTimeoutException` or the filter stops throwing. `ExceptOn` avoids the retry but still records Failed.
4. Worker count and server count. One worker often will not time out on a single-job lock.
5. Whether several methods share one explicit resource, or a long default `{Type}.{Method}` resource.
6. Pool exhaustion and SQL connectivity separately from applock timeouts. Raising the lock wait makes pool exhaustion worse.
7. For non-SQL storage, whether a lock row survives process death. That is a different bug.

## Run

Requires the .NET 10 SDK and Docker. `scripts/up-sql.sh` uses `docker compose` when that plugin exists, and otherwise runs the same image with `docker run`. This environment's Docker 29.1.3 package had no compose plugin, so the verified start was `docker run`. From the repo root:

```bash
bash scripts/up-sql.sh
dotnet test HangfireLockDemo.sln
dotnet run --project src/HangfireLockDemo -- broken
dotnet run --project src/HangfireLockDemo -- inflated-timeout
dotnet run --project src/HangfireLockDemo -- fixed
dotnet run --project src/HangfireLockDemo -- single-worker
```

Console runs recreate `HangfireLockDemo_broken`, `HangfireLockDemo_inflated`, `HangfireLockDemo_fixed`, or `HangfireLockDemo_single` and leave them so you can run `scripts/inspect-states.sql`. Tests use a throwaway database and drop it. Set `HANGFIRE_SQL` to point at a different local instance. Do not point it at a shared server.

The test project is the proof. Each mode must satisfy `ScenarioExpectations`: real applock denial while a row is open, the scheduler batch lock acquirable during that same window, no overlapping ledger rows, and the mode-specific outcome (timeout plus retry, serialized overlap, deleted overlap plus a later run, or single-worker queueing).

## Observed on this machine

Run on 2026-11-07 with SDK 10.0.403, Hangfire 1.8.25, Microsoft.Data.SqlClient 6.1.8, and `mcr.microsoft.com/mssql/server:2022-CU16-ubuntu-22.04`. `dotnet test` — 6 passed, about 1.5 minutes. This is that run, not a claim about any other environment.

`HangFire.State` for the broken overlap stored:

```text
Hangfire.Storage.DistributedLockTimeoutException: Timeout expired. The timeout elapsed prior to obtaining a distributed lock on the 'HangFire:ledger-post' resource.
   at Hangfire.SqlServer.SqlServerDistributedLock.Acquire(...) line 220
   at Hangfire.SqlServer.SqlServerConnection.AcquireLock(...) line 810
   at Hangfire.SqlServer.SqlServerConnection.AcquireDistributedLock(...) line 67
   at Hangfire.DisableConcurrentExecutionAttribute.OnPerforming(...) line 51
   at Hangfire.Server.BackgroundJobPerformer.InvokeOnPerforming(...) line 198
```

While that job's ledger row was open, a second session's `sp_getapplock` on `HangFire:ledger-post` returned **-1**. The same session got **0** (granted immediately) for `HangFire:recurring-jobs:lock`, `HangFire:lock:recurring-job:ledger-post`, and `HangFire:locks:schedulepoller`. `sys.dm_tran_locks` showed one granted `APPLICATION` lock. So the failure is the execution lock, and the scheduler locks were free for most of the run.

The failing job id was then enqueued by `DelayedJobScheduler` and its later attempt wrote `demo.WorkLog`. The retry's start timestamp equalled the holder's finish timestamp. Exclusion held. The body still ran twice.

With the 45 second attribute, that same overlap did not fail. It sat in **Processing** with no ledger row until the holder finished, then wrote a row at that same timestamp. Several other workers were also stuck in Processing. No `DistributedLockTimeoutException` was stored.

With `SkipConcurrentExecution`, overlaps moved to **Deleted** in a few milliseconds (`Canceled by filter 'SkipConcurrentExecutionAttribute'`), wrote no ledger row, and stored no lock exception. The next ledger row started about two seconds after the previous finish, on the following cron tick, not on the release instant. `sp_getapplock` still returned -1 during the hold.

With one worker and the broken attribute, the second occurrence stayed **Enqueued** until the worker was free, then succeeded. There was no lock failure, and the probe was still denied with -1 during the hold.

A connection to `127.0.0.1:1` threw `Microsoft.Data.SqlClient.SqlException` (TCP provider error 35), not `DistributedLockTimeoutException`.

Fresh databases came up with `DisableGlobalLocks=true` and `UseFineGrainedLocks=false` via schema auto-detect. Those values were the same in every mode.

## Layout

- `src/HangfireLockDemo/Program.cs` — console entry
- `src/HangfireLockDemo/ScenarioRunner.cs` — SQL Server, two Hangfire servers, recurring job, probes
- `src/HangfireLockDemo/LedgerJobs.cs` — broken, inflated, and fixed methods
- `src/HangfireLockDemo/SkipConcurrentExecutionAttribute.cs` — the fix
- `src/HangfireLockDemo/ScenarioExpectations.cs` — correctness checks shared with the console
- `tests/HangfireLockDemo.Tests/ScenarioTests.cs` — integration tests
- `docker-compose.yml` — local SQL Server
