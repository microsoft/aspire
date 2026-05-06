// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Xml.Linq;

namespace Aspire.Cli.Tests.Packaging;

public class PackagingServiceTests(ITestOutputHelper outputHelper)
{

    [Fact]
    public async Task GetChannelsAsync_WhenStagingChannelDisabled_DoesNotIncludeStagingChannel()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        
        var features = new TestFeatures();
        var configuration = new ConfigurationBuilder().Build();
        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert
        var channelNames = channels.Select(c => c.Name).ToList();
        Assert.DoesNotContain("staging", channelNames);
        Assert.Contains("default", channelNames);
        Assert.Contains("stable", channelNames);
        Assert.Contains("daily", channelNames);

        // Verify that non-staging channels have ConfigureGlobalPackagesFolder = false
        var defaultChannel = channels.First(c => c.Name == "default");
        Assert.False(defaultChannel.ConfigureGlobalPackagesFolder);
        
        var stableChannel = channels.First(c => c.Name == "stable");
        Assert.False(stableChannel.ConfigureGlobalPackagesFolder);
        
        var dailyChannel = channels.First(c => c.Name == "daily");
        Assert.False(dailyChannel.ConfigureGlobalPackagesFolder);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingChannelEnabled_IncludesStagingChannelWithOverrideFeed()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var testFeedUrl = "https://example.com/nuget/v3/index.json";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingFeed"] = testFeedUrl
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert
        var channelNames = channels.Select(c => c.Name).ToList();
        Assert.Contains("staging", channelNames);
        
        var stagingChannel = channels.First(c => c.Name == "staging");
        Assert.Equal(PackageChannelQuality.Stable, stagingChannel.Quality);
        Assert.True(stagingChannel.ConfigureGlobalPackagesFolder);
        Assert.NotNull(stagingChannel.Mappings);
        
        var aspireMapping = stagingChannel.Mappings!.FirstOrDefault(m => m.PackageFilter == "Aspire*");
        Assert.NotNull(aspireMapping);
        Assert.Equal(testFeedUrl, aspireMapping.Source);
        
        var nugetMapping = stagingChannel.Mappings!.FirstOrDefault(m => m.PackageFilter == "*");
        Assert.NotNull(nugetMapping);
        Assert.Equal("https://api.nuget.org/v3/index.json", nugetMapping.Source);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingChannelEnabledWithOverrideFeed_UsesFullUrl()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var customFeedUrl = "https://custom-feed.example.com/v3/index.json";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingFeed"] = customFeedUrl
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert
        var stagingChannel = channels.First(c => c.Name == "staging");
        var aspireMapping = stagingChannel.Mappings!.FirstOrDefault(m => m.PackageFilter == "Aspire*");
        Assert.NotNull(aspireMapping);
        Assert.Equal(customFeedUrl, aspireMapping.Source);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingChannelEnabledWithAzureDevOpsFeedOverride_UsesFullUrl()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var azureDevOpsFeedUrl = "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-abcd1234/nuget/v3/index.json";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingFeed"] = azureDevOpsFeedUrl
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert
        var stagingChannel = channels.First(c => c.Name == "staging");
        var aspireMapping = stagingChannel.Mappings!.FirstOrDefault(m => m.PackageFilter == "Aspire*");
        Assert.NotNull(aspireMapping);
        Assert.Equal(azureDevOpsFeedUrl, aspireMapping.Source);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingChannelEnabledWithInvalidOverrideFeed_FallsBackToDefault()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var invalidFeedUrl = "not-a-valid-url";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingFeed"] = invalidFeedUrl
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert
        // When invalid URL is provided, staging channel should not be created (falls back to default behavior which returns null)
        var channelNames = channels.Select(c => c.Name).ToList();
        Assert.DoesNotContain("staging", channelNames);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingChannelEnabledWithQualityOverride_UsesSpecifiedQuality()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingFeed"] = "https://example.com/nuget/v3/index.json",
                ["overrideStagingQuality"] = "Prerelease"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert
        var stagingChannel = channels.First(c => c.Name == "staging");
        Assert.Equal(PackageChannelQuality.Prerelease, stagingChannel.Quality);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingChannelEnabledWithQualityBoth_UsesQualityBoth()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingFeed"] = "https://example.com/nuget/v3/index.json",
                ["overrideStagingQuality"] = "Both"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert
        var stagingChannel = channels.First(c => c.Name == "staging");
        Assert.Equal(PackageChannelQuality.Both, stagingChannel.Quality);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingChannelEnabledWithInvalidQuality_DefaultsToStable()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingFeed"] = "https://example.com/nuget/v3/index.json",
                ["overrideStagingQuality"] = "InvalidValue"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert
        var stagingChannel = channels.First(c => c.Name == "staging");
        Assert.Equal(PackageChannelQuality.Stable, stagingChannel.Quality);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingChannelEnabledWithoutQualityOverride_DefaultsToStable()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingFeed"] = "https://example.com/nuget/v3/index.json"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert
        var stagingChannel = channels.First(c => c.Name == "staging");
        Assert.Equal(PackageChannelQuality.Stable, stagingChannel.Quality);
    }

    [Fact]
    public async Task NuGetConfigMerger_WhenChannelRequiresGlobalPackagesFolder_AddsGlobalPackagesFolderConfiguration()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingFeed"] = "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-48a11dae/nuget/v3/index.json"
            })
            .Build();

        var packagingService = new PackagingService(
            new CliExecutionContext(tempDir, tempDir, tempDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log"), 
            new FakeNuGetPackageCache(), 
            features, 
            configuration,
            NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var stagingChannel = channels.First(c => c.Name == "staging");

        // Act
        await NuGetConfigMerger.CreateOrUpdateAsync(tempDir, stagingChannel).DefaultTimeout();

        // Assert
        var nugetConfigPath = Path.Combine(tempDir.FullName, "nuget.config");
        Assert.True(File.Exists(nugetConfigPath));
        
        var configContent = await File.ReadAllTextAsync(nugetConfigPath);
        Assert.Contains("globalPackagesFolder", configContent);
        Assert.Contains(".nugetpackages", configContent);

        // Verify the XML structure
        var doc = XDocument.Load(nugetConfigPath);
        var configSection = doc.Root?.Element("config");
        Assert.NotNull(configSection);
        
        var globalPackagesFolderAdd = configSection.Elements("add")
            .FirstOrDefault(add => string.Equals((string?)add.Attribute("key"), "globalPackagesFolder", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(globalPackagesFolderAdd);
        var actualGlobalPackagesFolder = (string?)globalPackagesFolderAdd.Attribute("value");
        Assert.Equal(".nugetpackages", actualGlobalPackagesFolder);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingChannelEnabled_StagingAppearsAfterStableBeforeDaily()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        
        // Create some PR hives to ensure staging appears before them
        hivesDir.Create();
        Directory.CreateDirectory(Path.Combine(hivesDir.FullName, "pr-10167"));
        Directory.CreateDirectory(Path.Combine(hivesDir.FullName, "pr-11832"));
        
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingFeed"] = "https://example.com/nuget/v3/index.json"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert
        var channelNames = channels.Select(c => c.Name).ToList();
        
        // Verify all expected channels are present
        Assert.Contains("default", channelNames);
        Assert.Contains("stable", channelNames);
        Assert.Contains("staging", channelNames);
        Assert.Contains("daily", channelNames);
        Assert.Contains("pr-10167", channelNames);
        Assert.Contains("pr-11832", channelNames);
        
        // Verify the order: default, stable, staging, daily, pr-*
        var defaultIndex = channelNames.IndexOf("default");
        var stableIndex = channelNames.IndexOf("stable");
        var stagingIndex = channelNames.IndexOf("staging");
        var dailyIndex = channelNames.IndexOf("daily");
        var pr10167Index = channelNames.IndexOf("pr-10167");
        var pr11832Index = channelNames.IndexOf("pr-11832");
        
        Assert.True(defaultIndex < stableIndex, $"default should come before stable (default: {defaultIndex}, stable: {stableIndex})");
        Assert.True(stableIndex < stagingIndex, $"stable should come before staging (stable: {stableIndex}, staging: {stagingIndex})");
        Assert.True(stagingIndex < dailyIndex, $"staging should come before daily (staging: {stagingIndex}, daily: {dailyIndex})");
        Assert.True(dailyIndex < pr10167Index, $"daily should come before pr-10167 (daily: {dailyIndex}, pr-10167: {pr10167Index})");
        Assert.True(dailyIndex < pr11832Index, $"daily should come before pr-11832 (daily: {dailyIndex}, pr-11832: {pr11832Index})");
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingChannelDisabled_OrderIsDefaultStableDailyPr()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        
        // Create some PR hives
        hivesDir.Create();
        Directory.CreateDirectory(Path.Combine(hivesDir.FullName, "pr-12345"));
        
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        
        var features = new TestFeatures();
        // Staging disabled by default
        var configuration = new ConfigurationBuilder().Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert
        var channelNames = channels.Select(c => c.Name).ToList();
        
        // Verify staging is not present
        Assert.DoesNotContain("staging", channelNames);
        
        // Verify the order: default, stable, daily, pr-*
        var defaultIndex = channelNames.IndexOf("default");
        var stableIndex = channelNames.IndexOf("stable");
        var dailyIndex = channelNames.IndexOf("daily");
        var pr12345Index = channelNames.IndexOf("pr-12345");
        
        Assert.True(defaultIndex < stableIndex, "default should come before stable");
        Assert.True(stableIndex < dailyIndex, "stable should come before daily");
        Assert.True(dailyIndex < pr12345Index, "daily should come before pr-12345");
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingQualityPrerelease_AndNoFeedOverride_UsesSharedFeed()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        // Set quality to Prerelease but do NOT set overrideStagingFeed
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingQuality"] = "Prerelease",
                // Pin the CLI version for the daily-CLI guard so this test exercises the
                // shared-feed code path regardless of the actual test-assembly version
                // (which on CI is daily-flavored and would otherwise omit staging).
                ["internal:packaging:cliVersionForTesting"] = "13.4.0"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert
        var stagingChannel = channels.First(c => c.Name == "staging");
        Assert.Equal(PackageChannelQuality.Prerelease, stagingChannel.Quality);
        Assert.False(stagingChannel.ConfigureGlobalPackagesFolder);
        
        var aspireMapping = stagingChannel.Mappings!.FirstOrDefault(m => m.PackageFilter == "Aspire*");
        Assert.NotNull(aspireMapping);
        Assert.Equal("https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json", aspireMapping.Source);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingQualityBoth_AndNoFeedOverride_UsesSharedFeed()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        // Set quality to Both but do NOT set overrideStagingFeed
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingQuality"] = "Both",
                // Pin the CLI version for the daily-CLI guard so this test exercises the
                // shared-feed code path regardless of the actual test-assembly version.
                ["internal:packaging:cliVersionForTesting"] = "13.4.0"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert
        var stagingChannel = channels.First(c => c.Name == "staging");
        Assert.Equal(PackageChannelQuality.Both, stagingChannel.Quality);
        Assert.False(stagingChannel.ConfigureGlobalPackagesFolder);
        
        var aspireMapping = stagingChannel.Mappings!.FirstOrDefault(m => m.PackageFilter == "Aspire*");
        Assert.NotNull(aspireMapping);
        Assert.Equal("https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json", aspireMapping.Source);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingQualityPrerelease_WithFeedOverride_UsesFeedOverride()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        // Set both quality override AND feed override — feed override should win
        var customFeed = "https://custom-feed.example.com/v3/index.json";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingQuality"] = "Prerelease",
                ["overrideStagingFeed"] = customFeed
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert
        var stagingChannel = channels.First(c => c.Name == "staging");
        Assert.Equal(PackageChannelQuality.Prerelease, stagingChannel.Quality);
        // When an explicit feed override is provided, globalPackagesFolder stays enabled
        Assert.True(stagingChannel.ConfigureGlobalPackagesFolder);
        
        var aspireMapping = stagingChannel.Mappings!.FirstOrDefault(m => m.PackageFilter == "Aspire*");
        Assert.NotNull(aspireMapping);
        Assert.Equal(customFeed, aspireMapping.Source);
    }

    [Fact]
    public async Task NuGetConfigMerger_WhenStagingUsesSharedFeed_DoesNotAddGlobalPackagesFolder()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        // Quality=Prerelease with no feed override → shared feed mode
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingQuality"] = "Prerelease",
                // Pin the CLI version for the daily-CLI guard so the staging channel is
                // created on shared feed regardless of the actual test-assembly version.
                ["internal:packaging:cliVersionForTesting"] = "13.4.0"
            })
            .Build();

        var packagingService = new PackagingService(
            new CliExecutionContext(tempDir, tempDir, tempDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log"), 
            new FakeNuGetPackageCache(), 
            features, 
            configuration,
            NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var stagingChannel = channels.First(c => c.Name == "staging");

        // Act
        await NuGetConfigMerger.CreateOrUpdateAsync(tempDir, stagingChannel).DefaultTimeout();

        // Assert
        var nugetConfigPath = Path.Combine(tempDir.FullName, "nuget.config");
        Assert.True(File.Exists(nugetConfigPath));
        
        var configContent = await File.ReadAllTextAsync(nugetConfigPath);
        Assert.DoesNotContain("globalPackagesFolder", configContent);
        Assert.DoesNotContain(".nugetpackages", configContent);
        
        // Verify it still has the shared feed URL
        Assert.Contains("dotnet9", configContent);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingPinToCliVersionSet_ChannelHasPinnedVersion()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingQuality"] = "Prerelease",
                ["stagingPinToCliVersion"] = "true",
                // Pin the CLI version for the daily-CLI guard so this test exercises the
                // pinning code path regardless of the actual test-assembly version.
                ["internal:packaging:cliVersionForTesting"] = "13.4.0"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert
        var stagingChannel = channels.First(c => c.Name == "staging");
        Assert.NotNull(stagingChannel.PinnedVersion);
        // Should not contain build metadata (+hash)
        Assert.DoesNotContain("+", stagingChannel.PinnedVersion);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingPinToCliVersionNotSet_ChannelHasNoPinnedVersion()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingQuality"] = "Prerelease",
                // No stagingPinToCliVersion
                // Pin the CLI version for the daily-CLI guard so this test exercises the
                // shared-feed code path regardless of the actual test-assembly version.
                ["internal:packaging:cliVersionForTesting"] = "13.4.0"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert
        var stagingChannel = channels.First(c => c.Name == "staging");
        Assert.Null(stagingChannel.PinnedVersion);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingPinToCliVersionSetButNotSharedFeed_ChannelHasNoPinnedVersion()
    {
        // Arrange - pin is set but explicit feed override means not using shared feed
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingFeed"] = "https://example.com/nuget/v3/index.json",
                ["stagingPinToCliVersion"] = "true"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert
        var stagingChannel = channels.First(c => c.Name == "staging");
        // With explicit feed override, useSharedFeed is false, so pinning is not activated
        Assert.Null(stagingChannel.PinnedVersion);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenLocalHiveContainsProjectTemplatesPackage_ChannelHasPinnedVersion()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var localPackagesDir = new DirectoryInfo(Path.Combine(hivesDir.FullName, "local", "packages"));
        localPackagesDir.Create();

        const string localVersion = "13.3.0-local.20260413.t002308";
        File.WriteAllText(Path.Combine(localPackagesDir.FullName, $"Aspire.ProjectTemplates.{localVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(localPackagesDir.FullName, $"Aspire.Hosting.{localVersion}.nupkg"), string.Empty);

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), new ConfigurationBuilder().Build(), NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert
        var localChannel = channels.First(c => c.Name == "local");
        Assert.Equal(localVersion, localChannel.PinnedVersion);
    }

    [Fact]
    public async Task LocalHiveChannel_WithPinnedVersion_ReturnsSyntheticTemplatePackage()
    {
        // Arrange - simulate package search returning a mismatched stable version
        var fakeCache = new FakeNuGetPackageCacheWithPackages(
        [
            new() { Id = "Aspire.ProjectTemplates", Version = "13.2.2", Source = "https://api.nuget.org/v3/index.json" },
        ]);

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        var localPackagesDir = new DirectoryInfo(Path.Combine(hivesDir.FullName, "local", "packages"));
        localPackagesDir.Create();

        const string localVersion = "13.3.0-local.20260413.t002308";
        File.WriteAllText(Path.Combine(localPackagesDir.FullName, $"Aspire.ProjectTemplates.{localVersion}.nupkg"), string.Empty);

        var packagingService = new PackagingService(executionContext, fakeCache, new TestFeatures(), new ConfigurationBuilder().Build(), NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var localChannel = channels.First(c => c.Name == "local");
        var templatePackages = await localChannel.GetTemplatePackagesAsync(tempDir, CancellationToken.None).DefaultTimeout();

        // Assert
        var package = Assert.Single(templatePackages);
        Assert.Equal("Aspire.ProjectTemplates", package.Id);
        Assert.Equal(localVersion, package.Version);
        Assert.Equal(localPackagesDir.FullName.Replace('\\', '/'), package.Source);
    }

    /// <summary>
    /// Verifies that when pinned to CLI version, GetTemplatePackagesAsync returns a synthetic result
    /// with the pinned version, bypassing actual NuGet search.
    /// </summary>
    [Fact]
    public async Task StagingChannel_WithPinnedVersion_ReturnsSyntheticTemplatePackage()
    {
        // Arrange - simulate a shared feed that has packages from both 13.2 and 13.3 version lines
        var fakeCache = new FakeNuGetPackageCacheWithPackages(
        [
            new() { Id = "Aspire.ProjectTemplates", Version = "13.3.0-preview.1.26201.1", Source = "dotnet9" },
            new() { Id = "Aspire.ProjectTemplates", Version = "13.3.0-preview.1.26200.5", Source = "dotnet9" },
            new() { Id = "Aspire.ProjectTemplates", Version = "13.2.0-preview.1.26111.6", Source = "dotnet9" },
            new() { Id = "Aspire.ProjectTemplates", Version = "13.2.0-preview.1.26110.3", Source = "dotnet9" },
            new() { Id = "Aspire.ProjectTemplates", Version = "13.1.0", Source = "dotnet9" },
        ]);

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");

        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingQuality"] = "Prerelease",
                ["stagingPinToCliVersion"] = "true",
                // Pin the CLI version for the daily-CLI guard so this test exercises the
                // shared-feed pinning code path regardless of the actual test-assembly version.
                ["internal:packaging:cliVersionForTesting"] = "13.4.0"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, fakeCache, features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var stagingChannel = channels.First(c => c.Name == "staging");
        var templatePackages = await stagingChannel.GetTemplatePackagesAsync(tempDir, CancellationToken.None).DefaultTimeout();

        // Assert - should return exactly one synthetic package with the CLI's pinned version
        var packageList = templatePackages.ToList();
        outputHelper.WriteLine($"Template packages returned: {packageList.Count}");
        foreach (var p in packageList)
        {
            outputHelper.WriteLine($"  {p.Id} {p.Version}");
        }

        Assert.Single(packageList);
        Assert.Equal("Aspire.ProjectTemplates", packageList[0].Id);
        Assert.Equal(stagingChannel.PinnedVersion, packageList[0].Version);
        // Pinned version should not contain build metadata
        Assert.DoesNotContain("+", packageList[0].Version!);
    }

    /// <summary>
    /// Verifies that when pinned to CLI version, GetIntegrationPackagesAsync discovers packages
    /// from the feed but overrides their version to the pinned version.
    /// </summary>
    [Fact]
    public async Task StagingChannel_WithPinnedVersion_OverridesIntegrationPackageVersions()
    {
        // Arrange - integration packages with various versions
        var fakeCache = new FakeNuGetPackageCacheWithPackages(
        [
            new() { Id = "Aspire.Hosting.Redis", Version = "13.3.0-preview.1.26201.1", Source = "dotnet9" },
            new() { Id = "Aspire.Hosting.PostgreSQL", Version = "13.3.0-preview.1.26201.1", Source = "dotnet9" },
        ]);

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");

        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingQuality"] = "Prerelease",
                ["stagingPinToCliVersion"] = "true",
                // Pin the CLI version for the daily-CLI guard so this test exercises the
                // shared-feed pinning code path regardless of the actual test-assembly version.
                ["internal:packaging:cliVersionForTesting"] = "13.4.0"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, fakeCache, features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var stagingChannel = channels.First(c => c.Name == "staging");
        var integrationPackages = await stagingChannel.GetIntegrationPackagesAsync(tempDir, CancellationToken.None).DefaultTimeout();

        // Assert - should discover both packages but with pinned version
        var packageList = integrationPackages.ToList();
        outputHelper.WriteLine($"Integration packages returned: {packageList.Count}");
        foreach (var p in packageList)
        {
            outputHelper.WriteLine($"  {p.Id} {p.Version}");
        }

        Assert.Equal(2, packageList.Count);
        Assert.All(packageList, p => Assert.Equal(stagingChannel.PinnedVersion, p.Version));
        Assert.Contains(packageList, p => p.Id == "Aspire.Hosting.Redis");
        Assert.Contains(packageList, p => p.Id == "Aspire.Hosting.PostgreSQL");
    }

    /// <summary>
    /// Verifies that without pinning, all prerelease packages from the feed are returned as-is.
    /// </summary>
    [Fact]
    public async Task StagingChannel_WithoutPinnedVersion_ReturnsAllPrereleasePackages()
    {
        // Arrange
        var fakeCache = new FakeNuGetPackageCacheWithPackages(
        [
            new() { Id = "Aspire.ProjectTemplates", Version = "13.3.0-preview.1.26201.1", Source = "dotnet9" },
            new() { Id = "Aspire.ProjectTemplates", Version = "13.2.0-preview.1.26111.6", Source = "dotnet9" },
            new() { Id = "Aspire.ProjectTemplates", Version = "13.1.0", Source = "dotnet9" },
        ]);

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");

        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingQuality"] = "Prerelease",
                // No stagingPinToCliVersion — should return all prerelease
                // Pin the CLI version for the daily-CLI guard so this test exercises the
                // shared-feed code path regardless of the actual test-assembly version.
                ["internal:packaging:cliVersionForTesting"] = "13.4.0"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, fakeCache, features, configuration, NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var stagingChannel = channels.First(c => c.Name == "staging");
        var templatePackages = await stagingChannel.GetTemplatePackagesAsync(tempDir, CancellationToken.None).DefaultTimeout();

        // Assert
        var packageList = templatePackages.ToList();
        outputHelper.WriteLine($"Template packages returned: {packageList.Count}");
        foreach (var p in packageList)
        {
            outputHelper.WriteLine($"  {p.Id} {p.Version}");
        }

        // Should return only the prerelease ones (quality filter), but both 13.3 and 13.2
        Assert.Equal(2, packageList.Count);
        Assert.Contains(packageList, p => p.Version!.StartsWith("13.3"));
        Assert.Contains(packageList, p => p.Version!.StartsWith("13.2"));
    }

    // ==========================================================================================
    // Regression tests for issue #16652:
    //   Ensure that on a daily/CI Aspire CLI build, `--channel staging` cannot silently resolve
    //   to daily packages. The `internal:packaging:cliVersionForTesting` configuration key is a
    //   test-only seam used to deterministically simulate stable / blessed-prerelease / daily
    //   CLI builds without depending on the actual built CLI version at test time.
    // ==========================================================================================

    [Theory]
    [InlineData("13.4.0", false)]                          // stable release → not daily
    [InlineData("13.4.0-preview.1", false)]                // blessed preview → not daily
    [InlineData("13.4.0-rc.1", false)]                     // blessed RC → not daily
    [InlineData("13.4.0-dev", false)]                      // local dev build → not daily
    [InlineData("13.4.0-preview.1.26210.1", true)]         // arcade daily build → daily
    [InlineData("13.4.0-preview.1.26210.1+abc1234", true)] // daily with build metadata → daily
    [InlineData("13.4.0-rc.1.26300.5", true)]              // RC-flavored CI build → daily
    [InlineData("not-a-valid-version", true)]              // unparseable → fail-safe (treat as daily)
    [InlineData("", true)]                                 // empty → fail-safe (treat as daily)
    [InlineData(null, true)]                               // null → fail-safe (treat as daily)
    public void IsCliPrereleaseDailyBuild_HeuristicProducesExpectedResult(string? informationalVersion, bool expectedIsDaily)
    {
        var actual = PackagingService.IsCliPrereleaseDailyBuild(informationalVersion);
        Assert.Equal(expectedIsDaily, actual);
    }

    [Fact]
    public async Task GetChannelsAsync_DailyCliRequestsStaging_NoFeedOverride_StagingChannelOmittedAndReasonExplains()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");

        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["internal:packaging:cliVersionForTesting"] = "13.4.0-preview.1.26210.1+abc1234"
            })
            .Build();

        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(loggerProvider);
        });

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, loggerFactory.CreateLogger<PackagingService>());

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        Assert.DoesNotContain(channels, c => c.Name == "staging");

        var reason = packagingService.GetStagingChannelUnavailableReason();
        Assert.NotNull(reason);
        Assert.Contains("staging", reason);
        Assert.Contains("daily", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("13.4.0-preview.1.26210.1", reason);
        Assert.Contains("overrideStagingFeed", reason);

        // Warning was logged so the omission is observable in CLI output.
        Assert.Contains(loggerProvider.LogEntries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("staging") &&
            e.Message.Contains("13.4.0-preview.1.26210.1"));
    }

    [Fact]
    public async Task GetChannelsAsync_DailyCliRequestsStaging_NoFeedOverride_PrereleaseQuality_AlsoOmitted()
    {
        // The Prerelease/Both quality + no-feed-override path used to silently fall back
        // to the shared dotnet9 daily feed and return daily packages — exactly the bug
        // tracked by #16652. On a daily CLI we now block it too.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");

        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["internal:packaging:cliVersionForTesting"] = "13.4.0-preview.1.26210.1",
                ["overrideStagingQuality"] = "Prerelease"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        Assert.DoesNotContain(channels, c => c.Name == "staging");
        Assert.NotNull(packagingService.GetStagingChannelUnavailableReason());
    }

    [Fact]
    public async Task GetChannelsAsync_DailyCliRequestsStaging_WithExplicitFeedOverride_UsesOverrideFeed()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");

        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);

        const string explicitFeedUrl = "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-deadbeef/nuget/v3/index.json";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["internal:packaging:cliVersionForTesting"] = "13.4.0-preview.1.26210.1+abc1234",
                ["overrideStagingFeed"] = explicitFeedUrl
            })
            .Build();

        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(loggerProvider);
        });

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, loggerFactory.CreateLogger<PackagingService>());

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Explicit feed override is honored even on a daily CLI.
        var stagingChannel = Assert.Single(channels, c => c.Name == "staging");
        Assert.Null(packagingService.GetStagingChannelUnavailableReason());

        var aspireMapping = stagingChannel.Mappings!.FirstOrDefault(m => m.PackageFilter == "Aspire*");
        Assert.NotNull(aspireMapping);
        Assert.Equal(explicitFeedUrl, aspireMapping.Source);

        // Resolved feed URL is logged so users can verify channel resolution from CLI output.
        Assert.Contains(loggerProvider.LogEntries, e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains(explicitFeedUrl) &&
            e.Message.Contains("staging"));
    }

    [Fact]
    public async Task GetChannelsAsync_StableCliRequestsStaging_NoFeedOverride_StagingChannelCreatedFromShaUrl()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");

        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // A real stable CLI: no prerelease label.
                ["internal:packaging:cliVersionForTesting"] = "13.4.0+a1b2c3d4e5f6"
            })
            .Build();

        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(loggerProvider);
        });

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, loggerFactory.CreateLogger<PackagingService>());

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Staging channel is created. The exact feed URL on a real stable CLI uses the
        // real assembly commit hash; this test only asserts the channel exists and uses
        // the darc-pub-* naming pattern so we don't tightly couple to assembly metadata.
        var stagingChannel = Assert.Single(channels, c => c.Name == "staging");
        Assert.Null(packagingService.GetStagingChannelUnavailableReason());

        var aspireMapping = stagingChannel.Mappings!.FirstOrDefault(m => m.PackageFilter == "Aspire*");
        Assert.NotNull(aspireMapping);
        Assert.StartsWith("https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-", aspireMapping.Source);

        Assert.Contains(loggerProvider.LogEntries, e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("staging") &&
            e.Message.Contains("darc-pub-microsoft-aspire-"));
    }

    [Fact]
    public async Task GetChannelsAsync_BlessedPrereleaseCliRequestsStaging_NoFeedOverride_StagingChannelCreated()
    {
        // Blessed previews/RCs (e.g. "13.4.0-preview.1", "13.4.0-rc.1") have darc-pub-* feeds
        // because the build pipeline creates them for those builds. They are NOT daily CI
        // builds, so they should still be allowed to synthesize the staging channel.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");

        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["internal:packaging:cliVersionForTesting"] = "13.4.0-rc.1+a1b2c3d4e5f6"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        Assert.Contains(channels, c => c.Name == "staging");
        Assert.Null(packagingService.GetStagingChannelUnavailableReason());
    }

    [Fact]
    public void GetStagingChannelUnavailableReason_StagingFeatureDisabled_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");

        // Daily CLI but staging is not enabled — there's no "unavailability" to report.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["internal:packaging:cliVersionForTesting"] = "13.4.0-preview.1.26210.1+abc1234"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), configuration, NullLogger<PackagingService>.Instance);

        Assert.Null(packagingService.GetStagingChannelUnavailableReason());
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<CapturedLogEntry> LogEntries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, LogEntries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string categoryName, List<CapturedLogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (entries)
                {
                    entries.Add(new CapturedLogEntry(categoryName, logLevel, formatter(state, exception)));
                }
            }
        }
    }

    private sealed record CapturedLogEntry(string Category, LogLevel Level, string Message);

    private sealed class FakeNuGetPackageCacheWithPackages(List<Aspire.Shared.NuGetPackageCli> packages) : INuGetPackageCache
    {
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetTemplatePackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        {
            // Simulate what the real cache does: filter by prerelease flag
            var filtered = prerelease
                ? packages.Where(p => Semver.SemVersion.Parse(p.Version).IsPrerelease)
                : packages.Where(p => !Semver.SemVersion.Parse(p.Version).IsPrerelease);
            return Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(filtered.ToList());
        }

        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetIntegrationPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
            => GetTemplatePackagesAsync(workingDirectory, prerelease, nugetConfigFile, cancellationToken);

        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetCliPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
            => Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);

        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetPackagesAsync(DirectoryInfo workingDirectory, string packageId, Func<string, bool>? filter, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken)
            => GetTemplatePackagesAsync(workingDirectory, prerelease, nugetConfigFile, cancellationToken);

        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetPackageVersionsAsync(DirectoryInfo workingDirectory, string exactPackageId, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken)
            => GetTemplatePackagesAsync(workingDirectory, prerelease, nugetConfigFile, cancellationToken);
    }
}
