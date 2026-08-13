// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire CLI with Empty AppHost template.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class EmptyAppHostTemplateTests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task CreateAndRunEmptyAppHostProject()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.AspireNewAsync("AspireEmptyApp", counter, template: AspireTemplate.EmptyAppHost);

        // Start the empty AppHost to verify the scaffolded project works
        await auto.TypeAsync("cd AspireEmptyApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);
    }

    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task CreateEmptyAppHostWithSourceOverrideDoesNotContactNuGetOrg()
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

        await auto.RunCommandAsync("mkdir source-feed && cp \"$HOME\"/.aspire/hives/*/packages/Aspire.ProjectTemplates.*.nupkg source-feed/", counter);
        await auto.RunCommandAsync("aspire --version > /tmp/aspire-source-version", counter);
        await auto.RunCommandAsync("export ASPIRE_CLI_CHANNEL=staging ASPIRE_CLI_VERSION=\"$(cat /tmp/aspire-source-version)\"", counter);
        await auto.RunCommandAsync("rm -rf \"$HOME/.aspire/logs\" && mkdir -p \"$HOME/.aspire/logs\"", counter);

        await auto.RunCommandAsync(
            "aspire new aspire-empty --name SourceOverrideApp --output SourceOverrideApp --language csharp " +
            "--source source-feed --non-interactive --suppress-agent-init --localhost-tld false --log-level Debug",
            counter,
            TimeSpan.FromMinutes(2));

        await auto.RunCommandAsync("test -f SourceOverrideApp/apphost.cs", counter);
        await auto.RunCommandAsync(
            "find \"$HOME/.aspire/logs\" -type f -name '*.log' -print -quit | grep -q . && " +
            "! grep -R -F 'api.nuget.org' \"$HOME/.aspire/logs\"",
            counter);
    }
}
