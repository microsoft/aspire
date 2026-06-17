// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for adding and starting a Dapr sidecar via the CommunityToolkit.Aspire.Hosting.Dapr integration.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class DaprIntegrationTests(ITestOutputHelper output)
{
    private const string ProjectName = "AspireDaprTest";
    private const string DaprPackageVersion = "13.4.0-beta.651";

    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task AddDaprSidecarAndStart()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Step 1: Create a Starter App without Redis cache so we have project resources to attach the sidecar to
        await auto.AspireNewAsync(ProjectName, counter, useRedisCache: false);

        // Step 2: Navigate into the project directory
        await auto.RunCommandAsync($"cd {ProjectName}", counter);

        // Step 3: Add the CommunityToolkit Dapr hosting integration with a specific version
        await auto.TypeAsync($"aspire add communitytoolkit-dapr --version {DaprPackageVersion}");
        await auto.EnterAsync();
        await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(2));

        // Step 4: Modify AppHost.cs to add a Dapr sidecar to the apiservice project.
        // The Starter template scaffolds AppHost.cs with AddProject calls for apiservice and webfrontend.
        // We chain .WithDaprSidecar() onto the apiservice builder to attach a Dapr sidecar resource.
        {
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName);
            var appHostDir = Path.Combine(projectDir, $"{ProjectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);
            output.WriteLine($"Original AppHost.cs content:{Environment.NewLine}{content}");

            // The Starter template produces a line like:
            //   var apiService = builder.AddProject<Projects.AspireDaprTest_ApiService>("apiservice");
            // Replace it to chain .WithDaprSidecar() so the Dapr sidecar is attached to the apiservice.
            var originalLine = $"builder.AddProject<Projects.{ProjectName}_ApiService>(\"apiservice\")";
            var modifiedLine = $"builder.AddProject<Projects.{ProjectName}_ApiService>(\"apiservice\")\n    .WithDaprSidecar()";

            content = content.Replace(originalLine, modifiedLine);
            File.WriteAllText(appHostFilePath, content);

            output.WriteLine($"Modified AppHost.cs content:{Environment.NewLine}{content}");
        }

        // Step 5: Install the Dapr CLI and initialize it.
        // The CommunityToolkit.Aspire.Hosting.Dapr lifecycle hook requires the `dapr` binary on PATH.
        await auto.RunCommandAsync("curl -fsSL https://raw.githubusercontent.com/dapr/cli/master/install/install.sh | bash", counter, TimeSpan.FromSeconds(120));
        await auto.RunCommandAsync("dapr init", counter, TimeSpan.FromSeconds(120));

        // Step 6: Start the AppHost and verify it comes up successfully
        await auto.AspireStartAsync(counter, startTimeout: TimeSpan.FromMinutes(5));

        // Step 7: Wait for all resources to reach a running state.
        // The Starter template (no Redis) produces apiservice and webfrontend.
        // WithDaprSidecar() adds a Dapr sidecar resource named apiservice-dapr.
        foreach (var resource in new[] { "apiservice", "webfrontend", "apiservice-dapr" })
        {
            await auto.TypeAsync($"aspire wait {resource} --status up --timeout 300");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("is up (running).", timeout: TimeSpan.FromMinutes(6));
            await auto.WaitForSuccessPromptAsync(counter);
        }

        // Step 8: Stop the AppHost
        await auto.AspireStopAsync(counter);
    }
}
