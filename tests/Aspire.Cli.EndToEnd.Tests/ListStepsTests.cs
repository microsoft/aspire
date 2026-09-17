// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the --list-steps feature on the aspire do, publish, and deploy commands.
/// Verifies that the CLI can list pipeline steps without executing them.
/// </summary>
public sealed class ListStepsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DoPublishAndDeployListStepsWork()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Create a new Aspire project
        await auto.AspireNewAsync("ListStepsApp", counter);

        // Navigate to the AppHost project
        await auto.TypeAsync("cd ListStepsApp/ListStepsApp.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // 1. `aspire do --list-steps` lists every available step without requiring a target.
        await auto.TypeAsync("aspire do --list-steps");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(s =>
            s.ContainsText("Depends on:") || s.ContainsText("No dependencies"),
            timeout: TimeSpan.FromMinutes(3),
            description: "waiting for aspire do --list-steps output");
        await auto.WaitForSuccessPromptAsync(counter);

        // 2. `aspire do <step> --list-steps` lists pipeline steps for that step.
        await auto.TypeAsync("aspire do deploy --list-steps");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(s =>
            s.ContainsText("Depends on:") || s.ContainsText("No dependencies"),
            timeout: TimeSpan.FromMinutes(3),
            description: "waiting for aspire do deploy --list-steps output");
        await auto.WaitForSuccessPromptAsync(counter);

        // 3. `aspire publish --list-steps` lists steps for the publish target.
        await auto.TypeAsync("aspire publish --list-steps");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(s =>
            s.ContainsText("Depends on:") || s.ContainsText("No dependencies"),
            timeout: TimeSpan.FromMinutes(3),
            description: "waiting for aspire publish --list-steps output");
        await auto.WaitForSuccessPromptAsync(counter);

        // 4. `aspire deploy --list-steps` lists steps for the deploy target.
        await auto.TypeAsync("aspire deploy --list-steps");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(s =>
            s.ContainsText("Depends on:") || s.ContainsText("No dependencies"),
            timeout: TimeSpan.FromMinutes(3),
            description: "waiting for aspire deploy --list-steps output");
        await auto.WaitForSuccessPromptAsync(counter);

        // Exit the terminal
    }
}
