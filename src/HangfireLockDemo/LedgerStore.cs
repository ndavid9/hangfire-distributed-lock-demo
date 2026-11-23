using Microsoft.Data.SqlClient;

namespace HangfireLockDemo;

public static class LedgerStore
{
    public static void EnsureSchema(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        // CREATE SCHEMA must be the first statement in its batch, so it is executed dynamically.
        command.CommandText = """
            IF SCHEMA_ID(N'demo') IS NULL EXEC(N'CREATE SCHEMA demo');
            IF OBJECT_ID(N'demo.WorkLog', N'U') IS NULL
            CREATE TABLE demo.WorkLog
            (
                Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_demo_WorkLog PRIMARY KEY,
                HangfireJobId nvarchar(50) NOT NULL,
                StartedAt datetime2(3) NOT NULL,
                FinishedAt datetime2(3) NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public static void MarkStarted(string connectionString, string hangfireJobId)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO demo.WorkLog (HangfireJobId, StartedAt)
            VALUES (@jobId, SYSUTCDATETIME());
            """;
        command.Parameters.Add(new SqlParameter("@jobId", hangfireJobId));
        command.ExecuteNonQuery();
    }

    public static void MarkFinished(string connectionString, string hangfireJobId)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE demo.WorkLog
            SET FinishedAt = SYSUTCDATETIME()
            WHERE HangfireJobId = @jobId
              AND FinishedAt IS NULL;
            """;
        command.Parameters.Add(new SqlParameter("@jobId", hangfireJobId));
        command.ExecuteNonQuery();
    }
}
