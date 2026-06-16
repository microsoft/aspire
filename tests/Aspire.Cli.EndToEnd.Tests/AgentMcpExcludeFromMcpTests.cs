// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the ExcludeFromMcp resource filtering in the aspire agent mcp command.
/// Verifies that resources marked with ExcludeFromMcp() are hidden from MCP tool results.
/// </summary>
public sealed class AgentMcpExcludeFromMcpTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task AgentMcpListResources_ExcludesResourceMarkedWithExcludeFromMcp()
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

        await auto.AspireNewAsync("McpExcludeApp", counter, useRedisCache: false);

        // Modify the AppHost to mark apiservice with ExcludeFromMcp()
        var appHostFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "McpExcludeApp", "McpExcludeApp.AppHost", "AppHost.cs");
        var content = File.ReadAllText(appHostFilePath);
        var modified = content.Replace(
            ".WithHttpHealthCheck(\"/health\");",
            ".WithHttpHealthCheck(\"/health\")\n    .ExcludeFromMcp();",
            StringComparison.Ordinal);
        File.WriteAllText(appHostFilePath, modified);

        // Navigate to the AppHost directory
        await auto.TypeAsync("cd McpExcludeApp/McpExcludeApp.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Start the AppHost
        await auto.AspireStartAsync(counter);

        // Wait for webfrontend to be up (confirms the app is running)
        await auto.TypeAsync("aspire wait webfrontend --status up --timeout 300");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("is up (running).", timeout: TimeSpan.FromMinutes(6));
        await auto.WaitForSuccessPromptAsync(counter);

        // Call list_resources via MCP and verify apiservice is excluded but webfrontend is present
        await auto.CallAgentMcpToolAsync(
            counter,
            "list_resources",
            expectedMarker: "\"webfrontend\"",
            doesNotContainMarker: "\"apiservice\"");

        // Stop the AppHost
        await auto.AspireStopAsync(counter);
    }
}
