// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class IntegrationIndexTests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task CanSearchAndAddBuiltInStaticIndexEntry()
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

        await auto.RunCommandAsync("aspire integration search cache --format json | tee integration-search.json", counter, timeout: TimeSpan.FromMinutes(2));
        await auto.RunCommandAsync("grep -F '\"name\": \"redis\"' integration-search.json && grep -F '\"package\": \"Aspire.Hosting.Redis\"' integration-search.json", counter);

        await auto.AspireNewAsync("IndexApp", counter, template: AspireTemplate.EmptyAppHost);
        await auto.RunCommandAsync("cd IndexApp", counter);

        await auto.TypeAsync("aspire add aspire/redis --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForAspireAddSuccessAsync(counter, timeout: TimeSpan.FromMinutes(4));

        await auto.RunCommandAsync("grep -R -F 'Aspire.Hosting.Redis' .", counter);
    }
}
