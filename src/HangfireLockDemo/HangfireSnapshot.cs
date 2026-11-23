using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace HangfireLockDemo;

public static class HangfireSnapshot
{
    public static (IReadOnlyList<WorkLogRow> Work, IReadOnlyList<StateRow> States) Read(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        return (ReadWork(connection), ReadStates(connection));
    }

    public static bool HasOpenWork(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM demo.WorkLog WHERE FinishedAt IS NULL;";
        return (int)command.ExecuteScalar()! > 0;
    }

    public static IReadOnlyList<LockFailureView> ReadLockFailures(IReadOnlyList<StateRow> states)
    {
        var failures = new List<LockFailureView>();
        foreach (var state in states)
        {
            if (!string.Equals(state.Name, "Failed", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(state.Data))
            {
                continue;
            }

            if (!TryReadFailure(state.Data, out var type, out var message, out var details))
            {
                continue;
            }

            if (type is null || message is null || details is null)
            {
                continue;
            }

            if (!type.EndsWith("DistributedLockTimeoutException", StringComparison.Ordinal))
            {
                continue;
            }

            failures.Add(new LockFailureView(state.JobId, type, message, details));
        }

        return failures;
    }

    private static List<WorkLogRow> ReadWork(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, HangfireJobId, StartedAt, FinishedAt
            FROM demo.WorkLog
            ORDER BY Id;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<WorkLogRow>();
        while (reader.Read())
        {
            rows.Add(new WorkLogRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3)));
        }

        return rows;
    }

    private static List<StateRow> ReadStates(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                CONVERT(nvarchar(50), j.Id) AS JobId,
                j.CreatedAt AS JobCreatedAt,
                s.Name,
                s.Reason,
                s.Data,
                s.CreatedAt AS StateCreatedAt
            FROM HangFire.Job AS j
            INNER JOIN HangFire.State AS s ON s.JobId = j.Id
            ORDER BY j.Id, s.Id;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<StateRow>();
        while (reader.Read())
        {
            rows.Add(new StateRow(
                reader.GetString(0),
                reader.GetDateTime(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetDateTime(5)));
        }

        return rows;
    }

    private static bool TryReadFailure(string data, out string? type, out string? message, out string? details)
    {
        type = null;
        message = null;
        details = null;
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.TryGetProperty("ExceptionType", out var typeElement))
            {
                type = typeElement.GetString();
            }

            if (root.TryGetProperty("ExceptionMessage", out var messageElement))
            {
                message = messageElement.GetString();
            }

            if (root.TryGetProperty("ExceptionDetails", out var detailsElement))
            {
                details = detailsElement.GetString();
            }

            return type is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
