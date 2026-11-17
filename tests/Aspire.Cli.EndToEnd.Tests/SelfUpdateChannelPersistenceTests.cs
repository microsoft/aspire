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
    public async Task SelfUpdateToStaging_PersistsChannelForRelaunchedCli()
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

        if (strategy.Mode is CliInstallMode.LocalHive)
        {
            // LocalHive setup pins its original Aspire home to local. Remove that competing
            // global setting before switching to the isolated script-style install.
            await auto.RunCommandAsync("aspire config delete channel -g", counter);
        }

        // Local-hive setup exports identity overrides, which take precedence over the sidecar.
        await auto.RunCommandAsync(
            "unset ASPIRE_CLI_CHANNEL ASPIRE_CLI_VERSION ASPIRE_CLI_COMMIT ASPIRE_CLI_PACKAGES ASPIRE_CLI_NUGET_SERVICE_INDEX",
            counter);

        // Keep the writable install outside the captured workspace so failures don't upload
        // the CLI and its backup. Copy only the sidecar into the workspace for assertions.
        // Parse --version as e.g. "17.0.0-pr.123.gabcdef12+<sha>"; only seed a hex commit suffix.
        // The packages override makes fresh invocations emit the effective-identity notice.
        await auto.RunCommandAsync(
            "install_root=$HOME/.aspire-self-update-e2e; " +
            "package_path=$(find ~/.aspire/hives -type f -name 'Aspire.Hosting.*.nupkg' -print -quit) && " +
            "test -n \"$package_path\" && " +
            "packages_dir=$(dirname \"$package_path\") && " +
            "cli_version=$(aspire --version 2>/dev/null) && " +
            "mkdir -p \"$install_root/bin\" && " +
            "cp \"$(command -v aspire)\" \"$install_root/bin/aspire\" && " +
            "chmod +x \"$install_root/bin/aspire\" && " +
            "jq -n --arg version \"${cli_version%%+*}\" --arg commit \"${cli_version#*+}\" --arg packages \"$packages_dir\" " +
            "'{source:\"script\",channel:\"stable\",version:$version,packages:$packages,e2eMarker:\"keep\"} + " +
            "(if ($commit | test(\"^[0-9a-fA-F]{8,64}$\")) then {commit:$commit} else {} end)' " +
            "> \"$install_root/bin/.aspire-install.json\" && " +
            "cp \"$install_root/bin/.aspire-install.json\" initial-install-sidecar.json && " +
            "export PATH=\"$install_root/bin:$PATH\" ASPIRE_CLI_TELEMETRY_OPTOUT=true && hash -r && " +
            "test \"$(command -v aspire)\" = \"$install_root/bin/aspire\"",
            counter);

        var initialSidecar = ReadSidecar(Path.Combine(workspace.WorkspaceRoot.FullName, "initial-install-sidecar.json"));
        Assert.Equal("stable", initialSidecar["channel"]?.GetValue<string>());
        Assert.NotNull(initialSidecar["version"]);
        Assert.NotNull(initialSidecar["packages"]);

        await auto.RunCommandAsync(
            "aspire update --self --channel staging --non-interactive --yes",
            counter,
            timeout: TimeSpan.FromMinutes(10));

        await auto.RunCommandAsync(
            "cp \"$install_root/bin/.aspire-install.json\" updated-install-sidecar.json",
            counter);

        var updatedSidecar = ReadSidecar(Path.Combine(workspace.WorkspaceRoot.FullName, "updated-install-sidecar.json"));
        Assert.Equal("staging", updatedSidecar["channel"]?.GetValue<string>());
        Assert.Equal("script", updatedSidecar["source"]?.GetValue<string>());
        Assert.Equal(initialSidecar["packages"]?.GetValue<string>(), updatedSidecar["packages"]?.GetValue<string>());
        Assert.Equal("keep", updatedSidecar["e2eMarker"]?.GetValue<string>());
        Assert.False(updatedSidecar.ContainsKey("version"));
        Assert.False(updatedSidecar.ContainsKey("commit"));

        // This invocation runs the downloaded CLI, not the current-source build. Its notice
        // checks the effective identity without relying on C# updates, which preserve native
        // NuGet policy. If the binary itself is stamped staging, only the sidecar assertions
        // above distinguish persisted identity from its built-in channel.
        await auto.RunCommandAsync(
            "aspire --version > relaunched-version.txt 2> relaunched-identity.txt && " +
            "cat relaunched-identity.txt && " +
            "grep -F \"emulating identity 'staging'\" relaunched-identity.txt",
            counter);
    }

    private static JsonObject ReadSidecar(string path)
    {
        return JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidOperationException($"Unable to read {path}.");
    }
}
