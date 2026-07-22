// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for running .NET csproj AppHost projects using the Aspire bundle.
/// Validates that the bundle correctly provides DCP and Dashboard paths to the hosting
/// infrastructure when running SDK-based app hosts (not just polyglot/guest app hosts).
/// </summary>
public sealed class BundleSmokeTests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task CreateAndRunAspireStarterProjectWithBundle()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.AspireNewAsync("BundleStarterApp", counter);

        await auto.AspireStartAsync(counter);
        await auto.AspireStopAsync(counter);
    }

    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task DotNetRunProjectAppHostUsesAspireCliBundle()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var projectName = "DotNetRunBundleApp";

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.AspireNewAsync(projectName, counter, useRedisCache: false);

        var projectRoot = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
        var appHostDirectory = Path.Combine(projectRoot, $"{projectName}.AppHost");
        var appHostProjectPath = Path.Combine(appHostDirectory, $"{projectName}.AppHost.csproj");
        var appHostSourcePath = Path.Combine(appHostDirectory, "AppHost.cs");
        var argsOutputPath = Path.Combine(projectRoot, "apphost-args.txt");
        var containerArgsOutputPath = CliE2ETestHelpers.ToContainerPath(argsOutputPath, workspace);

        Assert.True(File.Exists(appHostProjectPath), $"Expected AppHost project file to exist at: {appHostProjectPath}");
        Assert.True(File.Exists(appHostSourcePath), $"Expected AppHost source file to exist at: {appHostSourcePath}");

        var appHostProject = File.ReadAllText(appHostProjectPath);
        Assert.Contains("<Nullable>enable</Nullable>", appHostProject);
        Assert.DoesNotContain("AspireUseCliBundle", appHostProject);
        appHostProject = appHostProject.Replace(
            "<Nullable>enable</Nullable>",
            """
            <Nullable>enable</Nullable>
                <AspireUseCliBundle>true</AspireUseCliBundle>
            """,
            StringComparison.Ordinal);
        File.WriteAllText(appHostProjectPath, appHostProject);

        File.WriteAllText(
            appHostSourcePath,
            $$"""
            File.WriteAllText({{JsonSerializer.Serialize(containerArgsOutputPath)}}, string.Join("|", args));

            var builder = DistributedApplication.CreateBuilder(args);

            builder.Build().Run();
            """);

        var appHostProjectRelativePath = Path.GetRelativePath(workspace.WorkspaceRoot.FullName, appHostProjectPath);
        var quotedAppHostProjectPath = AspireCliShellCommandHelpers.QuoteBashArg(appHostProjectRelativePath);

        await auto.TypeAsync($"dotnet run --no-launch-profile --project {quotedAppHostProjectPath} -- --from-dotnet-run");
        await auto.EnterAsync();

        await auto.WaitUntilAsync(
            s => s.ContainsText("Press CTRL+C to stop the AppHost and exit."),
            timeout: CliE2EAutomatorHelpers.AspireRunReadyTimeout,
            description: "Press CTRL+C message from aspire run");

        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForSuccessPromptAsync(counter);

        Assert.True(File.Exists(argsOutputPath), $"Expected AppHost to write forwarded args to: {argsOutputPath}");
        Assert.Equal("--from-dotnet-run", File.ReadAllText(argsOutputPath));
    }

    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task DotNetRunProjectAppHostRestoresAndUsesAspireCliThroughDnx()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        Assert.SkipUnless(
            strategy.Mode is CliInstallMode.LocalHive or CliInstallMode.LocalArchive,
            "Real DNX delegation requires a locally built Aspire CLI package hive.");

        const string ProjectName = "DotNetRunDnxBundleApp";
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.AspireNewAsync(ProjectName, counter, useRedisCache: false);

        var projectRoot = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName);
        var appHostDirectory = Path.Combine(projectRoot, $"{ProjectName}.AppHost");
        var appHostProjectPath = Path.Combine(appHostDirectory, $"{ProjectName}.AppHost.csproj");
        var appHostSourcePath = Path.Combine(appHostDirectory, "AppHost.cs");
        var argsOutputPath = Path.Combine(projectRoot, "apphost-dnx-args.txt");
        var containerArgsOutputPath = CliE2ETestHelpers.ToContainerPath(argsOutputPath, workspace);

        Assert.True(File.Exists(appHostProjectPath), $"Expected AppHost project file to exist at: {appHostProjectPath}");
        Assert.True(File.Exists(appHostSourcePath), $"Expected AppHost source file to exist at: {appHostSourcePath}");

        var appHostProject = File.ReadAllText(appHostProjectPath);
        appHostProject = appHostProject.Replace(
            "<Nullable>enable</Nullable>",
            """
            <Nullable>enable</Nullable>
                <AspireUseCliBundle>true</AspireUseCliBundle>
            """,
            StringComparison.Ordinal);
        File.WriteAllText(appHostProjectPath, appHostProject);

        var appHostSource = File.ReadAllText(appHostSourcePath);
        File.WriteAllText(
            appHostSourcePath,
            appHostSource.Replace(
                "var builder = DistributedApplication.CreateBuilder(args);",
                $$"""
                File.WriteAllText({{JsonSerializer.Serialize(containerArgsOutputPath)}}, string.Join("|", args));

                var builder = DistributedApplication.CreateBuilder(args);
                """,
                StringComparison.Ordinal));

        await auto.RunCommandAsync($"cd {AspireCliShellCommandHelpers.QuoteBashArg(ProjectName)}", counter);
        await auto.RunCommandAsync("test ! -e .config/dotnet-tools.json", counter);

        // The PR/local-hive installer places the exact locally built packages under this hive.
        // Ensuring it is registered in the app's NuGet.config makes the target's pinned DNX package
        // reference resolve the same Aspire.Cli build as the AppHost SDK without relying on a tool manifest.
        await auto.RunCommandAsync(
            "DNX_HIVE=$(find \"$HOME/.aspire/hives\" -mindepth 2 -maxdepth 2 -type d -name packages -print -quit); " +
            "test -n \"$DNX_HIVE\"; " +
            "test -f NuGet.config || dotnet new nugetconfig; " +
            "dotnet nuget list source --configfile NuGet.config | grep -F \"$DNX_HIVE\" >/dev/null || " +
            "dotnet nuget add source \"$DNX_HIVE\" --name aspire-dnx-local --configfile NuGet.config",
            counter);

        // Remove both supported Aspire installation locations while leaving the SDK-adjacent DNX
        // executable available. This forces the automatic fallback rather than PATH CLI delegation.
        await auto.RunCommandAsync(
            "export PATH=\"${PATH#\"$HOME/.aspire/bin:\"}\"; " +
            "export PATH=\"${PATH#\"$HOME/.aspire:\"}\"; " +
            "hash -r; ! command -v aspire; command -v dnx",
            counter);

        var appHostProjectRelativePath = Path.GetRelativePath(projectRoot, appHostProjectPath);
        await auto.TypeAsync(
            $"dotnet run --no-launch-profile --project {AspireCliShellCommandHelpers.QuoteBashArg(appHostProjectRelativePath)} -- --from-real-dnx");
        await auto.EnterAsync();

        await auto.WaitUntilAsync(
            s => s.ContainsText("Press CTRL+C to stop the AppHost and exit."),
            timeout: CliE2EAutomatorHelpers.AspireRunReadyTimeout,
            description: "Press CTRL+C message from Aspire CLI restored through DNX");

        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForSuccessPromptAsync(counter);

        Assert.True(File.Exists(argsOutputPath), $"Expected AppHost to write forwarded args to: {argsOutputPath}");
        Assert.Equal("--from-real-dnx", File.ReadAllText(argsOutputPath));
    }

    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task DotNetRunFileBasedAppHostUsesAspireCliBundle()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var projectName = "DotNetRunFileBundleApp";

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.AspireNewCSharpEmptyAppHostAsync(projectName, counter);

        var projectRoot = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
        var appHostSourcePath = Path.Combine(projectRoot, "apphost.cs");
        var argsOutputPath = Path.Combine(projectRoot, "apphost-file-args.txt");
        var containerArgsOutputPath = CliE2ETestHelpers.ToContainerPath(argsOutputPath, workspace);

        Assert.True(File.Exists(appHostSourcePath), $"Expected AppHost source file to exist at: {appHostSourcePath}");

        var appHostSource = File.ReadAllText(appHostSourcePath);
        Assert.Contains("#:sdk Aspire.AppHost.Sdk", appHostSource);
        Assert.Contains("var builder = DistributedApplication.CreateBuilder(args);", appHostSource);
        Assert.DoesNotContain("AspireUseCliBundle", appHostSource);
        appHostSource = appHostSource.Replace(
            "#:sdk Aspire.AppHost.Sdk",
            """
            #:sdk Aspire.AppHost.Sdk
            #:property AspireUseCliBundle=true
            """,
            StringComparison.Ordinal);

        File.WriteAllText(
            appHostSourcePath,
            appHostSource.Replace(
                "var builder = DistributedApplication.CreateBuilder(args);",
                $$"""
                File.WriteAllText({{JsonSerializer.Serialize(containerArgsOutputPath)}}, string.Join("|", args));

                var builder = DistributedApplication.CreateBuilder(args);
                """));

        await auto.RunCommandAsync($"cd {AspireCliShellCommandHelpers.QuoteBashArg(projectName)}", counter);

        await auto.TypeAsync("dotnet run apphost.cs -- --from-dotnet-run-file");
        await auto.EnterAsync();

        await auto.WaitUntilAsync(
            s => s.ContainsText("Press CTRL+C to stop the AppHost and exit."),
            timeout: CliE2EAutomatorHelpers.AspireRunReadyTimeout,
            description: "Press CTRL+C message from aspire run");

        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForSuccessPromptAsync(counter);

        Assert.True(File.Exists(argsOutputPath), $"Expected AppHost to write forwarded args to: {argsOutputPath}");
        Assert.Equal("--from-dotnet-run-file", File.ReadAllText(argsOutputPath));
    }
}
