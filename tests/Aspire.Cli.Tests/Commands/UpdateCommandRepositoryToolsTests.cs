// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Commands;
using Aspire.Cli.Npm;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Commands;

public class UpdateCommandRepositoryToolsTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_UpdatesBothManifestsWithoutReplacingExecutable(bool hasAppHost)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var directory = workspace.WorkspaceRoot;
        var (dotnetManifest, npmManifest) = await CreateManifestsAsync(directory);
        var appHost = new FileInfo(Path.Combine(directory.FullName, "AppHost.csproj"));
        if (hasAppHost)
        {
            await File.WriteAllTextAsync(appHost.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        }

        var projectUpdated = false;
        var interaction = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            ConfigureUpdates(options, interaction);
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileAsyncCallback = (_, _, _) => hasAppHost
                    ? Task.FromResult<FileInfo?>(appHost)
                    : throw new ProjectLocatorException(ErrorStrings.NoProjectFileFound, ProjectLocatorFailureReason.NoProjectFileFound)
            };
            options.ProjectUpdaterFactory = _ => new TestProjectUpdater
            {
                UpdateProjectAsyncCallback = async (_, cancellationToken) =>
                {
                    projectUpdated = true;
                    var packageJson = JsonNode.Parse(await File.ReadAllTextAsync(npmManifest, cancellationToken))!;
                    Assert.Equal("^13.4.0", packageJson["devDependencies"]![RepositoryToolUpdater.NpmPackageId]!.GetValue<string>());
                    packageJson["description"] = "Changed by project update";
                    await File.WriteAllTextAsync(npmManifest, packageJson.ToJsonString(), cancellationToken);
                    return new ProjectUpdateResult { UpdatedApplied = true };
                }
            };
        });
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse("update --channel stable --yes --non-interactive").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        Assert.Equal(hasAppHost, projectUpdated);
        Assert.Equal("13.5.4", JsonNode.Parse(await File.ReadAllTextAsync(dotnetManifest))!["tools"]!["aspire.cli"]!["version"]!.GetValue<string>());
        Assert.Equal("^13.5.4", JsonNode.Parse(await File.ReadAllTextAsync(npmManifest))!["devDependencies"]![RepositoryToolUpdater.NpmPackageId]!.GetValue<string>());
        if (hasAppHost)
        {
            Assert.Equal("Changed by project update", JsonNode.Parse(await File.ReadAllTextAsync(npmManifest))!["description"]!.GetValue<string>());
        }
        Assert.Contains(UpdateCommandStrings.RepositoryToolsUpdated, interaction.DisplayedSuccess);
        Assert.Empty(interaction.BooleanPromptCalls);
    }

    [Fact]
    public async Task Update_NewerGuestSdkRequiresRestoringRepositoryToolWithoutReplacingExecutable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (dotnetManifest, _) = await CreateManifestsAsync(workspace.WorkspaceRoot);
        var appHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"));
        await File.WriteAllTextAsync(appHost.FullName, "// test apphost");
        var projectUpdated = false;
        var interaction = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            ConfigureUpdates(options, interaction);
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileAsyncCallback = (_, _, _) => Task.FromResult<FileInfo?>(appHost)
            };
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                CanHandleCallback = _ => true,
                LanguageId = "typescript/nodejs",
                DisplayName = "TypeScript (Node.js)",
                DetectionPatterns = ["apphost.ts"],
                UpdatePackagesAsyncCallback = (_, _) =>
                {
                    projectUpdated = true;
                    return Task.FromResult(new UpdatePackagesResult { UpdatesApplied = true });
                }
            };
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>(
                [
                    new PackageChannel(PackageChannelNames.Stable, PackageChannelQuality.Stable, [],
                        new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance,
                        cliDownloadBaseUrl: "https://example.invalid/cli", pinnedVersion: "99.0.0")
                ])
            };
        });
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse("update --channel stable --yes --non-interactive").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        Assert.False(projectUpdated);
        Assert.Equal("99.0.0", JsonNode.Parse(await File.ReadAllTextAsync(dotnetManifest))!["tools"]!["aspire.cli"]!["version"]!.GetValue<string>());
        Assert.Contains(interaction.DisplayedMessages, message => message.Message == UpdateCommandStrings.ProjectUpdateSkippedAfterCliUpdateMessage);
        Assert.Empty(interaction.BooleanPromptCalls);
    }

    [Fact]
    public async Task Update_ExplicitAppHostOnlyUpdatesItsRepository()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (cwdManifest, _) = await CreateManifestsAsync(workspace.WorkspaceRoot);
        var originalCwdManifest = await File.ReadAllTextAsync(cwdManifest);
        var selectedDirectory = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "selected"));
        var (selectedManifest, _) = await CreateManifestsAsync(selectedDirectory);
        var appHost = Path.Combine(selectedDirectory.FullName, "AppHost.csproj");
        await File.WriteAllTextAsync(appHost, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            ConfigureUpdates(options, new TestInteractionService());
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileAsyncCallback = (file, _, _) => Task.FromResult(file)
            };
            options.ProjectUpdaterFactory = _ => new TestProjectUpdater
            {
                UpdateProjectAsyncCallback = (_, _) => Task.FromResult(new ProjectUpdateResult { UpdatedApplied = false })
            };
        });
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse(["update", "--apphost", appHost, "--channel", "stable", "--yes", "--non-interactive"])
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        Assert.Equal(originalCwdManifest, await File.ReadAllTextAsync(cwdManifest));
        Assert.Equal("13.5.4", JsonNode.Parse(await File.ReadAllTextAsync(selectedManifest))!["tools"]!["aspire.cli"]!["version"]!.GetValue<string>());
    }

    [Fact]
    public async Task Update_SelfDoesNotReadOrChangeRepositoryManifests()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (dotnetManifest, npmManifest) = await CreateManifestsAsync(workspace.WorkspaceRoot);
        await File.WriteAllTextAsync(npmManifest, "invalid JSON");
        var originalDotNetManifest = await File.ReadAllTextAsync(dotnetManifest);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            ConfigureUpdates(options, new TestInteractionService());
            options.ProcessPathProviderFactory = _ => new TestProcessPathProvider("/home/test/.dotnet/tools/aspire");
        });
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse("update --self --yes --non-interactive").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result);
        Assert.Equal(originalDotNetManifest, await File.ReadAllTextAsync(dotnetManifest));
        Assert.Equal("invalid JSON", await File.ReadAllTextAsync(npmManifest));
    }

    [Fact]
    public async Task Update_ResolutionFailureDoesNotChangeEitherManifest()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (dotnetManifest, npmManifest) = await CreateManifestsAsync(workspace.WorkspaceRoot);
        var originalDotNetManifest = await File.ReadAllTextAsync(dotnetManifest);
        var originalNpmManifest = await File.ReadAllTextAsync(npmManifest);
        var interaction = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            ConfigureUpdates(options, interaction);
            options.NpmRunnerFactory = _ => new FakeNpmRunner();
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileAsyncCallback = (_, _, _) => Task.FromResult<FileInfo?>(null)
            };
        });
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse("update --channel stable --yes --non-interactive").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToUpgradeProject, result);
        Assert.Equal(originalDotNetManifest, await File.ReadAllTextAsync(dotnetManifest));
        Assert.Equal(originalNpmManifest, await File.ReadAllTextAsync(npmManifest));
        Assert.Contains(interaction.DisplayedErrors, message => message.Contains("@microsoft/aspire-cli@latest", StringComparison.Ordinal));
    }

    private static void ConfigureUpdates(CliServiceCollectionTestOptions options, TestInteractionService interaction)
    {
        options.InteractionServiceFactory = _ => interaction;
        options.PackagingServiceFactory = _ => new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>(
            [
                new PackageChannel(PackageChannelNames.Stable, PackageChannelQuality.Stable,
                    [new PackageMapping("Aspire*", "https://api.nuget.org/v3/index.json")],
                    new FakeNuGetPackageCache
                    {
                        GetPackagesAsyncCallback = (_, packageId, _, _, _, _, _) =>
                            Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(
                            [
                                new() { Id = packageId, Version = "13.5.4", Source = "https://api.nuget.org/v3/index.json" }
                            ])
                    }, new TestFeatures(), NullLogger.Instance, cliDownloadBaseUrl: "https://example.invalid/cli")
            ])
        };
        options.NpmRunnerFactory = _ => new FakeNpmRunner
        {
            ResolvePackageAsyncCallback = (_, _, _) => Task.FromResult<NpmPackageInfo?>(new() { Version = SemVersion.Parse("13.5.4") })
        };
        options.CliDownloaderFactory = sp => new TestCliDownloader(sp.GetRequiredService<CliExecutionContext>().WorkingDirectory)
        {
            DownloadLatestCliAsyncCallback = (_, _) => throw new InvalidOperationException("Repository updates must not replace the CLI executable.")
        };
        options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier { IsUpdateAvailableCallback = () => true };
    }

    private static async Task<(string DotnetManifest, string NpmManifest)> CreateManifestsAsync(DirectoryInfo directory)
    {
        Directory.CreateDirectory(Path.Combine(directory.FullName, ".git"));
        var config = Directory.CreateDirectory(Path.Combine(directory.FullName, ".config"));
        var dotnetManifest = Path.Combine(config.FullName, "dotnet-tools.json");
        await File.WriteAllTextAsync(dotnetManifest, """
            {
              "version": 1,
              "isRoot": true,
              "tools": {
                "aspire.cli": { "version": "13.4.0", "commands": ["aspire"], "rollForward": true }
              }
            }
            """);
        var npmManifest = Path.Combine(directory.FullName, "package.json");
        await File.WriteAllTextAsync(npmManifest, """
            {
              "name": "test-app",
              "devDependencies": { "@microsoft/aspire-cli": "^13.4.0" }
            }
            """);
        return (dotnetManifest, npmManifest);
    }
}
