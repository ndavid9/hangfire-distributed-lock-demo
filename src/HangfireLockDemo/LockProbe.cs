using Microsoft.Data.SqlClient;

namespace HangfireLockDemo;

public static class LockProbe
{
    public static int TryExclusive(SqlConnection connection, string resource)
    {
        var result = ExecuteResult(
            connection,
            """
            DECLARE @result int;
            EXEC @result = sp_getapplock
                @Resource = @resource,
                @LockMode = N'Exclusive',
                @LockOwner = N'Session',
                @LockTimeout = 0,
                @DbPrincipal = N'public';
            SELECT @result;
            """,
            resource);

        if (result >= 0)
        {
            var released = ExecuteResult(
                connection,
                """
                DECLARE @result int;
                EXEC @result = sp_releaseapplock
                    @Resource = @resource,
                    @LockOwner = N'Session',
                    @DbPrincipal = N'public';
                SELECT @result;
                """,
                resource);
            if (released < 0)
            {
                throw new InvalidOperationException($"sp_releaseapplock('{resource}') returned {released}.");
            }
        }

        return result;
    }

    public static int? TryCountApplicationGrants(SqlConnection connection, out string? error)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM sys.dm_tran_locks
                WHERE resource_type = 'APPLICATION'
                  AND request_status = 'GRANT';
                """;
            error = null;
            return (int)command.ExecuteScalar()!;
        }
        catch (SqlException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static int ExecuteResult(SqlConnection connection, string commandText, string resource)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.Add(new SqlParameter("@resource", System.Data.SqlDbType.NVarChar, 255) { Value = resource });
        return (int)command.ExecuteScalar()!;
    }
}
