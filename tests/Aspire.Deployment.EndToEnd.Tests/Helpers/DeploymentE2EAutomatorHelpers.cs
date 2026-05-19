// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests.Helpers;

/// <summary>
/// Extension methods for <see cref="Hex1bTerminalAutomator"/> providing deployment E2E test patterns.
/// </summary>
internal static class DeploymentE2EAutomatorHelpers
{
    /// <summary>
    /// Prepares the terminal environment with a custom prompt for command tracking.
    /// </summary>
    internal static async Task PrepareEnvironmentAsync(
        this Hex1bTerminalAutomator auto,
        TemporaryWorkspace workspace,
        SequenceCounter counter)
    {
        await auto.PrepareBashEnvironmentAsync(workspace.WorkspaceRoot.FullName, counter, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Installs the Aspire CLI in a non-Docker shell using the given install strategy.
    /// </summary>
    internal static async Task InstallAspireCliAsync(
        this Hex1bTerminalAutomator auto,
        CliInstallStrategy strategy,
        SequenceCounter counter,
        bool includeBundlePath = false)
    {
        switch (strategy.Mode)
        {
            case CliInstallMode.LocalHive:
                var archivePath = strategy.ArchivePath ?? throw new InvalidOperationException("LocalHive strategy is missing the archive path.");
                await auto.ExtractLocalHiveArchiveAsync(archivePath, counter);
                await auto.SourceAspireEnvironmentAsync(counter, includeBundlePath);
                await auto.ConfigureLocalHiveAsync(counter);
                break;

            case CliInstallMode.Preinstalled:
                await auto.SourceAspireEnvironmentAsync(counter, includeBundlePath);
                break;

            case CliInstallMode.PullRequest:
                var prNumber = DeploymentE2ETestHelpers.GetPrNumber();
                if (prNumber <= 0)
                {
                    throw new InvalidOperationException("PullRequest strategy requires a positive GITHUB_PR_NUMBER.");
                }

                if (includeBundlePath)
                {
                    await auto.RunCommandFailFastAsync(
                        AspireCliShellCommandHelpers.GetBundlePullRequestInstallCommand(prNumber),
                        counter,
                        TimeSpan.FromSeconds(300));
                }
                else
                {
                    await auto.RunCommandFailFastAsync(
                        AspireCliShellCommandHelpers.GetPullRequestInstallCommand(prNumber, AspireCliShellCommandHelpers.MainPullRequestInstallCommandPrefix),
                        counter,
                        TimeSpan.FromSeconds(300));
                }

                await auto.SourceAspireEnvironmentAsync(counter, includeBundlePath);
                break;

            case CliInstallMode.LocalArchive:
                var archiveDir = strategy.ArchiveDir ?? throw new InvalidOperationException("LocalArchive strategy is missing the archive directory.");
                await auto.RunCommandFailFastAsync(
                    AspireCliShellCommandHelpers.GetLocalArchiveInstallCommandFromCurrentRef(archiveDir),
                    counter,
                    TimeSpan.FromSeconds(120));
                await auto.SourceAspireEnvironmentAsync(counter, includeBundlePath);
                break;

            case CliInstallMode.InstallScript:
                await auto.RunCommandFailFastAsync(
                    AspireCliShellCommandHelpers.GetInstallScriptCommand(strategy, AspireCliShellCommandHelpers.AkaMsInstallScriptCommandPrefix),
                    counter,
                    TimeSpan.FromSeconds(300));
                await auto.SourceAspireEnvironmentAsync(counter, includeBundlePath);
                break;

            case CliInstallMode.DotnetTool:
                throw new InvalidOperationException(
                    "DotnetTool install mode is not supported for deployment E2E tests. " +
                    "Deployment tests require the native CLI. Clear ASPIRE_E2E_DOTNET_TOOL* environment variables and retry.");

            default:
                throw new ArgumentOutOfRangeException(nameof(strategy), strategy.Mode, "Unknown install mode");
        }

        await auto.LogAspireCliVersionAsync(counter);
    }

    /// <summary>
    /// Logs and installs the specified Aspire CLI strategy for a deployment test step.
    /// </summary>
    internal static async Task<CliInstallStrategy> InstallAspireCliAsync(
        this Hex1bTerminalAutomator auto,
        CliInstallStrategy strategy,
        SequenceCounter counter,
        ITestOutputHelper output,
        string stepLabel = "Step 2",
        bool includeBundlePath = false,
        string artifactName = "CLI")
    {
        output.WriteLine($"{stepLabel}: Installing Aspire {artifactName} using {strategy}...");
        await auto.InstallAspireCliAsync(strategy, counter, includeBundlePath);
        return strategy;
    }

    /// <summary>
    /// Installs the current-build Aspire CLI using the deployment test defaults.
    /// </summary>
    internal static Task<CliInstallStrategy> InstallCurrentBuildAspireCliAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        ITestOutputHelper output,
        string stepLabel = "Step 2")
    {
        return auto.InstallAspireCliAsync(
            DeploymentE2ETestHelpers.GetCurrentBuildCliInstallStrategy(),
            counter,
            output,
            stepLabel);
    }

    /// <summary>
    /// Installs the current-build Aspire bundle using the deployment test defaults.
    /// </summary>
    internal static Task<CliInstallStrategy> InstallCurrentBuildAspireBundleAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        ITestOutputHelper output,
        string stepLabel = "Step 2")
    {
        return auto.InstallAspireCliAsync(
            DeploymentE2ETestHelpers.GetCurrentBuildCliInstallStrategy(),
            counter,
            output,
            stepLabel,
            includeBundlePath: true,
            artifactName: "bundle");
    }

    /// <summary>
    /// Configures the PATH and environment variables for the Aspire CLI.
    /// </summary>
    internal static async Task SourceAspireCliEnvironmentAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        await auto.SourceAspireEnvironmentAsync(counter, includeBundlePath: false);
    }

    /// <summary>
    /// Destroys the current deployment using <c>aspire destroy --yes</c> and waits for pipeline success.
    /// </summary>
    internal static async Task AspireDestroyAsync(
        this Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromMinutes(5);
        await auto.TypeAsync("aspire destroy --yes");
        await auto.EnterAsync();
        await auto.WaitForPipelineSuccessAsync(timeout: timeout.Value);
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));
    }
}
