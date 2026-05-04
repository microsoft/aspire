// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the TypeScript Empty AppHost template (aspire-ts-empty).
/// Validates that aspire new creates a working TypeScript AppHost project
/// and that aspire start runs it successfully.
/// </summary>
public sealed class TypeScriptEmptyAppHostTemplateTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task CreateAndRunTypeScriptEmptyAppHostProject()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.AspireNewAsync("TsEmptyApp", counter, template: AspireTemplate.TypeScriptEmptyAppHost);

        GitIgnoreAssertions.AssertContainsEntry(
            Path.Combine(workspace.WorkspaceRoot.FullName, "TsEmptyApp"),
            ".aspire/");

        // Start the empty TypeScript AppHost to verify the scaffolded project works
        await auto.TypeAsync("cd TsEmptyApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.RunCommandFailFastAsync("npm run build", counter, TimeSpan.FromMinutes(2));

        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task RunTypeScriptEmptyAppHost_ForwardsArgumentsToAppHostProcess()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.AspireNewAsync("TsRunArgsApp", counter, template: AspireTemplate.TypeScriptEmptyAppHost);

        var projectRoot = Path.Combine(workspace.WorkspaceRoot.FullName, "TsRunArgsApp");
        File.WriteAllText(Path.Combine(projectRoot, "apphost.ts"), """
            // Aspire TypeScript AppHost
            // For more information, see: https://aspire.dev

            import { writeFileSync } from 'node:fs';
            import { createBuilder } from './.modules/aspire.js';

            writeFileSync('run-args.txt', process.argv.slice(2).join('\n'));

            const builder = await createBuilder();

            await builder.build().run();
            """);

        await auto.TypeAsync("cd TsRunArgsApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire run -- arg1=value1 --flag -- literal");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(s =>
        {
            if (s.ContainsText("Select an AppHost to use:"))
            {
                throw new InvalidOperationException(
                    "Unexpected apphost selection prompt detected! " +
                    "This indicates multiple apphosts were incorrectly detected.");
            }

            return s.ContainsText("Press CTRL+C to stop the AppHost and exit.");
        }, timeout: TimeSpan.FromMinutes(3), description: "Press CTRL+C message (aspire run started)");

        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForSuccessPromptAsync(counter);

        var runArgsPath = Path.Combine(projectRoot, "run-args.txt");
        Assert.True(File.Exists(runArgsPath), $"Expected forwarded arguments file not found: {runArgsPath}");
        Assert.Equal(["arg1=value1", "--flag", "--", "literal"], File.ReadAllLines(runArgsPath));

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
