// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")));
        
        var features = new TestFeatures();
        var configuration = new ConfigurationBuilder().Build();
        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);

        // Act
        var channels = await packagingService.GetChannelsAsync();

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
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")));
        
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
        var channels = await packagingService.GetChannelsAsync();

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
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")));
        
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
        var channels = await packagingService.GetChannelsAsync();

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
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")));
        
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
        var channels = await packagingService.GetChannelsAsync();

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
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")));
        
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
        var channels = await packagingService.GetChannelsAsync();

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
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")));
        
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
        var channels = await packagingService.GetChannelsAsync();

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
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")));
        
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
        var channels = await packagingService.GetChannelsAsync();

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
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")));
        
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
        var channels = await packagingService.GetChannelsAsync();

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
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")));
        
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
        var channels = await packagingService.GetChannelsAsync();

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
            new CliExecutionContext(tempDir, tempDir, tempDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes"))), 
            new FakeNuGetPackageCache(), 
            features, 
            configuration);

        var channels = await packagingService.GetChannelsAsync();
        var stagingChannel = channels.First(c => c.Name == "staging");

        // Act
        await NuGetConfigMerger.CreateOrUpdateAsync(tempDir, stagingChannel);

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
        
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")));
        
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
        var channels = await packagingService.GetChannelsAsync();

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
        
        var executionContext = new CliExecutionContext(tempDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")));
        
        var features = new TestFeatures();
        // Staging disabled by default
        var configuration = new ConfigurationBuilder().Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);

        // Act
        var channels = await packagingService.GetChannelsAsync();

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
}
