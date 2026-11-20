// <copyright file="HeaderTamperEnvironmentLoaderTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using ChaosProxy.Container.Policy;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Chaos.UnitTests;

public class HeaderTamperEnvironmentLoaderTests
{
    private static IConfiguration BuildConfig(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void LoadBootstrap_EnvVarUnset_NoHeaderTamper()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>());

        Assert.Null(EnvironmentPolicyLoader.LoadBootstrap(cfg));
    }

    [Fact]
    public void LoadBootstrap_FullPayload_LoadsAllSections()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_HEADER_TAMPER_JSON"] = """
                {
                  "direction":"Request",
                  "remove":["Authorization"],
                  "set":{"X-Trace":"chaos"},
                  "add":{"X-Multi":"extra"}
                }
                """,
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.NotNull(policy);
        Assert.NotNull(policy!.HeaderTamper);
        var ht = policy.HeaderTamper!;
        Assert.Equal(HeaderTamperDirection.Request, ht.Direction);
        Assert.Equal(new[] { "Authorization" }, ht.Remove);
        Assert.Equal("chaos", ht.Set!["X-Trace"]);
        Assert.Equal("extra", ht.Add!["X-Multi"]);
    }

    [Fact]
    public void LoadBootstrap_NoDirection_DefaultsToBoth()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_HEADER_TAMPER_JSON"] = """{"set":{"X-Foo":"v"}}""",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.Equal(HeaderTamperDirection.Both, policy!.HeaderTamper!.Direction);
    }

    [Fact]
    public void LoadBootstrap_UnknownDirection_FallsBackToBoth()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_HEADER_TAMPER_JSON"] = """{"direction":"garbage","set":{"X-Foo":"v"}}""",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.Equal(HeaderTamperDirection.Both, policy!.HeaderTamper!.Direction);
    }

    [Fact]
    public void LoadBootstrap_MalformedJson_SkipsHeaderTamper()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_HEADER_TAMPER_JSON"] = "not json",
        });

        Assert.Null(EnvironmentPolicyLoader.LoadBootstrap(cfg));
    }

    [Fact]
    public void LoadDeclaredPolicies_HeaderTamperPolicy_ReturnsOne()
    {
        var json = """[{"id":"ht","headerTamper":{"direction":"Response","set":{"X-Server":"chaos"}}}]""";
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Single(policies);
        var ht = policies[0].HeaderTamper!;
        Assert.Equal(HeaderTamperDirection.Response, ht.Direction);
        Assert.Equal("chaos", ht.Set!["X-Server"]);
    }
}
