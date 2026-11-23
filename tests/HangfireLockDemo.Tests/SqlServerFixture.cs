using HangfireLockDemo;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace HangfireLockDemo.Tests;

public sealed class SqlServerFixture
{
    public string ServerConnectionString { get; } = LocalConnections.ServerConnectionString;

    public SqlServerFixture()
    {
        var master = LocalConnections.ForMaster(ServerConnectionString);
        var started = DateTime.UtcNow;
        Exception? last = null;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(120))
        {
            try
            {
                using var connection = new SqlConnection(master);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT @@VERSION";
                _ = command.ExecuteScalar();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(1000);
            }
        }

        throw new InvalidOperationException(
            "SQL Server did not accept connections on 127.0.0.1:14333 within 120 seconds. Start it with scripts/up-sql.sh. Last error: " + last);
    }
}

[CollectionDefinition(SqlCollection.Name)]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sqlserver";
}
