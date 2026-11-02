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

    builder.Build().Run();
}
finally
{
    probeReports.Delete(recursive: true);
}
