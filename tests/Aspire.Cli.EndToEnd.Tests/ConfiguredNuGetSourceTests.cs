// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Xml.Linq;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end coverage for persisted NuGet source selection.
/// </summary>
public sealed class ConfiguredNuGetSourceTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task PersistedConfiguredSourceIsConsumedWithoutBeingCopiedToProject()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        Assert.SkipUnless(
            strategy.Mode is CliInstallMode.LocalHive or CliInstallMode.LocalArchive or CliInstallMode.PullRequest,
            $"Configured-source E2E coverage requires a hive-backed CLI install; current mode is {strategy.Mode}.");
        using var workspace = TemporaryWorkspace.Create(output);

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

        // Copy one complete installed hive into an isolated feed. Removing the originals makes
        // successful template and package resolution causal evidence that the persisted source
        // was reloaded, rather than an accidental fallback to the CLI's installation hive.
        await auto.RunCommandAsync(
            "SOURCE_HIVE=\"$(dirname \"$(find \"$HOME/.aspire/hives\" -path '*/packages/Aspire.ProjectTemplates.*.nupkg' -print -quit)\")\"; " +
            "test -n \"$SOURCE_HIVE\"; " +
            "mkdir configured-source-feed; " +
            "cp \"$SOURCE_HIVE\"/Aspire.*.nupkg configured-source-feed/; " +
            "test -n \"$(find configured-source-feed -name 'Aspire.ProjectTemplates.*.nupkg' -print -quit)\"; " +
            "test -n \"$(find configured-source-feed -name 'Aspire.Hosting.Seq.*.nupkg' -print -quit)\"; " +
            "rm -f \"$SOURCE_HIVE\"/Aspire.*.nupkg",
            counter);
        await auto.RunCommandAsync(
            "dotnet nuget add source \"$PWD/configured-source-feed\" --name configured-source-e2e",
            counter);

        await auto.RunCommandAsync("aspire config set features.updateNotificationsEnabled false -g", counter);
        await auto.RunCommandAsync(
            "aspire config set nugetSource \"$PWD/configured-source-feed\" -g",
            counter);
        await auto.RunCommandAsync(
            "aspire config get nugetSource | grep -F \"$PWD/configured-source-feed\"",
            counter);
        await auto.RunCommandAsync("rm -rf \"$HOME/.aspire/logs\" && mkdir -p \"$HOME/.aspire/logs\"", counter);

        const string projectName = "ConfiguredSourceApp";
        await auto.RunCommandAsync(
            $"aspire new aspire-starter --name {projectName} --output {projectName} " +
            "--non-interactive --suppress-agent-init --log-level Debug",
            counter,
            TimeSpan.FromMinutes(3));

        var projectDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
        var projectConfigPath = Path.Combine(projectDirectory, "aspire.config.json");
        using (var projectConfig = JsonDocument.Parse(await File.ReadAllTextAsync(projectConfigPath)))
        {
            Assert.True(projectConfig.RootElement.TryGetProperty("appHost", out _));
            Assert.False(projectConfig.RootElement.TryGetProperty("nugetSource", out _));
        }
        var generatedNuGetConfigs = new DirectoryInfo(projectDirectory)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(file => string.Equals(file.Name, "NuGet.Config", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.FullName)
            .ToArray();
        Assert.True(
            generatedNuGetConfigs.Length == 0,
            $"The configured source must not be copied into a generated NuGet.Config. Found: {string.Join(", ", generatedNuGetConfigs)}");

        await auto.RunCommandAsync("dotnet nuget remove source configured-source-e2e", counter);
        await auto.RunCommandAsync($"cd {projectName}/{projectName}.AppHost", counter);
        await auto.TypeAsync("aspire add Aspire.Hosting.Seq --non-interactive --log-level Debug");
        await auto.EnterAsync();
        await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(3));

        var appHostProjectPath = Path.Combine(
            projectDirectory,
            $"{projectName}.AppHost",
            $"{projectName}.AppHost.csproj");
        var appHostProject = XDocument.Load(appHostProjectPath);
        var seqReference = Assert.Single(
            appHostProject.Descendants("PackageReference"),
            element => string.Equals(
                element.Attribute("Include")?.Value,
                "Aspire.Hosting.Seq",
                StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(seqReference.Attribute("Version")?.Value));

        // The template install path names the isolated feed, proving `aspire new` consumed the
        // reloaded setting. Removing the ambient source before `aspire add` makes the package-add
        // invocation's --source argument direct evidence that the next process reloaded it too.
        await auto.RunCommandAsync(
            "grep -R -E 'Running dotnet in .* with args: new install [^ ]*/configured-source-feed/Aspire\\.ProjectTemplates\\.[^ ]*\\.nupkg' \"$HOME/.aspire/logs\" && " +
            "grep -R -E 'Running dotnet in .* with args: package add Aspire\\.Hosting\\.Seq --version .* --project [^ ]*\\.csproj --source [^ ]*/configured-source-feed' \"$HOME/.aspire/logs\"",
            counter);
    }
}
