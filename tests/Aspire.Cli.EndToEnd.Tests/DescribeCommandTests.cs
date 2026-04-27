// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the aspire describe command.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class DescribeCommandTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DescribeCommandShowsRunningResources()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // Create a new project using aspire new
        await auto.AspireNewAsync("AspireResourcesTestApp", counter);

        // Navigate to the AppHost directory
        await auto.TypeAsync("cd AspireResourcesTestApp/AspireResourcesTestApp.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Start the AppHost in the background using aspire start
        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Wait a bit for resources to stabilize
        await auto.TypeAsync("sleep 5");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Now verify aspire describe shows the running resources (human-readable table)
        await auto.TypeAsync("aspire describe");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Name", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitUntilTextAsync("webfrontend", timeout: TimeSpan.FromSeconds(5));
        await auto.WaitUntilTextAsync("apiservice", timeout: TimeSpan.FromSeconds(5));
        await auto.WaitForSuccessPromptAsync(counter);

        // Test aspire describe --format json output - pipe to file to avoid terminal buffer issues
        await auto.TypeAsync("aspire describe --format json > resources.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify the JSON file contains expected resources
        await auto.TypeAsync("cat resources.json | grep webfrontend");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("webfrontend", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Stop the AppHost using aspire stop
        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(StopCommandStrings.AppHostStoppedSuccessfully, timeout: TimeSpan.FromMinutes(1));
        await auto.WaitForSuccessPromptAsync(counter);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    public async Task DescribeCommandResolvesReplicaNames()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        // Pattern for describe output showing a specific replica
        var waitForApiserviceReplicaName = new CellPatternSearcher()
            .FindPattern("apiservice-[a-z0-9]+");

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // Create a new project using aspire new
        await auto.AspireNewAsync("AspireReplicaTestApp", counter);

        // Navigate to the AppHost directory
        await auto.TypeAsync("cd AspireReplicaTestApp/AspireReplicaTestApp.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Add .WithReplicas(2) to the apiservice resource in the AppHost
        var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, "AspireReplicaTestApp");
        var appHostDir = Path.Combine(projectDir, "AspireReplicaTestApp.AppHost");
        var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

        output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

        var content = File.ReadAllText(appHostFilePath);

        // Add .WithReplicas(2) to the first .WithHttpHealthCheck("/health"); occurrence (apiservice)
        // and clear the fixed target ports so DCP can assign unique ports to each replica.
        var originalPattern = ".WithHttpHealthCheck(\"/health\");";
        var replacement = """
            .WithHttpHealthCheck("/health")
            .WithReplicas(2)
            .WithEndpoint("http", e => e.TargetPort = null, createIfNotExists: false)
            .WithEndpoint("https", e => e.TargetPort = null, createIfNotExists: false);
            """;

        // Only replace the first occurrence (apiservice), not the second (webfrontend)
        var index = content.IndexOf(originalPattern);
        if (index >= 0)
        {
            content = content[..index] + replacement + content[(index + originalPattern.Length)..];
        }

        File.WriteAllText(appHostFilePath, content);

        output.WriteLine("Modified AppHost.cs to add .WithReplicas(2) to apiservice and clear fixed endpoint target ports");

        // Start the AppHost in the background using aspire start
        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Wait for resources to stabilize
        await auto.TypeAsync("sleep 10");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Test 1: aspire describe with friendly name for a non-replicated resource (cache)
        // This should resolve via DisplayName since cache has only one instance
        await auto.TypeAsync("aspire describe cache --format json > cache-describe.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify cache resource was found in the output
        await auto.TypeAsync("cat cache-describe.json | grep cache");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("cache", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Test 2: Get all resources to find an apiservice replica name
        await auto.TypeAsync("aspire describe --format json > all-resources.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Extract a replica name from the JSON - apiservice replicas have names like apiservice-<suffix>
        await auto.TypeAsync("REPLICA_NAME=$(cat all-resources.json | grep -o '\"name\": *\"apiservice-[a-z0-9]*\"' | head -1 | sed 's/.*\"\\(apiservice-[a-z0-9]*\\)\"/\\1/')");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify we captured a replica name
        await auto.TypeAsync("echo \"Found replica: $REPLICA_NAME\"");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(s => waitForApiserviceReplicaName.Search(s).Count > 0, timeout: TimeSpan.FromSeconds(10), description: "waiting for apiservice replica name");
        await auto.WaitForSuccessPromptAsync(counter);

        // Test 3: aspire describe with the replica name
        // This should resolve via exact Name match
        await auto.TypeAsync("aspire describe $REPLICA_NAME --format json > replica-describe.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify the replica was found and output contains the replica name
        await auto.TypeAsync("cat replica-describe.json | grep apiservice");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(s => waitForApiserviceReplicaName.Search(s).Count > 0, timeout: TimeSpan.FromSeconds(10), description: "waiting for apiservice replica in describe output");
        await auto.WaitForSuccessPromptAsync(counter);

        // Stop the AppHost using aspire stop
        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(StopCommandStrings.AppHostStoppedSuccessfully, timeout: TimeSpan.FromMinutes(1));
        await auto.WaitForSuccessPromptAsync(counter);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
