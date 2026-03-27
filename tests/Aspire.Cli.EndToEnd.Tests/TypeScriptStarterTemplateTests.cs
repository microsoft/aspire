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

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliInDockerAsync(installMode, counter);

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
}
