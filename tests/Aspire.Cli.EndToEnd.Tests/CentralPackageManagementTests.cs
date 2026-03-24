// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Central Package Management (CPM) compatibility.
/// Validates that aspire update correctly handles CPM projects.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class CentralPackageManagementTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AspireUpdateRemovesAppHostPackageVersionFromDirectoryPackagesProps()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliInDockerAsync(installMode, counter);

        // Disable update notifications to prevent the CLI self-update prompt
        // from appearing after "Update successful!" and blocking the test.
        await auto.TypeAsync("aspire config set features.updateNotificationsEnabled false -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Set up an old-format AppHost project with CPM that has a PackageVersion
        // for Aspire.Hosting.AppHost. This simulates a pre-migration project where
        // the user adopted CPM before the SDK started adding the implicit reference.
        var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, "CpmTest");
        var appHostDir = Path.Combine(projectDir, "CpmTest.AppHost");
        var appHostCsprojPath = Path.Combine(appHostDir, "CpmTest.AppHost.csproj");
        var directoryPackagesPropsPath = Path.Combine(projectDir, "Directory.Packages.props");
        var containerAppHostCsprojPath = CliE2ETestHelpers.ToContainerPath(appHostCsprojPath, workspace);

        Directory.CreateDirectory(appHostDir);

        File.WriteAllText(appHostCsprojPath, """
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.1.0" />
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net9.0</TargetFramework>
                    <IsAspireHost>true</IsAspireHost>
                </PropertyGroup>
                <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.AppHost" />
                </ItemGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(appHostDir, "Program.cs"), """
            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        File.WriteAllText(directoryPackagesPropsPath, """
            <Project>
                <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                </PropertyGroup>
                <ItemGroup>
                    <PackageVersion Include="Aspire.Hosting.AppHost" Version="9.1.0" />
                </ItemGroup>
            </Project>
            """);

        // Use --channel stable to skip the channel selection prompt that appears
        // in CI when PR hive directories are present.
        await auto.TypeAsync($"aspire update --project \"{containerAppHostCsprojPath}\" --channel stable");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Perform updates?", timeout: TimeSpan.FromSeconds(60));
        await auto.EnterAsync(); // confirm "Perform updates?" (default: Yes)
        // The updater may prompt for a NuGet.config location and ask to apply changes
        // when the project doesn't have an existing NuGet.config. Accept defaults for both.
        await auto.WaitUntilTextAsync("Which directory for NuGet.config file?", timeout: TimeSpan.FromSeconds(30));
        await auto.EnterAsync(); // accept default directory
        await auto.WaitUntilTextAsync("Apply these changes to NuGet.config?", timeout: TimeSpan.FromSeconds(30));
        await auto.EnterAsync(); // confirm "Apply these changes to NuGet.config?" (default: Yes)
        await auto.WaitUntilTextAsync("Update successful!", timeout: TimeSpan.FromSeconds(60));
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify the PackageVersion for Aspire.Hosting.AppHost was removed
        {
            var content = File.ReadAllText(directoryPackagesPropsPath);
            if (content.Contains("Aspire.Hosting.AppHost"))
            {
                throw new InvalidOperationException($"File {directoryPackagesPropsPath} unexpectedly contains: Aspire.Hosting.AppHost");
            }
        }

        // Verify dotnet restore succeeds (would fail with NU1009 without the fix)
        await auto.TypeAsync($"dotnet restore \"{containerAppHostCsprojPath}\"");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(120));
        // Clean up: re-enable update notifications
        await auto.TypeAsync("aspire config delete features.updateNotificationsEnabled -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    public async Task AspireAddPackageVersionToDirectoryPackagesProps()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliInDockerAsync(installMode, counter);

        // Set up an AppHost project with CPM, but no installed packages
        var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, "CpmTest");
        var appHostDir = Path.Combine(projectDir, "CpmTest.AppHost");
        var appHostCsprojPath = Path.Combine(appHostDir, "CpmTest.AppHost.csproj");
        var directoryPackagesPropsPath = Path.Combine(projectDir, "Directory.Packages.props");
        var containerAppHostCsprojPath = CliE2ETestHelpers.ToContainerPath(appHostCsprojPath, workspace);

        Directory.CreateDirectory(appHostDir);

        File.WriteAllText(appHostCsprojPath, """
            <Project Sdk="Aspire.AppHost.Sdk/13.1.2">
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net9.0</TargetFramework>
                    <IsAspireHost>true</IsAspireHost>
                </PropertyGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(appHostDir, "Program.cs"), """
            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        File.WriteAllText(directoryPackagesPropsPath, """
            <Project>
                <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                </PropertyGroup>
            </Project>
            """);

        var waitingForVersionSelection = new CellPatternSearcher()
            .Find("Select a version of");
        var versionSelectionShown = false;

        await auto.TypeAsync("aspire add Aspire.Hosting.Redis");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(s =>
        {
            if (waitingForVersionSelection.Search(s).Count > 0)
            {
                versionSelectionShown = true;
                return true;
            }

            var successPromptSearcher = new CellPatternSearcher()
                .FindPattern(counter.Value.ToString())
                .RightText(" OK] $ ");

            return successPromptSearcher.Search(s).Count > 0;
        }, timeout: TimeSpan.FromSeconds(180), description: "version selection prompt or success prompt");

        if (versionSelectionShown)
        {
            // PR hives can surface multiple channels in CI. Accept the default implicit-channel version
            // so this test validates CPM behavior without pinning a specific package version.
            await auto.EnterAsync();
        }

        await auto.WaitForSuccessPromptAsync(counter);

        // Verify the AppHost project does not end up with a version-pinned Redis PackageReference.
        {
            var appHostProject = XDocument.Load(appHostCsprojPath);
            var hasVersionPinnedRedisReference = false;

            foreach (var element in appHostProject.Descendants())
            {
                if (element.Name.LocalName == "PackageReference" &&
                    string.Equals((string?)element.Attribute("Include"), "Aspire.Hosting.Redis", StringComparison.Ordinal) &&
                    element.Attribute("Version") is not null)
                {
                    hasVersionPinnedRedisReference = true;
                    break;
                }
            }

            if (hasVersionPinnedRedisReference)
            {
                throw new InvalidOperationException($"File {appHostCsprojPath} unexpectedly contains a version-pinned PackageReference for Aspire.Hosting.Redis");
            }
        }

        // Verify dotnet restore succeeds (would fail with NU1009 if AppHost.csproj contained a version)
        await auto.TypeAsync($"dotnet restore \"{containerAppHostCsprojPath}\"");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(120));
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
