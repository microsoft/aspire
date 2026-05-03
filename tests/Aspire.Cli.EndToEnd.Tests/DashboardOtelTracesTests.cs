// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the aspire dashboard run and aspire otel traces commands.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class DashboardOtelTracesTests(ITestOutputHelper output)
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

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task AppHostOtelTracesReturnsTraces()
        => AppHostOtelTracesReturnsTracesCore(useDevLocalhost: false);

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task AppHostOtelTracesReturnsTraces_DevLocalhost()
        => AppHostOtelTracesReturnsTracesCore(useDevLocalhost: true);

    private async Task AppHostOtelTracesReturnsTracesCore(bool useDevLocalhost)
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

        // Create a new Starter project (includes an ASP.NET Core apiservice that generates traces)
        await auto.AspireNewAsync("AspireOtelTracesApp", counter, useDevLocalhost: useDevLocalhost);

        // Navigate to the AppHost directory
        await auto.TypeAsync("cd AspireOtelTracesApp/AspireOtelTracesApp.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Start the AppHost in the background
        await auto.AspireStartAsync(counter);

        // Wait for the apiservice resource to be running before querying traces
        await auto.TypeAsync("aspire wait apiservice --status up --timeout 300");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("is up (running).", timeout: TimeSpan.FromMinutes(6));
        await auto.WaitForSuccessPromptAsync(counter);

        // Generate some traces by hitting the apiservice endpoint via the dashboard's resource proxy
        await auto.TypeAsync("curl -sSL http://localhost:$(aspire describe apiservice --format json | grep -oP '\"port\":\\s*\\K[0-9]+')/weatherforecast > /dev/null 2>&1 || true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Brief pause so traces propagate to the dashboard
        await auto.TypeAsync("for i in $(seq 1 5); do sleep 1; done");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(15));

        // Run aspire otel traces and capture output to a file
        await auto.TypeAsync("aspire otel traces > otel_traces.txt 2>&1");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify the output contains trace entries
        await auto.TypeAsync("cat otel_traces.txt | head -20");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Check that traces are present (either from apiservice or webfrontend)
        await auto.TypeAsync("if [ ! -r otel_traces.txt ]; then echo 'OTEL_TRACES_FILE_UNREADABLE'; elif [ -s otel_traces.txt ] && ! grep -q 'No traces found' otel_traces.txt; then echo 'TRACES_PRESENT'; else echo 'TRACES_MISSING'; fi");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("TRACES_PRESENT", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForAnyPromptAsync(counter);

        // Also verify JSON format works
        await auto.TypeAsync("aspire otel traces --format json > otel_traces_json.txt 2>&1");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify JSON output contains trace data
        await auto.TypeAsync("grep -q 'traceId' otel_traces_json.txt && echo 'JSON_TRACES_PRESENT' || echo 'JSON_TRACES_MISSING'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("JSON_TRACES_PRESENT", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForAnyPromptAsync(counter);

        // Stop the AppHost
        await auto.AspireStopAsync(counter);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
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

        // Extract the dashboard login URL (with token) from the aspire dashboard run output.
        // The dashboard outputs: "Login to the dashboard at http://host:port/login?t=xxx ."
        // For the dev.localhost variant, the CLI normalizes *.localhost to localhost for HTTP requests.
        await auto.TypeAsync("OTEL_DASHBOARD_URL=$(grep -oE 'https?://[^ ]+/login\\?t=[^ ]+' " + dashboardLogPath + " | head -1 | sed 's/ \\.$//')");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("echo \"OTEL_DASHBOARD_URL=$OTEL_DASHBOARD_URL\"");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire otel traces --dashboard-url \"$OTEL_DASHBOARD_URL\"");
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
}
