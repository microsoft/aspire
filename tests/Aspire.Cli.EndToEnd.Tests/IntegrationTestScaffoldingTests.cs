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
        await auto.RunCommandAsync(
            "aspire new aspire-starter --name IntegrationTestApp --output IntegrationTestApp --non-interactive --suppress-agent-init",
            counter,
            TimeSpan.FromMinutes(5));
        await auto.RunCommandAsync(
            "echo '// TypeScript AppHost' > IntegrationTestApp/apphost.mts",
            counter);

        await auto.TypeAsync(
            "aspire new aspire-test " +
            "--name IntegrationTestApp.Discovered.Tests --output IntegrationTestApp/IntegrationTestApp.Discovered.Tests --suppress-agent-init");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("> MSTest").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(60),
            description: "integration test framework selection list (> MSTest)");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

        await auto.RunCommandAsync(
            "aspire new aspire-test " +
            "--apphost IntegrationTestApp/IntegrationTestApp.AppHost/IntegrationTestApp.AppHost.csproj " +
            "--test-framework MSTest --name IntegrationTestApp.Explicit.Tests " +
            "--output IntegrationTestApp/IntegrationTestApp.Explicit.Tests --non-interactive --suppress-agent-init",
            counter,
            TimeSpan.FromMinutes(5));

        await auto.RunCommandAsync(
            "aspire new aspire-test " +
            "--apphost IntegrationTestApp " +
            "--test-framework MSTest --name IntegrationTestApp.Directory.Tests " +
            "--output IntegrationTestApp/IntegrationTestApp.Directory.Tests --non-interactive --suppress-agent-init",
            counter,
            TimeSpan.FromMinutes(5));

        await auto.RunCommandAsync(
            "dotnet test IntegrationTestApp/IntegrationTestApp.Discovered.Tests/IntegrationTestApp.Discovered.Tests.csproj -- --filter-method \"*.AppHostBuilds\"",
            counter,
            TimeSpan.FromMinutes(5));
        await auto.RunCommandAsync(
            "dotnet test IntegrationTestApp/IntegrationTestApp.Explicit.Tests/IntegrationTestApp.Explicit.Tests.csproj -- --filter-method \"*.AppHostBuilds\"",
            counter,
            TimeSpan.FromMinutes(5));
        await auto.RunCommandAsync(
            "dotnet test IntegrationTestApp/IntegrationTestApp.Directory.Tests/IntegrationTestApp.Directory.Tests.csproj -- --filter-method \"*.AppHostBuilds\"",
            counter,
            TimeSpan.FromMinutes(5));

        // Older templates should remain standalone even when discovery would find multiple AppHosts.
        await auto.RunCommandAsync(
            "mkdir IntegrationTestApp/SecondAppHost && " +
            "cp IntegrationTestApp/IntegrationTestApp.AppHost/IntegrationTestApp.AppHost.csproj IntegrationTestApp/SecondAppHost/SecondAppHost.csproj",
            counter);
        await auto.RunCommandAsync(
            "aspire new aspire-test --version 13.5.3 --source https://api.nuget.org/v3/index.json " +
            "--test-framework MSTest --name OlderTemplate.Tests --output OlderTemplate.Tests --non-interactive --suppress-agent-init",
            counter,
            TimeSpan.FromMinutes(5));
        await auto.RunCommandAsync(
            "dotnet build OlderTemplate.Tests/OlderTemplate.Tests.csproj",
            counter,
            TimeSpan.FromMinutes(5));

        await auto.TypeAsync(
            "aspire new aspire-test --version 13.5.3 --source https://api.nuget.org/v3/index.json " +
            "--apphost IntegrationTestApp/IntegrationTestApp.AppHost/IntegrationTestApp.AppHost.csproj " +
            "--test-framework MSTest --name UnsupportedTemplate.Tests --output UnsupportedTemplate.Tests --non-interactive --suppress-agent-init");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("does not support AppHost references").Search(s).Count > 0,
            timeout: TimeSpan.FromMinutes(5),
            description: "unsupported template AppHost reference diagnostic");
        var errorPrompt = new CellPatternSearcher().Find($"[{counter.Value} ERR:");
        await auto.WaitUntilAsync(
            s => errorPrompt.Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(30),
            description: "unsupported template command returning a non-zero exit code");
        counter.Increment();
    }
}
