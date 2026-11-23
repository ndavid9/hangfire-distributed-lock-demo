using Hangfire.Storage;
using HangfireLockDemo;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace HangfireLockDemo.Tests;

[Collection(SqlCollection.Name)]
public sealed class ScenarioTests
{
    private readonly SqlServerFixture _sql;
    private readonly ITestOutputHelper _output;

    public ScenarioTests(SqlServerFixture sql, ITestOutputHelper output)
    {
        _sql = sql;
        _output = output;
    }

    [Fact(Timeout = 180000)]
    public Task BrokenOverlap_FailsOnSqlApplock_AndRetryRunsTheBody()
    {
        return Task.Run(() => AssertMode(DemoMode.Broken));
    }

    [Fact(Timeout = 180000)]
    public Task InflatedTimeout_HidesTheException_AndStillRunsTheOverlap()
    {
        return Task.Run(() => AssertMode(DemoMode.InflatedTimeout));
    }

    [Fact(Timeout = 180000)]
    public Task FixedSkip_DeletesOverlap_KeepsLaterExecution_AndHoldsApplock()
    {
        return Task.Run(() => AssertMode(DemoMode.Fixed));
    }

    [Fact(Timeout = 180000)]
    public Task SingleWorker_HoldsApplock_WithoutTimingOut()
    {
        return Task.Run(() => AssertMode(DemoMode.SingleWorker));
    }

    [Fact]
    public void InstalledExceptionText_MatchesTheResourceMessage()
    {
        var exception = new DistributedLockTimeoutException(DemoTiming.ExecutionLockResource);
        Assert.Equal(DemoTiming.ExpectedLockTimeoutMessage, exception.Message);
        Assert.Equal(DemoTiming.ExpectedExceptionType, exception.GetType().FullName);
    }

    [Fact]
    public void ClosedPort_IsSqlException_NotDistributedLockTimeout()
    {
        var builder = new SqlConnectionStringBuilder(LocalConnections.ServerConnectionString)
        {
            DataSource = "127.0.0.1,1",
            InitialCatalog = "master",
            ConnectTimeout = 2
        };

        var exception = Record.Exception(() =>
        {
            using var connection = new SqlConnection(builder.ConnectionString);
            connection.Open();
        });

        Assert.NotNull(exception);
        _output.WriteLine(exception.GetType().FullName + ": " + exception.Message);
        Assert.IsType<SqlException>(exception);
        Assert.IsNotType<DistributedLockTimeoutException>(exception);
    }

    private void AssertMode(DemoMode mode)
    {
        var databaseName = "LockDemo_" + mode + "_" + Guid.NewGuid().ToString("N");
        try
        {
            var report = ScenarioRunner.Run(mode, _sql.ServerConnectionString, databaseName);
            _output.WriteLine(report.Format());
            var problems = ScenarioExpectations.Evaluate(report);
            Assert.True(
                problems.Count == 0,
                string.Join(Environment.NewLine, problems) + Environment.NewLine + report.Format());
        }
        finally
        {
            DatabaseAdmin.DropIfExists(_sql.ServerConnectionString, databaseName);
        }
    }
}
