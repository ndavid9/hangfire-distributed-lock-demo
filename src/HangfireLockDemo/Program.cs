using HangfireLockDemo;

if (args.Length != 1 || !DemoModeParser.TryParse(args[0], out var mode))
{
    Console.Error.WriteLine("Usage: HangfireLockDemo <broken|inflated-timeout|fixed|single-worker>");
    Console.Error.WriteLine("This lab reproduces one documented Hangfire.SqlServer overlap, not an unseen production incident.");
    return 2;
}

var databaseName = mode switch
{
    DemoMode.Broken => "HangfireLockDemo_broken",
    DemoMode.InflatedTimeout => "HangfireLockDemo_inflated",
    DemoMode.Fixed => "HangfireLockDemo_fixed",
    DemoMode.SingleWorker => "HangfireLockDemo_single",
    _ => "HangfireLockDemo"
};

Console.WriteLine("Hangfire SQL distributed-lock lab");
Console.WriteLine("Assumption: local SQL Server on 127.0.0.1:14333, Hangfire.SqlServer 1.8.25, two clocks both using SQL UTC.");
Console.WriteLine("This is not a diagnosis of a stack trace you did not provide.");
Console.WriteLine($"Mode {mode}. Database {databaseName} will be recreated and left in place for inspection.");

ScenarioReport report;
try
{
    report = ScenarioRunner.Run(mode, LocalConnections.ServerConnectionString, databaseName);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

Console.WriteLine(report.Format());
var problems = ScenarioExpectations.Evaluate(report);
if (problems.Count == 0)
{
    Console.WriteLine("Expectations met.");
    return 0;
}

Console.Error.WriteLine("Expectations not met:");
foreach (var problem in problems)
{
    Console.Error.WriteLine(" - " + problem);
}

return 1;
