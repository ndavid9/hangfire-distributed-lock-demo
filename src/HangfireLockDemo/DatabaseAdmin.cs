using Microsoft.Data.SqlClient;

namespace HangfireLockDemo;

public static class DatabaseAdmin
{
    public static void Recreate(string serverConnectionString, string databaseName)
    {
        SqlNames.RequireDatabaseName(databaseName);
        using var connection = new SqlConnection(LocalConnections.ForMaster(serverConnectionString));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 60;
        command.CommandText = $"""
            IF DB_ID(N'{databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
            END
            CREATE DATABASE [{databaseName}];
            """;
        command.ExecuteNonQuery();
    }

    public static void DropIfExists(string serverConnectionString, string databaseName)
    {
        SqlNames.RequireDatabaseName(databaseName);
        SqlConnection.ClearAllPools();
        using var connection = new SqlConnection(LocalConnections.ForMaster(serverConnectionString));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 60;
        command.CommandText = $"""
            IF DB_ID(N'{databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
            END
            """;
        command.ExecuteNonQuery();
    }
}
