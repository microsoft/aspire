// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the aspire agent mcp command with structured logs.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class AgentMcpLogsTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task AgentMcpListStructuredLogsReturnsLogsFromStarterApp()
        => AgentMcpListStructuredLogsFromStarterAppCore(isolated: false, useDevLocalhost: false);

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task AgentMcpListStructuredLogsReturnsLogsFromStarterApp_Isolated()
        => AgentMcpListStructuredLogsFromStarterAppCore(isolated: true, useDevLocalhost: false);

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task AgentMcpListStructuredLogsReturnsLogsFromStarterApp_DevLocalhost()
        => AgentMcpListStructuredLogsFromStarterAppCore(isolated: false, useDevLocalhost: true);

    private async Task AgentMcpListStructuredLogsFromStarterAppCore(bool isolated, bool useDevLocalhost)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Create a new Starter project (includes an ASP.NET Core apiservice)
        await auto.AspireNewAsync("AspireMcpLogsApp", counter, useDevLocalhost: useDevLocalhost);

        // Navigate to the AppHost directory
        await auto.TypeAsync("cd AspireMcpLogsApp/AspireMcpLogsApp.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Start the AppHost
        await auto.AspireStartAsync(counter, isolated: isolated);

        // Wait for the apiservice resource to be running before querying logs
        await auto.TypeAsync("aspire wait apiservice --status up --timeout 300");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("is up (running).", timeout: TimeSpan.FromMinutes(6));
        await auto.WaitForSuccessPromptAsync(counter);

        // Call the MCP tool against the running AppHost
        await auto.CallAgentMcpToolAsync(counter, "list_structured_logs", "STRUCTURED LOGS DATA");

        // Stop the AppHost
        await auto.AspireStopAsync(counter);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
