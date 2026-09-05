// <copyright file="SlowResponseEnvironmentLoaderTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Text;
using ChaosProxy.Container.Policy;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Chaos.UnitTests;

public class SlowResponseEnvironmentLoaderTests
{
    private static IConfiguration BuildConfig(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void LoadBootstrap_DisabledOrAbsent_ReturnsNull()
    {
        var disabled = BuildConfig(new Dictionary<string, string?> { ["CHAOS_SLOW_RESPONSE_ENABLED"] = "false" });
        var absent = BuildConfig(new Dictionary<string, string?>());

        Assert.Null(EnvironmentPolicyLoader.LoadBootstrap(disabled));
        Assert.Null(EnvironmentPolicyLoader.LoadBootstrap(absent));
    }

    [Fact]
    public void LoadBootstrap_FullConfig_LoadsAllFields()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_SLOW_RESPONSE_ENABLED"] = "true",
            ["CHAOS_SLOW_RESPONSE_STATUS"] = "206",
            ["CHAOS_SLOW_RESPONSE_CONTENT_TYPE"] = "application/json",
            ["CHAOS_SLOW_RESPONSE_BODY"] = "stream-body",
            ["CHAOS_SLOW_RESPONSE_BYTES_PER_SECOND"] = "100",
            ["CHAOS_SLOW_RESPONSE_CHUNK_SIZE"] = "8",
            ["CHAOS_SLOW_RESPONSE_PROBABILITY"] = "0.5",
            ["CHAOS_SLOW_RESPONSE_FAIL_FIRST"] = "2",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.NotNull(policy);
        var sr = policy!.SlowResponse!;
        Assert.Equal(206, sr.Status);
        Assert.Equal("application/json", sr.ContentType);
        Assert.Equal("stream-body", Encoding.UTF8.GetString(sr.Body));
        Assert.Equal(100, sr.BytesPerSecond);
        Assert.Equal(8, sr.ChunkSize);
        Assert.Equal(0.5, sr.Probability);
        Assert.Equal(2, sr.FailFirst);
    }

    [Fact]
    public void LoadBootstrap_DefaultsApplied()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_SLOW_RESPONSE_ENABLED"] = "true",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        var sr = policy!.SlowResponse!;
        Assert.Equal(200, sr.Status);
        Assert.Empty(sr.Body);
        Assert.Equal(1024, sr.BytesPerSecond);
        Assert.Equal(64, sr.ChunkSize);
        Assert.Equal(1.0, sr.Probability);
    }

    [Fact]
    public void LoadDeclaredPolicies_SlowResponsePolicy_ReturnsOne()
    {
        var json = """[{"id":"sr","slowResponse":{"body":"slow!","bytesPerSecond":500,"chunkSize":16}}]""";
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Single(policies);
        var sr = policies[0].SlowResponse!;
        Assert.Equal("slow!", Encoding.UTF8.GetString(sr.Body));
        Assert.Equal(500, sr.BytesPerSecond);
        Assert.Equal(16, sr.ChunkSize);
    }

    [Fact]
    public void LoadDeclaredPolicies_SlowResponseDefaults_ApplyMissingFields()
    {
        var json = """[{"id":"sr","slowResponse":{}}]""";
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        var sr = policies[0].SlowResponse!;
        Assert.Equal(200, sr.Status);
        Assert.Empty(sr.Body);
        Assert.Equal(1024, sr.BytesPerSecond);
        Assert.Equal(64, sr.ChunkSize);
        Assert.Equal(1.0, sr.Probability);
    }
}
