// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.TestUtilities;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class DotnetWatchCSharpTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task CSharpAppHostReconstructsTheModelAndCleansOldGenerations() =>
        DotnetWatchTestRunner.RunAsync(output, "csharp");
}

public sealed class DotnetWatchCSharpSdk10Tests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task CSharpAppHostOnSdk10ReconstructsTheModelAndCleansOldGenerations() =>
        DotnetWatchTestRunner.RunAsync(output, "csharp", "--sdk-major", "10");
}

public sealed class DotnetWatchMixedSdkTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task MixedSdkAppHostReconstructionAndServiceHotReloadUseMajorSpecificPayloads() =>
        DotnetWatchTestRunner.RunAsync(output, "mixed-sdk");
}

public sealed class DotnetWatchTypeScriptTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task TypeScriptAppHostAndManagedServicesUseTheSameWatchSelection() =>
        DotnetWatchTestRunner.RunAsync(output, "typescript");
}

public sealed class DotnetWatchNativeTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task NativeTypeScriptAppHostRetainsItsSelectedServerWithReducedCoverage() =>
        DotnetWatchTestRunner.RunAsync(output, "native");
}

public sealed class DotnetWatchReplicaTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task ReplicasAndDuplicateLaunchesKeepStoppedInstanceIntentDuringRefresh() =>
        DotnetWatchTestRunner.RunAsync(output, "replicas");
}

public sealed class DotnetWatchBuildTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task InitialBuildRecoveryAndCommandsRespectWatchBuildOwnership() =>
        DotnetWatchTestRunner.RunAsync(output, "builds");
}

public sealed class DotnetWatchBuildSdk10Tests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task InitialBuildRecoveryAndCommandsOnSdk10RespectWatchBuildOwnership() =>
        DotnetWatchTestRunner.RunAsync(output, "builds", "--sdk-major", "10");
}

public sealed class DotnetWatchCompatibilityTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task ManagedFileAppsAndLaunchContextsRemainWatchCompatible() =>
        DotnetWatchTestRunner.RunAsync(output, "compatibility");
}

public sealed class DotnetWatchIsolationTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task GuardedRefreshReevaluatesReferencesImportsAndSdkOutputIsolation() =>
        DotnetWatchTestRunner.RunAsync(output, "isolation");
}

public sealed class DotnetWatchHttpsTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task HttpsTrustAndPrivateWatchEnvironmentStayWithTheirOwners() =>
        DotnetWatchTestRunner.RunAsync(output, "https");
}

public sealed class DotnetWatchPayloadTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task PackedWatchPayloadResolvesWithoutRuntimeAcquisition() =>
        DotnetWatchTestRunner.RunAsync(output, "payload");
}

public sealed class DotnetWatchPolicyTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task CSharpAndTypeScriptResourceDefaultsOverridesAndNoBuildRemainIndependent() =>
        DotnetWatchTestRunner.RunAsync(output, "policy");
}

public sealed class DotnetWatchTerminalTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task RealForegroundPtyRetainsDiagnosticsWithoutRawWatcherOutput() =>
        DotnetWatchTestRunner.RunAsync(output, "terminal");
}

public sealed class DotnetWatchStartupTests(ITestOutputHelper output)
{
    [Fact]
    [OuterloopTest("Repeated cold/warm startup measurements and profiling captures.")]
    [CaptureWorkspaceOnFailure]
    public Task ResourceWatchStartupCostIsMeasuredSeparatelyFromAppHostWatching() =>
        DotnetWatchTestRunner.RunAsync(output, "startup");
}

internal static class DotnetWatchTestRunner
{
    internal static async Task RunAsync(ITestOutputHelper output, string scenario, params string[] verifierArguments)
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
            // The image supplies only the latest .NET 10 SDK. Watch scenarios exercise
            // the latest patch of each SDK major, so install .NET 11 from its channel.
            await auto.RunCommandAsync(
                "curl -fsSL https://dot.net/v1/dotnet-install.sh -o watch-dotnet-install.sh && " +
                "bash watch-dotnet-install.sh --channel 11.0 --install-dir /usr/share/dotnet --no-path && " +
                "rm watch-dotnet-install.sh",
                counter,
                TimeSpan.FromMinutes(5));
            await auto.InstallAspireCliAsync(strategy, counter);
        }

        try
        {
            // The verifier selects an installed SDK explicitly and owns only fixture-tagged
            // processes. A zero exit is insufficient: its marker follows HTTP and cleanup assertions.
            var arguments = string.Join(" ", verifierArguments.Select(argument => "'" + argument.Replace("'", "'\\''") + "'"));
            await auto.RunCommandAsync(
                $"python3 verify-watch.py {scenario} {arguments}",
                counter,
                TimeSpan.FromMinutes(scenario == "startup" ? 30 : 12));
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
