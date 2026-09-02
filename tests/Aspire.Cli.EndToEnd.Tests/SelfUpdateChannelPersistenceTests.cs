// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class SelfUpdateChannelPersistenceTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task SelfUpdateToStaging_RelaunchedCliUsesStagingForImplicitProjectUpdate()
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

        const string projectName = "SelfUpdateChannelApp";
        var projectPath = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
        var configPath = Path.Combine(projectPath, "aspire.config.json");

        // Model the reported scenario where the AppHost predates the self-update. Normalize any
        // channel assigned by the current-source install so the later update must use CLI identity.
        await auto.AspireNewCSharpEmptyAppHostAsync(
            projectName,
            counter,
            timeout: TimeSpan.FromMinutes(5));

        var createdConfig = ReadConfig(configPath);
        createdConfig.Remove("channel");
        File.WriteAllText(configPath, createdConfig.ToJsonString());
        Assert.False(ReadConfig(configPath).ContainsKey("channel"));

        if (strategy.Mode is CliInstallMode.LocalHive)
        {
            // LocalHive setup pins its original Aspire home to local. Remove that competing
            // global setting before switching to the isolated script-style install.
            await auto.RunCommandAsync("aspire config delete channel -g", counter);
        }

        // Copy the current build into a dedicated get-aspire-cli.sh-style prefix. This gives the
        // self-update a realistic writable route without replacing the harness's original install.
        //
        // The sidecar's "packages" field pins Aspire package resolution to the harness's local
        // hive. Without it the relaunched CLI derives its feed from the identity it just persisted
        // (darc-pub-microsoft-aspire-<commit>), which stops carrying matching packages once that
        // staging build is promoted to GA -- the exact failure reported in
        // https://github.com/microsoft/aspire/issues/19708. InstallSidecarWriter.PrepareForSelfUpdate
        // rewrites only channel/version/commit, so this field survives the self-update below.
        await auto.RunCommandAsync(
            "install_root=$HOME/.aspire-self-update-e2e; " +
            "package_path=$(find ~/.aspire/hives -type f -name 'Aspire.Hosting.*.nupkg' -print -quit); " +
            "test -n \"$package_path\"; " +
            "packages_dir=$(dirname \"$package_path\"); " +
            "mkdir -p \"$install_root/bin\"; " +
            "cp \"$(command -v aspire)\" \"$install_root/bin/aspire\"; " +
            "chmod +x \"$install_root/bin/aspire\"; " +
            "printf '{\"source\":\"script\",\"channel\":\"stable\",\"packages\":\"%s\"}\\n' \"$packages_dir\" > \"$install_root/bin/.aspire-install.json\"; " +
            "export PATH=\"$install_root/bin:$PATH\" ASPIRE_CLI_TELEMETRY_OPTOUT=true; hash -r; " +
            "test \"$(command -v aspire)\" = \"$install_root/bin/aspire\"",
            counter);

        await auto.RunCommandAsync(
            "aspire update --self --channel staging --non-interactive --yes",
            counter,
            timeout: TimeSpan.FromMinutes(10));

        await auto.ClearScreenAsync(counter);

        await auto.RunCommandAsync("aspire config set features.updateNotificationsEnabled false -g", counter);

        // Relaunch from the replaced path without specifying a channel. Seeing staging land in the
        // project config proves the new process resolved the identity persisted in the install
        // sidecar. A second `aspire update --self` cannot be used to assert this: the preserved
        // "packages" field synthesizes a local-hive channel that replaces the same-named built-in
        // one (PackagingService.GetChannelsAsync), and a local hive has no CliDownloadBaseUrl, so
        // self-download reports "Channel 'staging' does not support CLI downloads".
        await auto.RunCommandAsync($"cd {projectName}", counter);
        await auto.RunCommandAsync(
            "aspire update --non-interactive --yes",
            counter,
            timeout: TimeSpan.FromMinutes(10));

        var updatedConfig = ReadConfig(configPath);
        Assert.Equal("staging", updatedConfig["channel"]?.GetValue<string>());
    }

    private static JsonObject ReadConfig(string path)
    {
        return JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidOperationException($"Unable to read {path}.");
    }
}
