// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the TypeScript Express/React starter template (aspire-ts-starter).
/// Validates that aspire new creates a working Express API + React frontend project
/// and that aspire run starts it successfully.
/// </summary>
public sealed class TypeScriptStarterTemplateTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CreateAndRunTypeScriptStarterProject()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(repoRoot, workspace, installMode);
        var bundlePath = FindLocalBundlePath(repoRoot, installMode);

        var additionalVolumes = new List<string>();
        if (bundlePath is not null)
        {
            additionalVolumes.Add($"{bundlePath}:/opt/aspire-bundle:ro");
        }

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, mountDockerSocket: true, workspace: workspace, additionalVolumes: additionalVolumes);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliInDockerAsync(installMode, counter);

        // Set up bundle layout for SourceBuild mode so the CLI can find
        // aspire-managed and DCP relative to the CLI binary location.
        if (bundlePath is not null)
        {
            await auto.TypeAsync("ln -s /opt/aspire-bundle/managed ~/.aspire/managed && ln -s /opt/aspire-bundle/dcp ~/.aspire/dcp");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);
        }

        // Set up local channel NuGet packages for SourceBuild mode so the
        // CLI can resolve Aspire packages during template creation.
        if (localChannel is not null)
        {
            var containerLocalChannelPackagesPath = CliE2ETestHelpers.ToContainerPath(localChannel.PackagesPath, workspace);
            await auto.TypeAsync($"mkdir -p ~/.aspire/hives/local && rm -rf ~/.aspire/hives/local/packages && ln -s '{containerLocalChannelPackagesPath}' ~/.aspire/hives/local/packages");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Set channel and SDK version globally so aspire new uses the local
            // channel with the correct prerelease version (dev builds fall back to
            // the last stable release by default, which won't match local packages).
            await auto.TypeAsync("aspire config set channel local --global");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync($"aspire config set sdk.version {localChannel.SdkVersion} --global");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);
        }

        // Step 1: Create project using aspire new, selecting the Express/React template
        await auto.AspireNewAsync("TsStarterApp", counter, template: AspireTemplate.ExpressReact);

        // Step 1.5: Verify starter creation also restored the generated TypeScript SDK.
        var projectRoot = Path.Combine(workspace.WorkspaceRoot.FullName, "TsStarterApp");
        var modulesDir = Path.Combine(projectRoot, ".modules");

        if (!Directory.Exists(modulesDir))
        {
            throw new InvalidOperationException($".modules directory was not created at {modulesDir}");
        }

        var aspireModulePath = Path.Combine(modulesDir, "aspire.ts");
        if (!File.Exists(aspireModulePath))
        {
            throw new InvalidOperationException($"Expected generated file not found: {aspireModulePath}");
        }

        // Step 2: Navigate into the project and start it
        await auto.TypeAsync("cd TsStarterApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    /// <summary>
    /// Copies locally-built NuGet packages to the workspace for SourceBuild mode.
    /// Returns null for non-SourceBuild modes (CI installs packages via the PR script).
    /// </summary>
    /// <summary>
    /// Finds the extracted bundle layout directory for SourceBuild mode.
    /// The bundle provides the aspire-managed server and DCP needed for template creation.
    /// Returns null for non-SourceBuild modes (CI installs the full bundle via the PR script).
    /// </summary>
    private static string? FindLocalBundlePath(string repoRoot, CliE2ETestHelpers.DockerInstallMode installMode)
    {
        if (installMode != CliE2ETestHelpers.DockerInstallMode.SourceBuild)
        {
            return null;
        }

        var bundlePath = Path.Combine(repoRoot, "artifacts", "bundle", "linux-x64");
        if (!Directory.Exists(bundlePath))
        {
            throw new InvalidOperationException("Local source-built TypeScript E2E tests require the bundle layout. Run './build.sh --bundle' first.");
        }

        var managedPath = Path.Combine(bundlePath, "managed", "aspire-managed");
        if (!File.Exists(managedPath))
        {
            throw new InvalidOperationException($"Bundle layout is missing aspire-managed at {managedPath}. Run './build.sh --bundle' first.");
        }

        return bundlePath;
    }
}
