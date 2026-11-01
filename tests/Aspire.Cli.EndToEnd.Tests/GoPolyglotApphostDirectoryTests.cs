// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// Polyglot (non-.NET) AppHosts run under a generic host process, so the Go SDK forwards the
/// AppHost path and project directory the CLI provides (<c>ASPIRE_APPHOST_FILEPATH</c> and
/// <c>ASPIRE_PROJECT_DIRECTORY</c>) when it creates the builder. Without them the running AppHost
/// would report the wrong path and <c>aspire &lt;cmd&gt; --apphost &lt;directory&gt;</c> could not
/// match it. This test starts a Go AppHost in the background and then issues
/// <c>aspire stop --non-interactive --apphost &lt;directory&gt;</c> from an unrelated cwd so the stop
/// command is forced to resolve the AppHost via the path the host reported over the backchannel.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class GoPolyglotApphostDirectoryTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task StopGoPolyglotAppHostUsingApphostDirectory()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.PolyglotGo, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableExperimentalGoSupportAsync(counter);

        // Put the AppHost in a subdirectory so we can `cd ..` away from it and then resolve it
        // back via `--apphost goapp`. That forces AppHost discovery through the path the running
        // host reported over the backchannel rather than through the current working directory.
        await auto.RunCommandAsync("mkdir goapp && cd goapp", counter);
        await auto.AspireInitGoAppHostAsync(counter);

        // Start the AppHost in the background (returns to the prompt once the AppHost reports it
        // has started over the backchannel). The scaffolded AppHost has no resources, which is
        // all this scenario needs.
        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.ClearScreenAsync(counter);

        await auto.RunCommandAsync("cd ..", counter);

        await auto.TypeAsync("aspire stop --non-interactive --apphost goapp");
        await auto.EnterAsync();
        await auto.WaitUntilAppHostStoppedSuccessfullyAsync(timeout: TimeSpan.FromMinutes(1));
        await auto.WaitForSuccessPromptAsync(counter);
    }
}
