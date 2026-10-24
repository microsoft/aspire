// <copyright file="DropResponseEnvironmentLoaderTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using ChaosProxy.Container.Policy;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Chaos.UnitTests;

public class DropResponseEnvironmentLoaderTests
{
    private static IConfiguration BuildConfig(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void LoadBootstrap_DropResponseEnabledFalse_NoDrop()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_DROP_RESPONSE_ENABLED"] = "false",
            ["CHAOS_DROP_RESPONSE_PROBABILITY"] = "1.0",
        });

        Assert.Null(EnvironmentPolicyLoader.LoadBootstrap(cfg));
    }

    [Fact]
    public void LoadBootstrap_DropResponseEnabledTrue_ReturnsPolicy()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_DROP_RESPONSE_ENABLED"] = "true",
            ["CHAOS_DROP_RESPONSE_PROBABILITY"] = "0.25",
            ["CHAOS_DROP_RESPONSE_FAIL_FIRST"] = "2",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.NotNull(policy);
        Assert.NotNull(policy!.DropResponse);
        Assert.Equal(0.25, policy.DropResponse!.Probability);
        Assert.Equal(2, policy.DropResponse.FailFirst);
    }

    [Fact]
    public void LoadDeclaredPolicies_SinglePolicyWithDropResponse_ReturnsOne()
    {
        var json = """[{"id":"drop","dropResponse":{"probability":0.5}}]""";
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Single(policies);
        Assert.Equal("drop", policies[0].Id);
        Assert.NotNull(policies[0].DropResponse);
        Assert.Equal(0.5, policies[0].DropResponse!.Probability);
    }

    [Fact]
    public void LoadDeclaredPolicies_DropResponseDefaultsProbabilityToOne()
    {
        var json = """[{"id":"drop","dropResponse":{}}]""";
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Single(policies);
        Assert.Equal(1.0, policies[0].DropResponse!.Probability);
    }
}
