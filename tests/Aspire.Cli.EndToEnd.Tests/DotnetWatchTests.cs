// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class DotnetWatchTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task CSharpAppHostReconstructsTheModelAndCleansOldGenerations() => RunAsync("csharp");

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task TypeScriptAppHostAndManagedServicesUseTheSameWatchSelection() => RunAsync("typescript");

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task NativeTypeScriptAppHostRetainsItsSelectedServerWithReducedCoverage() => RunAsync("native");

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task ReplicasAndDuplicateLaunchesKeepStoppedInstanceIntentDuringRefresh() => RunAsync("replicas");

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task InitialBuildRecoveryAndCommandsRespectWatchBuildOwnership() => RunAsync("builds");

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task ManagedFileAppsAndLaunchContextsRemainWatchCompatible() => RunAsync("compatibility");

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task GuardedRefreshReevaluatesReferencesImportsAndSdkOutputIsolation() => RunAsync("isolation");

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task HttpsTrustAndPrivateWatchEnvironmentStayWithTheirOwners() => RunAsync("https");

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task PackedWatchPayloadResolvesWithoutRuntimeAcquisition() => RunAsync("payload");

    private async Task RunAsync(string scenario)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        if (strategy.Mode == CliInstallMode.InstallScript && strategy.Quality is null && strategy.Version is null)
        {
            Assert.Skip("Assembled watch regressions require the current repository CLI and packed integrations, not the released CLI.");
        }

        using var workspace = DotnetWatchTestHelpers.CreateWorkspace(repoRoot, output);
        await DotnetWatchTestHelpers.CopyVerifierAsync(repoRoot, workspace, TestContext.Current.CancellationToken);

        var preinstalled = strategy.Mode == CliInstallMode.Preinstalled;
        using var terminal = preinstalled
            ? CliE2ETestHelpers.CreateTestTerminal()
            : CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(
            terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        if (preinstalled)
        {
            await auto.PrepareEnvironmentAsync(workspace, counter);
            await auto.InstallAspireCliInShellAsync(strategy, counter);
        }
        else
        {
            await auto.PrepareDockerEnvironmentAsync(counter, workspace);
            await auto.InstallAspireCliAsync(strategy, counter);
        }

        try
        {
            // The verifier selects an installed SDK explicitly and owns only fixture-tagged
            // processes. A zero exit is insufficient: its marker follows HTTP and cleanup assertions.
            await auto.RunCommandAsync(
                $"python3 verify-watch.py {scenario}",
                counter,
                TimeSpan.FromMinutes(12));
        }
        catch (InvalidOperationException)
        {
            // An error prompt has been consumed, but RunCommandAsync has not advanced the
            // counter. Keep TerminalRun's diagnostic commands synchronized with the shell.
            counter.Increment();
            throw;
        }

        await auto.WaitUntilTextAsync($"WATCH_E2E_OK:{scenario}", timeout: TimeSpan.FromSeconds(10));
    }
}
