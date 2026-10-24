// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class LaunchGroupTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, "start")]
    [InlineData(false, "run --detach")]
    [InlineData(true, "start")]
    [InlineData(true, "run --detach")]
    [CaptureWorkspaceOnFailure]
    public async Task LaunchGroupOptionSelectsResourcesAndOverridesConfiguration(bool typeScript, string command)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        const string projectName = "LaunchGroups";
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        if (typeScript)
        {
            await auto.AspireNewTypeScriptEmptyAppHostAsync(projectName, counter);
        }
        else
        {
            await auto.AspireNewCSharpEmptyAppHostAsync(projectName, counter);
        }

        var directory = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
        var appHostPath = Path.Combine(directory, typeScript ? "apphost.mts" : "apphost.cs");
        var source = await File.ReadAllTextAsync(appHostPath, TestContext.Current.CancellationToken);
        var builderDeclaration = typeScript ? "const builder = await createBuilder();" : "var builder = DistributedApplication.CreateBuilder(args);";
        Assert.Contains(builderDeclaration, source);
        var resources = typeScript
            ? """
              await builder.defineLaunchGroups(["catalog", "basket"]);
              await builder.addExecutable("catalog", "sleep", ".", ["600"]).withLaunchGroup("catalog");
              await builder.addExecutable("basket", "sleep", ".", ["600"]).withLaunchGroup("basket");
              """
            : """
              builder.DefineLaunchGroups("catalog", "basket");
              builder.AddExecutable("catalog", "sleep", ".", "600").WithLaunchGroup("catalog");
              builder.AddExecutable("basket", "sleep", ".", "600").WithLaunchGroup("basket");
              """;
        await File.WriteAllTextAsync(appHostPath, source.Replace(builderDeclaration, $"{builderDeclaration}\n{resources}", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "appsettings.json"), """{ "Aspire": { "LaunchGroup": "basket" } }""",
            TestContext.Current.CancellationToken);

        await auto.RunCommandAsync($"cd {projectName}", counter);
        await auto.RunCommandAsync($"Aspire__LaunchGroup=basket aspire {command} --launch-group catalog --isolated --non-interactive", counter,
            TimeSpan.FromMinutes(3));
        await auto.RunCommandAsync("aspire wait catalog --status up --timeout 60 --non-interactive", counter);
        await auto.RunCommandAsync("aspire describe --format json --non-interactive > resources.json", counter);
        await auto.RunCommandAsync(
            "jq -e '.resources | map(select(.displayName == \"catalog\" or .displayName == \"basket\") | {displayName, state}) | sort_by(.displayName) | " +
            ". == [{displayName: \"basket\", state: \"NotStarted\"}, {displayName: \"catalog\", state: \"Running\"}]' resources.json",
            counter);
        await auto.AspireStopAsync(counter);
    }
}
