using Microsoft.Data.SqlClient;

namespace HangfireLockDemo;

/// <summary>
/// Local-only defaults. The listener is 127.0.0.1 and the password exists only for this lab database.
/// Override the server with the HANGFIRE_SQL environment variable (a connection string without a catalog,
/// or with any catalog; the runner replaces the catalog).
/// </summary>
public static class LocalConnections
{
    public const string Password = "LocalOnly_hf_Passw0rd";
    public const int Port = 14333;

    public static string ServerConnectionString { get; } =
        Environment.GetEnvironmentVariable("HANGFIRE_SQL")
        ?? $"Server=127.0.0.1,{Port};User ID=sa;Password={Password};TrustServerCertificate=True;Encrypt=True;Connect Timeout=5;Application Name=HangfireLockDemo";

    public static string ForDatabase(string connectionString, string databaseName)
    {
        SqlNames.RequireDatabaseName(databaseName);
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = databaseName,
            TrustServerCertificate = true,
            Encrypt = true,
            ConnectTimeout = Math.Max(new SqlConnectionStringBuilder(connectionString).ConnectTimeout, 5)
        };
        return builder.ConnectionString;
    }

    public static string ForMaster(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master",
            TrustServerCertificate = true,
            Encrypt = true
        };
        return builder.ConnectionString;
    }
}
