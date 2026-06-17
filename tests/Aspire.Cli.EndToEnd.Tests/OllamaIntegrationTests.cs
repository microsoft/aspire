// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for adding and starting an Ollama container via the CommunityToolkit.Aspire.Hosting.Ollama integration.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class OllamaIntegrationTests(ITestOutputHelper output)
{
    private const string ProjectName = "AspireOllamaTest";

    // Uses an older version of Ollama integration to test using old integration with new Aspire AppHost.
    private const string OllamaPackageVersion = "13.4.0";

    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task AddOllamaAndStart()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Step 1: Create a Starter App without Redis cache so we have a clean project
        await auto.AspireNewAsync(ProjectName, counter, useRedisCache: false);

        // Step 2: Navigate into the project directory
        await auto.RunCommandAsync($"cd {ProjectName}", counter);

        // Step 3: Add the CommunityToolkit Ollama hosting integration with a specific version
        await auto.TypeAsync($"aspire add communitytoolkit-ollama --version {OllamaPackageVersion}");
        await auto.EnterAsync();
        await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(2));

        // Step 4: Modify AppHost.cs to add an Ollama container resource.
        // The Starter template scaffolds AppHost.cs with AddProject calls for apiservice and webfrontend.
        // We add an AddOllama call to create a standalone Ollama container resource.
        {
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName);
            var appHostDir = Path.Combine(projectDir, $"{ProjectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

            var content = File.ReadAllText(appHostFilePath);
            output.WriteLine($"Original AppHost.cs content:{Environment.NewLine}{content}");

            // Insert an AddOllama call after the builder creation line.
            // We add a standalone Ollama resource that the AppHost will manage as a container.
            var builderLine = "var builder = DistributedApplication.CreateBuilder(args);";
            var replacement = $"{builderLine}\n\nvar ollama = builder.AddOllama(\"ollama\");";

            content = content.Replace(builderLine, replacement);
            File.WriteAllText(appHostFilePath, content);

            output.WriteLine($"Modified AppHost.cs content:{Environment.NewLine}{content}");
        }

        // Step 5: Enable crash diagnostics so the native stack trace is captured if the AppHost segfaults.
        // Write crash reports to the workspace logs directory so they get captured on failure.
        var crashDir = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName, "crash");
        await auto.RunCommandAsync($"mkdir -p {crashDir}", counter);
        await auto.RunCommandAsync(
            $"export DOTNET_EnableCrashReport=1 DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=1 DOTNET_DbgMiniDumpName={crashDir}/coredump.%p",
            counter);

        // Step 6: Start the AppHost and verify it comes up successfully
        await auto.AspireStartAsync(counter, startTimeout: TimeSpan.FromMinutes(5));

        // Step 7: Wait for all resources to reach a running state.
        // The Starter template (no Redis) produces apiservice and webfrontend.
        // AddOllama adds a container resource named "ollama".
        foreach (var resource in new[] { "apiservice", "webfrontend", "ollama" })
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
