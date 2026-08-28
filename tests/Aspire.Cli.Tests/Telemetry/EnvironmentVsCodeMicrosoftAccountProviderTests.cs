// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Acquisition;
using Aspire.Cli.Tests.TestServices;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Telemetry;

[Collection(EnvVarMutatingTestCollection.Name)]
public class EnvironmentVsCodeMicrosoftAccountProviderTests
{
    [Theory]
    [InlineData("internal", "Current.Alias", true, "current.alias", false)]
    [InlineData("not_internal", null, true, null, false)]
    [InlineData("unavailable", null, false, null, true)]
    [InlineData("refreshing", null, false, null, false)]
    [InlineData(null, null, false, null, false)]
    [InlineData("internal", null, false, null, true)]
    [InlineData("internal", "bad alias", false, null, true)]
    [InlineData("not_internal", "unexpected.alias", false, null, true)]
    [InlineData(null, "unexpected.alias", false, null, true)]
    [InlineData("unknown", null, false, null, true)]
    public void CaptureAndClear_ParsesStateAndClearsSourceValues(
        string? state,
        string? alias,
        bool expectedAvailable,
        string? expectedAlias,
        bool expectedUnavailable)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KnownConfigNames.ExtensionEndpoint] = "localhost:1234",
                [KnownConfigNames.ExtensionToken] = "extension-token",
                [KnownConfigNames.ExtensionCert] = "extension-cert",
                [EnvironmentVsCodeMicrosoftAccountProvider.StateEnvironmentVariable] = state,
                [EnvironmentVsCodeMicrosoftAccountProvider.AliasEnvironmentVariable] = alias
            })
            .Build();
        var clearedVariables = new List<string>();

        var provider = EnvironmentVsCodeMicrosoftAccountProvider.CaptureAndClear(configuration, clearedVariables.Add);
        var result = provider.GetInternalMicrosoftAccount();

        Assert.Equal(expectedAvailable, result.IsAvailable);
        Assert.Equal(expectedAlias, result.Alias);
        Assert.Equal(expectedUnavailable, result.IsUnavailable);
        Assert.Equal(state == EnvironmentVsCodeMicrosoftAccountProvider.RefreshingState && alias is null, result.InvalidatesCache);
        Assert.Equal(
            (state is null && alias is null) ||
            (state == EnvironmentVsCodeMicrosoftAccountProvider.RefreshingState && alias is null),
            result.PreventsNegativeCache);
        Assert.Null(configuration[EnvironmentVsCodeMicrosoftAccountProvider.StateEnvironmentVariable]);
        Assert.Null(configuration[EnvironmentVsCodeMicrosoftAccountProvider.AliasEnvironmentVariable]);
        Assert.Equal(
            [
                EnvironmentVsCodeMicrosoftAccountProvider.StateEnvironmentVariable,
                EnvironmentVsCodeMicrosoftAccountProvider.AliasEnvironmentVariable
            ],
            clearedVariables);
    }

    [Fact]
    public void CaptureAndClear_RemovesValuesFromActualProcessEnvironment()
    {
        using var state = new EnvVarOverride(
            EnvironmentVsCodeMicrosoftAccountProvider.StateEnvironmentVariable,
            EnvironmentVsCodeMicrosoftAccountProvider.InternalState);
        using var alias = new EnvVarOverride(
            EnvironmentVsCodeMicrosoftAccountProvider.AliasEnvironmentVariable,
            "current.alias");
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KnownConfigNames.ExtensionEndpoint] = "localhost:1234",
                [KnownConfigNames.ExtensionToken] = "extension-token",
                [KnownConfigNames.ExtensionCert] = "extension-cert"
            })
            .Build();

        var provider = EnvironmentVsCodeMicrosoftAccountProvider.CaptureAndClear(configuration);

        Assert.Equal("current.alias", provider.GetInternalMicrosoftAccount().Alias);
        Assert.Null(Environment.GetEnvironmentVariable(EnvironmentVsCodeMicrosoftAccountProvider.StateEnvironmentVariable));
        Assert.Null(Environment.GetEnvironmentVariable(EnvironmentVsCodeMicrosoftAccountProvider.AliasEnvironmentVariable));
    }

    [Fact]
    public void CaptureAndClear_IgnoresAccountValuesOutsideExtensionInvocation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EnvironmentVsCodeMicrosoftAccountProvider.StateEnvironmentVariable] = EnvironmentVsCodeMicrosoftAccountProvider.InternalState,
                [EnvironmentVsCodeMicrosoftAccountProvider.AliasEnvironmentVariable] = "spoofed.alias"
            })
            .Build();

        var provider = EnvironmentVsCodeMicrosoftAccountProvider.CaptureAndClear(configuration, _ => { });
        var result = provider.GetInternalMicrosoftAccount();

        Assert.False(result.IsAvailable);
        Assert.False(result.IsUnavailable);
        Assert.Null(result.Alias);
    }

    [Theory]
    [InlineData(EnvironmentVsCodeMicrosoftAccountProvider.UnavailableState, false, false, true)]
    [InlineData(EnvironmentVsCodeMicrosoftAccountProvider.RefreshingState, false, false, true)]
    [InlineData(EnvironmentVsCodeMicrosoftAccountProvider.NotInternalState, true, false, false)]
    public void CaptureAndClear_AcceptsNonPositiveStateWithoutExtensionConnection(
        string state,
        bool expectedAvailable,
        bool expectedUnavailable,
        bool expectedPreventsNegativeCache)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EnvironmentVsCodeMicrosoftAccountProvider.StateEnvironmentVariable] = state
            })
            .Build();

        var provider = EnvironmentVsCodeMicrosoftAccountProvider.CaptureAndClear(configuration, _ => { });
        var result = provider.GetInternalMicrosoftAccount();

        Assert.Equal(expectedAvailable, result.IsAvailable);
        Assert.Equal(expectedUnavailable, result.IsUnavailable);
        Assert.Equal(expectedPreventsNegativeCache, result.PreventsNegativeCache);
        Assert.Null(result.Alias);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void CaptureAndClear_RequiresCompleteExtensionConnection(
        bool hasEndpoint,
        bool hasToken,
        bool hasCertificate)
    {
        var values = new Dictionary<string, string?>
        {
            [EnvironmentVsCodeMicrosoftAccountProvider.StateEnvironmentVariable] = EnvironmentVsCodeMicrosoftAccountProvider.InternalState,
            [EnvironmentVsCodeMicrosoftAccountProvider.AliasEnvironmentVariable] = "spoofed.alias"
        };
        if (hasEndpoint)
        {
            values[KnownConfigNames.ExtensionEndpoint] = "localhost:1234";
        }
        if (hasToken)
        {
            values[KnownConfigNames.ExtensionToken] = "extension-token";
        }
        if (hasCertificate)
        {
            values[KnownConfigNames.ExtensionCert] = "extension-cert";
        }
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var provider = EnvironmentVsCodeMicrosoftAccountProvider.CaptureAndClear(configuration, _ => { });
        var result = provider.GetInternalMicrosoftAccount();

        Assert.False(result.IsAvailable);
        Assert.False(result.IsUnavailable);
        Assert.Null(result.Alias);
    }
}
