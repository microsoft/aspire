// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.DotNet;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.NuGet;

public class NuGetConfigurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildStable_PreservesProtectedChannelSourcesWithNarrowMappings(bool isEnabled)
    {
        var settings = CreateSettings(
        [
            new("aspire-daily", ["Aspire.Hosting.Private", "Private.*"]),
            new("public", ["*"])
        ]) with
        {
            Sources =
            [
                new("aspire-daily", "integration", isEnabled, true, false) { IsIntegrationSource = true },
                new("public", "public", true, false, false)
            ],
            DisabledPackageSourceKeys = isEnabled ? [] : ["aspire-daily"]
        };
        var channel = PackageChannel.CreateExplicitChannel(
            "stable", PackageChannelQuality.Stable, [new("*", PackageSources.NuGetOrg)],
            new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);

        var configuration = DotNetAppHostNuGetConfigBuilder.BuildStable(settings, "test", channel);

        var overlay = Assert.IsType<NuGetConfigOverlay>(configuration.Overlay);
        Assert.Empty(overlay.RetiredSourceKeys);
        Assert.Equal(settings.DisabledPackageSourceKeys, overlay.DisabledPackageSourceKeys);
        Assert.Equal(["Aspire.Hosting.Private", "Private.*"],
            Assert.Single(overlay.PackageSourceMappings, mapping => mapping.SourceKey == "aspire-daily").Patterns);
        var persistence = Assert.IsType<DotNetAppHostNuGetConfig>(configuration.AppHostConfig);
        Assert.Empty(persistence.SourceChanges.RetiredSourceKeys);
        Assert.Empty(persistence.RemovedPackageSourceMappings);
    }

    [Theory]
    [InlineData("Aspire*")]
    [InlineData("*")]
    public void BuildStable_RequiresManualConfigurationForAmbiguousIntegrationRouting(string pattern)
    {
        var settings = CreateSettings([new("aspire-daily", [pattern])]) with
        {
            ConfigPaths = ["nuget.config"],
            Sources = [new("aspire-daily", "integration", true, false, false) { IsIntegrationSource = true }]
        };
        var channel = PackageChannel.CreateExplicitChannel(
            "stable", PackageChannelQuality.Stable, [new("*", PackageSources.NuGetOrg)],
            new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);

        var exception = Assert.Throws<ProjectUpdaterException>(() =>
            DotNetAppHostNuGetConfigBuilder.BuildStable(settings, "test", channel));

        Assert.Equal(string.Format(CultureInfo.CurrentCulture,
            UpdateCommandStrings.ChannelFeedAlreadyConfiguredFormat, "nuget.config", "aspire-daily"), exception.Message);
    }

    [Theory]
    [InlineData("Aspire*", "Contoso.*", false)]
    [InlineData("*", "Contoso.*", false)]
    [InlineData("Aspire*", "Aspire.Hosting.Private", false)]
    [InlineData("Aspire*", "Contoso.*", true)]
    [InlineData("Aspire*", "Aspire.Hosting.Private", true)]
    public void BuildStable_RequiresManualConfigurationWhenNarrowerRoutingIsInsufficient(
        string broadPattern, string narrowPattern, bool separateAlias)
    {
        var settings = CreateSettings(separateAlias
            ? [new("aspire-daily", [broadPattern]), new("company", [narrowPattern])]
            : [new("aspire-daily", [broadPattern, narrowPattern])]) with
        {
            ConfigPaths = ["nuget.config"],
            Sources =
            [
                new("aspire-daily", "integration", true, false, false) { IsIntegrationSource = true },
                new("company", "integration", true, false, false) { IsIntegrationSource = true }
            ]
        };
        var channel = PackageChannel.CreateExplicitChannel(
            "stable", PackageChannelQuality.Stable, [new("*", PackageSources.NuGetOrg)],
            new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);

        var exception = Assert.Throws<ProjectUpdaterException>(() =>
            DotNetAppHostNuGetConfigBuilder.BuildStable(settings, "test", channel));

        Assert.Equal(string.Format(CultureInfo.CurrentCulture,
            UpdateCommandStrings.ChannelFeedAlreadyConfiguredFormat, "nuget.config", "aspire-daily"), exception.Message);
    }

    [Theory]
    [InlineData("aspire-integration-team")]
    [InlineData("company")]
    public void BuildStable_PreservesExplicitIntegrationAliasMappings(string sourceKey)
    {
        var settings = CreateSettings([new(sourceKey, ["Aspire*", "*"])]) with
        {
            Sources = [new(sourceKey, "integration", true, false, false) { IsIntegrationSource = true }]
        };
        var channel = PackageChannel.CreateExplicitChannel(
            "stable", PackageChannelQuality.Stable, [new("*", PackageSources.NuGetOrg)],
            new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);

        var configuration = DotNetAppHostNuGetConfigBuilder.BuildStable(settings, "test", channel);

        var overlay = Assert.IsType<NuGetConfigOverlay>(configuration.Overlay);
        Assert.Equal(["Aspire*", "*"], Assert.Single(overlay.PackageSourceMappings).Patterns);
        Assert.Empty(overlay.RetiredSourceKeys);
        Assert.Empty(Assert.IsType<DotNetAppHostNuGetConfig>(configuration.AppHostConfig).RemovedPackageSourceMappings);
    }

    [Theory]
    [InlineData("Contoso.Hosting.One", null)]
    [InlineData("Contoso.Hosting.One", "*")]
    [InlineData("Contoso.Hosting.One", "Aspire*")]
    [InlineData("Aspire.Hosting.Custom", null)]
    [InlineData("Aspire.Hosting.Custom", "*")]
    [InlineData("Aspire.Hosting.Custom", "Aspire*")]
    public void Build_ExactIntegrationSelectionPreservesSdkChannelSources(string packageId, string? sdkPattern)
    {
        const string integrationSource = "https://team.example/v3/index.json";
        const string sdkSourceKey = "aspire-pr-21366";
        var settings = CreateSettings(sdkPattern is null
            ? []
            : [new(sdkSourceKey, [sdkPattern]), new("public", ["*"])]) with
        {
            Sources =
            [
                new(sdkSourceKey, "sdk", true, false, false) { IsCliManaged = true },
                new("public", "public", true, false, false)
            ]
        };

        var configuration = DotNetAppHostNuGetConfigBuilder.Build(
            settings, "integration-add", [new(packageId, integrationSource) { SourceKey = "team" }]);

        var overlay = Assert.IsType<NuGetConfigOverlay>(configuration.Overlay);
        Assert.Empty(overlay.RetiredSourceKeys);
        Assert.Empty(overlay.DisabledPackageSourceKeys);
        var sdkMapping = Assert.Single(overlay.PackageSourceMappings, mapping => mapping.SourceKey == sdkSourceKey);
        Assert.Equal([sdkPattern ?? "*"], sdkMapping.Patterns);
        var persistence = Assert.IsType<DotNetAppHostNuGetConfig>(configuration.AppHostConfig);
        Assert.Empty(persistence.SourceChanges.RetiredSourceKeys);
        Assert.Empty(persistence.RemovedPackageSourceMappings);
        Assert.Equal([("aspire-integration-team", integrationSource)], persistence.SourceChanges.Sources);
    }

    [Theory]
    [InlineData("team")]
    [InlineData("aspire-integration-team")]
    public void Build_NamedFeedSwitchRemovesOnlyThePreviousExactIntegrationMapping(string previousKey)
    {
        const string previousSource = "https://team.example/v3/index.json";
        const string selectedSource = "https://other.example/v3/index.json";
        var settings = CreateSettings(
        [
            new(previousKey, ["Contoso.Hosting.One", "Contoso.Hosting.Keep", "Contoso.Internal.*"]),
            new("public", ["*"]),
            new("sdk", ["Aspire*"])
        ]);
        settings = settings with
        {
            Sources =
            [
                new(previousKey, NuGetSourceIdentity.Compute(previousSource, settings.SourceIdentityKey),
                    true, true, false) { IsIntegrationSource = true },
                new("public", "public", true, false, false),
                new("sdk", "sdk", true, false, false)
            ]
        };

        var configuration = DotNetAppHostNuGetConfigBuilder.Build(
            settings, "integration-add", [new("Contoso.Hosting.One", selectedSource) { SourceKey = "other" }]);

        var persistence = Assert.IsType<DotNetAppHostNuGetConfig>(configuration.AppHostConfig);
        var removed = Assert.Single(persistence.RemovedPackageSourceMappings);
        Assert.Equal(previousKey, removed.SourceKey);
        Assert.Equal(["Contoso.Hosting.One"], removed.Patterns);
        var added = Assert.Single(persistence.PackageSourceMappings);
        Assert.Equal("aspire-integration-other", added.SourceKey);
        Assert.Equal(["Contoso.Hosting.One"], added.Patterns);
        Assert.Empty(persistence.SourceChanges.RetiredSourceKeys);
    }

    [Theory]
    [InlineData("Contoso.*", "*", true, false)]
    [InlineData("contoso.*", "*", true, false)]
    [InlineData("Contoso.Hosting.One", "Contoso.*", true, false)]
    [InlineData("*", "Contoso.*", true, true)]
    [InlineData("Contoso.*", "Contoso.Hosting.*", true, true)]
    [InlineData("Contoso.*", "Contoso.Hosting.One", true, true)]
    [InlineData("*", "*", true, true)]
    [InlineData("Contoso.*", "Contoso.*", true, true)]
    [InlineData("Contoso.*", "*", false, true)]
    public void Build_ExistingIntegrationMappingMustUniquelyRouteToSelectedFeed(
        string selectedPattern, string otherPattern, bool selectedSourceEnabled, bool requiresMapping)
    {
        const string selectedSource = "https://selected.example/v3/index.json";
        const string otherSource = "https://other.example/v3/index.json";
        var settings = CreateSettings([new("company-feed", [selectedPattern]), new("other", [otherPattern])]);
        settings = settings with
        {
            Sources =
            [
                new("company-feed", NuGetSourceIdentity.Compute(selectedSource, settings.SourceIdentityKey),
                    selectedSourceEnabled, true, false) { IsIntegrationSource = true },
                new("other", NuGetSourceIdentity.Compute(otherSource, settings.SourceIdentityKey), true, false, false)
            ],
            DisabledPackageSourceKeys = selectedSourceEnabled ? [] : ["company-feed"]
        };

        var configuration = DotNetAppHostNuGetConfigBuilder.Build(
            settings, "test", [new("Contoso.Hosting.One", selectedSource) { SourceKey = "team" }]);

        var persistence = Assert.IsType<DotNetAppHostNuGetConfig>(configuration.AppHostConfig);
        Assert.Equal(requiresMapping ? ["company-feed"] : [],
            persistence.PackageSourceMappings.Select(static mapping => mapping.SourceKey));
        Assert.Empty(persistence.SourceChanges.Sources);
        Assert.Empty(persistence.RemovedPackageSourceMappings);
    }

    [Fact]
    public void Build_ExistingIntegrationMappingCanUseMultipleAliasesForSameFeed()
    {
        const string source = "https://selected.example/v3/index.json";
        var settings = CreateSettings([new("authenticated", ["Contoso.*"]), new("anonymous", ["Contoso.*"])]);
        var identity = NuGetSourceIdentity.Compute(source, settings.SourceIdentityKey);
        settings = settings with
        {
            Sources =
            [
                new("authenticated", identity, true, true, false) { IsIntegrationSource = true },
                new("anonymous", identity, true, false, false) { IsIntegrationSource = true }
            ]
        };

        var configuration = DotNetAppHostNuGetConfigBuilder.Build(
            settings, "test", [new("Contoso.Hosting.One", source) { SourceKey = "team" }]);

        var persistence = Assert.IsType<DotNetAppHostNuGetConfig>(configuration.AppHostConfig);
        Assert.Equal(["authenticated", "anonymous"], persistence.SelectedSourceKeys);
        Assert.Empty(persistence.PackageSourceMappings);
        Assert.Empty(persistence.SourceChanges.Sources);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildStable_PreservesDisabledConfiguredIntegrationSources(bool hasMappings)
    {
        var settings = CreateSettings(hasMappings
            ? [new("aspire-daily", ["Aspire.Hosting.Private"]), new("aspire-staging", ["Aspire*"])]
            : []) with
        {
            Sources =
            [
                new("aspire-daily", "integration", false, false, false) { IsIntegrationSource = true },
                new("aspire-staging", "channel", true, false, false)
            ],
            DisabledPackageSourceKeys = ["aspire-daily"]
        };
        var channel = PackageChannel.CreateExplicitChannel(
            "stable", PackageChannelQuality.Stable, [new("*", PackageSources.NuGetOrg)],
            new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);

        var configuration = DotNetAppHostNuGetConfigBuilder.BuildStable(settings, "test", channel);

        var overlay = Assert.IsType<NuGetConfigOverlay>(configuration.Overlay);
        Assert.Equal(["aspire-staging"], overlay.RetiredSourceKeys);
        Assert.Equal(["aspire-daily", "aspire-staging"], overlay.DisabledPackageSourceKeys);
        var persistence = Assert.IsType<DotNetAppHostNuGetConfig>(configuration.AppHostConfig);
        Assert.Equal(["aspire-staging"], persistence.SourceChanges.RetiredSourceKeys);
        Assert.Empty(persistence.RemovedPackageSourceMappings);
    }

    [Theory]
    [InlineData("Aspire*", false)]
    [InlineData("Aspire.Hosting.Private", true)]
    public void Build_ConfiguredIntegrationMappingsSurviveChannelChangesButAllowExactSelection(string selectedPattern, bool replacesMapping)
    {
        var settings = CreateSettings([new("aspire-daily", ["Aspire.Hosting.Private"])]) with
        {
            Sources =
            [
                new("aspire-daily", "integration", true, false, false) { IsIntegrationSource = true }
            ]
        };

        var configuration = DotNetAppHostNuGetConfigBuilder.Build(
            settings, "test", [new(selectedPattern, "https://selected.example/v3/index.json")]);

        var overlay = Assert.IsType<NuGetConfigOverlay>(configuration.Overlay);
        Assert.Empty(overlay.RetiredSourceKeys);
        Assert.Equal(
            replacesMapping ? ["aspire-test"] : ["aspire-daily", "aspire-test"],
            overlay.PackageSourceMappings.Select(static mapping => mapping.SourceKey));
        var persistence = Assert.IsType<DotNetAppHostNuGetConfig>(configuration.AppHostConfig);
        Assert.Empty(persistence.SourceChanges.RetiredSourceKeys);
        Assert.Equal(replacesMapping ? ["aspire-daily"] : [],
            persistence.RemovedPackageSourceMappings.Select(static mapping => mapping.SourceKey));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildStableAppHostConfiguration_PreservesExistingPublicAliasAndDisabledState(bool isEnabled)
    {
        var settings = CreateSettings(
        [
            new("company", ["*"]),
            new("aspire-test", ["Aspire*"]),
            new("aspire-test-0", ["Aspire*"])
        ]);
        settings = settings with
        {
            Sources =
            [
                new("company", "company", true, false, false),
                new("aspire-test", "daily", true, false, false),
                new("aspire-test-0", NuGetSourceIdentity.Compute(PackageSources.NuGetOrg, settings.SourceIdentityKey), isEnabled, true, false)
            ],
            DisabledPackageSourceKeys = isEnabled ? [] : ["aspire-test-0"]
        };
        var channel = PackageChannel.CreateExplicitChannel(
            "stable", PackageChannelQuality.Stable,
            [new("*", "https://company.example/v3/index.json")],
            new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);

        var configuration = NuGetConfigurationBuilder.BuildStableAppHostConfiguration(settings, "test", channel);

        var overlay = Assert.IsType<NuGetConfigOverlay>(configuration.Overlay);
        Assert.Empty(overlay.Sources);
        Assert.Equal(["aspire-test"], overlay.RetiredSourceKeys);
        Assert.Equal(isEnabled ? ["aspire-test"] : ["aspire-test-0", "aspire-test"], overlay.DisabledPackageSourceKeys);
        Assert.Collection(overlay.PackageSourceMappings,
            mapping =>
            {
                Assert.Equal("company", mapping.SourceKey);
                Assert.Equal(["*"], mapping.Patterns);
            },
            mapping =>
            {
                Assert.Equal("aspire-test-0", mapping.SourceKey);
                Assert.Equal(["Aspire*"], mapping.Patterns);
            });
    }

    [Theory]
    [InlineData("aspire-test", false)]
    [InlineData("legacy-hive", true)]
    [InlineData("ASPIRE-DAILY", false)]
    [InlineData("aspire-staging", false)]
    [InlineData("aspire-pr-19763", false)]
    [InlineData("aspire-apphost-0123456789abcdef", false)]
    [InlineData("aspire-apphost-0123456789abcdef-0", false)]
    public void BuildStableAppHostConfiguration_PreservesUnrelatedMappingsOnChannelSource(string sourceKey, bool isCliManaged)
    {
        var settings = CreateSettings(
        [
            new("company", ["Aspire.Hosting.Redis", "Company.*"]),
            new(sourceKey, ["Aspire*", "Private.*"])
        ]) with
        {
            Sources =
            [
                new("company", "company", true, true, true),
                new(sourceKey, "channel", true, true, false) { IsCliManaged = isCliManaged }
            ]
        };
        var channel = PackageChannel.CreateExplicitChannel(
            "stable", PackageChannelQuality.Stable, [new("*", PackageSources.NuGetOrg)],
            new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);

        var configuration = NuGetConfigurationBuilder.BuildStableAppHostConfiguration(settings, "test", channel);

        var overlay = Assert.IsType<NuGetConfigOverlay>(configuration.Overlay);
        Assert.Empty(overlay.Sources);
        Assert.Empty(overlay.RetiredSourceKeys);
        Assert.False(overlay.ClearDisabledPackageSources);
        Assert.Collection(overlay.PackageSourceMappings,
            mapping =>
            {
                Assert.Equal("company", mapping.SourceKey);
                Assert.Equal(["Aspire.Hosting.Redis", "Company.*"], mapping.Patterns);
            },
            mapping =>
            {
                Assert.Equal(sourceKey, mapping.SourceKey);
                Assert.Equal(["Private.*"], mapping.Patterns);
            });
    }

    [Fact]
    public void BuildStableAppHostConfiguration_DoesNotAddFallbackWhenOnlyChannelSourceExists()
    {
        var settings = CreateSettings([new("aspire-test", ["Aspire*"])]) with
        {
            Sources = [new("aspire-test", "daily", true, false, false)]
        };
        var channel = PackageChannel.CreateExplicitChannel(
            "stable", PackageChannelQuality.Stable, [new("*", PackageSources.NuGetOrg)],
            new FakeNuGetPackageCache(), new TestFeatures(), NullLogger.Instance);

        var configuration = NuGetConfigurationBuilder.BuildStableAppHostConfiguration(settings, "test", channel);

        var overlay = Assert.IsType<NuGetConfigOverlay>(configuration.Overlay);
        Assert.Empty(overlay.Sources);
        Assert.Empty(overlay.PackageSourceMappings);
        Assert.Equal(["aspire-test"], overlay.RetiredSourceKeys);
        Assert.Equal(["aspire-test"], overlay.DisabledPackageSourceKeys);
        Assert.True(overlay.ClearPackageSourceMappings);
    }

    [Fact]
    public void Build_PersistenceLeavesCompetingUserFeedAlone()
    {
        var settings = CreateSettings(
        [
            new("company", ["Aspire.Hosting.Redis"]),
            new("aspire-staging", ["Aspire*"])
        ]) with
        {
            Sources =
            [
                new("company", "company", true, true, true),
                new("aspire-staging", "staging", true, false, false),
                new("disabled", "disabled", false, false, false)
            ],
            DisabledPackageSourceKeys = ["disabled"]
        };

        var configuration = DotNetAppHostNuGetConfigBuilder.Build(
            settings, "test", [new("Aspire*", "https://selected.example/v3/index.json")]);

        var operation = Assert.IsType<NuGetConfigOverlay>(configuration.Overlay);
        Assert.Equal(["company", "aspire-staging"], operation.RetiredSourceKeys);
        var persistence = Assert.IsType<DotNetAppHostNuGetConfig>(configuration.AppHostConfig);
        Assert.Equal(["aspire-staging"], persistence.SourceChanges.RetiredSourceKeys);
        Assert.False(persistence.SourceChanges.ClearDisabledPackageSources);
        Assert.Empty(persistence.SourceChanges.DisabledPackageSourceKeys);
        Assert.Empty(persistence.SourceChanges.PackageSourceMappings);
        Assert.False(persistence.SourceChanges.ClearPackageSourceMappings);
        var mapping = Assert.Single(persistence.PackageSourceMappings);
        Assert.Equal("aspire-test", mapping.SourceKey);
        Assert.Equal(["Aspire*"], mapping.Patterns);
        Assert.Equal(settings.PackageSourceMappings, configuration.Settings.PackageSourceMappings);
        Assert.Equal(settings.DisabledPackageSourceKeys, configuration.Settings.DisabledPackageSourceKeys);
    }

    [Fact]
    public void Build_PersistencePreservesUnrelatedPatternsOnCliFeed()
    {
        var settings = CreateSettings([new("aspire-staging", ["Aspire*", "Private.*"])]) with
        {
            Sources = [new("aspire-staging", "staging", true, false, false)]
        };

        var configuration = DotNetAppHostNuGetConfigBuilder.Build(
            settings, "test", [new("Aspire*", "https://selected.example/v3/index.json")]);

        var persistence = Assert.IsType<DotNetAppHostNuGetConfig>(configuration.AppHostConfig);
        Assert.Empty(persistence.SourceChanges.RetiredSourceKeys);
        Assert.Empty(persistence.SourceChanges.DisabledPackageSourceKeys);
        var removed = Assert.Single(persistence.RemovedPackageSourceMappings);
        Assert.Equal("aspire-staging", removed.SourceKey);
        Assert.Equal(["Aspire*"], removed.Patterns);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Build_PersistenceDoesNotWriteDefaultSourceBindings(bool hasAmbientDefaultSource, bool hasAmbientMappings)
    {
        const string channelSource = "https://channel.example/v3/index.json";
        var settings = CreateSettings(hasAmbientMappings ? [new("company-public", ["*"])] : []);
        if (hasAmbientDefaultSource)
        {
            settings = settings with
            {
                Sources =
                [
                    new("company-public", NuGetSourceIdentity.Compute(PackageSources.NuGetOrg, settings.SourceIdentityKey),
                        true, false, false)
                ]
            };
        }

        var configuration = DotNetAppHostNuGetConfigBuilder.Build(
            settings, "test", [new("Aspire*", channelSource), new("*", PackageSources.NuGetOrg)]);

        var persistence = Assert.IsType<DotNetAppHostNuGetConfig>(configuration.AppHostConfig);
        Assert.Equal([("aspire-test", channelSource)], persistence.SourceChanges.Sources);
        if (hasAmbientMappings)
        {
            var mapping = Assert.Single(persistence.PackageSourceMappings);
            Assert.Equal("aspire-test", mapping.SourceKey);
            Assert.Equal(["Aspire*"], mapping.Patterns);
        }
        else
        {
            Assert.Empty(persistence.PackageSourceMappings);
        }
        Assert.Equal(2, configuration.ConfigSources.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_PersistenceDoesNotReenableDisabledChannelOrFallbackSources(bool channelIsDisabled)
    {
        const string channelSource = "https://selected.example/v3/index.json";
        var settings = CreateSettings([new("company", ["*"])]);
        settings = settings with
        {
            Sources =
            [
                new("company", "company", true, false, false),
                new("aspire-test", NuGetSourceIdentity.Compute(channelSource, settings.SourceIdentityKey),
                    !channelIsDisabled, false, false),
                new("nuget.org", NuGetSourceIdentity.Compute(PackageSources.NuGetOrg, settings.SourceIdentityKey),
                    false, false, false)
            ],
            DisabledPackageSourceKeys = channelIsDisabled ? ["aspire-test", "nuget.org"] : ["nuget.org"]
        };

        var configuration = DotNetAppHostNuGetConfigBuilder.Build(
            settings, "test", [new("Aspire*", channelSource), new("*", PackageSources.NuGetOrg)]);

        var operation = Assert.IsType<NuGetConfigOverlay>(configuration.Overlay);
        Assert.True(operation.ClearDisabledPackageSources);
        Assert.Empty(operation.DisabledPackageSourceKeys);
        var persistence = Assert.IsType<DotNetAppHostNuGetConfig>(configuration.AppHostConfig);
        Assert.False(persistence.SourceChanges.ClearDisabledPackageSources);
        Assert.Empty(persistence.SourceChanges.DisabledPackageSourceKeys);
        Assert.Equal(settings.DisabledPackageSourceKeys, configuration.Settings.DisabledPackageSourceKeys);
    }

    [Fact]
    public void HasSourcePolicyChanges_AmbientPolicyIsUnchanged()
    {
        var configuration = new NuGetConfiguration(CreateSettings([]), [], overlay: null);

        Assert.False(configuration.HasSourcePolicyChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HasSourcePolicyChanges_MappingOrderAndCaseAreIgnored(bool clearMappings)
    {
        var settings = CreateSettings(
        [
            new("company", ["Aspire*", "Company.*"]),
            new("public", ["*"])
        ]);
        var overlay = new NuGetConfigOverlay(
            [], [new("PUBLIC", ["*"]), new("COMPANY", ["company.*", "ASPIRE*"])], false, [], null)
        {
            ClearPackageSourceMappings = clearMappings
        };

        Assert.False(new NuGetConfiguration(settings, [], overlay).HasSourcePolicyChanges);
    }

    [Theory]
    [InlineData("company", "Other.*")]
    [InlineData("other", "Aspire*")]
    public void HasSourcePolicyChanges_ChangedMappingIsDetected(string sourceKey, string pattern)
    {
        var settings = CreateSettings([new("company", ["Aspire*"])]);
        var overlay = new NuGetConfigOverlay([], [new(sourceKey, [pattern])], false, [], null);

        Assert.True(new NuGetConfiguration(settings, [], overlay).HasSourcePolicyChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HasSourcePolicyChanges_EmptyMappingOnlyChangesPolicyWhenCleared(bool clearMappings)
    {
        var settings = CreateSettings([new("company", ["Aspire*"])]);
        var overlay = new NuGetConfigOverlay([], [], false, [], null)
        {
            ClearPackageSourceMappings = clearMappings
        };

        Assert.Equal(clearMappings, new NuGetConfiguration(settings, [], overlay).HasSourcePolicyChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HasSourcePolicyChanges_DisabledSourcesAreComparedByEffectiveState(bool reenableSource)
    {
        var settings = CreateSettings([]) with
        {
            Sources = [new("company", "identity", false, false, false)],
            DisabledPackageSourceKeys = ["company"]
        };
        var overlay = new NuGetConfigOverlay([], [], true, reenableSource ? [] : ["COMPANY"], null);

        Assert.Equal(reenableSource, new NuGetConfiguration(settings, [], overlay).HasSourcePolicyChanges);
    }

    [Fact]
    public void HasSourcePolicyChanges_NewSourceIsDetected()
    {
        var overlay = new NuGetConfigOverlay([("new", "https://feed.example/v3/index.json")], [], false, [], null);

        Assert.True(new NuGetConfiguration(CreateSettings([]), [], overlay).HasSourcePolicyChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HasSourcePolicyChanges_RetiredSourceOnlyChangesPolicyWhenStillEnabled(bool isEnabled)
    {
        var overlay = new NuGetConfigOverlay([], [], false, [], null) { RetiredSourceKeys = ["old"] };
        var settings = CreateSettings([]) with
        {
            Sources = [new("old", "identity", isEnabled, false, false)]
        };

        Assert.Equal(isEnabled, new NuGetConfiguration(settings, [], overlay).HasSourcePolicyChanges);
    }

    [Fact]
    public void HasSourcePolicyChanges_CacheFolderIsNotASourcePolicyChange()
    {
        var overlay = new NuGetConfigOverlay([], [], false, [], ".packages");

        Assert.False(new NuGetConfiguration(CreateSettings([]), [], overlay).HasSourcePolicyChanges);
    }

    private static NuGetSettingsInfo CreateSettings(IReadOnlyList<NuGetPackageSourceMapping> mappings)
        => new([], "ambient", [], [], mappings, [], [], new byte[NuGetSourceIdentity.KeySizeInBytes]);
}
