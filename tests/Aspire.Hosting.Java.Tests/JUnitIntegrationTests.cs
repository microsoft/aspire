// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Cli;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.Java.Tests;

public class JUnitIntegrationTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("pass")]
    [InlineData("fail")]
    [InlineData("error")]
    [InlineData("skipped")]
    [InlineData("empty")]
    [InlineData("build")]
    [InlineData("missing-report")]
    [RequiresTools(["java"])]
    [OuterloopTest("Downloads the pinned Maven/JUnit dependencies and runs Java tests through DCP.")]
    public async Task RealMavenJUnitUsesFreshReportsAndTheCommonAggregate(string scenario)
    {
        using var directory = new TempJavaAppDirectory(withWrappers: false, directoryName: "JUnit project with spaces");
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "java-tests");
        foreach (var file in Directory.GetFiles(fixture, "*", SearchOption.AllDirectories))
        {
            directory.Write(Path.GetRelativePath(fixture, file), File.ReadAllText(file));
        }
        directory.Write("target/surefire-reports/TEST-stale.xml", "<stale-report/>");
        directory.Write("src/test/java/dev/aspire/AdditionalTest.java", scenario == "build" ? "invalid Java source" : """
            package dev.aspire;
            import org.junit.jupiter.api.Test;
            import org.junit.jupiter.params.ParameterizedTest;
            import org.junit.jupiter.params.provider.ValueSource;
            import static org.junit.jupiter.api.Assertions.*;
            class AdditionalTest {
                @ParameterizedTest
                @ValueSource(ints = {1, 2})
                void values(int value) { assertTrue(value > 0); }
                @Test
                void exception() {
                    if ("error".equals(System.getenv("SCENARIO"))) {
                        throw new IllegalStateException("intentional Java exception");
                    }
                }
            }
            """);
        var servicePath = directory.Write("service/service.cs",
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "service.cs")));
        using var builder = TestDistributedApplicationBuilder.Create(options => options.DisableDashboard = true, outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(directory.Path, "test output");
        var service = builder.AddDotnetProject("service", servicePath)
            .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS").WithHttpHealthCheck("/health");
        var resource = builder.AddJavaApp("java-tests", directory.Path)
            .WithMavenGoal("test", "-B", "-ntp").WithJUnitTesting()
            .WithEnvironment("JAVA_SERVICE_URL", service.GetEndpoint("http"))
            .WithEnvironment("JAVA_PLAYGROUND_FAIL", scenario == "fail" ? "true" : "false")
            .WithEnvironment("SCENARIO", scenario).WaitFor(service);
        if (scenario == "empty")
        {
            resource.WithArgs("-Dtest=NoSuchTest");
        }
        else if (scenario == "missing-report")
        {
            resource.WithArgs("-P!aspire-testing");
        }
        else if (scenario == "skipped")
        {
            resource.WithArgs("-Dtest=ServiceTest#skippedExample");
        }
        await using var app = builder.Build();
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        startup.CancelAfter(TimeSpan.FromMinutes(3));
        await app.StartAsync(startup.Token);
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource.Resource, serviceProvider: app.Services);
        var reports = environment[JUnitTestingExtensions.ReportEnvironmentVariable];
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var events = new List<TestRunEvent>();
            await foreach (var item in app.Services.GetRequiredService<TestRunCoordinator>().WatchAsync(timeout.Token))
            {
                events.Add(item);
            }
            Assert.Equal(scenario is "pass" or "skipped", events.Last().Passed);
            var results = events.Where(item => item.Result is not null).Select(item => item.Result!).ToArray();
            var expected = scenario switch
            {
                "pass" => (Passed: 6, Failed: 0, Error: 0, Skipped: 1),
                "fail" => (Passed: 5, Failed: 1, Error: 0, Skipped: 1),
                "error" => (Passed: 5, Failed: 0, Error: 1, Skipped: 1),
                "skipped" => (Passed: 0, Failed: 0, Error: 0, Skipped: 1),
                _ => (Passed: 0, Failed: 0, Error: 0, Skipped: 0)
            };
            Assert.Equal(expected, (results.Count(result => result.Outcome == "passed"),
                results.Count(result => result.Outcome == "failed"), results.Count(result => result.Outcome == "error"),
                results.Count(result => result.Outcome == "skipped")));
            Assert.Equal(expected.Passed + expected.Failed + expected.Error + expected.Skipped, results.Length);
            Assert.All(results, result => Assert.Equal("java-tests", result.ResourceName));
            var collection = events.Last(item => item.Collection is not null).Collection!;
            var hasReports = scenario is "pass" or "fail" or "error" or "skipped";
            Assert.Equal(hasReports ? "completed" : "failed", collection.Status);
            var output = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>()).OutputPath!;
            Assert.Equal(Path.Combine(builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"]!, "java-tests"), output);
            var staged = Directory.GetFiles(output);
            Assert.Equal(hasReports ? scenario == "skipped" ? 1 : 2 : 0, staged.Length);
            foreach (var file in staged)
            {
                Assert.Equal(File.ReadAllText(Path.Combine(reports, Path.GetFileName(file))), File.ReadAllText(file));
            }
            Assert.Equal("<stale-report/>", File.ReadAllText(Path.Combine(directory.Path, "target", "surefire-reports", "TEST-stale.xml")));
            if (scenario == "missing-report")
            {
                Assert.True(File.Exists(Path.Combine(directory.Path, "target", "surefire-reports", "TEST-dev.aspire.ServiceTest.xml")));
                Assert.Empty(Directory.GetFiles(reports));
            }
            if (scenario is "fail" or "error")
            {
                Assert.Contains("intentional Java", Assert.Single(results, result => result.Outcome is "failed" or "error").Message);
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
            Assert.False(Directory.Exists(reports));
        }
    }
}
