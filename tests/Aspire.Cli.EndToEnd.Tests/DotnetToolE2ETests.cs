// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for dotnet-tool-specific Aspire CLI behavior.
/// </summary>
public sealed class DotnetToolE2ETests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task DotnetToolInstall_UpdateSelfAndInit()
    {
        var strategy = DotnetToolE2ETestHelpers.ResolveRequiredStrategy();
        output.WriteLine($"DotnetTool strategy resolved: {strategy}");

        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire update --self");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText(UpdateCommandStrings.DotNetToolSelfUpdateMessage)
                 && s.ContainsText("dotnet tool update Aspire.Cli"),
            timeout: TimeSpan.FromSeconds(30),
            description: "waiting for dotnet tool self-update message");
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.RunCommandFailFastAsync("mkdir inittest && cd inittest && dotnet new web --name MyApi --output .", counter, TimeSpan.FromMinutes(2));
        await auto.RunCommandFailFastAsync("aspire init --language csharp --non-interactive", counter, TimeSpan.FromMinutes(3));

        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("test -f aspire.config.json && test -d MyApi.AppHost && test -d MyApi.ServiceDefaults && echo ASPIRE_INIT_OK");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("ASPIRE_INIT_OK"),
            timeout: TimeSpan.FromSeconds(10),
            description: "waiting for Aspire init outputs");
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
