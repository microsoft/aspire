// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Cli;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.Rust.Tests;

public class NextestExecutionTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("pass")]
    [InlineData("fail")]
    [InlineData("build")]
    [InlineData("empty")]
    [InlineData("filtered")]
    [InlineData("workspace")]
    [InlineData("two")]
    [RequiresTools(["cargo", "cargo-nextest"])]
    [OuterloopTest("Runs real nextest through DCP and collects final JUnit results.")]
    public async Task RealNextestContributesToCommonSummary(string scenario)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var directory = workspace.CreateDirectory("rust crate with spaces").FullName;
        var crate = directory;
        if (scenario == "workspace")
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "Cargo.toml"), "[workspace]\nmembers = [\"selected crate\"]\nresolver = \"2\"\n");
            crate = Directory.CreateDirectory(Path.Combine(directory, "selected crate")).FullName;
        }
        var source = Directory.CreateDirectory(Path.Combine(crate, "src")).FullName;
        await File.WriteAllTextAsync(Path.Combine(crate, "Cargo.toml"), """
            [package]
            name = "fixture"
            version = "0.1.0"
            edition = "2021"
            [features]
            integration = []
            """);
        await File.WriteAllTextAsync(Path.Combine(source, "lib.rs"), scenario switch
        {
            "build" => "pub fn broken() { does_not_exist(); }",
            "empty" => "pub fn empty() {}",
            _ => """
                /// ```
                /// panic!("nextest deliberately excludes doctests");
                /// ```
                pub fn example() {}
                #[cfg(test)]
                mod tests {
                    #[test]
                    fn outcome() {
                        assert_eq!(std::env::var("REFERENCE").unwrap(), "configured");
                        assert!(cfg!(feature = "integration"));
                        assert_ne!(std::env::var("SCENARIO").unwrap(), "fail", "intentional failure");
                    }
                    #[test]
                    #[ignore = "intentional ignored test"]
                    fn ignored() { panic!("must be skipped"); }
                }
                """
        });
        var config = Directory.CreateDirectory(Path.Combine(directory, ".config")).FullName;
        // Repository config would redirect reports and retry failures. The explicit controlled
        // config must replace it without changing Cargo's feature/environment composition.
        await File.WriteAllTextAsync(Path.Combine(config, "nextest.toml"), """
            [profile.default]
            retries = 3
            [profile.default.junit]
            path = "wrong-report.xml"
            report-skipped = "none"
            """);
        using var builder = TestDistributedApplicationBuilder.Create(options => options.DisableDashboard = true, outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "results");
        var reference = builder.AddParameter("reference", "configured");
        var resource = builder.AddExecutable("tests", "cargo", directory, "nextest", "run", "--features=integration")
            .WithNextestTesting()
            .WithEnvironment("REFERENCE", reference)
            .WithEnvironment("SCENARIO", scenario)
            .WithEnvironment("NEXTEST_RETRIES", "4");
        if (scenario == "filtered")
        {
            resource.WithArgs("-E", "test(no_such_test)");
        }
        if (scenario == "workspace")
        {
            resource.WithArgs("--workspace", "--package=fixture", "--cargo-profile=test");
        }
        IResourceBuilder<ExecutableResource>? second = null;
        if (scenario == "two")
        {
            second = builder.AddExecutable("second-tests", "cargo", directory, "nextest", "run", "--features=integration")
                .WithNextestTesting()
                .WithEnvironment("REFERENCE", reference)
                .WithEnvironment("SCENARIO", "fail");
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
            Assert.Equal(scenario is "pass" or "workspace", events.Last().Passed);
            var results = events.Where(e => e.Result is not null).Select(e => e.Result!).ToArray();
            var expected = scenario switch
            {
                "pass" or "workspace" => (Passed: 1, Failed: 0, Skipped: 1),
                "fail" => (Passed: 0, Failed: 1, Skipped: 1),
                "filtered" => (Passed: 0, Failed: 0, Skipped: 1),
                "two" => (Passed: 1, Failed: 1, Skipped: 2),
                _ => (Passed: 0, Failed: 0, Skipped: 0)
            };
            Assert.Equal(expected, (results.Count(r => r.Outcome == "passed"),
                results.Count(r => r.Outcome == "failed"), results.Count(r => r.Outcome == "skipped")));
            var path = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>()).Options!.OutputPath!;
            Assert.True(File.Exists(Path.Combine(path, "nextest.toml")));
            Assert.True(File.Exists(Path.Combine(path, "output.complete")));
            Assert.True(File.Exists(Path.Combine(path, "stdout.log")));
            Assert.True(File.Exists(Path.Combine(path, "stderr.log")));
            if (scenario is "pass" or "fail" or "filtered" or "workspace" or "two")
            {
                Assert.True(File.Exists(Path.Combine(path, "junit.xml")));
                if (scenario != "filtered")
                {
                    Assert.Equal("fixture/tests::outcome", Assert.Single(results, r => r.Outcome != "skipped" && r.ResourceName == "tests").Name);
                }
                Assert.Equal("completed", events.Last(e => e.Collection is not null).Collection!.Status);
            }
            else
            {
                Assert.Equal("failed", events.Last(e => e.Collection is not null).Collection!.Status);
            }
            if (scenario == "fail")
            {
                Assert.Contains("intentional failure", Assert.Single(results, r => r.Outcome == "failed").Message);
            }
            if (second is not null)
            {
                var secondPath = Assert.Single(second.Resource.Annotations.OfType<TestingAnnotation>()).Options!.OutputPath!;
                Assert.NotEqual(path, secondPath);
                Assert.True(File.Exists(Path.Combine(secondPath, "junit.xml")));
                Assert.True(File.Exists(Path.Combine(secondPath, "output.complete")));
                Assert.Equal(2, events.Count(e => e.Collection?.Status == "completed"));
            }
            var repositoryConfig = await File.ReadAllTextAsync(Path.Combine(config, "nextest.toml"), timeout.Token);
            Assert.Equal("""
                [profile.default]
                retries = 3
                [profile.default.junit]
                path = "wrong-report.xml"
                report-skipped = "none"
                """, repositoryConfig);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
