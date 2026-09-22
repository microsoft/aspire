// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
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
                UpdateProjectAsyncCallback = async (context, cancellationToken) =>
                {
                    projectUpdated = true;
                    var packageJson = JsonNode.Parse(await File.ReadAllTextAsync(npmManifest, cancellationToken))!;
                    Assert.Equal("^13.4.0", packageJson["devDependencies"]![RepositoryToolUpdater.NpmPackageId]!.GetValue<string>());
                    await Assert.Single(context.AdditionalUpdateSteps).Callback();
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
                UpdateProjectAsyncCallback = async (context, _) =>
                {
                    await Assert.Single(context.AdditionalUpdateSteps).Callback();
                    return new ProjectUpdateResult { UpdatedApplied = true };
                }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_ResolutionFailureDoesNotChangeEitherManifest(bool hasAppHost)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (dotnetManifest, npmManifest) = await CreateManifestsAsync(workspace.WorkspaceRoot);
        var originalDotNetManifest = await File.ReadAllTextAsync(dotnetManifest);
        var originalNpmManifest = await File.ReadAllTextAsync(npmManifest);
        var appHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        if (hasAppHost)
        {
            await File.WriteAllTextAsync(appHost.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        }
        var interaction = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            ConfigureUpdates(options, interaction);
            options.NpmRunnerFactory = _ => new FakeNpmRunner();
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileAsyncCallback = (_, _, _) => Task.FromResult(hasAppHost ? appHost : null)
            };
            options.ProjectUpdaterFactory = _ => new TestProjectUpdater
            {
                UpdateProjectAsyncCallback = (_, _) => throw new InvalidOperationException("Project updates must not run if tool version resolution fails.")
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

    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 1)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 0)]
    public async Task Update_AppliesManifestAndProjectEditsTogetherBeforeRestore(bool confirmUpdates, bool projectNeedsUpdates, int restoreExitCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var directory = workspace.WorkspaceRoot;
        var (dotnetManifest, npmManifest) = await CreateManifestsAsync(directory);
        const string targetVersion = "13.6.0-pr.20295.test";
        var projectVersion = projectNeedsUpdates ? "13.4.0" : targetVersion;
        var appHostDirectory = workspace.CreateDirectory(Path.Combine("src", "AppHost"));
        var appHost = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHost.FullName, $"""
            <Project Sdk="Aspire.AppHost.Sdk/{projectVersion}">
              <ItemGroup>
                <PackageReference Include="Aspire.Hosting.Redis" />
              </ItemGroup>
            </Project>
            """);
        var packagesPath = Path.Combine(directory.FullName, "Directory.Packages.props");
        await File.WriteAllTextAsync(packagesPath, $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Aspire.Hosting.Redis" Version="{projectVersion}" />
                <PackageVersion Include="Unrelated.Package" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        var configPath = Path.Combine(directory.FullName, "aspire.config.json");
        await File.WriteAllTextAsync(configPath, $$"""
            {
              "appHost": { "path": "src/AppHost/AppHost.csproj" },
              "channel": "{{(projectNeedsUpdates ? "stable" : "daily")}}",
              "sdk": { "version": "{{projectVersion}}" }
            }
            """);
        var nugetPath = Path.Combine(directory.FullName, "NuGet.config");
        await File.WriteAllTextAsync(nugetPath, """
            <configuration>
              <packageSources>
                <clear />
                <add key="Existing" value="https://example.invalid/existing/v3/index.json" />
              </packageSources>
            </configuration>
            """);
        var lockPath = Path.Combine(directory.FullName, "package-lock.json");
        await File.WriteAllTextAsync(lockPath, """{"lockfileVersion":3}""");
        var originals = new Dictionary<string, byte[]>();
        foreach (var path in new[] { dotnetManifest, npmManifest, appHost.FullName, packagesPath, configPath, nugetPath, lockPath })
        {
            originals.Add(path, await File.ReadAllBytesAsync(path));
        }

        var restoreCalls = 0;
        var interaction = new TestInteractionService();
        interaction.ConfirmCallback = (prompt, _) =>
        {
            if (prompt != UpdateCommandStrings.PerformUpdatesPrompt)
            {
                Assert.Equal(UpdateCommandStrings.ApplyChangesToNuGetConfig, prompt);
                return false;
            }

            Assert.Contains(interaction.DisplayedMessages, message =>
                message.Message.Contains(RepositoryToolUpdater.DotNetPackageId, StringComparison.Ordinal) &&
                message.Message.Contains(RepositoryToolUpdater.NpmPackageId, StringComparison.Ordinal));
            if (projectNeedsUpdates)
            {
                Assert.Contains(interaction.DisplayedMessages, message => message.Message.Contains("Aspire.Hosting.Redis", StringComparison.Ordinal));
                Assert.Contains(interaction.DisplayedMessages, message => message.Message.Contains("aspire.config.json#sdk.version", StringComparison.Ordinal));
            }
            foreach (var (path, original) in originals)
            {
                Assert.Equal(original, File.ReadAllBytes(path));
            }
            return confirmUpdates;
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            ConfigureUpdates(options, interaction);
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileAsyncCallback = (_, _, _) => Task.FromResult<FileInfo?>(appHost)
            };
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>(
                [
                    new PackageChannel(PackageChannelNames.Daily, PackageChannelQuality.Both,
                        [new PackageMapping("Aspire*", "https://example.invalid/daily/v3/index.json")],
                        new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance,
                        pinnedVersion: targetVersion)
                ])
            };
            options.NpmRunnerFactory = _ => new FakeNpmRunner
            {
                ResolvePackageAsyncCallback = (_, _, _) => Task.FromResult<NpmPackageInfo?>(new() { Version = SemVersion.Parse(targetVersion) })
            };
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) => (0, JsonDocument.Parse($$"""
                    {
                      "Properties": {
                        "AspireHostingSDKVersion": "{{projectVersion}}",
                        "ManagePackageVersionsCentrally": "true"
                      },
                      "Items": {
                        "PackageReference": [{ "Identity": "Aspire.Hosting.Redis", "Version": "{{projectVersion}}" }]
                      }
                    }
                    """)),
                GetNuGetConfigPathsAsyncCallback = (_, _, _) => (0, [nugetPath]),
                RestoreAsyncCallback = (_, _, _) =>
                {
                    restoreCalls++;
                    AssertFilesUpdated();
                    return restoreExitCode;
                }
            };
        });
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse("update --channel daily").InvokeAsync().DefaultTimeout();

        Assert.Equal(restoreExitCode == 0 ? CliExitCodes.Success : CliExitCodes.FailedToUpgradeProject, result);
        Assert.Single(interaction.BooleanPromptCalls, call => call.PromptText == UpdateCommandStrings.PerformUpdatesPrompt);
        Assert.Equal(confirmUpdates && projectNeedsUpdates ? 2 : 1, interaction.BooleanPromptCalls.Count);
        Assert.Equal(confirmUpdates && projectNeedsUpdates ? 1 : 0, restoreCalls);
        if (confirmUpdates)
        {
            AssertFilesUpdated();
            Assert.Contains(UpdateCommandStrings.RepositoryToolsUpdated, interaction.DisplayedSuccess);
        }
        else
        {
            foreach (var (path, original) in originals)
            {
                Assert.Equal(original, await File.ReadAllBytesAsync(path));
            }
        }
        Assert.Equal(originals[nugetPath], await File.ReadAllBytesAsync(nugetPath));
        Assert.Equal(originals[lockPath], await File.ReadAllBytesAsync(lockPath));

        void AssertFilesUpdated()
        {
            Assert.Equal(targetVersion, JsonNode.Parse(File.ReadAllText(dotnetManifest))!["tools"]!["aspire.cli"]!["version"]!.GetValue<string>());
            Assert.Equal("^" + targetVersion, JsonNode.Parse(File.ReadAllText(npmManifest))!["devDependencies"]![RepositoryToolUpdater.NpmPackageId]!.GetValue<string>());
            Assert.Equal("Aspire.AppHost.Sdk/" + targetVersion, XDocument.Load(appHost.FullName).Root!.Attribute("Sdk")!.Value);
            var packages = XDocument.Load(packagesPath).Descendants("PackageVersion").ToArray();
            Assert.Equal(targetVersion, packages.Single(package => package.Attribute("Include")!.Value == "Aspire.Hosting.Redis").Attribute("Version")!.Value);
            Assert.Equal("1.0.0", packages.Single(package => package.Attribute("Include")!.Value == "Unrelated.Package").Attribute("Version")!.Value);
            var config = JsonNode.Parse(File.ReadAllText(configPath))!;
            Assert.Equal(targetVersion, config["sdk"]!["version"]!.GetValue<string>());
            Assert.Equal("daily", config["channel"]!.GetValue<string>());
        }
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
