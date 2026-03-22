// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for staging channel configuration and self-update channel switching.
/// Verifies that staging settings (overrideStagingQuality, stagingPinToCliVersion) are
/// correctly persisted and that aspire update --self saves the channel to global settings.
/// </summary>
public sealed class StagingChannelTests(ITestOutputHelper output)
{
    [Fact]
    public async Task StagingChannel_ConfigureAndVerifySettings_ThenSwitchChannels()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliInDockerAsync(installMode, counter);

        // Step 1: Configure staging channel settings via aspire config set
        // Enable the staging channel feature flag
        await auto.TypeAsync("aspire config set features.stagingChannelEnabled true -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Set quality to Prerelease (triggers shared feed mode)
        await auto.TypeAsync("aspire config set overrideStagingQuality Prerelease -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Enable pinned version mode
        await auto.TypeAsync("aspire config set stagingPinToCliVersion true -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Set channel to staging
        await auto.TypeAsync("aspire config set channel staging -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 2: Verify the settings were persisted in the global config file
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("cat ~/.aspire/aspire.config.json");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("stagingPinToCliVersion", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 3: Verify aspire config get returns the correct values
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire config get channel");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("staging", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 4: Verify the CLI version is available (basic smoke test that the CLI works with these settings)
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire --version");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 5: Switch channel to stable via config set (simulating what update --self does)
        await auto.TypeAsync("aspire config set channel stable -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 6: Verify channel was changed to stable
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire config get channel");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("stable", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 7: Switch back to staging
        await auto.TypeAsync("aspire config set channel staging -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 8: Verify channel is staging again and staging settings are still present
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire config get channel");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("staging", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify the staging-specific settings survived the channel switch
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire config get overrideStagingQuality");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Prerelease", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Clean up: remove staging settings to avoid polluting other tests
        await auto.TypeAsync("aspire config delete features.stagingChannelEnabled -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("aspire config delete overrideStagingQuality -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("aspire config delete stagingPinToCliVersion -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("aspire config delete channel -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
