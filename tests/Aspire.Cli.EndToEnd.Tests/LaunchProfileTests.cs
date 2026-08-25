// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end coverage for selecting .NET AppHost launch profiles through the Aspire CLI.
/// </summary>
public sealed class LaunchProfileTests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task RunStartsAppHostWithSelectedLaunchProfile()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        // A solution file makes `aspire init` create a project-based AppHost with
        // Properties/launchSettings.json instead of a single-file AppHost.
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "LaunchProfile.sln"), "Fake solution file");

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.AspireInitAsync(counter);

        var appHostDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "LaunchProfile.AppHost");
        var appHostSourcePath = Path.Combine(appHostDirectory, "AppHost.cs");
        var launchSettingsPath = Path.Combine(appHostDirectory, "Properties", "launchSettings.json");
        var profileOutputPath = Path.Combine(appHostDirectory, "selected-profile.txt");
        var containerProfileOutputPath = CliE2ETestHelpers.ToContainerPath(profileOutputPath, workspace);

        Assert.True(File.Exists(appHostSourcePath), $"Expected AppHost source file to exist at: {appHostSourcePath}");
        Assert.True(File.Exists(launchSettingsPath), $"Expected launch settings to exist at: {launchSettingsPath}");

        File.WriteAllText(
            appHostSourcePath,
            $$"""
            File.WriteAllText(
                {{JsonSerializer.Serialize(containerProfileOutputPath)}},
                $"{Environment.GetEnvironmentVariable("SELECTED_PROFILE")}|{Environment.GetEnvironmentVariable("DOTNET_LAUNCH_PROFILE")}|{string.Join("|", args)}");

            var builder = DistributedApplication.CreateBuilder(args);

            builder.Build().Run();
            """);

        // The profiles deliberately carry different environment variables and arguments so the
        // assertion covers System.CommandLine parsing, CLI propagation, and direct AppHost launch.
        File.WriteAllText(
            launchSettingsPath,
            """
            {
              "profiles": {
                "default": {
                  "commandName": "Project",
                  "commandLineArgs": "--profile-argument default",
                  "environmentVariables": {
                    "SELECTED_PROFILE": "default"
                  }
                },
                "E2E": {
                  "commandName": "Project",
                  "commandLineArgs": "--profile-argument selected-profile",
                  "environmentVariables": {
                    "SELECTED_PROFILE": "selected-profile"
                  }
                }
              }
            }
            """);

        await auto.RunCommandAsync("cd LaunchProfile.AppHost", counter);
        await auto.TypeAsync(CliE2EAutomatorHelpers.GetAspireRunCommand("--launch-profile E2E"));
        await auto.EnterAsync();

        await auto.WaitUntilAsync(
            s => s.ContainsText("Press CTRL+C to stop the AppHost and exit."),
            timeout: CliE2EAutomatorHelpers.AspireRunReadyTimeout,
            description: "Press CTRL+C message from AppHost using selected launch profile");

        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForSuccessPromptAsync(counter);

        Assert.True(File.Exists(profileOutputPath), $"Expected AppHost to write selected profile details to: {profileOutputPath}");
        Assert.Equal("selected-profile|E2E|--profile-argument|selected-profile", File.ReadAllText(profileOutputPath));
    }
}
