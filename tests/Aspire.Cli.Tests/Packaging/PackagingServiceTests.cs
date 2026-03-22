// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Aspire.Cli.Configuration;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using System.Xml.Linq;

namespace Aspire.Cli.Tests.Packaging;

public class PackagingServiceTests(ITestOutputHelper outputHelper)
{

    private sealed class FakeNuGetPackageCache : INuGetPackageCache
    {
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetTemplatePackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken) => Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetIntegrationPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken) => Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetCliPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken) => Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetPackagesAsync(DirectoryInfo workingDirectory, string packageId, Func<string, bool>? filter, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken) => Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
    }

    private sealed class TestFeatures : IFeatures
    {
        private readonly Dictionary<string, bool> _features = new();

        public bool IsFeatureEnabled(string featureFlag, bool defaultValue)
        {
            return _features.TryGetValue(featureFlag, out var value) ? value : defaultValue;
        }

        public void SetFeature(string featureFlag, bool enabled)
        {
            _features[featureFlag] = enabled;
        }
    }

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
        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);

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

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);

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

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);

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
        
        var azureDevOpsFeedUrl = "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-dotnet-aspire-abcd1234/nuget/v3/index.json";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingFeed"] = azureDevOpsFeedUrl
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);

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

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);

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

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);

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

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);

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

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);

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

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);

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
                ["overrideStagingFeed"] = "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-dotnet-aspire-48a11dae/nuget/v3/index.json"
            })
            .Build();

        var packagingService = new PackagingService(
            new CliExecutionContext(tempDir, tempDir, tempDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log"), 
            new FakeNuGetPackageCache(), 
            features, 
            configuration);

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
        Assert.Equal(".nugetpackages", (string?)globalPackagesFolderAdd.Attribute("value"));
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

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);

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

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);

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
                ["overrideStagingQuality"] = "Prerelease"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);

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
                ["overrideStagingQuality"] = "Both"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);

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

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);

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
                ["overrideStagingQuality"] = "Prerelease"
            })
            .Build();

        var packagingService = new PackagingService(
            new CliExecutionContext(tempDir, tempDir, tempDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log"), 
            new FakeNuGetPackageCache(), 
            features, 
            configuration);

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
                ["stagingPinToCliVersion"] = "true"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);

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
                ["overrideStagingQuality"] = "Prerelease"
                // No stagingPinToCliVersion
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);

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

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert
        var stagingChannel = channels.First(c => c.Name == "staging");
        // With explicit feed override, useSharedFeed is false, so pinning is not activated
        Assert.Null(stagingChannel.PinnedVersion);
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
                ["stagingPinToCliVersion"] = "true"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, fakeCache, features, configuration);

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
                ["stagingPinToCliVersion"] = "true"
            })
            .Build();

        var packagingService = new PackagingService(executionContext, fakeCache, features, configuration);

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
                ["overrideStagingQuality"] = "Prerelease"
                // No stagingPinToCliVersion — should return all prerelease
            })
            .Build();

        var packagingService = new PackagingService(executionContext, fakeCache, features, configuration);

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
    }
}
