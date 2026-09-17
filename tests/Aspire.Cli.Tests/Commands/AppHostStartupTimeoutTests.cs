// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Commands;

public class AppHostStartupTimeoutTests
{
    [Fact]
    public void BackchannelConnectionTimeoutUsesLongerConfiguredAppHostStartupTimeout()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CliConfigNames.AppHostStartupTimeout] = "86400"
            })
            .Build();

        var timeout = AppHostStartupTimeout.GetBackchannelConnectionTimeout(configuration);

        Assert.Equal(TimeSpan.FromSeconds(86400), timeout);
    }

    [Fact]
    public void BackchannelConnectionTimeoutUsesDefaultAppHostStartupTimeout()
    {
        var configuration = new ConfigurationBuilder().Build();

        var timeout = AppHostStartupTimeout.GetBackchannelConnectionTimeout(configuration);

        Assert.Equal(TimeSpan.FromSeconds(120), timeout);
    }

    [Fact]
    public void ExplicitBackchannelConnectionTimeoutOverridesAppHostStartupTimeout()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CliConfigNames.AppHostStartupTimeout] = "180",
                [KnownConfigNames.CliBackchannelConnectTimeoutSeconds] = "5"
            })
            .Build();

        var timeout = AppHostStartupTimeout.GetBackchannelConnectionTimeout(configuration);

        Assert.Equal(TimeSpan.FromSeconds(5), timeout);
    }

    [Theory]
    [InlineData("Infinity")]
    [InlineData("1e30")]
    public void InvalidExplicitBackchannelConnectionTimeoutUsesAppHostStartupTimeout(string configuredValue)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CliConfigNames.AppHostStartupTimeout] = "180",
                [KnownConfigNames.CliBackchannelConnectTimeoutSeconds] = configuredValue
            })
            .Build();

        var timeout = AppHostStartupTimeout.GetBackchannelConnectionTimeout(configuration);

        Assert.Equal(TimeSpan.FromSeconds(180), timeout);
    }
}
