// <copyright file="EnvironmentPolicyLoaderTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using ChaosProxy.Container.Policy;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Chaos.UnitTests;

public class EnvironmentPolicyLoaderTests
{
    private static IConfiguration BuildConfig(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void LoadBootstrap_NoEnvVarsSet_ReturnsNull()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>());

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.Null(policy);
    }

    [Fact]
    public void LoadBootstrap_LatencyOnly_ReturnsLatencyPolicy()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_LATENCY_MIN_MS"] = "100",
            ["CHAOS_LATENCY_MAX_MS"] = "300",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.NotNull(policy);
        Assert.Equal("bootstrap", policy!.Id);
        Assert.NotNull(policy.Latency);
        Assert.Equal(100, policy.Latency!.MinMs);
        Assert.Equal(300, policy.Latency.MaxMs);
        Assert.Equal(1.0, policy.Latency.Probability);
        Assert.Null(policy.Latency.FailFirst);
        Assert.Null(policy.Error);
        Assert.Null(policy.ReplayDuplicate);
    }

    [Fact]
    public void LoadBootstrap_LatencyWithProbabilityAndFailFirst_ReadsBoth()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_LATENCY_MIN_MS"] = "50",
            ["CHAOS_LATENCY_MAX_MS"] = "75",
            ["CHAOS_LATENCY_PROBABILITY"] = "0.5",
            ["CHAOS_LATENCY_FAIL_FIRST"] = "3",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.NotNull(policy);
        Assert.Equal(0.5, policy!.Latency!.Probability);
        Assert.Equal(3, policy.Latency.FailFirst);
    }

    [Fact]
    public void LoadBootstrap_LatencyMinWithoutMax_DoesNotProduceLatency()
    {
        // Both min AND max required - prevents partial config from silently producing
        // a 0-max-ms policy that always fires with no actual delay.
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_LATENCY_MIN_MS"] = "100",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.Null(policy);
    }

    [Fact]
    public void LoadBootstrap_ErrorOnly_ReturnsErrorPolicy()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_ERROR_STATUS"] = "503",
            ["CHAOS_ERROR_BODY"] = "ServerBusy",
            ["CHAOS_ERROR_CONTENT_TYPE"] = "text/plain",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.NotNull(policy);
        Assert.NotNull(policy!.Error);
        Assert.Equal(503, policy.Error!.Status);
        Assert.Equal("ServerBusy", policy.Error.Body);
        Assert.Equal("text/plain", policy.Error.ContentType);
        Assert.Equal(1.0, policy.Error.Probability);
        Assert.Null(policy.Error.Headers);
    }

    [Fact]
    public void LoadBootstrap_ErrorWithHeadersJson_DeserializesHeaders()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_ERROR_STATUS"] = "429",
            ["CHAOS_ERROR_HEADERS_JSON"] = """{"x-ms-retry-after-ms":"250","Retry-After":"1"}""",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.NotNull(policy);
        Assert.NotNull(policy!.Error!.Headers);
        Assert.Equal("250", policy.Error.Headers!["x-ms-retry-after-ms"]);
        Assert.Equal("1", policy.Error.Headers["Retry-After"]);
    }

    [Fact]
    public void LoadBootstrap_ErrorWithMalformedHeadersJson_SkipsHeaders()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_ERROR_STATUS"] = "503",
            ["CHAOS_ERROR_HEADERS_JSON"] = "not valid json",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.NotNull(policy);
        Assert.NotNull(policy!.Error);
        Assert.Null(policy.Error!.Headers);
    }

    [Fact]
    public void LoadBootstrap_ReplayDuplicateEnabledFalse_NoReplay()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_REPLAY_DUPLICATE_ENABLED"] = "false",
            ["CHAOS_REPLAY_DUPLICATE_PROBABILITY"] = "1.0",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.Null(policy);
    }

    [Fact]
    public void LoadBootstrap_ReplayDuplicateEnabled_ReturnsPolicy()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_REPLAY_DUPLICATE_ENABLED"] = "true",
            ["CHAOS_REPLAY_DUPLICATE_PROBABILITY"] = "0.5",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.NotNull(policy);
        Assert.NotNull(policy!.ReplayDuplicate);
        Assert.Equal(0.5, policy.ReplayDuplicate!.Probability);
    }

    [Fact]
    public void LoadBootstrap_MatcherFields_PopulateMatcher()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_LATENCY_MIN_MS"] = "10",
            ["CHAOS_LATENCY_MAX_MS"] = "20",
            ["CHAOS_MATCH_METHOD"] = "POST",
            ["CHAOS_MATCH_PATH_PREFIX"] = "/api/v1",
            ["CHAOS_MATCH_PATH_CONTAINS"] = "things",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.NotNull(policy);
        Assert.NotNull(policy!.Matcher);
        Assert.Equal("POST", policy.Matcher!.Method);
        Assert.Equal("/api/v1", policy.Matcher.PathPrefix);
        Assert.Equal("things", policy.Matcher.PathContains);
    }

    [Fact]
    public void LoadBootstrap_AllTransforms_ReturnsCompositePolicy()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_LATENCY_MIN_MS"] = "50",
            ["CHAOS_LATENCY_MAX_MS"] = "100",
            ["CHAOS_ERROR_STATUS"] = "429",
            ["CHAOS_REPLAY_DUPLICATE_ENABLED"] = "true",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.NotNull(policy);
        Assert.NotNull(policy!.Latency);
        Assert.NotNull(policy.Error);
        Assert.NotNull(policy.ReplayDuplicate);
    }

    [Fact]
    public void LoadDeclaredPolicies_EnvVarUnset_ReturnsEmpty()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>());

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Empty(policies);
    }

    [Fact]
    public void LoadDeclaredPolicies_EmptyJson_ReturnsEmpty()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_POLICIES_JSON"] = string.Empty,
        });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Empty(policies);
    }

    [Fact]
    public void LoadDeclaredPolicies_SinglePolicyWithLatency_ReturnsOne()
    {        var json = """[{"id":"slow","latency":{"minMs":500,"maxMs":800,"probability":1.0}}]""";
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Single(policies);
        Assert.Equal("slow", policies[0].Id);
        Assert.Equal(500, policies[0].Latency!.MinMs);
        Assert.Equal(800, policies[0].Latency!.MaxMs);
    }

    [Fact]
    public void LoadDeclaredPolicies_RandomFault_MapsConfigWithDefaults()
    {
        var json = """[{"id":"rnd","randomFault":{"profileId":"azure.cosmos","intensity":0.2,"seed":99,"maxFires":5,"excludePaths":["/health"]}}]""";
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Single(policies);
        var rnd = policies[0].RandomFault;
        Assert.NotNull(rnd);
        Assert.Equal("azure.cosmos", rnd!.ProfileId);
        Assert.Equal(0.2, rnd.Intensity);
        Assert.Equal(99, rnd.Seed);
        Assert.Equal(5, rnd.MaxFires);
        Assert.Equal(new[] { "/health" }, rnd.ExcludePaths);
    }

    [Fact]
    public void LoadDeclaredPolicies_RandomFault_AppliesProfileAndIntensityDefaults()
    {
        var json = """[{"id":"rnd","randomFault":{"seed":1}}]""";
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Single(policies);
        var rnd = policies[0].RandomFault;
        Assert.NotNull(rnd);
        Assert.Equal("service.http", rnd!.ProfileId);
        Assert.Equal(0.1, rnd.Intensity);
    }

    [Fact]
    public void LoadDeclaredPolicies_MultiplePolicies_ReturnsAllInOrder()
    {
        var json = """
            [
              {"id":"a","latency":{"minMs":10,"maxMs":20}},
              {"id":"b","error":{"status":503}},
              {"id":"c","replayDuplicate":{"probability":0.5}}
            ]
            """;
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Equal(3, policies.Count);
        Assert.Equal(new[] { "a", "b", "c" }, policies.Select(p => p.Id));
        Assert.NotNull(policies[0].Latency);
        Assert.NotNull(policies[1].Error);
        Assert.NotNull(policies[2].ReplayDuplicate);
    }

    [Fact]
    public void LoadDeclaredPolicies_PolicyWithoutTransforms_Skipped()
    {
        var json = """[{"id":"empty"},{"id":"real","error":{"status":503}}]""";
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Single(policies);
        Assert.Equal("real", policies[0].Id);
    }

    [Fact]
    public void LoadDeclaredPolicies_NoIdProvided_GeneratesGuidId()
    {
        var json = """[{"latency":{"minMs":10,"maxMs":20}}]""";
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Single(policies);
        Assert.StartsWith("declared-", policies[0].Id);
    }

    [Fact]
    public void LoadDeclaredPolicies_TtlSecondsSet_SetsExpiresAt()
    {
        var json = """[{"id":"ttl","latency":{"minMs":10,"maxMs":20},"ttlSeconds":120}]""";
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });
        var before = DateTimeOffset.UtcNow;

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Single(policies);
        Assert.NotNull(policies[0].ExpiresAt);
        Assert.InRange(policies[0].ExpiresAt!.Value, before.AddSeconds(118), before.AddSeconds(125));
    }

    [Fact]
    public void LoadDeclaredPolicies_NoTtl_ExpiresAtIsNull()
    {
        var json = """[{"id":"forever","latency":{"minMs":10,"maxMs":20}}]""";
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Null(policies[0].ExpiresAt);
    }

    [Fact]
    public void LoadDeclaredPolicies_MatcherFields_PopulateMatcher()
    {
        var json = """[{"id":"m","matcher":{"method":"GET","pathPrefix":"/api"},"error":{"status":503}}]""";
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Single(policies);
        Assert.NotNull(policies[0].Matcher);
        Assert.Equal("GET", policies[0].Matcher!.Method);
        Assert.Equal("/api", policies[0].Matcher!.PathPrefix);
    }

    [Fact]
    public void LoadDeclaredPolicies_MalformedJson_ReturnsEmpty()
    {
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = "not json" });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Empty(policies);
    }
}
