// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests verifying that two isolated Aspire instances produce distinct telemetry.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class IsolatedInstancesOtelLogsTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task TwoIsolatedInstancesProduceDifferentOtelLogs()
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

        // Create a new Starter project (without Redis to avoid extra Docker dependencies)
        await auto.AspireNewAsync("IsolatedApp", counter, useRedisCache: false);

        // Move the created project into instance1 directory and copy to instance2
        await auto.TypeAsync("mv IsolatedApp instance1");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("cp -r instance1 instance2");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Define AppHost paths for both instances
        const string appHost1 = "instance1/IsolatedApp.AppHost/IsolatedApp.AppHost.csproj";
        const string appHost2 = "instance2/IsolatedApp.AppHost/IsolatedApp.AppHost.csproj";

        // Start instance1 with --isolated
        await auto.TypeAsync($"aspire start --isolated --apphost {appHost1}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Start instance2 with --isolated
        await auto.TypeAsync($"aspire start --isolated --apphost {appHost2}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Wait for the apiservice resource in instance1 to be running
        await auto.TypeAsync($"aspire wait apiservice --apphost {appHost1} --status up --timeout 300");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("is up (running).", timeout: TimeSpan.FromMinutes(6));
        await auto.WaitForSuccessPromptAsync(counter);

        // Wait for the apiservice resource in instance2 to be running
        await auto.TypeAsync($"aspire wait apiservice --apphost {appHost2} --status up --timeout 300");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("is up (running).", timeout: TimeSpan.FromMinutes(6));
        await auto.WaitForSuccessPromptAsync(counter);

        // Capture otel logs from instance1
        await auto.TypeAsync($"aspire otel logs --apphost {appHost1} --format json > /tmp/otel_instance1.json 2>&1");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Capture otel logs from instance2
        await auto.TypeAsync($"aspire otel logs --apphost {appHost2} --format json > /tmp/otel_instance2.json 2>&1");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Assert both files have structured log content (resourceLogs key)
        await auto.TypeAsync("grep -q 'resourceLogs' /tmp/otel_instance1.json && echo 'INSTANCE1_HAS_LOGS' || echo 'INSTANCE1_NO_LOGS'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("INSTANCE1_HAS_LOGS", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForAnyPromptAsync(counter);

        await auto.TypeAsync("grep -q 'resourceLogs' /tmp/otel_instance2.json && echo 'INSTANCE2_HAS_LOGS' || echo 'INSTANCE2_NO_LOGS'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("INSTANCE2_HAS_LOGS", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForAnyPromptAsync(counter);

        // Assert that telemetry from the two instances is different.
        // Each instance has its own service.instance.id in OTLP, so the JSON payloads differ.
        await auto.TypeAsync("if ! diff -q /tmp/otel_instance1.json /tmp/otel_instance2.json > /dev/null 2>&1; then echo 'TELEMETRY_DIFFERS'; else echo 'TELEMETRY_SAME'; fi");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("TELEMETRY_DIFFERS", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForAnyPromptAsync(counter);

        // Stop both instances
        await auto.TypeAsync($"aspire stop --apphost {appHost1}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync($"aspire stop --apphost {appHost2}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }
}
