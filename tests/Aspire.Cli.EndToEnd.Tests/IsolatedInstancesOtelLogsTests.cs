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

        // Start both instances with --isolated
        await auto.AspireStartAsync(counter, isolated: true, apphost: appHost1);
        await auto.AspireStartAsync(counter, isolated: true, apphost: appHost2);

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

        // Capture otel logs from both instances into workspace-relative files
        // so they are included in [CaptureWorkspaceOnFailure] artifacts.
        await auto.TypeAsync($"aspire otel logs --apphost {appHost1} --format json > otel_instance1.json 2>&1");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync($"aspire otel logs --apphost {appHost2} --format json > otel_instance2.json 2>&1");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Assert both files have structured log content (resourceLogs key)
        await auto.TypeAsync("grep -q 'resourceLogs' otel_instance1.json && echo 'INSTANCE1_HAS_LOGS' || echo 'INSTANCE1_NO_LOGS'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("INSTANCE1_HAS_LOGS", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForAnyPromptAsync(counter);

        await auto.TypeAsync("grep -q 'resourceLogs' otel_instance2.json && echo 'INSTANCE2_HAS_LOGS' || echo 'INSTANCE2_NO_LOGS'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("INSTANCE2_HAS_LOGS", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForAnyPromptAsync(counter);

        // Extract the ContentRoot values from each instance's structured logs and verify they
        // point at the correct instance directory. The OTLP JSON body contains log messages like:
        //   "Content root path: /workspace/instance1/IsolatedApp.AppHost"
        // Matching against the instance directory proves each AppHost runs from its own root.
        // Use unique sentinel markers so WaitUntilTextAsync doesn't match pre-existing scrollback
        // (e.g. from the mv/cp/--apphost commands that also contain "instance1"/"instance2").
        await auto.TypeAsync("grep -o 'Content root path: [^\"]*' otel_instance1.json | grep -q 'instance1' && echo 'CONTENTROOT1_OK' || echo 'CONTENTROOT1_FAIL'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("CONTENTROOT1_OK", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForAnyPromptAsync(counter);

        await auto.TypeAsync("grep -o 'Content root path: [^\"]*' otel_instance2.json | grep -q 'instance2' && echo 'CONTENTROOT2_OK' || echo 'CONTENTROOT2_FAIL'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("CONTENTROOT2_OK", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForAnyPromptAsync(counter);

        // Stop both instances
        await auto.AspireStopAsync(counter, apphost: appHost1);
        await auto.AspireStopAsync(counter, apphost: appHost2);
    }
}
