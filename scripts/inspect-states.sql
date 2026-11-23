-- Run against one of the lab databases left by the console app, for example HangfireLockDemo_broken.
-- Application lock resource_description is a hash, not the Hangfire resource name.
-- The resource name is in HangFire.State.Data (ExceptionMessage) for a failed attempt.

SELECT j.Id AS JobId,
       j.CreatedAt AS JobCreatedAt,
       s.Name AS StateName,
       s.Reason,
       s.CreatedAt AS StateCreatedAt,
       s.Data
FROM HangFire.State AS s
INNER JOIN HangFire.Job AS j ON j.Id = s.JobId
ORDER BY s.Id;

SELECT Id, HangfireJobId, StartedAt, FinishedAt
FROM demo.WorkLog
ORDER BY Id;

SELECT resource_type, request_mode, request_status, request_session_id, resource_description
FROM sys.dm_tran_locks
WHERE resource_type = 'APPLICATION';
