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
        await auto.RunCommandAsync("aspire config set features.updateNotificationsEnabled false -g", counter);
        await auto.RunCommandAsync("export ASPIRE_CLI_CHANNEL=staging ASPIRE_CLI_VERSION=\"$(cat /tmp/aspire-source-version)\"", counter);

        // Package search can suppress source failures, so use a TCP tripwire to detect connection attempts independently of logs.
        await auto.RunCommandAsync("printf '127.0.0.1 api.nuget.org azuresearch-usnc.nuget.org azuresearch-ussc.nuget.org\\n' >> /etc/hosts", counter);
        await auto.RunCommandAsync(
            "rm -f /tmp/nuget-org-contacted /tmp/nuget-org-listener-ready && " +
            "python3 -c 'import pathlib,socket; s=socket.socket(); s.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1); " +
            "s.bind((\"0.0.0.0\",443)); s.listen(); pathlib.Path(\"/tmp/nuget-org-listener-ready\").touch(); " +
            "c,_=s.accept(); pathlib.Path(\"/tmp/nuget-org-contacted\").touch(); c.close()' >/tmp/nuget-org-listener.log 2>&1 & " +
            "while [ ! -f /tmp/nuget-org-listener-ready ]; do sleep 0.1; done",
            counter);
        await auto.RunCommandAsync("rm -rf \"$HOME/.aspire/logs\" && mkdir -p \"$HOME/.aspire/logs\"", counter);

        await auto.RunCommandAsync(
            "aspire new aspire-empty --name SourceOverrideApp --output SourceOverrideApp --language csharp " +
            "--source source-feed --non-interactive --suppress-agent-init --localhost-tld false --log-level Debug",
            counter,
            TimeSpan.FromMinutes(2));

        await auto.RunCommandAsync("test -f SourceOverrideApp/apphost.cs", counter);
        await auto.RunCommandAsync(
            "test ! -e /tmp/nuget-org-contacted && " +
            "find \"$HOME/.aspire/logs\" -type f -name '*.log' -print -quit | grep -q . && " +
            "! grep -R -F 'api.nuget.org' \"$HOME/.aspire/logs\"",
            counter);
    }
}
