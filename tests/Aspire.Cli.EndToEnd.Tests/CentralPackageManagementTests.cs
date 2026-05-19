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
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

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
    public async Task AspireUpdateRemovesOrphanAppHostPackageVersionWhenSdkAlreadyCurrent()
    {
        // Reproduces https://github.com/microsoft/aspire/issues/15476.
        //
        // Scenario: an AppHost is already on the latest stable Aspire SDK using the
        // new <Project Sdk="Aspire.AppHost.Sdk/<version>"> format, and the user has
        // (or copied) a CPM PackageVersion entry for Aspire.Hosting.AppHost in
        // Directory.Packages.props. Because the new SDK implicitly adds the
        // Aspire.Hosting.AppHost PackageReference with IsImplicitlyDefined=true,
        // NuGet rejects the orphan PackageVersion with NU1009.
        //
        // PR #14585 fixed the migration path (old format -> new format) by removing
        // the orphan PackageVersion as part of the SDK update step. But that step is
        // skipped entirely in AnalyzeAppHostSdkAsync when the SDK version is already
        // current, so a second run of `aspire update` does not clean up an
        // orphan that was introduced after the initial migration.
        //
        // To get the project onto the *exact* latest stable SDK without hard-coding
        // the version (which would go stale), this test runs `aspire update` once to
        // migrate from an old SDK version. After that first update the csproj is on
        // the latest stable SDK and Directory.Packages.props is clean. The test then
        // re-introduces an orphan PackageVersion entry and runs `aspire update`
        // again - which currently leaves the orphan in place and breaks the project.

        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // Disable update notifications to prevent the CLI self-update prompt
        // from appearing after "Update successful!" and blocking the test.
        await auto.TypeAsync("aspire config set features.updateNotificationsEnabled false -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, "CpmTest");
        var appHostDir = Path.Combine(projectDir, "CpmTest.AppHost");
        var appHostCsprojPath = Path.Combine(appHostDir, "CpmTest.AppHost.csproj");
        var directoryPackagesPropsPath = Path.Combine(projectDir, "Directory.Packages.props");
        var containerAppHostCsprojPath = CliE2ETestHelpers.ToContainerPath(appHostCsprojPath, workspace);

        Directory.CreateDirectory(appHostDir);

        // Start from an old-format AppHost so the first `aspire update` performs an
        // SDK migration and pins the csproj to the latest stable SDK version.
        File.WriteAllText(appHostCsprojPath, """
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.1.0" />
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

        // Start with a CPM-enabled props file that has no orphans yet.
        File.WriteAllText(directoryPackagesPropsPath, """
            <Project>
                <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                </PropertyGroup>
            </Project>
            """);

        // First update: migrate to the new SDK format on the latest stable version.
        await auto.TypeAsync($"aspire update --project \"{containerAppHostCsprojPath}\" --channel stable");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Perform updates?", timeout: TimeSpan.FromSeconds(60));
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Which directory for NuGet.config file?", timeout: TimeSpan.FromSeconds(30));
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Apply these changes to NuGet.config?", timeout: TimeSpan.FromSeconds(30));
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Update successful!", timeout: TimeSpan.FromSeconds(60));
        await auto.WaitForSuccessPromptAsync(counter);

        // Now the csproj is on the latest stable SDK. Discover that version from the
        // migrated csproj so we can write a matching orphan PackageVersion entry.
        var migratedCsproj = File.ReadAllText(appHostCsprojPath);
        var sdkMatch = System.Text.RegularExpressions.Regex.Match(
            migratedCsproj,
            @"Aspire\.AppHost\.Sdk/(?<version>[^""\s;]+)");
        if (!sdkMatch.Success)
        {
            throw new InvalidOperationException(
                $"Could not find Aspire.AppHost.Sdk/<version> directive in migrated csproj:\n{migratedCsproj}");
        }
        var latestStableSdkVersion = sdkMatch.Groups["version"].Value;
        output.WriteLine($"Latest stable AppHost SDK version: {latestStableSdkVersion}");

        // Re-introduce an orphan PackageVersion for Aspire.Hosting.AppHost. This is
        // the configuration that triggers NU1009 because the new SDK implicitly adds
        // the matching PackageReference.
        File.WriteAllText(directoryPackagesPropsPath, $$"""
            <Project>
                <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                </PropertyGroup>
                <ItemGroup>
                    <PackageVersion Include="Aspire.Hosting.AppHost" Version="{{latestStableSdkVersion}}" />
                </ItemGroup>
            </Project>
            """);

        // Second update: SDK is already current, so AnalyzeAppHostSdkAsync will
        // skip the SDK update step. The updater must still detect and remove the
        // orphan PackageVersion - that cleanup is itself an update step, so the
        // run prompts for confirmation just like the first update did, and
        // re-prompts for the NuGet.config because any update step (not just SDK
        // migration) can introduce package mappings the existing config may not
        // cover.
        await auto.TypeAsync($"aspire update --project \"{containerAppHostCsprojPath}\" --channel stable");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Perform updates?", timeout: TimeSpan.FromSeconds(60));
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Which directory for NuGet.config file?", timeout: TimeSpan.FromSeconds(30));
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Apply these changes to NuGet.config?", timeout: TimeSpan.FromSeconds(30));
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Update successful!", timeout: TimeSpan.FromSeconds(60));
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(120));

        // Verify the orphan PackageVersion was removed from Directory.Packages.props.
        {
            var content = File.ReadAllText(directoryPackagesPropsPath);
            if (content.Contains("Aspire.Hosting.AppHost"))
            {
                throw new InvalidOperationException(
                    $"File {directoryPackagesPropsPath} unexpectedly still contains an Aspire.Hosting.AppHost entry after the second `aspire update`:\n{content}");
            }
        }

        // Verify dotnet restore succeeds. Without the fix this fails with NU1009.
        await auto.TypeAsync($"dotnet restore \"{containerAppHostCsprojPath}\"");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(120));

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
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

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

        await auto.TypeAsync("aspire add Aspire.Hosting.Redis --non-interactive");
        await auto.EnterAsync();

        await auto.WaitForSuccessPromptAsync(counter);

        // Verify the AppHost project does not end up with a version-pinned Redis PackageReference.
        {
            var appHostProject = XDocument.Load(appHostCsprojPath);
            var directoryPackagesProps = XDocument.Load(directoryPackagesPropsPath);

            static IEnumerable<XElement> FindRedisProperties(XDocument document, string propertyName)
            {
                return document.Descendants()
                    .Where(element => element.Name.LocalName == propertyName)
                    .Where(element => string.Equals((string?)element.Attribute("Include"), "Aspire.Hosting.Redis", StringComparison.Ordinal));
            }

            var projectHasRedisVersionPin = FindRedisProperties(appHostProject, "PackageReference")
                .Any(element => element.Attribute("Version") is not null);
            var directoryPackagesHasRedisVersionPin = FindRedisProperties(directoryPackagesProps, "PackageVersion")
                .Single().Attribute("Version") is not null;

            if (projectHasRedisVersionPin)
            {
                throw new InvalidOperationException($"File {appHostCsprojPath} unexpectedly contains a version-pinned PackageReference for Aspire.Hosting.Redis");
            }
            if (!directoryPackagesHasRedisVersionPin)
            {
                throw new InvalidOperationException($"File {directoryPackagesPropsPath} unexpectedly does not contain the central PackageVersion for Aspire.Hosting.Redis");
            }
        }

        // Verify dotnet restore succeeds (would fail with NU1008 if AppHost.csproj contained a version)
        await auto.TypeAsync($"dotnet restore \"{containerAppHostCsprojPath}\"");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(120));
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
