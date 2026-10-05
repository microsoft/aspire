// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class SdkExportTests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task ExportPackageFromInstalledHiveWritesJsonToStandardOutput()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        Assert.SkipUnless(
            strategy.Mode is CliInstallMode.LocalHive or CliInstallMode.LocalArchive or CliInstallMode.PullRequest,
            "The sdk export E2E test requires a locally built package hive.");

        var workspace = TemporaryWorkspace.Create(output);
        var scriptPath = Path.Combine(workspace.WorkspaceRoot.FullName, "run-sdk-export.sh");
        var exportPath = Path.Combine(workspace.WorkspaceRoot.FullName, "sdk-export.json");

        await File.WriteAllTextAsync(
            scriptPath,
            """
            #!/usr/bin/env bash
            set -euo pipefail

            find "$HOME/.aspire/hives" -type f -name 'Aspire.Hosting.Redis.*.nupkg' -print -quit > sdk-export-package-path.txt
            read -r package_path < sdk-export-package-path.txt
            test -n "$package_path"

            package_file="${package_path##*/}"
            package_version="${package_file#Aspire.Hosting.Redis.}"
            package_version="${package_version%.nupkg}"
            export ASPIRE_CLI_PACKAGES="${package_path%/*}"

            aspire sdk export \
                --language typescript \
                --package "Aspire.Hosting.Redis@${package_version}" \
                > sdk-export.json
            """,
            TestContext.Current.CancellationToken);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(
            terminal,
            workspace,
            auto,
            counter,
            output,
            TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.RunCommandAsync("bash run-sdk-export.sh", counter, TimeSpan.FromMinutes(5));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            exportPath,
            TestContext.Current.CancellationToken));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("typescript", root.GetProperty("language").GetString());
        Assert.Equal("Aspire.Hosting.Redis", root.GetProperty("package").GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            root.GetProperty("package").GetProperty("version").GetString()));
        Assert.Equal(JsonValueKind.Array, root.GetProperty("modules").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("declarations").ValueKind);
    }
}
