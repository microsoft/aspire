// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// Regression coverage for https://github.com/microsoft/aspire/issues/16513.
/// TypeScript polyglot AppHosts run under the generic "aspire-managed" host process.
/// <c>AtsTypeScriptCodeGenerator</c> forwards <c>ASPIRE_APPHOST_FILEPATH</c> /
/// <c>ASPIRE_PROJECT_DIRECTORY</c> through <c>createBuilder</c> so the AppHost reports its
/// real path over the backchannel; this test guards that wiring by starting the AppHost in
/// the background and stopping it from an unrelated cwd via
/// <c>aspire stop --apphost &lt;directory&gt;</c>. If the forwarding ever regresses the stop
/// call would fail to locate the running AppHost.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class TypeScriptPolyglotApphostDirectoryTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task StopTypeScriptPolyglotAppHostUsingApphostDirectory()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(repoRoot, strategy,
            ["Aspire.Hosting.CodeGeneration.TypeScript.", "Aspire.Hosting.JavaScript."]);

        // LocalHive strategy only: PrepareLocalChannel returned a real channel, so pass
        // --channel local explicitly. Other strategies return null and rely on the CLI's baked
        // channel + ambient NuGet feeds.
        var channelArgument = localChannel is not null ? " --channel local" : string.Empty;

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.Polyglot, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Put the AppHost in a subdirectory so the stop command must resolve the AppHost via
        // the path the running host reported over the backchannel rather than via the current
        // cwd. This is the scenario from issue 16513.
        await auto.TypeAsync("mkdir tsapp && cd tsapp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync($"aspire init --language typescript --non-interactive{channelArgument}");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.mts", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        var projectRoot = Path.Combine(workspace.WorkspaceRoot.FullName, "tsapp");

        if (localChannel is not null)
        {
            CliE2ETestHelpers.WriteLocalChannelSettings(projectRoot, localChannel.SdkVersion);
        }

        await auto.TypeAsync("npm create -y vite@latest viteapp -- --template vanilla-ts --no-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        await auto.TypeAsync("cd viteapp && npm install && cd ..");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        await auto.TypeAsync("aspire add Aspire.Hosting.JavaScript");
        await auto.EnterAsync();
        await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(2));

        var appHostPath = Path.Combine(projectRoot, "apphost.mts");
        var newContent = """
            // Aspire TypeScript AppHost
            // For more information, see: https://aspire.dev

            import { createBuilder } from './.modules/aspire.mjs';

            const builder = await createBuilder();

            const viteApp = await builder.addViteApp("viteapp", "./viteapp");

            await builder.build().run();
            """;

        File.WriteAllText(appHostPath, newContent);

        // Start the AppHost in the background. `aspire start` returns to the prompt once the
        // backchannel is established and the AppHost reports it has started.
        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.ClearScreenAsync(counter);

        // Leave the AppHost directory so the only way `aspire stop` can find the running guest
        // AppHost is via the AppHostPath it reported over the backchannel.
        await auto.TypeAsync("cd ..");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire stop --non-interactive --apphost tsapp");
        await auto.EnterAsync();
        await auto.WaitUntilAppHostStoppedSuccessfullyAsync(timeout: TimeSpan.FromMinutes(1));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
