// Directory.Build.targets replaces this package with the repository project.
#:package Aspire.Hosting.AppHost@*
#:property IsAspireHost=true
#:property PublishAot=false
#:property UserSecretsId=aspire-cli-testing-playground

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIRETESTING001

var builder = DistributedApplication.CreateBuilder(args);

#if !SKIP_DASHBOARD_REFERENCE
// A build reference alone does not select the source dashboard when the CLI
// supplies a bundled path. The reserved resource replaces automatic launch.
// Keep explicit DcpPublisher configuration available for binary-level testing.
if (string.IsNullOrWhiteSpace(builder.Configuration["DcpPublisher:DashboardPath"]))
{
    builder.AddDotnetProject(KnownResourceNames.AspireDashboard, "../../src/Aspire.Dashboard/Aspire.Dashboard.csproj");
}
#endif

var service = builder.AddDotnetProject("service", "service/service.cs")
    .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS")
    .WithHttpHealthCheck("/health");
var serviceEndpoint = service.GetEndpoint("http");

var probeReports = Directory.CreateTempSubdirectory("aspire-cli-testing-probe-");
try
{
    var foundationTests = builder.AddDotnetProject("foundation-tests", "foundation-tests/tests.cs")
        .WithEnvironment("SERVICE_URL", serviceEndpoint)
        .WithEnvironment("TEST_OUTPUT_PATH", probeReports.FullName)
        .WaitFor(service);

    foundationTests.WithTesting(async context =>
    {
        var passed = await context.WaitForCompletionAsync();
        if (passed)
        {
            var reportPath = Path.Combine(probeReports.FullName, "probe.txt");
            var artifact = await File.ReadAllTextAsync(reportPath, context.CancellationToken);
            passed = artifact == "Aspire CLI testing";
            File.Copy(reportPath, Path.Combine(context.OutputPath, "probe.txt"));
        }

        context.PublishResult(new TestResult
        {
            Name = "shared-service-probe",
            Outcome = passed ? TestOutcome.Passed : TestOutcome.Failed,
            Message = passed ? null : "The shared service probe or its output artifact failed."
        });
    });

    builder.AddMsTest("mstest-tests", "mstest-tests/Playground.MSTest.csproj")
        .WithEnvironment("MSTEST_SERVICE_URL", serviceEndpoint)
        .WithEnvironment("MSTEST_PLAYGROUND_FAIL",
            Environment.GetEnvironmentVariable("MSTEST_PLAYGROUND_FAIL") ?? "false")
        .WaitFor(service);

    builder.AddPytest("pytest-tests", "pytest-tests")
        .WithUv()
        .WithArgs("-q")
        .WithEnvironment("PYTEST_SERVICE_URL", serviceEndpoint)
        .WithEnvironment("PYTEST_PLAYGROUND_FAIL",
            Environment.GetEnvironmentVariable("PYTEST_PLAYGROUND_FAIL") ?? "false")
        .WaitFor(service);

    builder.AddGoTest("go-tests", "go-tests")
        .WithEnvironment("GO_SERVICE_URL", serviceEndpoint)
        .WithEnvironment("GO_PLAYGROUND_FAIL",
            Environment.GetEnvironmentVariable("GO_PLAYGROUND_FAIL") ?? "false")
        .WaitFor(service);

    builder.AddExecutable("rust-tests", "cargo", "rust-tests", "nextest", "run")
        .WithNextestTesting()
        .WithEnvironment("RUST_SERVICE_URL", serviceEndpoint)
        .WithEnvironment("RUST_PLAYGROUND_FAIL",
            Environment.GetEnvironmentVariable("RUST_PLAYGROUND_FAIL") ?? "false")
        .WaitFor(service);

    builder.Build().Run();
}
finally
{
    probeReports.Delete(recursive: true);
}
