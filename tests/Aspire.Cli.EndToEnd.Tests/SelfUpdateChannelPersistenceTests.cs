// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class SelfUpdateChannelPersistenceTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task SelfUpdateToStaging_RelaunchedCliUsesStagingForImplicitSelfUpdate()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        if (strategy.Mode is not (CliInstallMode.LocalHive or CliInstallMode.PullRequest or CliInstallMode.LocalArchive))
        {
            Assert.Skip(
                "This test must start from a current source build so its first self-update exercises " +
                "the channel-persistence implementation under test. Run with ASPIRE_E2E_ARCHIVE or in pull request CI.");
        }

        var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Copy the current build into a dedicated get-aspire-cli.sh-style prefix. This gives the
        // self-update a realistic writable route without replacing the harness's original install.
        await auto.RunCommandAsync(
            "install_root=$HOME/.aspire-self-update-e2e; " +
            "mkdir -p \"$install_root/bin\"; " +
            "cp \"$(command -v aspire)\" \"$install_root/bin/aspire\"; " +
            "chmod +x \"$install_root/bin/aspire\"; " +
            "printf '%s\\n' '{\"source\":\"script\",\"channel\":\"stable\"}' > \"$install_root/bin/.aspire-install.json\"; " +
            "export PATH=\"$install_root/bin:$PATH\" ASPIRE_CLI_TELEMETRY_OPTOUT=true; hash -r; " +
            "test \"$(command -v aspire)\" = \"$install_root/bin/aspire\"",
            counter);

        await auto.RunCommandAsync(
            "aspire update --self --channel staging --non-interactive --yes",
            counter,
            timeout: TimeSpan.FromMinutes(10));

        await auto.ClearScreenAsync(counter);

        // Relaunch from the replaced path without specifying a channel. Seeing staging selected
        // proves the new process resolved the identity persisted in the install sidecar.
        await auto.TypeAsync("hash -r; aspire update --self --non-interactive --yes");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Updating to channel: staging", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter, timeout: TimeSpan.FromMinutes(10));
    }
}
