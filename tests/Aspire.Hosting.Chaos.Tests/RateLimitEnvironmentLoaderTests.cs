// <copyright file="RateLimitEnvironmentLoaderTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using ChaosProxy.Container.Policy;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Chaos.UnitTests;

public class RateLimitEnvironmentLoaderTests
{
    private static IConfiguration BuildConfig(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void LoadBootstrap_PartialConfig_ReturnsNull()
    {
        // Missing windowMs - rate-limit should not be loaded.
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_RATE_LIMIT_REQUESTS_PER_WINDOW"] = "10",
        });

        Assert.Null(EnvironmentPolicyLoader.LoadBootstrap(cfg));
    }

    [Fact]
    public void LoadBootstrap_FullConfig_ReturnsRateLimitPolicy()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_RATE_LIMIT_REQUESTS_PER_WINDOW"] = "10",
            ["CHAOS_RATE_LIMIT_WINDOW_MS"] = "5000",
            ["CHAOS_RATE_LIMIT_STATUS"] = "503",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.NotNull(policy);
        Assert.NotNull(policy!.RateLimit);
        Assert.Equal(10, policy.RateLimit!.RequestsPerWindow);
        Assert.Equal(5000, policy.RateLimit.WindowMs);
        Assert.Equal(503, policy.RateLimit.Status);
        Assert.Null(policy.RateLimit.Headers);
    }

    [Fact]
    public void LoadBootstrap_StatusOmitted_DefaultsTo429()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_RATE_LIMIT_REQUESTS_PER_WINDOW"] = "10",
            ["CHAOS_RATE_LIMIT_WINDOW_MS"] = "5000",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.NotNull(policy);
        Assert.Equal(429, policy!.RateLimit!.Status);
    }

    [Fact]
    public void LoadBootstrap_HeadersJson_DeserializesHeaders()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_RATE_LIMIT_REQUESTS_PER_WINDOW"] = "1",
            ["CHAOS_RATE_LIMIT_WINDOW_MS"] = "1000",
            ["CHAOS_RATE_LIMIT_HEADERS_JSON"] = """{"Retry-After":"5","X-Rate-Limit":"1"}""",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.NotNull(policy);
        Assert.NotNull(policy!.RateLimit!.Headers);
        Assert.Equal("5", policy.RateLimit.Headers!["Retry-After"]);
        Assert.Equal("1", policy.RateLimit.Headers["X-Rate-Limit"]);
    }

    [Fact]
    public void LoadBootstrap_MalformedHeadersJson_SkipsHeaders()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_RATE_LIMIT_REQUESTS_PER_WINDOW"] = "1",
            ["CHAOS_RATE_LIMIT_WINDOW_MS"] = "1000",
            ["CHAOS_RATE_LIMIT_HEADERS_JSON"] = "not json",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.NotNull(policy);
        Assert.Null(policy!.RateLimit!.Headers);
    }

    [Fact]
    public void LoadDeclaredPolicies_RateLimitPolicy_ReturnsOne()
    {
        var json = """[{"id":"rl","rateLimit":{"requestsPerWindow":5,"windowMs":2000}}]""";
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Single(policies);
        Assert.NotNull(policies[0].RateLimit);
        Assert.Equal(5, policies[0].RateLimit!.RequestsPerWindow);
        Assert.Equal(2000, policies[0].RateLimit!.WindowMs);
        Assert.Equal(429, policies[0].RateLimit!.Status);
    }

    [Fact]
    public void LoadDeclaredPolicies_RateLimitWithCustomStatus_PreservesIt()
    {
        var json = """[{"id":"rl","rateLimit":{"requestsPerWindow":1,"windowMs":1000,"status":503}}]""";
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Equal(503, policies[0].RateLimit!.Status);
    }
}
