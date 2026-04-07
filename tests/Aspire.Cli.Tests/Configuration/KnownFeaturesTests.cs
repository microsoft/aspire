// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Packaging;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Configuration;

public class KnownFeaturesTests
{
    [Fact]
    public void IsStagingChannelEnabled_ReturnsTrue_WhenChannelIsStaging()
    {
        var features = new TestFeatures(stagingChannelEnabled: false);
        var configuration = BuildConfiguration(channel: PackageChannelNames.Staging);

        Assert.True(KnownFeatures.IsStagingChannelEnabled(features, configuration));
    }

    [Fact]
    public void IsStagingChannelEnabled_ReturnsTrue_WhenFeatureFlagIsTrue()
    {
        var features = new TestFeatures(stagingChannelEnabled: true);
        var configuration = BuildConfiguration(channel: PackageChannelNames.Stable);

        Assert.True(KnownFeatures.IsStagingChannelEnabled(features, configuration));
    }

    [Fact]
    public void IsStagingChannelEnabled_ReturnsTrue_WhenBothChannelIsStagingAndFlagIsTrue()
    {
        var features = new TestFeatures(stagingChannelEnabled: true);
        var configuration = BuildConfiguration(channel: PackageChannelNames.Staging);

        Assert.True(KnownFeatures.IsStagingChannelEnabled(features, configuration));
    }

    [Fact]
    public void IsStagingChannelEnabled_ReturnsFalse_WhenChannelIsNotStagingAndFlagNotSet()
    {
        var features = new TestFeatures(stagingChannelEnabled: false);
        var configuration = BuildConfiguration(channel: PackageChannelNames.Stable);

        Assert.False(KnownFeatures.IsStagingChannelEnabled(features, configuration));
    }

    [Fact]
    public void IsStagingChannelEnabled_ReturnsFalse_WhenChannelIsNullAndFlagNotSet()
    {
        var features = new TestFeatures(stagingChannelEnabled: false);
        var configuration = BuildConfiguration(channel: null);

        Assert.False(KnownFeatures.IsStagingChannelEnabled(features, configuration));
    }

    [Fact]
    public void IsStagingChannelEnabled_IsCaseInsensitive_ForChannelValue()
    {
        var features = new TestFeatures(stagingChannelEnabled: false);
        var configuration = BuildConfiguration(channel: "Staging");

        Assert.True(KnownFeatures.IsStagingChannelEnabled(features, configuration));
    }

    [Fact]
    public void IsStagingChannelEnabled_IsCaseInsensitive_ForUppercaseChannelValue()
    {
        var features = new TestFeatures(stagingChannelEnabled: false);
        var configuration = BuildConfiguration(channel: "STAGING");

        Assert.True(KnownFeatures.IsStagingChannelEnabled(features, configuration));
    }

    [Fact]
    public void IsStagingChannelEnabled_ReturnsFalse_WhenChannelIsDailyAndFlagNotSet()
    {
        var features = new TestFeatures(stagingChannelEnabled: false);
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

    private sealed class TestFeatures(bool stagingChannelEnabled) : IFeatures
    {
        public bool IsFeatureEnabled(string featureFlag, bool defaultValue)
        {
            if (featureFlag == KnownFeatures.StagingChannelEnabled)
            {
                return stagingChannelEnabled;
            }

            return defaultValue;
        }
    }
}
