#:sdk Microsoft.NET.Sdk

var mode = args.Length > 0 ? args[0] : "unknown";

Console.WriteLine($"scenario={Environment.GetEnvironmentVariable("ASPIRE_COMMAND_SCENARIO") ?? "manual"}");
Console.WriteLine($"mode={mode}");

if (mode == "seed")
{
    var dataset = args.Length > 1 ? args[1] : "small";
    var customers = args.Length > 2 ? args[2] : "25";

    Console.WriteLine($"seed-dataset={dataset}");
    Console.WriteLine($"seed-customers={customers}");
    Console.WriteLine($"connection-set={!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ConnectionStrings__mainDb"))}");
}
else if (mode == "test")
{
    var filter = args.Length > 1 ? args[1] : "smoke";

    Console.WriteLine($"test-filter={filter}");
    Console.WriteLine($"configuration={Environment.GetEnvironmentVariable("ASPIRE_TEST_CONFIGURATION") ?? "unknown"}");
}
else if (mode == "job")
{
    var jobName = args.Length > 1 ? args[1] : "daily-import";
    var stdin = await Console.In.ReadToEndAsync();

    Console.WriteLine($"job-name={jobName}");
    Console.WriteLine($"job-payload={stdin.Trim()}");
}
else
{
    Console.Error.WriteLine($"unknown-mode={mode}");
    Environment.Exit(2);
}
