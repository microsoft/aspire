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
    [InlineData("rpc", null, false, null, false)]
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
        var result = provider.State;

        Assert.Equal(expectedAvailable, result.IsAvailable);
        Assert.Equal(expectedAlias, result.Alias);
        Assert.Equal(expectedUnavailable, result.IsUnavailable);
        Assert.Equal(state == EnvironmentVsCodeMicrosoftAccountProvider.RefreshingState && alias is null, result.InvalidatesCache);
        Assert.Equal(
            (state is null && alias is null) ||
            (state == EnvironmentVsCodeMicrosoftAccountProvider.RefreshingState && alias is null),
            result.PreventsNegativeCache);
        if (state is null && alias is null)
        {
            Assert.Equal(VsCodeMicrosoftAccountStateKind.Suppressed, result.Kind);
        }
        else if (state == EnvironmentVsCodeMicrosoftAccountProvider.RpcState && alias is null)
        {
            Assert.Equal(VsCodeMicrosoftAccountStateKind.RpcFallback, result.Kind);
        }
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

        Assert.Equal("current.alias", provider.State.Alias);
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
        var result = provider.State;

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
        var result = provider.State;

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
        var result = provider.State;

        Assert.False(result.IsAvailable);
        Assert.False(result.IsUnavailable);
        Assert.Null(result.Alias);
    }

    [Fact]
    public async Task FallbackProvider_UsesRpcOnlyWhenEnvironmentRequestsFallback()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KnownConfigNames.ExtensionEndpoint] = "localhost:1234",
                [KnownConfigNames.ExtensionToken] = "extension-token",
                [KnownConfigNames.ExtensionCert] = "extension-cert",
                [EnvironmentVsCodeMicrosoftAccountProvider.StateEnvironmentVariable] = EnvironmentVsCodeMicrosoftAccountProvider.RpcState
            })
            .Build();
        var environmentProvider = EnvironmentVsCodeMicrosoftAccountProvider.CaptureAndClear(configuration, _ => { });
        var rpcProvider = new TestVsCodeMicrosoftAccountProvider
        {
            GetInternalMicrosoftAccountAsyncCallback = _ =>
                Task.FromResult(VsCodeMicrosoftAccountState.Available("rpc.alias"))
        };
        var provider = new FallbackVsCodeMicrosoftAccountProvider(environmentProvider, () => rpcProvider);

        var result = await provider.GetInternalMicrosoftAccountAsync(CancellationToken.None);

        Assert.Equal("rpc.alias", result.Alias);
        Assert.Equal(VsCodeMicrosoftAccountTransport.Rpc, result.Transport);
    }

    [Fact]
    public async Task FallbackProvider_DoesNotResolveRpcForEnvironmentSnapshot()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KnownConfigNames.ExtensionEndpoint] = "localhost:1234",
                [KnownConfigNames.ExtensionToken] = "extension-token",
                [KnownConfigNames.ExtensionCert] = "extension-cert",
                [EnvironmentVsCodeMicrosoftAccountProvider.StateEnvironmentVariable] = EnvironmentVsCodeMicrosoftAccountProvider.InternalState,
                [EnvironmentVsCodeMicrosoftAccountProvider.AliasEnvironmentVariable] = "environment.alias"
            })
            .Build();
        var environmentProvider = EnvironmentVsCodeMicrosoftAccountProvider.CaptureAndClear(configuration, _ => { });
        var rpcProviderResolved = false;
        var provider = new FallbackVsCodeMicrosoftAccountProvider(environmentProvider, () =>
        {
            rpcProviderResolved = true;
            return new TestVsCodeMicrosoftAccountProvider();
        });

        var result = await provider.GetInternalMicrosoftAccountAsync(CancellationToken.None);

        Assert.Equal("environment.alias", result.Alias);
        Assert.False(rpcProviderResolved);
    }

    [Theory]
    [InlineData("internal", "rpc.alias", "Available")]
    [InlineData("not_internal", null, "Available")]
    [InlineData("refreshing", null, "Refreshing")]
    [InlineData("unavailable", null, "Unavailable")]
    [InlineData("rpc", null, "Unavailable")]
    public void ParseRpc_ParsesStructuredStateAndRejectsRecursiveFallback(
        string state,
        string? alias,
        string expectedKind)
    {
        var payload = alias is null ? new[] { state } : [state, alias];

        var result = EnvironmentVsCodeMicrosoftAccountProvider.ParseRpc(payload);

        Assert.Equal(expectedKind, result.Kind.ToString());
        Assert.Equal(VsCodeMicrosoftAccountTransport.Rpc, result.Transport);
    }

    [Theory]
    [InlineData()]
    [InlineData("internal", "alias", "unexpected")]
    public void ParseRpc_RejectsMalformedArity(params string[] payload)
    {
        var result = EnvironmentVsCodeMicrosoftAccountProvider.ParseRpc(payload);

        Assert.Equal(VsCodeMicrosoftAccountStateKind.Unavailable, result.Kind);
        Assert.Equal(VsCodeMicrosoftAccountTransport.Rpc, result.Transport);
    }
}
