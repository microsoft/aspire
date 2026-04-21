// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the aspire do --list-steps feature.
/// Verifies that the CLI can list pipeline steps without executing them.
/// </summary>
public sealed class ListStepsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DoListStepsShowsPipelineSteps()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Create a new Aspire project
        await auto.AspireNewAsync("ListStepsApp", counter);

        // Navigate to the AppHost project
        await auto.TypeAsync("cd ListStepsApp/ListStepsApp.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Run aspire do deploy --list-steps
        await auto.TypeAsync("aspire do deploy --list-steps");
        await auto.EnterAsync();

        // Wait for the output to contain step information
        // The output should contain numbered steps with dependencies
        await auto.WaitUntilAsync(s =>
            s.ContainsText("Depends on:") || s.ContainsText("No dependencies"),
            timeout: TimeSpan.FromMinutes(3),
            description: "waiting for --list-steps output with step dependency information");

        await auto.WaitForSuccessPromptAsync(counter);

        // Exit the terminal
        await auto.TypeAsync("exit");
        await auto.EnterAsync();
        await pendingRun;
    }
}
