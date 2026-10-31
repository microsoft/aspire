// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Cli;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.Go.Tests;

public class GoTestExecutionTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("pass")]
    [InlineData("fail")]
    [InlineData("build")]
    [InlineData("empty")]
    [InlineData("filtered")]
    [InlineData("large")]
    [InlineData("repeat")]
    [RequiresTools(["go"])]
    [OuterloopTest("Runs real Go tests through DCP and captures their complete raw process output.")]
    public async Task RealGoResultsContributeToCommonAggregate(string scenario)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var directory = workspace.CreateDirectory("go project with spaces").FullName;
        File.WriteAllText(Path.Combine(directory, "go.mod"), "module example.com/fixture\n\ngo 1.24\n");
        File.WriteAllText(Path.Combine(directory, "fixture.go"),
            scenario == "build" ? "package fixture\nvar broken = doesNotExist\n" : "package fixture\n");
        if (scenario is not ("empty" or "build"))
        {
            File.WriteAllText(Path.Combine(directory, "fixture_test.go"), """
                package fixture
                import ("testing"; "os"; "fmt")
                func TestOutcome(t *testing.T) {
                    if os.Getenv("REFERENCE") != "configured" { t.Fatal("missing reference") }
                    t.Run("child", func(t *testing.T) { t.Log("subtest output") })
                    if os.Getenv("SCENARIO") == "fail" { t.Fatal("intentional parent cleanup failure") }
                    if os.Getenv("SCENARIO") == "large" {
                        for i := 0; i < 12000; i++ { fmt.Printf("bulk line %d\n", i) }
                        fmt.Fprintln(os.Stderr, "final test stderr")
                    }
                }
                func TestSkipped(t *testing.T) { t.Skip("intentional skip") }
                """);
        }
        using var builder = TestDistributedApplicationBuilder.Create(options => options.DisableDashboard = true, outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "results");
        var reference = builder.AddParameter("reference", "configured");
        var resource = builder.AddGoTest("tests", directory)
            .WithEnvironment("REFERENCE", reference)
            .WithEnvironment("SCENARIO", scenario)
            .WithEnvironment("GOTOOLCHAIN", "local");
        if (scenario == "filtered")
        {
            resource.WithArgs("-run=NoMatchingTest");
        }
        if (scenario == "repeat")
        {
            resource.WithArgs("-cpu=1,2");
        }
        await using var app = builder.Build();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        await app.StartAsync(timeout.Token);
        try
        {
            var events = new List<TestRunEvent>();
            await foreach (var item in app.Services.GetRequiredService<TestRunCoordinator>().WatchAsync(timeout.Token))
            {
                events.Add(item);
            }
            Assert.Equal(scenario is not ("fail" or "build"), events.Last().Passed);
            var results = events.Where(e => e.Result is not null).Select(e => e.Result!).ToArray();
            var expected = scenario switch
            {
                "empty" or "filtered" or "build" => (Passed: 0, Failed: 0, Skipped: 0),
                "fail" => (Passed: 0, Failed: 1, Skipped: 1),
                "repeat" => (Passed: 2, Failed: 0, Skipped: 2),
                _ => (Passed: 1, Failed: 0, Skipped: 1)
            };
            Assert.Equal(expected, (results.Count(r => r.Outcome == "passed"),
                results.Count(r => r.Outcome == "failed"), results.Count(r => r.Outcome == "skipped")));
            var path = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>()).Options!.OutputPath!;
            Assert.True(File.Exists(Path.Combine(path, "output.complete")));
            Assert.True(File.Exists(Path.Combine(path, "stderr.log")));
            var stdout = await File.ReadAllTextAsync(Path.Combine(path, "stdout.log"), timeout.Token);
            using var firstEvent = System.Text.Json.JsonDocument.Parse(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
            Assert.NotNull(firstEvent.RootElement.GetProperty("Action").GetString());
            if (scenario == "large")
            {
                Assert.Contains("bulk line 11999", stdout);
                Assert.Contains("final test stderr", stdout);
                Assert.Contains("[Message truncated;", Assert.Single(results, r => r.Name.Contains("TestOutcome")).Message);
            }
            if (scenario == "fail")
            {
                Assert.Contains("intentional parent cleanup failure", Assert.Single(results, r => r.Outcome == "failed").Message);
            }
            if (scenario == "repeat")
            {
                Assert.Equal(4, results.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count());
            }
            Assert.Equal(scenario is "fail" or "build" ? "failed" : "completed",
                events.Last(e => e.Collection is not null).Collection!.Status);
            if (!OperatingSystem.IsWindows())
            {
                foreach (var file in Directory.GetFiles(path))
                {
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
                }
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
