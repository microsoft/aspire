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
    public async Task SelfUpdateToStaging_RelaunchedCliUsesStagingForNewProject()
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

        if (strategy.Mode is CliInstallMode.LocalHive ||
            (strategy.Mode is CliInstallMode.LocalArchive && strategy.LocalArchiveHiveLabel == "local"))
        {
            // Local-hive setup pins its original Aspire home to local. Remove that competing
            // global setting before switching to the isolated script-style install.
            await auto.RunCommandAsync("aspire config delete channel -g", counter);
        }

        // Copy the current build into a dedicated get-aspire-cli.sh-style prefix. This gives the
        // self-update a realistic writable route without replacing the harness's original install.
        //
        // The sidecar's "packages" field pins Aspire package resolution to the harness's local
        // hive. Without it the relaunched CLI derives its template feed from the persisted identity
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

        await auto.RunCommandAsync(
            "cat \"$install_root/bin/.aspire-install.json\" > \"$ASPIRE_E2E_WORKSPACE/self-updated-install.json\"",
            counter);
        var sidecarPath = Path.Combine(workspace.WorkspaceRoot.FullName, "self-updated-install.json");
        Assert.Equal("staging", ReadConfig(sidecarPath)["channel"]?.GetValue<string>());

        await auto.RunCommandAsync("aspire config set features.updateNotificationsEnabled false -g", counter);

        // New projects use CLI identity when no channel is supplied. Existing C# updates instead
        // preserve native NuGet policy unless a channel is selected through options or configuration.
        const string projectName = "SelfUpdateChannelApp";
        await auto.AspireNewCSharpEmptyAppHostAsync(
            projectName,
            counter,
            timeout: TimeSpan.FromMinutes(5));

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, projectName, "aspire.config.json");
        Assert.Equal("staging", ReadConfig(configPath)["channel"]?.GetValue<string>());
    }

    private static JsonObject ReadConfig(string path)
    {
        return JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidOperationException($"Unable to read {path}.");
    }
}
