// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
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
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);
        
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
    public async Task GetChannelsAsync_WhenIdentityChannelIsStaging_IncludesStagingChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: PackageChannelNames.Staging);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideStagingFeedConfigKey] = "https://example.com/nuget/v3/index.json"
            })
            .Build();
        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), configuration, NullLogger<PackagingService>.Instance, isStableShapedCliVersion: () => false);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var channelNames = channels.Select(c => c.Name).ToList();
        Assert.Contains(PackageChannelNames.Staging, channelNames);
        Assert.Equal(
            channelNames.IndexOf(PackageChannelNames.Stable),
            channelNames.IndexOf(PackageChannelNames.Staging) - 1);
        Assert.Equal(
            channelNames.IndexOf(PackageChannelNames.Daily),
            channelNames.IndexOf(PackageChannelNames.Staging) + 1);

        var stagingChannel = channels.First(c => c.Name == PackageChannelNames.Staging);
        Assert.Equal(PackageChannelQuality.Both, stagingChannel.Quality);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenIdentityChannelIsStagingOnStableShapedCli_DefaultsToStableQuality()
    {
        // Regression test for https://github.com/microsoft/aspire/issues/17527: during release
        // stabilization the staging CLI ships with a stable-shaped version (e.g. "13.4.0"). The
        // shared dotnet9 daily feed only carries prerelease-tagged 13.4.0-preview.* packages,
        // so a stabilizing staging CLI must route Aspire.* to the SHA-derived darc-pub-aspire-<hash>
        // feed instead — which requires defaulting the synthesized staging channel quality to
        // Stable (so useSharedFeed in CreateStagingChannel resolves false). No overrideStagingFeed
        // is set: the injected informational version makes the darc derivation deterministic so the
        // test exercises (and asserts) the real SHA-feed routing rather than an override crutch.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: PackageChannelNames.Staging);

        var packagingService = new PackagingService(
            executionContext,
            new FakeNuGetPackageCache(),
            new TestFeatures(),
            new ConfigurationBuilder().Build(),
            NullLogger<PackagingService>.Instance,
            isStableShapedCliVersion: () => true,
            cliInformationalVersionProvider: () => "13.4.0+abcdef1234567890abcdef1234567890abcdef12");

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var stagingChannel = channels.First(c => c.Name == PackageChannelNames.Staging);
        Assert.Equal(PackageChannelQuality.Stable, stagingChannel.Quality);

        var aspireMapping = Assert.Single(stagingChannel.Mappings!, m => m.PackageFilter == "Aspire*");
        Assert.Equal(
            "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-abcdef12/nuget/v3/index.json",
            aspireMapping.Source);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenIdentityChannelIsStagingPrereleaseShaped_RoutesAspirePackagesToDarcFeed()
    {
        // Reproduces the C# vs polyglot divergence: a staging-identity CLI with a prerelease-shaped
        // version (e.g. "13.4.0-preview.1.26280.6") is still an officially published release-branch
        // build, so Aspire.* must resolve from its own SHA-specific darc-pub-microsoft-aspire-<commit>
        // feed — NOT the shared dnceng/dotnet9 daily feed (which only carries main-branch daily
        // packages). Before the fix, useSharedFeed was derived from the version shape (Both quality ->
        // shared daily feed), which is what broke `aspire add` for TypeScript apphosts while C#
        // apphosts (with the darc feed baked into nuget.config) resolved correctly.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: PackageChannelNames.Staging);

        // No overrideStagingFeed configured, so the real darc-vs-shared-daily routing is exercised.
        var packagingService = new PackagingService(
            executionContext,
            new FakeNuGetPackageCache(),
            new TestFeatures(),
            new ConfigurationBuilder().Build(),
            NullLogger<PackagingService>.Instance,
            isStableShapedCliVersion: () => false,
            cliInformationalVersionProvider: () => "13.4.0-preview.1.26280.6+abcdef1234567890abcdef1234567890abcdef12");

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var stagingChannel = channels.First(c => c.Name == PackageChannelNames.Staging);
        Assert.Equal(PackageChannelQuality.Both, stagingChannel.Quality);

        var aspireMapping = Assert.Single(stagingChannel.Mappings!, m => m.PackageFilter == "Aspire*");
        Assert.Equal(
            "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-abcdef12/nuget/v3/index.json",
            aspireMapping.Source);
        Assert.DoesNotContain("dotnet9", aspireMapping.Source);

        // The darc feed needs an isolated global packages folder, and it carries exactly the build's
        // matching packages, so no CLI-version pin is applied.
        Assert.True(stagingChannel.ConfigureGlobalPackagesFolder);
        Assert.Null(stagingChannel.PinnedVersion);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenIdentityChannelIsStagingStableShaped_RoutesAspirePackagesToDarcFeed()
    {
        // Regression guard for https://github.com/microsoft/aspire/issues/17527: a stable-shaped
        // staging CLI ("13.4.0") must resolve Aspire.* from its SHA-specific darc feed with Stable
        // quality (version filtering). The fix keeps this behavior while also covering the
        // prerelease-shaped case above.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: PackageChannelNames.Staging);

        var packagingService = new PackagingService(
            executionContext,
            new FakeNuGetPackageCache(),
            new TestFeatures(),
            new ConfigurationBuilder().Build(),
            NullLogger<PackagingService>.Instance,
            isStableShapedCliVersion: () => true,
            cliInformationalVersionProvider: () => "13.4.0+abcdef1234567890abcdef1234567890abcdef12");

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var stagingChannel = channels.First(c => c.Name == PackageChannelNames.Staging);
        Assert.Equal(PackageChannelQuality.Stable, stagingChannel.Quality);

        var aspireMapping = Assert.Single(stagingChannel.Mappings!, m => m.PackageFilter == "Aspire*");
        Assert.Equal(
            "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-abcdef12/nuget/v3/index.json",
            aspireMapping.Source);

        // Same darc-feed invariants as the prerelease-shaped case: isolated global packages folder
        // and no CLI-version pin (the SHA feed already carries exactly the build's packages).
        Assert.True(stagingChannel.ConfigureGlobalPackagesFolder);
        Assert.Null(stagingChannel.PinnedVersion);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenIdentityChannelIsStagingWithOverrideFeed_UsesOverrideFeed()
    {
        // An explicit overrideStagingFeed always wins over identity-based darc derivation.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: PackageChannelNames.Staging);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideStagingFeedConfigKey] = "https://example.com/nuget/v3/index.json"
            })
            .Build();
        var packagingService = new PackagingService(
            executionContext,
            new FakeNuGetPackageCache(),
            new TestFeatures(),
            configuration,
            NullLogger<PackagingService>.Instance,
            isStableShapedCliVersion: () => false,
            cliInformationalVersionProvider: () => "13.4.0-preview.1.26280.6+abcdef1234567890abcdef1234567890abcdef12");

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var stagingChannel = channels.First(c => c.Name == PackageChannelNames.Staging);
        var aspireMapping = Assert.Single(stagingChannel.Mappings!, m => m.PackageFilter == "Aspire*");
        Assert.Equal("https://example.com/nuget/v3/index.json", aspireMapping.Source);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingIdentityCannotDeriveFeedUrl_OmitsChannelAndWarns()
    {
        // A staging-identity CLI whose informational version carries no '+<commit>' build metadata
        // (e.g. an unstamped local/dev build) cannot derive its SHA-specific darc feed, and there is
        // no override feed. Synthesis was permitted by the identity gate, so the only safe outcome is
        // to omit the staging channel and surface a warning — silently routing to the shared daily
        // feed would resolve the wrong (main-branch) packages, which is the bug this PR fixes.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: PackageChannelNames.Staging);

        var logger = new CapturingLogger<PackagingService>();
        var packagingService = new PackagingService(
            executionContext,
            new FakeNuGetPackageCache(),
            new TestFeatures(),
            new ConfigurationBuilder().Build(),
            logger,
            isStableShapedCliVersion: () => false,
            cliInformationalVersionProvider: () => "13.4.0-preview.1.26280.6"); // no '+<commit>' build metadata

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        Assert.DoesNotContain(PackageChannelNames.Staging, channels.Select(c => c.Name));
        // Synthesis was allowed, so the unavailable-reason API has nothing to report — the warning
        // is the only diagnostic for this edge case.
        Assert.Null(packagingService.GetStagingChannelUnavailableReason());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("staging feed URL"));
    }

    public enum ExpectedStagingFeed
    {
        Absent,
        Darc,
        Shared,
        Override,
    }

    // Locks the full ShouldUseSharedStagingFeed decision table in one place: feed PROVENANCE is
    // identity-driven (staging identity and the Stable-quality feature-flag path -> SHA-specific
    // darc feed), while a non-staging identity that opts into staging with Both quality keeps the
    // shared dotnet9 daily feed, an explicit override always wins, and an identity with no staging
    // opt-in synthesizes no channel at all.
    [Theory]
    [InlineData(PackageChannelNames.Staging, false, false, false, null, ExpectedStagingFeed.Darc)]        // staging identity, prerelease-shaped
    [InlineData(PackageChannelNames.Staging, true, false, false, null, ExpectedStagingFeed.Darc)]         // staging identity, stable-shaped
    [InlineData(PackageChannelNames.Staging, false, false, false, "https://example.com/o/v3/index.json", ExpectedStagingFeed.Override)] // override always wins
    [InlineData(PackageChannelNames.Stable, false, false, true, null, ExpectedStagingFeed.Shared)]        // stable identity + config channel=staging => Both => shared
    [InlineData(PackageChannelNames.Stable, false, true, false, null, ExpectedStagingFeed.Darc)]          // stable identity + feature flag only => Stable => darc
    [InlineData(PackageChannelNames.Daily, false, true, false, null, ExpectedStagingFeed.Darc)]           // daily identity + feature flag only => Stable => darc
    [InlineData(PackageChannelNames.Local, false, false, false, null, ExpectedStagingFeed.Absent)]        // local identity, no opt-in => no channel
    public async Task GetChannelsAsync_StagingFeedRoutingDecisionTable(
        string identityChannel,
        bool isStableShaped,
        bool featureEnabled,
        bool configChannelStaging,
        string? overrideFeed,
        ExpectedStagingFeed expected)
    {
        const string DarcUrl = "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-abcdef12/nuget/v3/index.json";
        const string SharedUrl = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json";

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: identityChannel);

        var settings = new Dictionary<string, string?>();
        if (configChannelStaging)
        {
            settings["channel"] = PackageChannelNames.Staging;
        }
        if (overrideFeed is not null)
        {
            settings[PackagingService.OverrideStagingFeedConfigKey] = overrideFeed;
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var features = new TestFeatures();
        if (featureEnabled)
        {
            features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        }

        var packagingService = new PackagingService(
            executionContext,
            new FakeNuGetPackageCache(),
            features,
            configuration,
            NullLogger<PackagingService>.Instance,
            isStableShapedCliVersion: () => isStableShaped,
            cliInformationalVersionProvider: () => "13.4.0+abcdef1234567890abcdef1234567890abcdef12");

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var stagingChannel = channels.SingleOrDefault(c => c.Name == PackageChannelNames.Staging);

        if (expected == ExpectedStagingFeed.Absent)
        {
            Assert.Null(stagingChannel);
            return;
        }

        Assert.NotNull(stagingChannel);
        var aspireSource = Assert.Single(stagingChannel.Mappings!, m => m.PackageFilter == "Aspire*").Source;
        var expectedSource = expected switch
        {
            ExpectedStagingFeed.Darc => DarcUrl,
            ExpectedStagingFeed.Shared => SharedUrl,
            ExpectedStagingFeed.Override => overrideFeed,
            _ => throw new InvalidOperationException($"Unexpected expectation: {expected}"),
        };
        Assert.Equal(expectedSource, aspireSource);
    }

    // The following tests exercise the diagnostic override mechanism (overrideCliIdentityChannel +
    // overrideCliInformationalVersion) end-to-end through the REAL config-reading default providers
    // (the seams are intentionally NOT injected), which is exactly the local-validation recipe in
    // docs/cli-staging-validation.md. A locally built CLI bakes a 'local' identity, so without the
    // overrides these scenarios would never synthesize a staging channel at all.

    [Fact]
    public async Task GetChannelsAsync_WhenIdentityOverrideAndVersionOverrideSet_RoutesAspirePackagesToDarcFeed()
    {
        // Full local-validation recipe: a 'local' identity CLI is told (via config overrides) to behave
        // like a prerelease-shaped staging build. Both overrides are required — the identity override
        // makes ShouldUseSharedStagingFeed pick the darc feed, and the version override supplies the
        // '+<commit>' the darc URL is derived from.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: PackageChannelNames.Local);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideCliIdentityChannelConfigKey] = PackageChannelNames.Staging,
                [PackagingService.OverrideCliInformationalVersionConfigKey] = "13.4.0-preview.1.26280.6+abcdef1234567890abcdef1234567890abcdef12",
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), configuration, NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var stagingChannel = Assert.Single(channels, c => c.Name == PackageChannelNames.Staging);
        Assert.Equal(PackageChannelQuality.Both, stagingChannel.Quality);

        var aspireMapping = Assert.Single(stagingChannel.Mappings!, m => m.PackageFilter == "Aspire*");
        Assert.Equal(
            "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-abcdef12/nuget/v3/index.json",
            aspireMapping.Source);
        Assert.DoesNotContain("dotnet9", aspireMapping.Source);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenVersionOverrideIsStableShaped_DefaultsToStableQuality()
    {
        // A stable-shaped (no semver prerelease tag) version override drives the quality predicate to
        // Stable, mirroring how an official stable-shaped staging build is filtered.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: PackageChannelNames.Local);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideCliIdentityChannelConfigKey] = PackageChannelNames.Staging,
                [PackagingService.OverrideCliInformationalVersionConfigKey] = "13.4.0+abcdef1234567890abcdef1234567890abcdef12",
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), configuration, NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var stagingChannel = Assert.Single(channels, c => c.Name == PackageChannelNames.Staging);
        Assert.Equal(PackageChannelQuality.Stable, stagingChannel.Quality);
        Assert.Equal(
            "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-abcdef12/nuget/v3/index.json",
            Assert.Single(stagingChannel.Mappings!, m => m.PackageFilter == "Aspire*").Source);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenIdentityOverrideIsInvalid_FallsBackToRealIdentity()
    {
        // An unrecognized identity override (rejected by IdentityChannelReader.IsValidChannel) is
        // ignored and the real 'local' identity is used, so no staging channel is synthesized despite
        // the version override being present.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: PackageChannelNames.Local);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideCliIdentityChannelConfigKey] = "not-a-real-channel",
                [PackagingService.OverrideCliInformationalVersionConfigKey] = "13.4.0-preview.1.26280.6+abcdef1234567890abcdef1234567890abcdef12",
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), configuration, NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        Assert.DoesNotContain(PackageChannelNames.Staging, channels.Select(c => c.Name));
    }

    [Fact]
    public async Task GetChannelsAsync_WhenOverrideStagingFeedSet_WinsOverVersionOverrideDerivation()
    {
        // overrideStagingFeed is the most powerful escape hatch and must win over the SHA-derived darc
        // URL even when the diagnostic version override is also present.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: PackageChannelNames.Local);

        const string OverrideFeed = "https://example.com/override/v3/index.json";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideCliIdentityChannelConfigKey] = PackageChannelNames.Staging,
                [PackagingService.OverrideCliInformationalVersionConfigKey] = "13.4.0-preview.1.26280.6+abcdef1234567890abcdef1234567890abcdef12",
                [PackagingService.OverrideStagingFeedConfigKey] = OverrideFeed,
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), configuration, NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var stagingChannel = Assert.Single(channels, c => c.Name == PackageChannelNames.Staging);
        Assert.Equal(OverrideFeed, Assert.Single(stagingChannel.Mappings!, m => m.PackageFilter == "Aspire*").Source);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingDiagnosticOverridesActive_EmitsWarning()
    {
        // Any normal CLI invocation that has the diagnostic overrides set must leave a trace in the
        // logs so an overridden identity/feed can't silently resolve Aspire.* packages.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: PackageChannelNames.Local);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideCliIdentityChannelConfigKey] = PackageChannelNames.Staging,
                [PackagingService.OverrideCliInformationalVersionConfigKey] = "13.4.0-preview.1.26280.6+abcdef1234567890abcdef1234567890abcdef12",
            })
            .Build();

        var logger = new CapturingLogger<PackagingService>();
        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), configuration, logger);

        await packagingService.GetChannelsAsync().DefaultTimeout();

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("diagnostic overrides are active"));
    }

    [Fact]
    public async Task GetChannelsAsync_WhenOnlyVersionOverrideSet_WarnsButSynthesizesNoStagingChannel()
    {
        // Only the version override is set, so the identity stays 'local' and no staging channel is
        // synthesized. The warning must still fire — the override is active even though it had no
        // routing effect, and a silent no-op would hide a misconfiguration.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: PackageChannelNames.Local);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideCliInformationalVersionConfigKey] = "13.4.0-preview.1.26280.6+abcdef1234567890abcdef1234567890abcdef12",
            })
            .Build();

        var logger = new CapturingLogger<PackagingService>();
        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), configuration, logger);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        Assert.DoesNotContain(PackageChannelNames.Staging, channels.Select(c => c.Name));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("diagnostic overrides are active"));
    }

    [Theory]
    [InlineData("13.4.0+abcd-ef1234567890", true)]               // hyphen only in build metadata => stable-shaped
    [InlineData("13.4.0-preview.1.26280.6+abcd-ef1234567890", false)] // semver prerelease tag => prerelease-shaped
    public async Task GetChannelsAsync_VersionOverrideStableShapeIgnoresBuildMetadataHyphens(string overrideVersion, bool expectStableQuality)
    {
        // StripBuildMetadata removes the '+<commit>' before the prerelease-tag check, so a commit hash
        // containing '-' must not be misread as a semver prerelease tag.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: PackageChannelNames.Local);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideCliIdentityChannelConfigKey] = PackageChannelNames.Staging,
                [PackagingService.OverrideCliInformationalVersionConfigKey] = overrideVersion,
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), configuration, NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var stagingChannel = Assert.Single(channels, c => c.Name == PackageChannelNames.Staging);
        Assert.Equal(expectStableQuality ? PackageChannelQuality.Stable : PackageChannelQuality.Both, stagingChannel.Quality);
    }

    [Fact]
    public void GetStagingChannelUnavailableReason_WhenIdentityOverrideIsStaging_ReturnsNull()
    {
        // The unavailable-reason check (cached via Lazy) must also honor the identity override, so a
        // local CLI with overrideCliIdentityChannel=staging reports staging as available.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: PackageChannelNames.Local);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideCliIdentityChannelConfigKey] = PackageChannelNames.Staging,
            })
            .Build();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), configuration, NullLogger<PackagingService>.Instance);

        Assert.Null(packagingService.GetStagingChannelUnavailableReason());
    }

    [Fact]
    public async Task GetChannelsAsync_WhenRequestedChannelIsStaging_IncludesStagingChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: PackageChannelNames.Stable);

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), new ConfigurationBuilder().Build(), NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync(requestedChannelName: PackageChannelNames.Staging).DefaultTimeout();

        var stagingChannel = Assert.Single(channels, c => c.Name == PackageChannelNames.Staging);
        Assert.Equal(PackageChannelQuality.Both, stagingChannel.Quality);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenConfigurationChannelIsStagingOnLocalCli_DoesNotIncludeStagingChannel()
    {
        // Regression: https://github.com/microsoft/aspire/issues/16652
        // A local/daily/pr-N CLI must not silently fabricate a 'staging' channel from the shared
        // daily feed when config asks for staging — that resolves daily packages, not staging.
        // The escape hatches (overrideStagingFeed or the staging feature flag) are covered by
        // other tests in this file.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["channel"] = PackageChannelNames.Staging
            })
            .Build();
        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), configuration, NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var channelNames = channels.Select(c => c.Name).ToList();
        Assert.DoesNotContain(PackageChannelNames.Staging, channelNames);

        var reason = packagingService.GetStagingChannelUnavailableReason();
        Assert.NotNull(reason);
        Assert.Contains(PackageChannelNames.Local, reason);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenConfigurationChannelIsStagingOnStableCli_IncludesStagingChannelWithSharedFeed()
    {
        // Counterpart to the local/daily refusal: a stable-identity CLI can synthesize staging
        // because the SHA-specific darc feed for the stable commit exists. With quality=Both
        // (the default for stagingChannelConfigured), useSharedFeed=true so the channel points
        // at the shared dnceng/dotnet9 feed.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: PackageChannelNames.Stable);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["channel"] = PackageChannelNames.Staging
            })
            .Build();
        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), configuration, NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var stagingChannel = channels.First(c => c.Name == PackageChannelNames.Staging);
        Assert.Equal(PackageChannelQuality.Both, stagingChannel.Quality);
        Assert.False(stagingChannel.ConfigureGlobalPackagesFolder);
        Assert.Contains(stagingChannel.Mappings!, m =>
            m.PackageFilter == "Aspire*" &&
            m.Source == "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json");

        Assert.Null(packagingService.GetStagingChannelUnavailableReason());
    }

    [Fact]
    public async Task GetChannelsAsync_WhenChannelStagingRequestedOnDailyCli_DoesNotIncludeStagingChannel()
    {
        // Direct repro of https://github.com/microsoft/aspire/issues/16652.
        // A daily CLI invoked with `aspire update --channel staging` must NOT synthesize a
        // staging channel from either the SHA-specific darc feed (which doesn't exist for daily
        // commits) or the shared daily feed (which contains daily packages, not staging).
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: PackageChannelNames.Daily);

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), new ConfigurationBuilder().Build(), NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync(requestedChannelName: PackageChannelNames.Staging).DefaultTimeout();

        var channelNames = channels.Select(c => c.Name).ToList();
        Assert.DoesNotContain(PackageChannelNames.Staging, channelNames);

        var reason = packagingService.GetStagingChannelUnavailableReason();
        Assert.NotNull(reason);
        Assert.Contains(PackageChannelNames.Daily, reason);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenChannelStagingRequestedOnDailyCliWithOverrideFeed_IncludesStagingChannel()
    {
        // The overrideStagingFeed escape hatch must still work on a daily CLI: when the user has
        // explicitly named the staging feed, we trust them and synthesize the channel.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: PackageChannelNames.Daily);

        var overrideUrl = "https://example.com/staging/v3/index.json";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideStagingFeedConfigKey] = overrideUrl
            })
            .Build();
        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), configuration, NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync(requestedChannelName: PackageChannelNames.Staging).DefaultTimeout();

        var stagingChannel = channels.First(c => c.Name == PackageChannelNames.Staging);
        Assert.Contains(stagingChannel.Mappings!, m => m.PackageFilter == "Aspire*" && m.Source == overrideUrl);
        Assert.Null(packagingService.GetStagingChannelUnavailableReason());
    }

    [Theory]
    [InlineData(PackageChannelNames.Local)]
    [InlineData("pr-12345")]
    public async Task GetChannelsAsync_WhenChannelStagingRequestedOnNonReleaseIdentityWithoutOverride_DoesNotIncludeStagingChannel(string identity)
    {
        // The same gating applies to local and per-PR CLI identities. Per-PR (pr-<N>) builds
        // have a hive label baked in by CI but no staging feed of their own.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: identity);

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), new ConfigurationBuilder().Build(), NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync(requestedChannelName: PackageChannelNames.Staging).DefaultTimeout();

        Assert.DoesNotContain(PackageChannelNames.Staging, channels.Select(c => c.Name));

        var reason = packagingService.GetStagingChannelUnavailableReason();
        Assert.NotNull(reason);
        Assert.Contains(identity, reason);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenChannelStagingRequestedOnDailyCliWithFeatureFlag_IncludesStagingChannel()
    {
        // Back-compat: the StagingChannelEnabled feature flag is an explicit developer/test opt-in
        // and continues to bypass the identity gating. The feature-flag-only path defaults the
        // synthesized channel quality to Stable, so a non-staging identity routes Aspire.* to the
        // SHA-specific darc feed (not the shared daily feed). The informational version is injected
        // so the darc derivation is deterministic — no overrideStagingFeed crutch is needed, which
        // lets the assertions below isolate the feature-flag gate AND the real feed routing.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: PackageChannelNames.Daily);

        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);

        var packagingService = new PackagingService(
            executionContext,
            new FakeNuGetPackageCache(),
            features,
            new ConfigurationBuilder().Build(),
            NullLogger<PackagingService>.Instance,
            cliInformationalVersionProvider: () => "13.4.0+abcdef1234567890abcdef1234567890abcdef12");

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var stagingChannel = Assert.Single(channels, c => c.Name == PackageChannelNames.Staging);
        Assert.Null(packagingService.GetStagingChannelUnavailableReason());

        // Feature-flag-only opt-in => Stable quality => darc feed (the gate alone, with no
        // overrideStagingFeed, must both permit synthesis and route to the SHA feed).
        Assert.Equal(PackageChannelQuality.Stable, stagingChannel.Quality);
        var aspireMapping = Assert.Single(stagingChannel.Mappings!, m => m.PackageFilter == "Aspire*");
        Assert.Equal(
            "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-abcdef12/nuget/v3/index.json",
            aspireMapping.Source);
    }

    /// <summary>
    /// Locks in the structural invariant that <c>aspire init</c> and <c>aspire new</c> depend
    /// on: the <c>stable</c> channel is always <see cref="PackageChannelType.Explicit"/> with a
    /// non-empty <see cref="PackageChannel.Mappings"/> array containing a <see cref="PackageMapping.AllPackages"/>
    /// pattern. <c>TemplateNuGetConfigService.CreateOrUpdateNuGetConfigWithoutPromptAsync</c>
    /// short-circuits if the matching channel is not explicit or has no mappings, so a future
    /// refactor that flipped stable to implicit / removed its mappings would silently turn the
    /// workspace-NuGet.config write into a no-op for every stable-channel CLI user. The
    /// InitCommand-level tests use a fake stable channel and cannot catch this regression at the
    /// real <see cref="PackagingService"/> layer.
    /// </summary>
    [Fact]
    public async Task GetChannelsAsync_StableChannel_IsExplicitWithAllPackagesMappingToNuGetOrg()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);

        var features = new TestFeatures();
        var configuration = new ConfigurationBuilder().Build();
        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration, NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var stableChannel = channels.First(c => c.Name == PackageChannelNames.Stable);
        Assert.Equal(PackageChannelType.Explicit, stableChannel.Type);
        Assert.NotNull(stableChannel.Mappings);
        Assert.NotEmpty(stableChannel.Mappings!);
        Assert.Contains(stableChannel.Mappings!, m =>
            m.PackageFilter == PackageMapping.AllPackages &&
            m.Source == PackageSources.NuGetOrg);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingChannelEnabled_IncludesStagingChannelWithOverrideFeed()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);

        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);

        var testFeedUrl = "https://example.com/nuget/v3/index.json";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideStagingFeedConfigKey] = testFeedUrl
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
        Assert.Equal(PackageSources.NuGetOrg, nugetMapping.Source);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenStagingChannelEnabledWithOverrideFeed_UsesFullUrl()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var customFeedUrl = "https://custom-feed.example.com/v3/index.json";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideStagingFeedConfigKey] = customFeedUrl
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
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var azureDevOpsFeedUrl = "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-abcd1234/nuget/v3/index.json";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideStagingFeedConfigKey] = azureDevOpsFeedUrl
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
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var invalidFeedUrl = "not-a-valid-url";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideStagingFeedConfigKey] = invalidFeedUrl
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
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideStagingFeedConfigKey] = "https://example.com/nuget/v3/index.json",
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
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideStagingFeedConfigKey] = "https://example.com/nuget/v3/index.json",
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
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideStagingFeedConfigKey] = "https://example.com/nuget/v3/index.json",
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
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideStagingFeedConfigKey] = "https://example.com/nuget/v3/index.json"
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
                [PackagingService.OverrideStagingFeedConfigKey] = "https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-48a11dae/nuget/v3/index.json"
            })
            .Build();

        var packagingService = new PackagingService(
            TestExecutionContextHelper.CreateExecutionContext(tempDir),
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
        
        // Create some PR hives to ensure staging appears before them
        hivesDir.Create();
        Directory.CreateDirectory(Path.Combine(hivesDir.FullName, "pr-10167"));
        Directory.CreateDirectory(Path.Combine(hivesDir.FullName, "pr-11832"));
        
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideStagingFeedConfigKey] = "https://example.com/nuget/v3/index.json"
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
        
        // Create some PR hives
        hivesDir.Create();
        Directory.CreateDirectory(Path.Combine(hivesDir.FullName, "pr-12345"));
        
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);
        
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
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        // Set quality to Prerelease but do NOT set overrideStagingFeed
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingQuality"] = "Prerelease"
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
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        // Set quality to Both but do NOT set overrideStagingFeed
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingQuality"] = "Both"
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
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        // Set both quality override AND feed override — feed override should win
        var customFeed = "https://custom-feed.example.com/v3/index.json";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingQuality"] = "Prerelease",
                [PackagingService.OverrideStagingFeedConfigKey] = customFeed
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
                ["overrideStagingQuality"] = "Prerelease"
            })
            .Build();

        var packagingService = new PackagingService(
            TestExecutionContextHelper.CreateExecutionContext(tempDir),
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
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingQuality"] = "Prerelease",
                ["stagingPinToCliVersion"] = "true"
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
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingQuality"] = "Prerelease"
                // No stagingPinToCliVersion
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
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);
        
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);
        
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PackagingService.OverrideStagingFeedConfigKey] = "https://example.com/nuget/v3/index.json",
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
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);
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
    public async Task GetChannelsAsync_WhenPrIdentityRunsFromDogfoodInstallPrefix_AddsMatchingPrHiveChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;

        const string prChannelName = "pr-17225";
        const string prVersion = "13.4.0-pr.17225.g1234567";
        var installPrefix = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "custom-aspire-prefix"));
        var processPath = Path.Combine(installPrefix.FullName, "dogfood", prChannelName, "bin", "aspire");
        Directory.CreateDirectory(Path.GetDirectoryName(processPath)!);
        File.WriteAllText(processPath, string.Empty);

        var packagesDirectory = Directory.CreateDirectory(Path.Combine(installPrefix.FullName, "hives", prChannelName, "packages"));
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.ProjectTemplates.{prVersion}.nupkg"), string.Empty);

        // Deliberately point the execution context at the default Aspire home, not the custom
        // PR install prefix. This is the dogfood acquisition shape that previously made a
        // PR-acquired CLI fall back to normal channels unless the user passed --source.
        var defaultHivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            tempDir,
            hivesDirectory: defaultHivesDir,
            identityChannel: prChannelName);
        var packagingService = new PackagingService(
            executionContext,
            new FakeNuGetPackageCache(),
            new TestFeatures(),
            new ConfigurationBuilder().Build(),
            NullLogger<PackagingService>.Instance,
            processPathProvider: () => processPath);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var prChannel = Assert.Single(channels, c => string.Equals(c.Name, prChannelName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(prVersion, prChannel.PinnedVersion);
        Assert.Contains(prChannel.Mappings!, mapping =>
            mapping.PackageFilter == "Aspire*" &&
            mapping.Source == packagesDirectory.FullName.Replace('\\', '/'));
    }

    [Fact]
    public async Task GetChannelsAsync_WhenPrIdentityExistsInDefaultHiveAndDogfoodInstallPrefix_UsesDogfoodHiveChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;

        const string prChannelName = "pr-17225";
        const string defaultHiveVersion = "13.4.0-pr.17225.g1111111";
        const string dogfoodHiveVersion = "13.4.0-pr.17225.g2222222";

        var defaultHivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var defaultPackagesDirectory = Directory.CreateDirectory(Path.Combine(defaultHivesDir.FullName, prChannelName, "packages"));
        File.WriteAllText(Path.Combine(defaultPackagesDirectory.FullName, $"Aspire.ProjectTemplates.{defaultHiveVersion}.nupkg"), string.Empty);

        var installPrefix = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "custom-aspire-prefix"));
        var processPath = Path.Combine(installPrefix.FullName, "dogfood", prChannelName, "bin", "aspire");
        Directory.CreateDirectory(Path.GetDirectoryName(processPath)!);
        File.WriteAllText(processPath, string.Empty);

        var dogfoodPackagesDirectory = Directory.CreateDirectory(Path.Combine(installPrefix.FullName, "hives", prChannelName, "packages"));
        File.WriteAllText(Path.Combine(dogfoodPackagesDirectory.FullName, $"Aspire.ProjectTemplates.{dogfoodHiveVersion}.nupkg"), string.Empty);

        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            tempDir,
            hivesDirectory: defaultHivesDir,
            identityChannel: prChannelName);
        var packagingService = new PackagingService(
            executionContext,
            new FakeNuGetPackageCache(),
            new TestFeatures(),
            new ConfigurationBuilder().Build(),
            NullLogger<PackagingService>.Instance,
            processPathProvider: () => processPath);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var prChannel = Assert.Single(channels, c => string.Equals(c.Name, prChannelName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(dogfoodHiveVersion, prChannel.PinnedVersion);
        Assert.Contains(prChannel.Mappings!, mapping =>
            mapping.PackageFilter == "Aspire*" &&
            mapping.Source == dogfoodPackagesDirectory.FullName.Replace('\\', '/'));
    }

    [Fact]
    public async Task GetChannelsAsync_WhenPrDogfoodHiveHasOnlyMalformedPackageNames_AddsChannelWithoutPinnedVersion()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;

        const string prChannelName = "pr-17225";
        var installPrefix = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "custom-aspire-prefix"));
        var processPath = Path.Combine(installPrefix.FullName, "dogfood", prChannelName, "bin", "aspire");
        Directory.CreateDirectory(Path.GetDirectoryName(processPath)!);
        File.WriteAllText(processPath, string.Empty);

        var packagesDirectory = Directory.CreateDirectory(Path.Combine(installPrefix.FullName, "hives", prChannelName, "packages"));
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, "Aspire.ProjectTemplates.not-a-semver.nupkg"), string.Empty);

        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            tempDir,
            hivesDirectory: new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives")),
            identityChannel: prChannelName);
        var packagingService = new PackagingService(
            executionContext,
            new FakeNuGetPackageCache(),
            new TestFeatures(),
            new ConfigurationBuilder().Build(),
            NullLogger<PackagingService>.Instance,
            processPathProvider: () => processPath);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var prChannel = Assert.Single(channels, c => string.Equals(c.Name, prChannelName, StringComparison.OrdinalIgnoreCase));
        Assert.Null(prChannel.PinnedVersion);
        Assert.Contains(prChannel.Mappings!, mapping =>
            mapping.PackageFilter == "Aspire*" &&
            mapping.Source == packagesDirectory.FullName.Replace('\\', '/'));
    }

    [Fact]
    public async Task GetChannelsAsync_WhenPrIdentityDogfoodPackagesDirectoryIsMissing_DoesNotAddPrHiveChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;

        const string prChannelName = "pr-17225";
        var installPrefix = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "custom-aspire-prefix"));
        var processPath = Path.Combine(installPrefix.FullName, "dogfood", prChannelName, "bin", "aspire");
        Directory.CreateDirectory(Path.GetDirectoryName(processPath)!);
        File.WriteAllText(processPath, string.Empty);

        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            tempDir,
            hivesDirectory: new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives")),
            identityChannel: prChannelName);
        var packagingService = new PackagingService(
            executionContext,
            new FakeNuGetPackageCache(),
            new TestFeatures(),
            new ConfigurationBuilder().Build(),
            NullLogger<PackagingService>.Instance,
            processPathProvider: () => processPath);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        Assert.DoesNotContain(channels, c => string.Equals(c.Name, prChannelName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetChannelsAsync_WhenPrIdentityDoesNotMatchDogfoodDirectory_DoesNotAddPrHiveChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;

        const string installedPrChannelName = "pr-11111";
        const string identityPrChannelName = "pr-22222";
        var installPrefix = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "custom-aspire-prefix"));
        var processPath = Path.Combine(installPrefix.FullName, "dogfood", installedPrChannelName, "bin", "aspire");
        Directory.CreateDirectory(Path.GetDirectoryName(processPath)!);
        File.WriteAllText(processPath, string.Empty);
        Directory.CreateDirectory(Path.Combine(installPrefix.FullName, "hives", identityPrChannelName, "packages"));

        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            tempDir,
            hivesDirectory: new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives")),
            identityChannel: identityPrChannelName);
        var packagingService = new PackagingService(
            executionContext,
            new FakeNuGetPackageCache(),
            new TestFeatures(),
            new ConfigurationBuilder().Build(),
            NullLogger<PackagingService>.Instance,
            processPathProvider: () => processPath);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        Assert.DoesNotContain(channels, c => string.Equals(c.Name, identityPrChannelName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetChannelsAsync_WhenProcessPathProviderThrows_DoesNotAddPrHiveChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;

        const string prChannelName = "pr-17225";
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            tempDir,
            hivesDirectory: new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives")),
            identityChannel: prChannelName);
        var packagingService = new PackagingService(
            executionContext,
            new FakeNuGetPackageCache(),
            new TestFeatures(),
            new ConfigurationBuilder().Build(),
            NullLogger<PackagingService>.Instance,
            processPathProvider: () => throw new IOException("Process path unavailable."));

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        Assert.DoesNotContain(channels, c => string.Equals(c.Name, prChannelName, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(channels, c => c.Name == PackageChannelNames.Stable);
        Assert.Contains(channels, c => c.Name == PackageChannelNames.Daily);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenNonPrIdentityRunsFromDogfoodInstallPrefix_DoesNotAddPrHiveChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;

        const string prChannelName = "pr-17225";
        var installPrefix = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "custom-aspire-prefix"));
        var processPath = Path.Combine(installPrefix.FullName, "dogfood", prChannelName, "bin", "aspire");
        Directory.CreateDirectory(Path.GetDirectoryName(processPath)!);
        File.WriteAllText(processPath, string.Empty);
        Directory.CreateDirectory(Path.Combine(installPrefix.FullName, "hives", prChannelName, "packages"));

        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            tempDir,
            hivesDirectory: new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives")),
            identityChannel: PackageChannelNames.Daily);
        var packagingService = new PackagingService(
            executionContext,
            new FakeNuGetPackageCache(),
            new TestFeatures(),
            new ConfigurationBuilder().Build(),
            NullLogger<PackagingService>.Instance,
            processPathProvider: () => processPath);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        Assert.DoesNotContain(channels, c => string.Equals(c.Name, prChannelName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryResolvePrInstallPackagesDirectory_WithMalformedProcessPath_ReturnsNull()
    {
        Assert.Null(PackagingService.TryResolvePrInstallPackagesDirectory("bad\0path", "pr-17225"));
    }

    [Fact]
    public void TryResolvePrInstallPackagesDirectory_WithWrongInstallLayout_ReturnsNull()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;

        const string prChannelName = "pr-17225";
        var installPrefix = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "custom-aspire-prefix"));
        var processPath = Path.Combine(installPrefix.FullName, "bin", "aspire");
        Directory.CreateDirectory(Path.GetDirectoryName(processPath)!);
        File.WriteAllText(processPath, string.Empty);
        Directory.CreateDirectory(Path.Combine(installPrefix.FullName, "hives", prChannelName, "packages"));

        Assert.Null(PackagingService.TryResolvePrInstallPackagesDirectory(processPath, prChannelName));
    }

    [Fact]
    public async Task LocalHiveChannel_WithPinnedVersion_ReturnsSyntheticTemplatePackage()
    {
        // Arrange - simulate package search returning a mismatched stable version
        var fakeCache = new FakeNuGetPackageCacheWithPackages(
        [
            new() { Id = "Aspire.ProjectTemplates", Version = "13.2.2", Source = PackageSources.NuGetOrg },
        ]);

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);
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
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);

        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingQuality"] = "Prerelease",
                ["stagingPinToCliVersion"] = "true"
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
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);

        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingQuality"] = "Prerelease",
                ["stagingPinToCliVersion"] = "true"
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
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);

        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.StagingChannelEnabled, true);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["overrideStagingQuality"] = "Prerelease"
                // No stagingPinToCliVersion — should return all prerelease
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

    /// <summary>
    /// Verifies that hive channel names always match their directory name regardless of
    /// the CLI identity channel. PackagingService no longer renames hive directories
    /// in-memory; the script writes the hive as "local" directly.
    /// </summary>
    [Fact]
    public async Task GetChannelsAsync_HiveChannelNameAlwaysMatchesDirectoryName()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));

        // Hive directory named "local" (as written by get-aspire-cli-pr.sh --local-dir)
        const string localHiveName = "local";
        var localPackagesDir = new DirectoryInfo(Path.Combine(hivesDir.FullName, localHiveName, "packages"));
        localPackagesDir.Create();

        const string pinnedVersion = "13.4.0-pr.16820.g1a99aa46";
        File.WriteAllText(Path.Combine(localPackagesDir.FullName, $"Aspire.ProjectTemplates.{pinnedVersion}.nupkg"), string.Empty);

        // CLI binary built with AspireCliChannel=local
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: "local");

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), new ConfigurationBuilder().Build(), NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert: channel name == directory name (no in-memory rename)
        var localChannel = channels.FirstOrDefault(c => c.Name == localHiveName);
        Assert.NotNull(localChannel);
        Assert.Equal(pinnedVersion, localChannel.PinnedVersion);
    }

    /// <summary>
    /// Verifies that when the CLI identity channel is NOT "local" (e.g. "daily"),
    /// hive channels keep their directory-derived names.
    /// </summary>
    [Fact]
    public async Task GetChannelsAsync_WhenIdentityChannelIsNotLocal_HiveKeepsDirectoryName()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));

        const string runHiveName = "run-99999";
        var runPackagesDir = new DirectoryInfo(Path.Combine(hivesDir.FullName, runHiveName, "packages"));
        runPackagesDir.Create();

        const string pinnedVersion = "13.4.0-pr.16820.g1a99aa46";
        File.WriteAllText(Path.Combine(runPackagesDir.FullName, $"Aspire.ProjectTemplates.{pinnedVersion}.nupkg"), string.Empty);

        // CLI binary built with non-local channel ("daily") — explicit so the test stays
        // accurate even as the CliExecutionContext default channel evolves.
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir, identityChannel: "daily");

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), new ConfigurationBuilder().Build(), NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        // Assert: the hive channel keeps its directory name
        var hiveChannel = channels.FirstOrDefault(c => c.Name == runHiveName);
        Assert.NotNull(hiveChannel);
    }

    /// <summary>
    /// Verifies that for a local hive channel with a pinned version, GetIntegrationPackagesAsync
    /// enumerates .nupkg files directly from the local folder and returns Aspire.Hosting.* integration
    /// packages without calling dotnet package search (which does not support local folder sources).
    /// </summary>
    [Fact]
    public async Task LocalHiveChannel_WithPinnedVersion_ReturnsIntegrationPackagesFromNupkgFiles()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);

        var localPackagesDir = new DirectoryInfo(Path.Combine(hivesDir.FullName, "local", "packages"));
        localPackagesDir.Create();

        const string localVersion = "13.4.0-pr.16820.g1a99aa46";
        // Root hosting package that should not appear in integration search
        File.WriteAllText(Path.Combine(localPackagesDir.FullName, $"Aspire.Hosting.{localVersion}.nupkg"), string.Empty);
        // Hosting integration packages that should be returned
        File.WriteAllText(Path.Combine(localPackagesDir.FullName, $"Aspire.Hosting.Redis.{localVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(localPackagesDir.FullName, $"Aspire.Hosting.JavaScript.{localVersion}.nupkg"), string.Empty);
        // Non-hosting packages that should NOT be returned by GetIntegrationPackagesAsync
        File.WriteAllText(Path.Combine(localPackagesDir.FullName, $"Aspire.ProjectTemplates.{localVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(localPackagesDir.FullName, $"Aspire.AppHost.Sdk.{localVersion}.nupkg"), string.Empty);

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), new ConfigurationBuilder().Build(), NullLogger<PackagingService>.Instance);

        // Act
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var localChannel = channels.First(c => c.Name == "local");
        var integrationPackages = await localChannel.GetIntegrationPackagesAsync(tempDir, CancellationToken.None).DefaultTimeout();

        // Assert
        var packageList = integrationPackages.ToList();
        Assert.Equal(2, packageList.Count);
        Assert.All(packageList, p => Assert.Equal(localVersion, p.Version));
        Assert.Contains(packageList, p => p.Id == "Aspire.Hosting.Redis");
        Assert.Contains(packageList, p => p.Id == "Aspire.Hosting.JavaScript");
        // Non-hosting packages must not appear
        Assert.DoesNotContain(packageList, p => p.Id == "Aspire.Hosting");
        Assert.DoesNotContain(packageList, p => p.Id == "Aspire.ProjectTemplates");
        Assert.DoesNotContain(packageList, p => p.Id == "Aspire.AppHost.Sdk");
    }

    [Fact]
    public async Task GetChannelsAsync_LocalHive_AspireMappingPointsAtLocalDirectory_NotPublicFeed()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);

        var localPackagesDir = new DirectoryInfo(Path.Combine(hivesDir.FullName, PackageChannelNames.Local, "packages"));
        localPackagesDir.Create();
        File.WriteAllText(Path.Combine(localPackagesDir.FullName, "Aspire.Hosting.13.4.0-pr.16820.g1a99aa46.nupkg"), string.Empty);

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), new ConfigurationBuilder().Build(), NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var localChannel = channels.First(c => c.Name == PackageChannelNames.Local);

        Assert.NotNull(localChannel.Mappings);
        var aspireMapping = localChannel.Mappings!.FirstOrDefault(m => m.PackageFilter == "Aspire*");
        Assert.NotNull(aspireMapping);
        var expectedLocalPath = localPackagesDir.FullName.Replace('\\', '/');
        Assert.Equal(expectedLocalPath, aspireMapping.Source);
        Assert.False(UrlHelper.IsHttpUrl(aspireMapping.Source), "Local hive Aspire* mapping must be a filesystem path, not an HTTP feed.");

        var fallbackMapping = localChannel.Mappings!.FirstOrDefault(m => m.PackageFilter == PackageMapping.AllPackages);
        Assert.NotNull(fallbackMapping);
        Assert.Equal(PackageSources.NuGetOrg, fallbackMapping.Source);
    }

    [Fact]
    public async Task GetChannelsAsync_LocalHive_EmptyDirectory_ReturnsChannelWithNullPinnedVersion()
    {
        // Partial-state shape: the user has `~/.aspire/hives/local/` on disk but no
        // packages have been deposited yet (e.g., a partially-completed local build, or an
        // install script that created the layout but failed before staging packages).
        // Pinning behavior:
        //  - GetChannelsAsync still produces a `local` channel because GetDirectories() sees the dir.
        //  - GetLocalHivePinnedVersion returns null (no Aspire.ProjectTemplates / Aspire.Hosting /
        //    Aspire.AppHost.Sdk nupkgs to derive a version from), so the channel's PinnedVersion
        //    is null. Downstream, PackageChannel.GetIntegrationPackagesAsync only takes the
        //    direct-enumeration shortcut when PinnedVersion != null, so an empty local hive
        //    falls through to the standard NuGet search path with the local dir as a source.
        // This test pins the channel-construction half of that contract.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);

        // Create the hive root but no `packages/` subdir — directory-exists-but-unpopulated.
        var localHiveDir = new DirectoryInfo(Path.Combine(hivesDir.FullName, PackageChannelNames.Local));
        localHiveDir.Create();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), new ConfigurationBuilder().Build(), NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var localChannel = channels.FirstOrDefault(c => c.Name == PackageChannelNames.Local);
        Assert.NotNull(localChannel);
        Assert.Null(localChannel.PinnedVersion);
        Assert.NotNull(localChannel.Mappings);
        var aspireMapping = localChannel.Mappings!.FirstOrDefault(m => m.PackageFilter == "Aspire*");
        Assert.NotNull(aspireMapping);
        // Mapping still points at the (non-existent) packages dir; PackageChannel guards on
        // Directory.Exists before enumerating, so this is safe at downstream call time.
        var expectedLocalPackagesPath = Path.Combine(localHiveDir.FullName, "packages").Replace('\\', '/');
        Assert.Equal(expectedLocalPackagesPath, aspireMapping.Source);
    }

    [Fact]
    public async Task GetChannelsAsync_LocalHive_EmptyPackagesDirectory_ReturnsChannelWithNullPinnedVersion()
    {
        // Partial-state variant: `~/.aspire/hives/local/packages/` exists but contains zero `*.nupkg` files.
        // GetLocalHivePinnedVersion returns null because no FindHighestVersion lookup matches.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(tempDir, hivesDirectory: hivesDir);

        var localPackagesDir = new DirectoryInfo(Path.Combine(hivesDir.FullName, PackageChannelNames.Local, "packages"));
        localPackagesDir.Create();

        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), new ConfigurationBuilder().Build(), NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var localChannel = channels.FirstOrDefault(c => c.Name == PackageChannelNames.Local);
        Assert.NotNull(localChannel);
        Assert.Null(localChannel.PinnedVersion);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenPackagesOverrideSet_RegistersChannelMappedToOverrideDirectory()
    {
        // ASPIRE_CLI_PACKAGES points the Aspire* feed at a flat .nupkg directory and the service
        // synthesizes a channel named after the running CLI's identity channel that maps Aspire* there
        // and pins to the version discovered in the directory.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));

        var packagesOverrideDir = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "shipping"));
        const string overrideVersion = "13.5.0-preview.1.26310.9";
        File.WriteAllText(Path.Combine(packagesOverrideDir.FullName, $"Aspire.ProjectTemplates.{overrideVersion}.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesOverrideDir.FullName, $"Aspire.Hosting.{overrideVersion}.nupkg"), string.Empty);

        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            tempDir,
            identityChannel: PackageChannelNames.Daily,
            hivesDirectory: hivesDir,
            identityPackagesDirectory: packagesOverrideDir);
        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), new ConfigurationBuilder().Build(), NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var overrideChannel = Assert.Single(channels, c => string.Equals(c.Name, PackageChannelNames.Daily, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(overrideVersion, overrideChannel.PinnedVersion);
        var aspireMapping = Assert.Single(overrideChannel.Mappings!, m => m.PackageFilter == "Aspire*");
        Assert.Equal(packagesOverrideDir.FullName.Replace('\\', '/'), aspireMapping.Source);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenPackagesOverrideSet_ReplacesSameNamedDiscoveredHive()
    {
        // A discovered ~/.aspire/hives/<channel> hive must not mask the explicit override directory:
        // the override is the most specific signal of intent, so it wins and only one channel remains.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(tempDir.FullName, ".aspire", "hives"));

        var staleHiveDir = Directory.CreateDirectory(Path.Combine(hivesDir.FullName, PackageChannelNames.Daily, "packages"));
        File.WriteAllText(Path.Combine(staleHiveDir.FullName, "Aspire.Hosting.13.4.0.nupkg"), string.Empty);

        var packagesOverrideDir = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "shipping"));
        const string overrideVersion = "13.5.0-preview.1.26310.9";
        File.WriteAllText(Path.Combine(packagesOverrideDir.FullName, $"Aspire.Hosting.{overrideVersion}.nupkg"), string.Empty);

        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            tempDir,
            identityChannel: PackageChannelNames.Daily,
            hivesDirectory: hivesDir,
            identityPackagesDirectory: packagesOverrideDir);
        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), new ConfigurationBuilder().Build(), NullLogger<PackagingService>.Instance);

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();

        var overrideChannel = Assert.Single(channels, c => string.Equals(c.Name, PackageChannelNames.Daily, StringComparison.OrdinalIgnoreCase));
        var aspireMapping = Assert.Single(overrideChannel.Mappings!, m => m.PackageFilter == "Aspire*");
        Assert.Equal(packagesOverrideDir.FullName.Replace('\\', '/'), aspireMapping.Source);
        Assert.Equal(overrideVersion, overrideChannel.PinnedVersion);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenPackagesOverrideHasDuplicateAspireVersions_ThrowsFailFast()
    {
        // A flat directory has no latest-stable/latest-prerelease semantics, so two versions of the
        // same Aspire package would let NuGet silently resolve the highest. Fail fast instead.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;

        var packagesOverrideDir = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "shipping"));
        File.WriteAllText(Path.Combine(packagesOverrideDir.FullName, "Aspire.Hosting.13.4.1.nupkg"), string.Empty);
        File.WriteAllText(Path.Combine(packagesOverrideDir.FullName, "Aspire.Hosting.13.4.2.nupkg"), string.Empty);
        // A single-versioned package alongside the duplicate must not be reported as a conflict.
        File.WriteAllText(Path.Combine(packagesOverrideDir.FullName, "Aspire.ProjectTemplates.13.4.2.nupkg"), string.Empty);

        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            tempDir,
            identityChannel: PackageChannelNames.Daily,
            identityPackagesDirectory: packagesOverrideDir);
        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), new ConfigurationBuilder().Build(), NullLogger<PackagingService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => packagingService.GetChannelsAsync()).DefaultTimeout();
        Assert.Contains("Aspire.Hosting", ex.Message);
        Assert.Contains("13.4.1", ex.Message);
        Assert.Contains("13.4.2", ex.Message);
        Assert.DoesNotContain("Aspire.ProjectTemplates", ex.Message);
    }

    [Fact]
    public async Task GetChannelsAsync_WhenPackagesOverrideDirectoryMissing_ThrowsFailFast()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempDir = workspace.WorkspaceRoot;

        var missingDir = new DirectoryInfo(Path.Combine(tempDir.FullName, "does-not-exist"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(
            tempDir,
            identityChannel: PackageChannelNames.Daily,
            identityPackagesDirectory: missingDir);
        var packagingService = new PackagingService(executionContext, new FakeNuGetPackageCache(), new TestFeatures(), new ConfigurationBuilder().Build(), NullLogger<PackagingService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => packagingService.GetChannelsAsync()).DefaultTimeout();
        Assert.Contains(missingDir.FullName, ex.Message);
    }

    [Theory]
    [InlineData("Aspire.Hosting.Azure.Storage.13.4.0-preview.1.25366.3.nupkg", "Aspire.Hosting.Azure.Storage", "13.4.0-preview.1.25366.3")]
    [InlineData("Aspire.Cli.13.4.0.nupkg", "Aspire.Cli", "13.4.0")]
    [InlineData("Aspire.Hosting.13.5.0-dev.nupkg", "Aspire.Hosting", "13.5.0-dev")]
    [InlineData("Aspire.13.4.3.nupkg", "Aspire", "13.4.3")]
    [InlineData("Some.Package.1.0.0.nupkg", "Some.Package", "1.0.0")]
    // Case-insensitive ".nupkg" extension is honored.
    [InlineData("Aspire.Hosting.13.4.0.NUPKG", "Aspire.Hosting", "13.4.0")]
    public void TryParseNupkgFileName_ParsesIdAndVersion(string fileName, string expectedId, string expectedVersion)
    {
        Assert.True(PackagingService.TryParseNupkgFileName(fileName, out var id, out var version));
        Assert.Equal(expectedId, id);
        Assert.Equal(expectedVersion, version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Aspire.Hosting.nupkg")]                  // no version component
    [InlineData("Aspire.Hosting.13.4.0")]                 // missing .nupkg extension
    [InlineData("Aspire.Hosting.13.4.0.zip")]             // wrong extension
    [InlineData("Aspire.Hosting.not.a.version.nupkg")]    // no segment starts with a digit
    [InlineData("13.4.0.nupkg")]                          // version with no preceding id segment
    public void TryParseNupkgFileName_ReturnsFalse_ForInvalidNames(string fileName)
    {
        Assert.False(PackagingService.TryParseNupkgFileName(fileName, out var id, out var version));
        Assert.Equal(string.Empty, id);
        Assert.Equal(string.Empty, version);
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

        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetPackageVersionsAsync(DirectoryInfo workingDirectory, string exactPackageId, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken)
            => GetTemplatePackagesAsync(workingDirectory, prerelease, nugetConfigFile, cancellationToken);
    }
}
