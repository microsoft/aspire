// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class CliManagedCSharpAppHostTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task CreateRestoreAddAndStartCliManagedCSharpAppHost()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(repoRoot, strategy, ["Aspire.Hosting.Redis."]);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.RunCommandAsync("aspire config set features.experimentalCliManagedAppHost true -g", counter);

        const string projectName = "CliManagedCSharpApp";
        var projectRoot = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
        var appHostPath = Path.Combine(projectRoot, "apphost.cs");

        await auto.RunCommandAsync(
            $"aspire new aspire-cs-empty --name {projectName} --output {projectName} --localhost-tld false --suppress-agent-init --non-interactive",
            counter,
            TimeSpan.FromMinutes(2));

        if (localChannel is not null)
        {
            CliE2ETestHelpers.WriteLocalChannelSettings(projectRoot, localChannel.SdkVersion);
        }

        await auto.RunCommandAsync($"cd {projectName}", counter);
        await auto.RunCommandAsync("aspire restore --non-interactive", counter, TimeSpan.FromMinutes(3));
        await auto.RunCommandAsync("test -f .aspire/modules/Aspire.csproj && test -f .aspire/modules/Aspire.targets", counter);

        await auto.TypeAsync("aspire add Aspire.Hosting.Redis");
        await auto.EnterAsync();
        await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(2));

        var appHostContent = await File.ReadAllTextAsync(appHostPath, TestContext.Current.CancellationToken);
        appHostContent = appHostContent.Replace(
            "var builder = DistributedApplication.CreateBuilder(args);",
            """
            var builder = DistributedApplication.CreateBuilder(args);

            builder.AddRedis("cache");
            """,
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(appHostPath, appHostContent, TestContext.Current.CancellationToken);

        await auto.RunCommandAsync("grep -F 'Aspire.Hosting.Redis' aspire.config.json", counter);
        await auto.RunCommandAsync("aspire restore --non-interactive", counter, TimeSpan.FromMinutes(3));
        await auto.RunCommandAsync(
            "if dotnet build apphost.cs >/tmp/direct-dotnet-build.log 2>&1; then cat /tmp/direct-dotnet-build.log; exit 1; fi; " +
            "grep -F 'aspire run' /tmp/direct-dotnet-build.log",
            counter,
            TimeSpan.FromMinutes(2));

        await auto.AspireStartAsync(counter, startTimeout: TimeSpan.FromMinutes(4));
        await auto.AssertResourcesExistAsync(counter, "cache");
        await auto.AspireStopAsync(counter);
    }
}
