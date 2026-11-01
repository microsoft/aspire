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

public class CargoTestingExecutionTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("pass")]
    [InlineData("fail")]
    [InlineData("build")]
    [InlineData("empty")]
    [InlineData("doctest")]
    [RequiresTools(["cargo"])]
    [OuterloopTest("Runs built-in cargo test through DCP, without nextest or structured reports.")]
    public async Task BuiltInCargoExitIsAuthoritativeWithoutPublishingTestcases(string scenario)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var crate = workspace.CreateDirectory("crate with spaces").FullName;
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
            "doctest" => """
                /// ```
                /// panic!("intentional doctest failure");
                /// ```
                pub fn example() {}
                """,
            _ => """
                /// ```
                /// assert_eq!(fixture::answer(), 42);
                /// ```
                pub fn answer() -> u32 { 42 }
                #[cfg(test)]
                mod tests {
                    #[test]
                    fn outcome() {
                        assert_eq!(std::env::var("REFERENCE").unwrap(), "configured");
                        assert!(cfg!(feature = "integration"));
                        assert_ne!(std::env::var("SCENARIO").unwrap(), "fail", "intentional failure");
                    }
                    #[test]
                    #[ignore = "intentional ignored example"]
                    fn ignored() { panic!("must not run"); }
                }
                """
        });
        using var builder = TestDistributedApplicationBuilder.Create(options => options.DisableDashboard = true, outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "results");
        var reference = builder.AddParameter("reference", "configured");
        var resource = builder.AddExecutable("tests", "cargo", crate, "test", "--features=integration")
            .WithCargoTesting()
            .WithEnvironment("REFERENCE", reference)
            .WithEnvironment("SCENARIO", scenario);
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
            Assert.Equal(scenario is "pass" or "empty", events.Last().Passed);
            Assert.All(events, e => Assert.Null(e.Result));
            Assert.Equal("completed", Assert.Single(events, e => e.Collection?.Status == "completed").Collection!.Status);
            var options = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>()).Options!;
            var path = options.OutputPath!;
            Assert.Equal(["output.complete", "stderr.log", "stdout.log"],
                Directory.GetFiles(path).Select(Path.GetFileName).Order().ToArray());
            var stdout = await File.ReadAllTextAsync(Path.Combine(path, "stdout.log"), timeout.Token);
            var stderr = await File.ReadAllTextAsync(Path.Combine(path, "stderr.log"), timeout.Token);
            Assert.Contains(scenario switch
            {
                "build" => "does_not_exist",
                "fail" => "intentional failure",
                "doctest" => "intentional doctest failure",
                _ => "test result: ok."
            }, stdout + stderr);
            if (scenario == "pass")
            {
                Assert.Contains("Doc-tests fixture", stderr);
            }
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
