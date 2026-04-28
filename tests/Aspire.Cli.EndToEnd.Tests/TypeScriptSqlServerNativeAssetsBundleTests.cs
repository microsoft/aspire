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
    [CaptureWorkspaceOnFailure]
    public async Task StartAndWaitForTypeScriptSqlServerAppHostWithNativeAssets()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            variant: CliE2ETestHelpers.DockerfileVariant.Polyglot,
            mountDockerSocket: true,
            workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

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
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
