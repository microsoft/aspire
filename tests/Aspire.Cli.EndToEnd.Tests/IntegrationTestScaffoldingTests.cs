// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class IntegrationTestScaffoldingTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task CreateAndRunAppHostIntegrationTest()
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
        await auto.RunCommandAsync("aspire config set features.showAllTemplates true -g", counter);
        await auto.RunCommandAsync(
            "aspire new aspire-starter --name IntegrationTestApp --output IntegrationTestApp --non-interactive --suppress-agent-init",
            counter,
            TimeSpan.FromMinutes(5));

        await auto.TypeAsync(
            "aspire new aspire-test " +
            "--apphost IntegrationTestApp/IntegrationTestApp.AppHost/IntegrationTestApp.AppHost.csproj " +
            "--name IntegrationTestApp.Tests --output IntegrationTestApp/IntegrationTestApp.Tests --suppress-agent-init");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("> MSTest").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(60),
            description: "integration test framework selection list (> MSTest)");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

        await auto.RunCommandAsync(
            "dotnet test IntegrationTestApp/IntegrationTestApp.Tests/IntegrationTestApp.Tests.csproj -- --filter-method \"*.AppHostBuilds\"",
            counter,
            TimeSpan.FromMinutes(5));
    }
}
