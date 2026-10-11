// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Configuration;

public class KnownFeaturesTests
{
    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    public void HostingIntegrations_ProjectSettingOverridesCliScope(bool cliEnabled, bool? projectEnabled, bool expected)
    {
        var config = new Aspire.Cli.Configuration.AspireConfigFile();
        if (projectEnabled is { } enabled)
        {
            config.Features = new Dictionary<string, bool>
            {
                [KnownFeatures.ExperimentalHostingIntegrations] = enabled
            };
        }

        var features = new TestFeatures().SetFeature(KnownFeatures.ExperimentalHostingIntegrations, cliEnabled);
        Assert.Equal(expected, KnownFeatures.IsHostingIntegrationsEnabled(features, config));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void ExperimentalHostingIntegrations_DefaultsOffAndHonorsConfiguration(string? enabled, bool expected)
    {
        var metadata = KnownFeatures.GetFeatureMetadata(KnownFeatures.ExperimentalHostingIntegrations);
        Assert.NotNull(metadata);
        Assert.False(metadata.DefaultValue);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["features:experimentalHostingIntegrations"] = enabled
        }).Build();
        var features = new Aspire.Cli.Configuration.Features(configuration, NullLogger<Aspire.Cli.Configuration.Features>.Instance);

        Assert.Equal(expected, features.IsFeatureEnabled(metadata.Name, metadata.DefaultValue));
    }

    [Fact]
    public void IsStagingChannelEnabled_ReturnsTrue_WhenChannelIsStaging()
    {
        var features = new TestFeatures();
        var configuration = BuildConfiguration(channel: PackageChannelNames.Staging);

        Assert.True(KnownFeatures.IsStagingChannelEnabled(features, configuration));
    }

    [Fact]
    public void IsStagingChannelEnabled_ReturnsTrue_WhenFeatureFlagIsTrue()
    {
        var features = new TestFeatures().SetFeature(KnownFeatures.StagingChannelEnabled, true);
        var configuration = BuildConfiguration(channel: PackageChannelNames.Stable);

        Assert.True(KnownFeatures.IsStagingChannelEnabled(features, configuration));
    }

    [Fact]
    public void IsStagingChannelEnabled_ReturnsTrue_WhenBothChannelIsStagingAndFlagIsTrue()
    {
        var features = new TestFeatures().SetFeature(KnownFeatures.StagingChannelEnabled, true);
        var configuration = BuildConfiguration(channel: PackageChannelNames.Staging);

        Assert.True(KnownFeatures.IsStagingChannelEnabled(features, configuration));
    }

    [Fact]
    public void IsStagingChannelEnabled_ReturnsFalse_WhenChannelIsNotStagingAndFlagNotSet()
    {
        var features = new TestFeatures();
        var configuration = BuildConfiguration(channel: PackageChannelNames.Stable);

        Assert.False(KnownFeatures.IsStagingChannelEnabled(features, configuration));
    }

    [Fact]
    public void IsStagingChannelEnabled_ReturnsFalse_WhenChannelIsNullAndFlagNotSet()
    {
        var features = new TestFeatures();
        var configuration = BuildConfiguration(channel: null);

        Assert.False(KnownFeatures.IsStagingChannelEnabled(features, configuration));
    }

    [Fact]
    public void IsStagingChannelEnabled_IsCaseInsensitive_ForChannelValue()
    {
        var features = new TestFeatures();
        var configuration = BuildConfiguration(channel: "Staging");

        Assert.True(KnownFeatures.IsStagingChannelEnabled(features, configuration));
    }

    [Fact]
    public void IsStagingChannelEnabled_IsCaseInsensitive_ForUppercaseChannelValue()
    {
        var features = new TestFeatures();
        var configuration = BuildConfiguration(channel: "STAGING");

        Assert.True(KnownFeatures.IsStagingChannelEnabled(features, configuration));
    }

    [Fact]
    public void IsStagingChannelEnabled_ReturnsFalse_WhenChannelIsDailyAndFlagNotSet()
    {
        var features = new TestFeatures();
        var configuration = BuildConfiguration(channel: PackageChannelNames.Daily);

        Assert.False(KnownFeatures.IsStagingChannelEnabled(features, configuration));
    }

    private static IConfiguration BuildConfiguration(string? channel)
    {
        var configData = new Dictionary<string, string?>();

        if (channel is not null)
        {
            configData["channel"] = channel;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();
    }

}
