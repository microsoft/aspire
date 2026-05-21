// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the aspire dashboard run command combined with aspire otel and aspire agent mcp.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class DashboardRunTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DashboardRunWithOtelTracesReturnsNoTraces()
    {
        await DashboardRunWithOtelTracesReturnsNoTracesCore("http://localhost:18888", "http://localhost:18888");
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DashboardRunWithOtelTracesReturnsNoTraces_DevLocalhost()
    {
        await DashboardRunWithOtelTracesReturnsNoTracesCore("http://dashboard.dev.localhost:18888", "http://localhost:18888");
    }

    private async Task DashboardRunWithOtelTracesReturnsNoTracesCore(string frontendUrl, string localhostUrl)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: false, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Use a known browser token so we can construct the dashboard URL directly,
        // avoiding the need to parse it from logs (which contain Spectre Console OSC 8
        // escape sequences that break grep-based URL extraction).
        var browserToken = "testtoken1234567890abcdef12345678";

        // Set the browser token env var before starting the dashboard
        await auto.TypeAsync($"export DASHBOARD__FRONTEND__BROWSERTOKEN={browserToken}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Store the dashboard log path inside the workspace so it gets captured on failure
        var dashboardLogPath = $"/workspace/{workspace.WorkspaceRoot.Name}/dashboard.log";

        // Start the dashboard in the background with the specified frontend URL
        await auto.TypeAsync($"aspire dashboard run --frontend-url {frontendUrl} > {dashboardLogPath} 2>&1 &");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Store the dashboard PID for cleanup
        await auto.TypeAsync("DASHBOARD_PID=$!");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Wait for the dashboard to become ready by polling the localhost URL
        await auto.TypeAsync($"for i in $(seq 1 30); do curl -ksSL -o /dev/null -w '%{{http_code}}' {localhostUrl} 2>/dev/null | grep -q 200 && break; sleep 1; done");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

        // Dump dashboard log for debugging visibility in the recording
        await auto.TypeAsync($"echo '=== DASHBOARD LOG ==='; cat {dashboardLogPath}; echo '=== END DASHBOARD LOG ==='");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Dump CLI logs for debugging
        await auto.TypeAsync("echo '=== CLI LOGS ==='; ls -lt ~/.aspire/logs/ 2>/dev/null; CLI_LOG=$(ls -t ~/.aspire/logs/cli_*.log 2>/dev/null | head -1); [ -n \"$CLI_LOG\" ] && tail -50 \"$CLI_LOG\"; echo '=== END CLI LOGS ==='");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Construct the dashboard URL using the known token and the frontend URL.
        // In the dev.localhost variant this exercises the CLI's NormalizeDashboardUrl path end-to-end.
        var dashboardUrl = $"{frontendUrl}/login?t={browserToken}";

        await auto.TypeAsync($"aspire otel traces --dashboard-url \"{dashboardUrl}\"");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("No traces found", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitForSuccessPromptAsync(counter);

        // Clean up: kill the background dashboard process
        await auto.TypeAsync("kill -9 $DASHBOARD_PID 2>/dev/null; wait $DASHBOARD_PID 2>/dev/null; true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task DashboardRunWithAgentMcpListTracesReturnsNoTraces()
        => DashboardRunWithAgentMcpCore("http://localhost:18888", "http://localhost:18888", "list_traces", "TRACES DATA");

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task DashboardRunWithAgentMcpListTracesReturnsNoTraces_DevLocalhost()
        => DashboardRunWithAgentMcpCore("http://dashboard.dev.localhost:18888", "http://localhost:18888", "list_traces", "TRACES DATA");

    private async Task DashboardRunWithAgentMcpCore(string frontendUrl, string localhostUrl, string toolName, string expectedMarker)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: false, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Use a known browser token so we can construct the dashboard URL directly
        var browserToken = "testtoken1234567890abcdef12345678";

        // Set the browser token env var before starting the dashboard
        await auto.TypeAsync($"export DASHBOARD__FRONTEND__BROWSERTOKEN={browserToken}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Store the dashboard log path inside the workspace so it gets captured on failure
        var dashboardLogPath = $"/workspace/{workspace.WorkspaceRoot.Name}/dashboard.log";

        // Start the dashboard in the background with the specified frontend URL
        await auto.TypeAsync($"aspire dashboard run --frontend-url {frontendUrl} > {dashboardLogPath} 2>&1 &");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Store the dashboard PID for cleanup
        await auto.TypeAsync("DASHBOARD_PID=$!");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Wait for the dashboard to become ready by polling the localhost URL
        await auto.TypeAsync($"for i in $(seq 1 30); do curl -ksSL -o /dev/null -w '%{{http_code}}' {localhostUrl} 2>/dev/null | grep -q 200 && break; sleep 1; done");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

        // Construct the dashboard URL using the known token and the frontend URL
        var dashboardUrl = $"{frontendUrl}/login?t={browserToken}";

        // Call the MCP tool against the standalone dashboard
        await auto.CallAgentMcpToolAsync(counter, toolName, expectedMarker, $"--dashboard-url \"{dashboardUrl}\"");

        // Clean up: kill the background dashboard process
        await auto.TypeAsync("kill -9 $DASHBOARD_PID 2>/dev/null; wait $DASHBOARD_PID 2>/dev/null; true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
