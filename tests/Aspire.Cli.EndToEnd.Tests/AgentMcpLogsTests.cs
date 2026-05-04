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

        // Send JSON-RPC messages to the MCP server via a compound command.
        // The sleeps ensure proper protocol timing between initialize, initialized notification, and tool call.
        await auto.TypeAsync(
            "{ " +
            "echo '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"e2e-test\",\"version\":\"0.1.0\"}}}'; " +
            "sleep 3; " +
            "echo '{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}'; " +
            "sleep 1; " +
            "echo '{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"list_structured_logs\",\"arguments\":{}}}'; " +
            "sleep 15; " +
            "} | aspire agent mcp > /tmp/mcp_out.txt 2>/tmp/mcp_err.txt || true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

        // Verify the MCP output contains a successful tool call response with structured logs
        await auto.TypeAsync("cat /tmp/mcp_out.txt | head -50");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Check that the response contains structured log data (the tool returns text with "STRUCTURED LOGS DATA")
        await auto.TypeAsync(
            "if grep -q 'STRUCTURED LOGS DATA' /tmp/mcp_out.txt; then echo 'MCP_LOGS_PRESENT'; " +
            "elif grep -q 'list_structured_logs' /tmp/mcp_out.txt; then echo 'MCP_TOOL_FOUND_BUT_NO_LOGS'; " +
            "else echo 'MCP_LOGS_MISSING'; fi");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("MCP_LOGS_PRESENT", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForAnyPromptAsync(counter);

        // Also verify the initialize response was received (confirms MCP handshake worked)
        await auto.TypeAsync(
            "grep -q 'aspire-mcp-server' /tmp/mcp_out.txt && echo 'MCP_INIT_OK' || echo 'MCP_INIT_MISSING'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("MCP_INIT_OK", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForAnyPromptAsync(counter);

        // Stop the AppHost
        await auto.AspireStopAsync(counter);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
