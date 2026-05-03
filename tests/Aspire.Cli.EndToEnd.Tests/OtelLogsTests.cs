// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the aspire otel logs command with structured logs.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class OtelLogsTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task OtelLogsReturnsStructuredLogsFromStarterApp()
        => OtelLogsReturnsStructuredLogsFromStarterAppCore(isolated: false, useDevLocalhost: false);

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task OtelLogsReturnsStructuredLogsFromStarterApp_Isolated()
        => OtelLogsReturnsStructuredLogsFromStarterAppCore(isolated: true, useDevLocalhost: false);

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task OtelLogsReturnsStructuredLogsFromStarterApp_DevLocalhost()
        => OtelLogsReturnsStructuredLogsFromStarterAppCore(isolated: false, useDevLocalhost: true);

    private async Task OtelLogsReturnsStructuredLogsFromStarterAppCore(bool isolated, bool useDevLocalhost)
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
        await auto.AspireNewAsync("AspireOtelLogsApp", counter, useDevLocalhost: useDevLocalhost);

        // Navigate to the AppHost directory
        await auto.TypeAsync("cd AspireOtelLogsApp/AspireOtelLogsApp.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Start the AppHost
        await auto.AspireStartAsync(counter, isolated: isolated);

        // Wait for the apiservice resource to be running before querying logs
        await auto.TypeAsync("aspire wait apiservice --status up --timeout 300");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("is up (running).", timeout: TimeSpan.FromMinutes(6));
        await auto.WaitForSuccessPromptAsync(counter);

        // Run aspire otel logs and capture output to a file
        await auto.TypeAsync("aspire otel logs > otel_logs.txt 2>&1");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify the output contains structured log entries with apiservice content
        await auto.TypeAsync("cat otel_logs.txt | head -20");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Assert structured logs are present and contain apiservice entries
        await auto.TypeAsync("if [ ! -r otel_logs.txt ]; then echo 'OTEL_LOGS_FILE_UNREADABLE'; elif grep -q 'apiservice' otel_logs.txt; then echo 'STRUCTURED_LOGS_PRESENT'; else echo 'STRUCTURED_LOGS_MISSING'; fi");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("STRUCTURED_LOGS_PRESENT", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForAnyPromptAsync(counter);

        // Also verify JSON format works and contains structured data
        await auto.TypeAsync("aspire otel logs --format json > otel_logs_json.txt 2>&1");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify JSON output contains resourceLogs key
        await auto.TypeAsync("grep -q 'resourceLogs' otel_logs_json.txt && echo 'JSON_STRUCTURED_LOGS_PRESENT' || echo 'JSON_STRUCTURED_LOGS_MISSING'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("JSON_STRUCTURED_LOGS_PRESENT", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForAnyPromptAsync(counter);

        // Stop the AppHost
        await auto.AspireStopAsync(counter);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
