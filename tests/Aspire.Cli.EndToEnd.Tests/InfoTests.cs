// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class InfoTests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task InstalledCliReportsFullAndSelfInformationAsJson()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            mountDockerSocket: false,
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

        await auto.RunCommandAsync("aspire --info --format json > info.json", counter);
        await auto.RunCommandAsync(
            "jq -e '(.installs | map(select(.isCurrent == true)) | length) == 1' info.json >/dev/null",
            counter);

        await auto.RunCommandAsync("aspire --info --self --format json > self.json", counter);
        await auto.RunCommandAsync(
            "jq -e 'type == \"array\" and length == 1 and .[0].kind == \"installation\" and .[0].isCurrent == true and (.[0] | has(\"hive\") | not)' self.json >/dev/null",
            counter);
    }
}
