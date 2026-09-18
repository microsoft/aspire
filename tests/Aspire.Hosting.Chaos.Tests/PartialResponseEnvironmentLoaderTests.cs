// <copyright file="PartialResponseEnvironmentLoaderTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Text;
using ChaosProxy.Container.Policy;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Chaos.UnitTests;

public class PartialResponseEnvironmentLoaderTests
{
    private static IConfiguration BuildConfig(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void LoadBootstrap_DisabledOrAbsent_ReturnsNull()
    {
        var disabled = BuildConfig(new Dictionary<string, string?> { ["CHAOS_PARTIAL_RESPONSE_ENABLED"] = "false" });
        var absent = BuildConfig(new Dictionary<string, string?>());

        Assert.Null(EnvironmentPolicyLoader.LoadBootstrap(disabled));
        Assert.Null(EnvironmentPolicyLoader.LoadBootstrap(absent));
    }

    [Fact]
    public void LoadBootstrap_FullConfig_LoadsAllFields()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_PARTIAL_RESPONSE_ENABLED"] = "true",
            ["CHAOS_PARTIAL_RESPONSE_STATUS"] = "206",
            ["CHAOS_PARTIAL_RESPONSE_CONTENT_TYPE"] = "application/json",
            ["CHAOS_PARTIAL_RESPONSE_BODY"] = "{\"partial\":",
            ["CHAOS_PARTIAL_RESPONSE_ADVERTISED_CONTENT_LENGTH"] = "5000",
            ["CHAOS_PARTIAL_RESPONSE_ABORT_AFTER_MS"] = "100",
            ["CHAOS_PARTIAL_RESPONSE_PROBABILITY"] = "0.5",
            ["CHAOS_PARTIAL_RESPONSE_FAIL_FIRST"] = "3",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.NotNull(policy);
        var pr = policy!.PartialResponse!;
        Assert.Equal(206, pr.Status);
        Assert.Equal("application/json", pr.ContentType);
        Assert.Equal("{\"partial\":", Encoding.UTF8.GetString(pr.Body));
        Assert.Equal(5000, pr.AdvertisedContentLength);
        Assert.Equal(100, pr.AbortAfterMs);
        Assert.Equal(0.5, pr.Probability);
        Assert.Equal(3, pr.FailFirst);
    }

    [Fact]
    public void LoadBootstrap_DefaultsAppliedForOmittedFields()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_PARTIAL_RESPONSE_ENABLED"] = "true",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.NotNull(policy);
        var pr = policy!.PartialResponse!;
        Assert.Equal(200, pr.Status);
        Assert.Null(pr.ContentType);
        Assert.Empty(pr.Body);
        Assert.Null(pr.AdvertisedContentLength);
        Assert.Equal(0, pr.AbortAfterMs);
        Assert.Equal(1.0, pr.Probability);
        Assert.Null(pr.FailFirst);
    }

    [Fact]
    public void LoadDeclaredPolicies_PartialResponsePolicy_ReturnsOne()
    {
        var json = """[{"id":"truncated","partialResponse":{"status":200,"body":"hello","advertisedContentLength":100,"abortAfterMs":50}}]""";
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Single(policies);
        var pr = policies[0].PartialResponse!;
        Assert.Equal(200, pr.Status);
        Assert.Equal("hello", Encoding.UTF8.GetString(pr.Body));
        Assert.Equal(100, pr.AdvertisedContentLength);
        Assert.Equal(50, pr.AbortAfterMs);
        Assert.Equal(1.0, pr.Probability);
    }

    [Fact]
    public void LoadDeclaredPolicies_PartialResponseDefaults_StatusIs200ProbabilityIs1()
    {
        var json = """[{"id":"min","partialResponse":{}}]""";
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Single(policies);
        Assert.Equal(200, policies[0].PartialResponse!.Status);
        Assert.Equal(1.0, policies[0].PartialResponse!.Probability);
        Assert.Empty(policies[0].PartialResponse!.Body);
    }
}
