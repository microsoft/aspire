// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire CLI with Python/React (FastAPI/Vite) template.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class PythonReactTemplateTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task CreateAndRunPythonReactProject()
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

        // Step 1: Create project using aspire new, selecting the FastAPI/React template
        await auto.AspireNewAsync("AspirePyReactApp", counter, template: AspireTemplate.PythonReact, useRedisCache: false);

        // Step 2: Navigate into the project directory so config resolution finds the
        // project-level aspire.config.json (which has the packages section).
        // See https://github.com/microsoft/aspire/issues/15623
        await auto.TypeAsync("cd AspirePyReactApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 3: Verify the generated TypeScript AppHost builds successfully.
        await auto.RunCommandFailFastAsync("npm run build", counter, TimeSpan.FromMinutes(2));

        // Step 4: Start and stop the project
        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
