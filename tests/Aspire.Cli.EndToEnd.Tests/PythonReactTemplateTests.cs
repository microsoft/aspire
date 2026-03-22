// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire CLI with Python/React (FastAPI/Vite) template.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class PythonReactTemplateTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CreateAndRunPythonReactProject()
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

        await auto.AspireNewAsync("AspirePyReactApp", counter, template: AspireTemplate.PythonReact, useRedisCache: false);

        // Run the project with aspire run
        await auto.TypeAsync("aspire run");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Press CTRL+C to stop the apphost and exit.", timeout: TimeSpan.FromMinutes(2));

        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
