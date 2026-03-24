// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for running .NET csproj AppHost projects using the Aspire bundle.
/// Validates that the bundle correctly provides DCP and Dashboard paths to the hosting
/// infrastructure when running SDK-based app hosts (not just polyglot/guest app hosts).
/// </summary>
public sealed class BundleSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CreateAndRunAspireStarterProjectWithBundle()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliInDockerAsync(installMode, counter);

        await auto.AspireNewAsync("BundleStarterApp", counter);

        // Start AppHost in detached mode and capture JSON output
        await auto.TypeAsync("aspire start --format json | tee /tmp/aspire-detach.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

        // Verify the dashboard is reachable by extracting the URL from the detach output
        // and curling it. Extract just the base URL (https://localhost:PORT) using sed, which is
        // portable across macOS (BSD) and Linux (GNU) unlike grep -oP.
        await auto.TypeAsync("DASHBOARD_URL=$(sed -n 's/.*\"dashboardUrl\"[[:space:]]*:[[:space:]]*\"\\(https:\\/\\/localhost:[0-9]*\\).*/\\1/p' /tmp/aspire-detach.json | head -1)");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("curl -ksSL -o /dev/null -w 'dashboard-http-%{http_code}' \"$DASHBOARD_URL\" || echo 'dashboard-http-failed'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("dashboard-http-200", timeout: TimeSpan.FromSeconds(15));
        await auto.WaitForSuccessPromptAsync(counter);

        // Clean up: use aspire stop to gracefully shut down the detached AppHost.
        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
