// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

public class ProjectUpdaterNuGetConfigurationTests(ITestOutputHelper outputHelper)
{
    public static TheoryData<string, string, string, bool> ConfigurationOverrides
    {
        get
        {
            var data = new TheoryData<string, string, string, bool>();
            foreach (var fileName in new[] { "AppHost.csproj", "apphost.cs" })
            {
                foreach (var property in new[]
                {
                    "RestoreConfigFile", "RestoreRootConfigDirectory", "RestoreSources",
                    "RestoreAdditionalProjectSources", "_RestoreSourcesOverride", "NuGetRestoreTargets"
                })
                {
                    data.Add(fileName, property, "stable", true);
                    data.Add(fileName, property, "stable", false);
                    data.Add(fileName, property, "default", false);
                }
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ConfigurationOverrides))]
    public async Task UpdateAsync_CustomizedNuGetConfiguration_OnlyBlocksExplicitChannelPolicy(
        string fileName, string overriddenProperty, string channelName, bool hasExplicitChannel)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectFile = await CreateAppHostAsync(workspace, fileName);
        var configFile = new FileInfo(Path.Combine(workspace.Path, "NuGet.Config"));
        await File.WriteAllTextAsync(configFile.FullName, """
            <configuration>
              <packageSources>
                <add key="company" value="https://company.example/v3/index.json" />
              </packageSources>
            </configuration>
            """);
        var originalProject = await File.ReadAllBytesAsync(projectFile.FullName);
        var originalConfig = await File.ReadAllBytesAsync(configFile.FullName);
        var runner = CreateRunner(projectFile, hasExplicitChannel);
        var inspectionInvoked = false;
        runner.GetProjectItemsAndPropertiesAsyncCallbackWithTargets = (file, _, properties, targets, options, _) =>
        {
            Assert.Equal(projectFile.FullName, file.FullName);
            if (!properties.Contains("RestoreConfigFile", StringComparer.Ordinal))
            {
                return CreatePackageEvaluation();
            }

            inspectionInvoked = true;
            Assert.Empty(targets);
            Assert.True(options.NoRestore);
            Assert.True(options.ExcludeRestorePackageImports);
            Assert.True(options.SuppressLogging);
            using var output = TestDotNetCliRunner.CreateRestoreSettingsOutput(file, properties);
            var json = JsonNode.Parse(output.RootElement.GetRawText())!.AsObject();
            json["Properties"]![overriddenProperty] = Path.Combine(workspace.WorkspaceRoot.FullName, "custom-policy");
            return (0, JsonDocument.Parse(json.ToJsonString()));
        };

        using var provider = CreateServices(workspace, runner).BuildServiceProvider();
        var context = await CreateContextAsync(provider, projectFile, channelName, hasExplicitChannel);
        var updater = provider.GetRequiredService<IProjectUpdater>();
        if (hasExplicitChannel)
        {
            var error = await Assert.ThrowsAsync<ProjectUpdaterException>(() =>
                updater.UpdateProjectAsync(context, TestContext.Current.CancellationToken));
            Assert.Contains(overriddenProperty, error.Message);
            Assert.Equal(originalProject, await File.ReadAllBytesAsync(projectFile.FullName));
            var interaction = Assert.IsType<TestInteractionService>(provider.GetRequiredService<IInteractionService>());
            Assert.Empty(interaction.BooleanPromptCalls);
            Assert.Empty(interaction.FilePathPromptCalls);
        }
        else
        {
            var result = await updater.UpdateProjectAsync(context, TestContext.Current.CancellationToken);
            Assert.True(result.UpdatedApplied);
            AssertUpdatedSdk(provider, projectFile);
        }

        Assert.Equal(originalConfig, await File.ReadAllBytesAsync(configFile.FullName));
        Assert.Equal(hasExplicitChannel, inspectionInvoked);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(workspace.Path, ".aspire"), ".aspire-nuget-config-*"));
    }

    [Theory]
    [InlineData("AppHost.csproj", "stable", true)]
    [InlineData("AppHost.csproj", "stable", false)]
    [InlineData("AppHost.csproj", "default", false)]
    [InlineData("apphost.cs", "stable", true)]
    [InlineData("apphost.cs", "stable", false)]
    [InlineData("apphost.cs", "default", false)]
    public async Task UpdateAsync_FailedEvaluation_PreservesUnqualifiedSdkRepair(
        string fileName, string channelName, bool hasExplicitChannel)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectFile = await CreateAppHostAsync(workspace, fileName);
        var originalProject = await File.ReadAllBytesAsync(projectFile.FullName);
        var runner = CreateRunner(projectFile, hasExplicitChannel);
        runner.GetProjectItemsAndPropertiesAsyncCallbackWithTargets = (_, _, _, _, _, _) => (1, null);

        using var provider = CreateServices(workspace, runner).BuildServiceProvider();
        var context = await CreateContextAsync(provider, projectFile, channelName, hasExplicitChannel);
        var updater = provider.GetRequiredService<IProjectUpdater>();
        if (hasExplicitChannel)
        {
            await Assert.ThrowsAsync<ProjectUpdaterException>(() =>
                updater.UpdateProjectAsync(context, TestContext.Current.CancellationToken));
            Assert.Equal(originalProject, await File.ReadAllBytesAsync(projectFile.FullName));
        }
        else
        {
            var result = await updater.UpdateProjectAsync(context, TestContext.Current.CancellationToken);
            Assert.True(result.UpdatedApplied);
            AssertUpdatedSdk(provider, projectFile);
        }

        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(workspace.Path, ".aspire"), ".aspire-nuget-config-*"));
    }

    [Theory]
    [InlineData("AppHost.csproj", "stable", true)]
    [InlineData("AppHost.csproj", "stable", false)]
    [InlineData("AppHost.csproj", "default", true)]
    [InlineData("apphost.cs", "stable", true)]
    [InlineData("apphost.cs", "stable", false)]
    [InlineData("apphost.cs", "default", true)]
    public async Task UpdateAsync_CanceledEvaluation_DoesNotFallBackOrApplyUpdates(
        string fileName, string channelName, bool hasExplicitChannel)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectFile = await CreateAppHostAsync(workspace, fileName);
        var originalProject = await File.ReadAllBytesAsync(projectFile.FullName);
        using var cancellation = new CancellationTokenSource();
        var runner = CreateRunner(projectFile, hasExplicitChannel: true);
        runner.GetProjectItemsAndPropertiesAsyncCallbackWithTargetsAsync = (_, _, _, _, _, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<(int, JsonDocument?)>(token);
        };

        using var provider = CreateServices(workspace, runner).BuildServiceProvider();
        var context = await CreateContextAsync(provider, projectFile, channelName, hasExplicitChannel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<IProjectUpdater>().UpdateProjectAsync(context, cancellation.Token));

        Assert.Equal(originalProject, await File.ReadAllBytesAsync(projectFile.FullName));
        Assert.False(File.Exists(Path.Combine(workspace.Path, "nuget.config")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(workspace.Path, ".aspire"), ".aspire-nuget-config-*"));
    }

    private static async Task<FileInfo> CreateAppHostAsync(TemporaryWorkspace workspace, string fileName)
    {
        var content = fileName == "apphost.cs"
            ? """
              #:sdk Aspire.AppHost.Sdk@9.4.1
              #:package Aspire.Hosting.Redis@9.4.1
              """
            : """
              <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.4.1" />
                <ItemGroup>
                  <PackageReference Include="Aspire.Hosting.Redis" Version="9.4.1" />
                </ItemGroup>
              </Project>
              """;
        var file = new FileInfo(Path.Combine(workspace.Path, fileName));
        await File.WriteAllTextAsync(file.FullName, content);
        return file;
    }

    [Theory]
    [InlineData("AppHost.csproj", false, "daily", false, true)]
    [InlineData("apphost.cs", false, "daily", false, true)]
    [InlineData("AppHost.csproj", true, "daily", false, true)]
    [InlineData("apphost.cs", true, "daily", false, true)]
    [InlineData("AppHost.csproj", true, "staging", true, true)]
    [InlineData("apphost.cs", true, "staging", true, true)]
    [InlineData("AppHost.csproj", true, "different-channel", false, true)]
    [InlineData("AppHost.csproj", true, "daily", false, false)]
    [InlineData("apphost.cs", true, "staging", true, false)]
    [InlineData("AppHost.csproj", true, "stable", false, true)]
    [InlineData("apphost.cs", true, "stable", false, true)]
    public async Task UpdateAsync_UnchangedPolicyUsesNativeDiscoveryWithoutWritingConfiguration(
        string fileName, bool hasExplicitChannel, string channelName, bool configureCache, bool updateVersions)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectFile = await CreateAppHostAsync(workspace, fileName);
        const string currentSource = "https://company.example/v3/index.json";
        var configFile = await WriteCurrentPolicyAsync(workspace, currentSource, configureCache);
        var originalConfig = await File.ReadAllBytesAsync(configFile.FullName);
        if (!updateVersions)
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, AspireConfigFile.FileName),
                JsonSerializer.Serialize(new { channel = channelName, sdkVersion = "9.4.1" }));
        }

        var runner = CreateRunner(projectFile, hasExplicitChannel: false);
        runner.GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) => CreatePackageEvaluation();
        runner.SearchPackagesAsyncCallback = (directory, query, _, _, _, _, explicitConfig, _, _, _) =>
        {
            Assert.Equal(projectFile.DirectoryName, directory.FullName);
            Assert.Null(explicitConfig);
            Assert.Equal([currentSource], NuGetTestHelper.GetEligiblePackageSources(directory.FullName, query));
            return (0, [new NuGetPackageCli { Id = query, Version = updateVersions ? "9.4.2" : "9.4.1", Source = currentSource }]);
        };
        using var provider = CreateServices(workspace, runner).BuildServiceProvider();
        var channel = PackageChannel.CreateExplicitChannel(
            channelName, PackageChannelQuality.Both,
            [new("Aspire*", hasExplicitChannel ? currentSource : "https://replacement.example/v3/index.json")],
            provider.GetRequiredService<INuGetPackageCache>(), new TestFeatures(), NullLogger.Instance,
            configureGlobalPackagesFolder: configureCache);

        var result = await provider.GetRequiredService<IProjectUpdater>().UpdateProjectAsync(
            CreateContext(projectFile, channel, hasExplicitChannel), TestContext.Current.CancellationToken);

        Assert.Equal(updateVersions, result.UpdatedApplied);
        if (updateVersions)
        {
            AssertUpdatedSdk(provider, projectFile);
        }
        Assert.Equal(originalConfig, await File.ReadAllBytesAsync(configFile.FullName));
        var interaction = Assert.IsType<TestInteractionService>(provider.GetRequiredService<IInteractionService>());
        Assert.Empty(interaction.FilePathPromptCalls);
        Assert.Equal(updateVersions ? 1 : 0, interaction.BooleanPromptCalls.Count);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(workspace.Path, ".aspire"), ".aspire-nuget-config-*"));
    }

    [Theory]
    [InlineData("AppHost.csproj", false, true)]
    [InlineData("apphost.cs", false, true)]
    [InlineData("AppHost.csproj", true, true)]
    [InlineData("apphost.cs", true, true)]
    [InlineData("AppHost.csproj", false, false)]
    [InlineData("apphost.cs", false, false)]
    public async Task UpdateAsync_ExplicitStablePreservesAmbientSourcesWithoutAddingNuGetOrg(
        string fileName, bool hasNuGetOrg, bool hasMappings)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectFile = await CreateAppHostAsync(workspace, fileName);
        const string mirror = "https://company.example/v3/index.json";
        const string daily = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json";
        var channelSourceKey = $"aspire-{AppHostWorkloadId.Create(workspace.Path)}";
        var publicSource = hasNuGetOrg
            ? $$"""<add key="public" value="{{PackageSources.NuGetOrg}}" />"""
            : string.Empty;
        var publicMapping = hasNuGetOrg
            ? """<packageSource key="public"><package pattern="*" /></packageSource>"""
            : string.Empty;
        var mappings = hasMappings
            ? $$"""
              <packageSourceMapping>
                <clear />
                <packageSource key="company"><package pattern="*" /></packageSource>
                <packageSource key="{{channelSourceKey}}"><package pattern="Aspire*" /></packageSource>
                <packageSource key="private"><package pattern="Private.*" /></packageSource>
                {{publicMapping}}
              </packageSourceMapping>
              """
            : string.Empty;
        var configFile = new FileInfo(Path.Combine(workspace.Path, "NuGet.Config"));
        await File.WriteAllTextAsync(configFile.FullName, $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="company" value="{{mirror}}" />
                <add key="{{channelSourceKey}}" value="{{daily}}" />
                <add key="private" value="https://private.example/v3/index.json" />
                {{publicSource}}
              </packageSources>
              {{mappings}}
            </configuration>
            """);
        var originalConfig = await File.ReadAllBytesAsync(configFile.FullName);
        var expectedSources = hasMappings
            ? hasNuGetOrg ? new[] { mirror, PackageSources.NuGetOrg } : [mirror]
            : [mirror, "https://private.example/v3/index.json"];
        var runner = CreateRunner(projectFile, hasExplicitChannel: false);
        runner.GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) => CreatePackageEvaluation();
        runner.GetNuGetConfigPathsAsyncCallback = (_, _, _) => (0, [configFile.FullName]);
        runner.SearchPackagesAsyncCallback = (directory, query, _, _, _, _, explicitConfig, _, _, _) =>
        {
            Assert.Null(explicitConfig);
            Assert.Equal(expectedSources.Order(), NuGetTestHelper.GetEligiblePackageSources(directory.FullName, query).Order());
            return (0, [new NuGetPackageCli { Id = query, Version = "9.4.2", Source = mirror }]);
        };
        var restoreCount = 0;
        runner.RestoreAsyncCallback = (_, options, _) =>
        {
            Assert.Equal(originalConfig, File.ReadAllBytes(configFile.FullName));
            Assert.NotNull(options.NuGetRestoreTargetsFile);
            Assert.Equal(expectedSources.Order(),
                NuGetTestHelper.GetEligiblePackageSources(options.NuGetRestoreTargetsFile.DirectoryName!, "Aspire.Hosting.Redis").Order());
            restoreCount++;
            return 0;
        };
        using var provider = CreateServices(workspace, runner, new FakeNuGetClient
        {
            GetSettingsCallback = NuGetTestHelper.CreateClient().GetSettings
        }).BuildServiceProvider();
        var context = await CreateContextAsync(provider, projectFile, "stable", hasExplicitChannel: true);

        var result = await provider.GetRequiredService<IProjectUpdater>().UpdateProjectAsync(
            context, TestContext.Current.CancellationToken);

        Assert.True(result.UpdatedApplied);
        Assert.Equal(1, restoreCount);
        AssertUpdatedSdk(provider, projectFile);
        Assert.Equal(expectedSources.Order(),
            NuGetTestHelper.GetEligiblePackageSources(workspace.Path, "Aspire.Hosting.Redis").Order());
        var settings = provider.GetRequiredService<BundleNuGetService>()
            .GetNuGetSettings(workspace.Path, TestContext.Current.CancellationToken);
        Assert.Equal(hasNuGetOrg, settings.Sources.Any(static source => source.Name == "public"));
        string[] expectedSourceNames = hasNuGetOrg ? ["company", "private", "public"] : ["company", "private"];
        Assert.Equal(expectedSourceNames,
            settings.Sources.Where(static source => source.IsEnabled).Select(static source => source.Name).Order());
    }

    [Theory]
    [InlineData("AppHost.csproj", true, false)]
    [InlineData("apphost.cs", true, false)]
    [InlineData("AppHost.csproj", true, true)]
    [InlineData("apphost.cs", true, true)]
    [InlineData("AppHost.csproj", false, false)]
    [InlineData("apphost.cs", false, false)]
    [InlineData("AppHost.csproj", false, true)]
    [InlineData("apphost.cs", false, true)]
    public async Task UpdateAsync_ServiceIndexOverrideOnlyChangesExplicitStablePolicy(
        string fileName, bool hasExplicitChannel, bool hasOverrideSource)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectFile = await CreateAppHostAsync(workspace, fileName);
        const string currentSource = "https://company.example/v3/index.json";
        const string overrideSource = "https://mirror.example/v3/index.json";
        var overrideSourceEntry = hasOverrideSource
            ? $$"""<add key="mirror-alias" value="{{overrideSource}}" />"""
            : string.Empty;
        var overrideMapping = hasOverrideSource
            ? """<packageSource key="mirror-alias"><package pattern="*" /></packageSource>"""
            : string.Empty;
        var configFile = new FileInfo(Path.Combine(workspace.Path, "NuGet.Config"));
        await File.WriteAllTextAsync(configFile.FullName, $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="company" value="{{currentSource}}" />
                {{overrideSourceEntry}}
              </packageSources>
              <packageSourceMapping>
                <clear />
                <packageSource key="company"><package pattern="*" /></packageSource>
                {{overrideMapping}}
              </packageSourceMapping>
            </configuration>
            """);
        var originalConfig = await File.ReadAllBytesAsync(configFile.FullName);
        string[] expectedSources = hasExplicitChannel
            ? [overrideSource]
            : hasOverrideSource ? [currentSource, overrideSource] : [currentSource];
        var runner = CreateRunner(projectFile, hasExplicitChannel: false);
        runner.GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) => CreatePackageEvaluation();
        runner.GetNuGetConfigPathsAsyncCallback = (_, _, _) => (0, [configFile.FullName]);
        runner.SearchPackagesAsyncCallback = (directory, query, _, _, _, _, explicitConfig, _, _, _) =>
        {
            Assert.Null(explicitConfig);
            Assert.Equal(expectedSources.Order(),
                NuGetTestHelper.GetEligiblePackageSources(directory.FullName, query).Order());
            return (0, [new NuGetPackageCli { Id = query, Version = "9.4.2", Source = expectedSources[0] }]);
        };
        var restoreCount = 0;
        runner.RestoreAsyncCallback = (_, options, _) =>
        {
            Assert.Equal(originalConfig, File.ReadAllBytes(configFile.FullName));
            Assert.Equal(hasExplicitChannel, options.NuGetRestoreTargetsFile is not null);
            Assert.Equal(expectedSources.Order(),
                NuGetTestHelper.GetEligiblePackageSources(
                    options.NuGetRestoreTargetsFile?.DirectoryName ?? workspace.Path, "Aspire.Hosting.Redis").Order());
            restoreCount++;
            return 0;
        };
        using var provider = CreateServices(workspace, runner, new FakeNuGetClient
        {
            GetSettingsCallback = NuGetTestHelper.CreateClient().GetSettings
        }).AddSingleton(workspace.CreateExecutionContext(nugetServiceIndexOverride: overrideSource))
            .BuildServiceProvider();
        var context = await CreateContextAsync(provider, projectFile, "stable", hasExplicitChannel);

        var result = await provider.GetRequiredService<IProjectUpdater>().UpdateProjectAsync(
            context, TestContext.Current.CancellationToken);

        Assert.True(result.UpdatedApplied);
        Assert.Equal(1, restoreCount);
        AssertUpdatedSdk(provider, projectFile);
        Assert.Equal(expectedSources.Order(),
            NuGetTestHelper.GetEligiblePackageSources(workspace.Path, "Aspire.Hosting.Redis").Order());
        var settings = provider.GetRequiredService<BundleNuGetService>()
            .GetNuGetSettings(workspace.Path, TestContext.Current.CancellationToken);
        string[] expectedConfiguredSources = hasExplicitChannel || hasOverrideSource
            ? [currentSource, overrideSource]
            : [currentSource];
        Assert.Equal(
            expectedConfiguredSources.Select(source => NuGetSourceIdentity.Compute(source, settings.SourceIdentityKey)).Order(),
            settings.Sources.Where(static source => source.IsEnabled).Select(static source => source.Identity).Order());
        if (hasOverrideSource)
        {
            var overrideIdentity = NuGetSourceIdentity.Compute(overrideSource, settings.SourceIdentityKey);
            Assert.Equal("mirror-alias", Assert.Single(settings.Sources, source => source.Identity == overrideIdentity).Name);
        }
        if (!hasExplicitChannel)
        {
            Assert.Equal(originalConfig, await File.ReadAllBytesAsync(configFile.FullName));
        }
    }

    [Theory]
    [InlineData("AppHost.csproj")]
    [InlineData("apphost.cs")]
    public async Task UpdateAsync_ExplicitSameChannelWithDifferentFeedRestoresWithoutVersionChanges(string fileName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectFile = await CreateAppHostAsync(workspace, fileName);
        var originalProject = await File.ReadAllBytesAsync(projectFile.FullName);
        var configFile = await WriteCurrentPolicyAsync(workspace, "https://old.example/v3/index.json", configureCache: false);
        const string newSource = "https://new.example/v3/index.json";
        var runner = CreateRunner(projectFile, hasExplicitChannel: false);
        runner.GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) => CreatePackageEvaluation();
        runner.SearchPackagesAsyncCallback = (directory, query, _, _, _, _, explicitConfig, _, _, _) =>
        {
            Assert.Null(explicitConfig);
            Assert.Equal([newSource], NuGetTestHelper.GetEligiblePackageSources(directory.FullName, query));
            return (0, [new NuGetPackageCli { Id = query, Version = "9.4.1", Source = newSource }]);
        };
        runner.GetNuGetConfigPathsAsyncCallback = (_, _, _) => (0,
            [configFile.FullName, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "NuGet.Config")]);
        var restoreCount = 0;
        var originalConfig = await File.ReadAllBytesAsync(configFile.FullName);
        runner.RestoreAsyncCallback = (file, options, _) =>
        {
            Assert.Equal(projectFile.FullName, file.FullName);
            Assert.Null(options.EnvironmentVariables);
            Assert.Equal(originalConfig, File.ReadAllBytes(configFile.FullName));
            Assert.NotNull(options.NuGetRestoreTargetsFile);
            Assert.Equal([newSource], NuGetTestHelper.GetEligiblePackageSources(options.NuGetRestoreTargetsFile.DirectoryName!, "Aspire.AppHost.Sdk"));
            Assert.Equal([newSource], NuGetTestHelper.GetEligiblePackageSources(options.NuGetRestoreTargetsFile.DirectoryName!, "Aspire.Hosting.Redis"));
            restoreCount++;
            return 0;
        };
        using var provider = CreateServices(workspace, runner, new FakeNuGetClient
        {
            GetSettingsCallback = NuGetTestHelper.CreateClient().GetSettings
        }).BuildServiceProvider();
        var channel = PackageChannel.CreateExplicitChannel(
            "staging", PackageChannelQuality.Both, [new("Aspire*", newSource)],
            provider.GetRequiredService<INuGetPackageCache>(), new TestFeatures(), NullLogger.Instance);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, AspireConfigFile.FileName),
            """{ "channel": "staging", "sdkVersion": "9.4.1" }""");

        var result = await provider.GetRequiredService<IProjectUpdater>().UpdateProjectAsync(
            CreateContext(projectFile, channel, hasExplicitChannel: true), TestContext.Current.CancellationToken);

        Assert.True(result.UpdatedApplied);
        Assert.Equal(1, restoreCount);
        Assert.Equal(originalProject, await File.ReadAllBytesAsync(projectFile.FullName));
        Assert.Equal([newSource], NuGetTestHelper.GetEligiblePackageSources(projectFile.DirectoryName!, "Aspire.Hosting.Redis"));
    }

    [Theory]
    [InlineData("AppHost.csproj")]
    [InlineData("apphost.cs")]
    public async Task UpdateAsync_DecliningConfigurationStopsAllFileUpdates(string fileName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectFile = await CreateAppHostAsync(workspace, fileName);
        var originalProject = await File.ReadAllBytesAsync(projectFile.FullName);
        var configFile = await WriteCurrentPolicyAsync(workspace, "https://old.example/v3/index.json", configureCache: false);
        var originalConfig = await File.ReadAllBytesAsync(configFile.FullName);
        var aspireConfigPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        const string originalAspireConfig = """{ "channel": "staging", "sdkVersion": "9.4.1" }""";
        await File.WriteAllTextAsync(aspireConfigPath, originalAspireConfig);
        var runner = CreateRunner(projectFile, hasExplicitChannel: false);
        runner.GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) => CreatePackageEvaluation();
        runner.SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _, _) => (0,
            [new NuGetPackageCli
            {
                Id = query,
                Version = "9.4.2",
                Source = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json"
            }]);
        runner.GetNuGetConfigPathsAsyncCallback = (_, _, _) => (0,
            [configFile.FullName, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "NuGet.Config")]);
        runner.AddPackageAsyncCallback = (_, _, _, _, _, _, _) =>
            throw new InvalidOperationException("Declining the configuration must not apply package edits.");
        runner.RestoreAsyncCallback = (_, _, _) =>
            throw new InvalidOperationException("Declining the configuration must not restore.");
        using var provider = CreateServices(workspace, runner, new FakeNuGetClient
        {
            GetSettingsCallback = NuGetTestHelper.CreateClient().GetSettings
        }).BuildServiceProvider();
        var interaction = Assert.IsType<TestInteractionService>(provider.GetRequiredService<IInteractionService>());
        interaction.ConfirmCallback = (prompt, _) => prompt == UpdateCommandStrings.PerformUpdatesPrompt;
        var channel = PackageChannel.CreateExplicitChannel(
            "daily", PackageChannelQuality.Both,
            [new("Aspire*", "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json")],
            provider.GetRequiredService<INuGetPackageCache>(), new TestFeatures(), NullLogger.Instance);
        var additionalEditApplied = false;
        var context = new UpdatePackagesContext
        {
            AppHostFile = projectFile,
            Channel = channel,
            HasExplicitChannel = true,
            ConfirmBinding = PromptBinding.CreateDefault(true),
            NuGetConfigDirBinding = PromptBinding.CreateDefault<string?>(null),
            AdditionalUpdateSteps = [new PackageUpdateStep("Update repository tool", () =>
            {
                additionalEditApplied = true;
                return Task.CompletedTask;
            }, "Aspire.Cli", "9.4.1", "9.4.2", projectFile)]
        };

        var result = await provider.GetRequiredService<IProjectUpdater>().UpdateProjectAsync(context, TestContext.Current.CancellationToken);

        Assert.False(result.UpdatedApplied);
        Assert.False(additionalEditApplied);
        Assert.Equal(originalProject, await File.ReadAllBytesAsync(projectFile.FullName));
        Assert.Equal(originalConfig, await File.ReadAllBytesAsync(configFile.FullName));
        Assert.Equal(originalAspireConfig, await File.ReadAllTextAsync(aspireConfigPath));
        Assert.Equal(2, interaction.BooleanPromptCalls.Count);
    }

    [Theory]
    [InlineData("AppHost.csproj", "success")]
    [InlineData("apphost.cs", "success")]
    [InlineData("AppHost.csproj", "restore")]
    [InlineData("apphost.cs", "restore")]
    [InlineData("AppHost.csproj", "cancel")]
    [InlineData("apphost.cs", "cancel")]
    [InlineData("AppHost.csproj", "edit")]
    [InlineData("apphost.cs", "edit")]
    [InlineData("AppHost.csproj", "bootstrap")]
    [InlineData("apphost.cs", "bootstrap")]
    [InlineData("AppHost.csproj", "commit")]
    [InlineData("apphost.cs", "commit")]
    [InlineData("AppHost.csproj", "stale-config")]
    [InlineData("apphost.cs", "stale-config")]
    [InlineData("AppHost.csproj", "stale-project")]
    [InlineData("apphost.cs", "stale-project")]
    public async Task UpdateAsync_ValidatesCompleteCandidateBeforePersisting(string fileName, string outcome)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectFile = await CreateAppHostAsync(workspace, fileName);
        var originalProject = await File.ReadAllBytesAsync(projectFile.FullName);
        var configFile = await WriteCurrentPolicyAsync(workspace, "https://old.example/v3/index.json", configureCache: false);
        var originalConfig = await File.ReadAllBytesAsync(configFile.FullName);
        var aspireConfigPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        const string originalAspireConfig = """{ "channel": "staging", "sdkVersion": "9.4.1" }""";
        await File.WriteAllTextAsync(aspireConfigPath, originalAspireConfig);
        const string newSource = "https://new.example/v3/index.json";
        using var cancellation = new CancellationTokenSource();
        List<string> calls = [];
        var client = new FakeNuGetClient
        {
            GetSettingsCallback = NuGetTestHelper.CreateClient().GetSettings,
            RestoreCallback = (packages, _, _, _, _, _, directory, _, _, _) =>
            {
                Assert.Equal([("Aspire.AppHost.Sdk", "9.4.2")], packages);
                Assert.Equal([newSource], NuGetTestHelper.GetEligiblePackageSources(directory, "Aspire.AppHost.Sdk"));
                Assert.Equal(originalProject, File.ReadAllBytes(projectFile.FullName));
                Assert.Equal(originalConfig, File.ReadAllBytes(configFile.FullName));
                calls.Add("bootstrap");
                return outcome == "bootstrap"
                    ? Task.FromException(new InvalidOperationException("SDK acquisition failed."))
                    : Task.CompletedTask;
            }
        };
        var runner = CreateRunner(projectFile, hasExplicitChannel: false);
        runner.GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, options, _) =>
        {
            Assert.True(options.NoRestore);
            Assert.True(options.ExcludeRestorePackageImports);
            return CreatePackageEvaluation();
        };
        runner.SearchPackagesAsyncCallback = (directory, query, _, _, _, _, _, _, _, _) =>
        {
            Assert.Equal(originalProject, File.ReadAllBytes(projectFile.FullName));
            Assert.Equal(originalConfig, File.ReadAllBytes(configFile.FullName));
            Assert.Equal([newSource], NuGetTestHelper.GetEligiblePackageSources(directory.FullName, query));
            var call = $"discover:{query}";
            if (calls.Count == 0 || calls[^1] != call)
            {
                calls.Add(call);
            }
            return (0, [new NuGetPackageCli { Id = query, Version = "9.4.2", Source = newSource }]);
        };
        runner.GetNuGetConfigPathsAsyncCallback = (_, _, _) => (0,
            [configFile.FullName, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "NuGet.Config")]);
        runner.AddPackageAsyncCallback = (file, id, version, _, noRestore, _, _) =>
        {
            Assert.True(noRestore);
            Assert.Equal("Aspire.Hosting.Redis", id);
            Assert.Equal("9.4.2", version);
            Assert.Contains("Aspire.AppHost.Sdk", File.ReadAllText(file.FullName));
            var content = File.ReadAllText(file.FullName).Replace("9.4.1", version, StringComparison.Ordinal);
            File.WriteAllText(file.FullName, content);
            calls.Add("package");
            return outcome == "edit" ? 1 : 0;
        };
        runner.RestoreAsyncCallback = (file, options, _) =>
        {
            Assert.Equal(projectFile.FullName, file.FullName);
            using var parsed = new FallbackProjectParser(NullLogger<FallbackProjectParser>.Instance).ParseProject(file);
            Assert.Equal("9.4.2", parsed.RootElement.GetProperty("Properties").GetProperty("AspireHostingSDKVersion").GetString());
            var package = Assert.Single(parsed.RootElement.GetProperty("Items").GetProperty("PackageReference").EnumerateArray());
            Assert.Equal("9.4.2", package.GetProperty("Version").GetString());
            Assert.NotNull(options.NuGetRestoreTargetsFile);
            Assert.Equal([newSource], NuGetTestHelper.GetEligiblePackageSources(options.NuGetRestoreTargetsFile.DirectoryName!, "Aspire.Hosting.Redis"));
            Assert.Equal(originalConfig, File.ReadAllBytes(configFile.FullName));
            Assert.Equal(originalAspireConfig, File.ReadAllText(aspireConfigPath));
            calls.Add("restore");
            if (outcome == "cancel")
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            if (outcome == "stale-config")
            {
                File.WriteAllText(configFile.FullName, "<configuration><external /></configuration>");
            }
            if (outcome == "stale-project")
            {
                File.WriteAllText(projectFile.FullName, "// External edit.");
            }
            return outcome == "restore" ? 1 : 0;
        };
        using var provider = CreateServices(workspace, runner, client).BuildServiceProvider();
        var channel = PackageChannel.CreateExplicitChannel(
            "staging", PackageChannelQuality.Both, [new("Aspire*", newSource)],
            provider.GetRequiredService<INuGetPackageCache>(), new TestFeatures(), NullLogger.Instance);
        var context = new UpdatePackagesContext
        {
            AppHostFile = projectFile,
            Channel = channel,
            HasExplicitChannel = true,
            ConfirmBinding = PromptBinding.CreateDefault(true),
            NuGetConfigDirBinding = PromptBinding.CreateDefault<string?>(null),
            AdditionalUpdateSteps = [new PackageUpdateStep("Tool update", () =>
            {
                Assert.Equal("restore", calls[^1]);
                Assert.Equal([newSource], NuGetTestHelper.GetEligiblePackageSources(projectFile.DirectoryName!, "Aspire.Hosting.Redis"));
                calls.Add("tool");
                return outcome == "commit"
                    ? Task.FromException(new InvalidOperationException("Tool edit failed."))
                    : Task.CompletedTask;
            }, "Aspire.Cli", "9.4.1", "9.4.2", projectFile)]
        };
        var updater = provider.GetRequiredService<IProjectUpdater>();
        if (outcome == "success")
        {
            var result = await updater.UpdateProjectAsync(context, cancellation.Token);
            Assert.True(result.UpdatedApplied);
            AssertUpdatedSdk(provider, projectFile);
            Assert.Equal([newSource], NuGetTestHelper.GetEligiblePackageSources(projectFile.DirectoryName!, "Aspire.Hosting.Redis"));
            Assert.Equal(["discover:Aspire.AppHost.Sdk", "discover:Aspire.Hosting.Redis", "bootstrap", "package", "restore", "tool"], calls);
        }
        else
        {
            if (outcome == "cancel")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updater.UpdateProjectAsync(context, cancellation.Token));
            }
            else if (outcome is "restore" or "edit")
            {
                await Assert.ThrowsAsync<ProjectUpdaterException>(() => updater.UpdateProjectAsync(context, cancellation.Token));
            }
            else if (outcome == "stale-project")
            {
                var failure = await Assert.ThrowsAsync<AggregateException>(() => updater.UpdateProjectAsync(context, cancellation.Token));
                Assert.Equal(2, failure.InnerExceptions.Count);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => updater.UpdateProjectAsync(context, cancellation.Token));
            }
            Assert.Equal(outcome == "stale-project" ? System.Text.Encoding.UTF8.GetBytes("// External edit.") : originalProject,
                await File.ReadAllBytesAsync(projectFile.FullName));
            Assert.Equal(outcome == "stale-config" ? System.Text.Encoding.UTF8.GetBytes("<configuration><external /></configuration>") : originalConfig,
                await File.ReadAllBytesAsync(configFile.FullName));
            Assert.Equal(originalAspireConfig, await File.ReadAllTextAsync(aspireConfigPath));
        }
        Assert.Empty(Directory.EnumerateFiles(workspace.Path, ".aspire-nuget-candidate-*"));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(workspace.Path, ".aspire"), ".aspire-nuget-config-*"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateAsync_CreatesMissingConfigurationOnlyAfterSuccessfulCandidateRestore(bool succeeds)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var project = await CreateAppHostAsync(workspace, "AppHost.csproj");
        var original = await File.ReadAllBytesAsync(project.FullName);
        var configPath = Path.Combine(workspace.Path, "nuget.config");
        var runner = CreateRunner(project, hasExplicitChannel: false);
        runner.SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _, _) =>
            (0, [new NuGetPackageCli { Id = query, Version = "9.4.2", Source = "https://new.example/v3/index.json" }]);
        runner.GetNuGetConfigPathsAsyncCallback = (_, _, _) =>
            (0, [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "NuGet.Config")]);
        runner.AddPackageAsyncCallback = (file, _, version, _, noRestore, _, _) =>
        {
            Assert.True(noRestore);
            File.WriteAllText(file.FullName, File.ReadAllText(file.FullName).Replace("9.4.1", version, StringComparison.Ordinal));
            return 0;
        };
        var restored = false;
        runner.RestoreAsyncCallback = (_, options, _) =>
        {
            Assert.False(File.Exists(configPath));
            Assert.NotNull(options.NuGetRestoreTargetsFile);
            Assert.Contains("9.4.2", File.ReadAllText(project.FullName));
            restored = true;
            return succeeds ? 0 : 1;
        };
        using var provider = CreateServices(workspace, runner, new FakeNuGetClient
        {
            GetSettingsCallback = NuGetTestHelper.CreateClient().GetSettings
        }).BuildServiceProvider();
        var channel = PackageChannel.CreateExplicitChannel(
            "daily", PackageChannelQuality.Both, [new("Aspire*", "https://new.example/v3/index.json")],
            provider.GetRequiredService<INuGetPackageCache>(), new TestFeatures(), NullLogger.Instance);
        var context = CreateContext(project, channel, hasExplicitChannel: true);
        var updater = provider.GetRequiredService<IProjectUpdater>();
        if (succeeds)
        {
            var result = await updater.UpdateProjectAsync(context, TestContext.Current.CancellationToken);
            Assert.True(result.UpdatedApplied);
            Assert.Equal(["https://new.example/v3/index.json"],
                NuGetTestHelper.GetEligiblePackageSources(project.DirectoryName!, "Aspire.Hosting.Redis"));
            Assert.True(File.Exists(configPath));
        }
        else
        {
            await Assert.ThrowsAsync<ProjectUpdaterException>(() =>
                updater.UpdateProjectAsync(context, TestContext.Current.CancellationToken));
            Assert.Equal(original, await File.ReadAllBytesAsync(project.FullName));
            Assert.False(File.Exists(configPath));
        }
        Assert.True(restored);
        Assert.Empty(Directory.EnumerateFiles(workspace.Path, ".aspire-nuget-candidate-*"));
    }

    [Fact]
    public async Task UpdateAsync_FailedRestoreRollsBackCentralPackageAndSdkMigration()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectFile = await CreateAppHostAsync(workspace, "AppHost.csproj");
        await File.WriteAllTextAsync(projectFile.FullName, """
            <Project Sdk="Microsoft.NET.Sdk">
              <Sdk Name="Aspire.AppHost.Sdk" Version="9.4.1" />
              <ItemGroup>
                <PackageReference Include="Aspire.Hosting.AppHost" />
                <PackageReference Include="Aspire.Hosting.Redis" />
              </ItemGroup>
            </Project>
            """);
        var propsFile = new FileInfo(Path.Combine(workspace.Path, "Directory.Packages.props"));
        await File.WriteAllTextAsync(propsFile.FullName, """
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Aspire.Hosting.AppHost" Version="9.4.1" />
                <PackageVersion Include="Aspire.Hosting.Redis" Version="9.4.1" />
              </ItemGroup>
            </Project>
            """);
        var originalProject = await File.ReadAllBytesAsync(projectFile.FullName);
        var originalProps = await File.ReadAllBytesAsync(propsFile.FullName);
        var runner = CreateRunner(projectFile, hasExplicitChannel: false);
        runner.GetProjectItemsAndPropertiesAsyncCallback = (_, _, _, _, _) => CreatePackageEvaluation();
        runner.RestoreAsyncCallback = (_, _, _) =>
        {
            var props = System.Xml.Linq.XDocument.Load(propsFile.FullName);
            var package = Assert.Single(props.Descendants("PackageVersion"));
            Assert.Equal("Aspire.Hosting.Redis", (string?)package.Attribute("Include"));
            Assert.Equal("9.4.2", (string?)package.Attribute("Version"));
            return 1;
        };
        using var provider = CreateServices(workspace, runner).BuildServiceProvider();
        var context = await CreateContextAsync(provider, projectFile, "default", hasExplicitChannel: false);

        await Assert.ThrowsAsync<ProjectUpdaterException>(() =>
            provider.GetRequiredService<IProjectUpdater>().UpdateProjectAsync(context, TestContext.Current.CancellationToken));

        Assert.Equal(originalProject, await File.ReadAllBytesAsync(projectFile.FullName));
        Assert.Equal(originalProps, await File.ReadAllBytesAsync(propsFile.FullName));
    }

    private static async Task<FileInfo> WriteCurrentPolicyAsync(TemporaryWorkspace workspace, string source, bool configureCache)
    {
        var configFile = new FileInfo(Path.Combine(workspace.Path, "NuGet.Config"));
        var cacheSetting = configureCache
            ? """<config><add key="globalPackagesFolder" value=".nugetpackages" /></config>"""
            : string.Empty;
        await File.WriteAllTextAsync(configFile.FullName, $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="company" value="{{source}}" />
              </packageSources>
              <packageSourceMapping>
                <clear />
                <packageSource key="company"><package pattern="Aspire*" /></packageSource>
              </packageSourceMapping>
              {{cacheSetting}}
            </configuration>
            """);
        return configFile;
    }

    private static TestDotNetCliRunner CreateRunner(FileInfo projectFile, bool hasExplicitChannel) => new()
    {
        SearchPackagesAsyncCallback = (directory, query, _, _, _, _, configFile, _, _, _) =>
        {
            Assert.False(hasExplicitChannel, "A rejected explicit channel request must not discover packages.");
            Assert.Equal(projectFile.DirectoryName, directory.FullName);
            Assert.Null(configFile);
            return (0, [new NuGetPackageCli { Id = query, Version = "9.4.2", Source = PackageSources.NuGetOrg }]);
        },
        AddPackageAsyncCallback = (_, _, _, _, noRestore, _, _) =>
        {
            Assert.True(noRestore);
            return 0;
        },
        GetNuGetConfigPathsAsyncCallback = (_, _, _) =>
            throw new InvalidOperationException("Customized or unknown NuGet policy must not be persisted."),
        RestoreAsyncCallback = (_, options, _) =>
        {
            Assert.Null(options.EnvironmentVariables);
            return 0;
        }
    };

    private IServiceCollection CreateServices(TemporaryWorkspace workspace, TestDotNetCliRunner runner)
        => CreateServices(workspace, runner, new FakeNuGetClient
        {
            GetSettingsCallback = NuGetTestHelper.CreateClient().GetSettings,
            WriteNuGetConfigCallback = (_, _) =>
                throw new InvalidOperationException("Customized or unchanged NuGet policy must not create a preview or durable configuration.")
        });

    private IServiceCollection CreateServices(TemporaryWorkspace workspace, TestDotNetCliRunner runner, FakeNuGetClient client)
        => CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetCliRunnerFactory = _ => runner;
            options.NuGetClientFactory = _ => client;
            options.InteractionServiceFactory = _ => new TestInteractionService();
        });

    private static (int, JsonDocument) CreatePackageEvaluation()
    {
        var properties = new JsonObject();
        properties.WithSdkVersion("9.4.1");
        properties.WithPackageReference("Aspire.Hosting.Redis", "9.4.1");
        return (0, JsonDocument.Parse(properties.ToJsonString()));
    }

    private static async Task<UpdatePackagesContext> CreateContextAsync(
        IServiceProvider provider, FileInfo projectFile, string channelName, bool hasExplicitChannel)
    {
        var channels = await provider.GetRequiredService<IPackagingService>()
            .GetChannelsAsync(TestContext.Current.CancellationToken);
        return CreateContext(projectFile, channels.Single(channel => channel.Name == channelName), hasExplicitChannel);
    }

    private static UpdatePackagesContext CreateContext(FileInfo projectFile, PackageChannel channel, bool hasExplicitChannel)
        => new()
        {
            AppHostFile = projectFile,
            Channel = channel,
            HasExplicitChannel = hasExplicitChannel,
            ConfirmBinding = PromptBinding.CreateDefault(true),
            NuGetConfigDirBinding = PromptBinding.CreateDefault<string?>(null)
        };

    private static void AssertUpdatedSdk(IServiceProvider provider, FileInfo projectFile)
    {
        using var project = provider.GetRequiredService<FallbackProjectParser>().ParseProject(projectFile);
        Assert.Equal("9.4.2", project.RootElement.GetProperty("Properties").GetProperty("AspireHostingSDKVersion").GetString());
    }
}
