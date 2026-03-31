// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the TypeScript SQL Server native-assets polyglot repro using the Aspire bundle.
/// Validates that the bundled CLI can start the SQL Server resource and wait for the database
/// resource to reach the running state when native runtime assets are required.
/// </summary>
public sealed class TypeScriptSqlServerNativeAssetsBundleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task StartAndWaitForTypeScriptSqlServerAppHostWithNativeAssets()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(
            repoRoot,
            workspace,
            installMode,
            ["Aspire.Hosting.CodeGeneration.TypeScript.", "Aspire.Hosting.SqlServer."]);
        var bundlePath = FindLocalBundlePath(repoRoot, installMode);

        var additionalVolumes = new List<string>();
        if (bundlePath is not null)
        {
            additionalVolumes.Add($"{bundlePath}:/opt/aspire-bundle:ro");
        }

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            installMode,
            output,
            variant: CliE2ETestHelpers.DockerfileVariant.Polyglot,
            mountDockerSocket: true,
            workspace: workspace,
            additionalVolumes: additionalVolumes);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliInDockerAsync(installMode, counter);

        if (bundlePath is not null)
        {
            await auto.TypeAsync("ln -s /opt/aspire-bundle/managed ~/.aspire/managed && ln -s /opt/aspire-bundle/dcp ~/.aspire/dcp");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);
        }

        if (localChannel is not null)
        {
            var containerLocalChannelPackagesPath = CliE2ETestHelpers.ToContainerPath(localChannel.PackagesPath, workspace);
            await auto.TypeAsync($"mkdir -p ~/.aspire/hives/local && rm -rf ~/.aspire/hives/local/packages && ln -s '{containerLocalChannelPackagesPath}' ~/.aspire/hives/local/packages");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("aspire config set channel local --global");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync($"aspire config set sdk.version {localChannel.SdkVersion} --global");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);
        }

        await auto.TypeAsync("aspire init --language typescript --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.ts", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire add Aspire.Hosting.SqlServer");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("The package Aspire.Hosting.", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts");
        File.WriteAllText(appHostPath, """
            import { createBuilder, ContainerLifetime } from './.modules/aspire.js';

            const builder = await createBuilder();
            const sql = await builder.addSqlServer('sql')
                .withLifetime(ContainerLifetime.Persistent)
                .withDataVolume();

            await sql.addDatabase('mydb');
            await builder.build().run();
            """);

        await auto.AspireStartAsync(counter, TimeSpan.FromMinutes(4));

        await auto.TypeAsync("aspire wait mydb --status up --timeout 300");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("mydb is up (running).", timeout: TimeSpan.FromMinutes(6));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

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
