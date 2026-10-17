// <copyright file="IdempotencyCollisionEnvironmentLoaderTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using ChaosProxy.Container.Policy;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Chaos.UnitTests;

public class IdempotencyCollisionEnvironmentLoaderTests
{
    private static IConfiguration BuildConfig(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void LoadBootstrap_DisabledOrAbsent_ReturnsNull()
    {
        var disabled = BuildConfig(new Dictionary<string, string?> { ["CHAOS_IDEMPOTENCY_COLLISION_ENABLED"] = "false" });
        var absent = BuildConfig(new Dictionary<string, string?>());

        Assert.Null(EnvironmentPolicyLoader.LoadBootstrap(disabled));
        Assert.Null(EnvironmentPolicyLoader.LoadBootstrap(absent));
    }

    [Fact]
    public void LoadBootstrap_FullConfig_LoadsAllFields()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_IDEMPOTENCY_COLLISION_ENABLED"] = "true",
            ["CHAOS_IDEMPOTENCY_COLLISION_KEY_HEADER_NAME"] = "X-Request-ID",
            ["CHAOS_IDEMPOTENCY_COLLISION_STATUS"] = "422",
            ["CHAOS_IDEMPOTENCY_COLLISION_BODY"] = "duplicate request",
            ["CHAOS_IDEMPOTENCY_COLLISION_CONTENT_TYPE"] = "text/plain",
            ["CHAOS_IDEMPOTENCY_COLLISION_WINDOW_MS"] = "30000",
            ["CHAOS_IDEMPOTENCY_COLLISION_HEADERS_JSON"] = """{"X-Conflict":"true"}""",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.NotNull(policy);
        var ic = policy!.IdempotencyCollision!;
        Assert.Equal("X-Request-ID", ic.KeyHeaderName);
        Assert.Equal(422, ic.Status);
        Assert.Equal("duplicate request", ic.Body);
        Assert.Equal("text/plain", ic.ContentType);
        Assert.Equal(30_000, ic.WindowMs);
        Assert.Equal("true", ic.Headers!["X-Conflict"]);
    }

    [Fact]
    public void LoadBootstrap_DefaultsApplied()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["CHAOS_IDEMPOTENCY_COLLISION_ENABLED"] = "true",
        });

        var policy = EnvironmentPolicyLoader.LoadBootstrap(cfg);

        Assert.NotNull(policy);
        var ic = policy!.IdempotencyCollision!;
        Assert.Equal("Idempotency-Key", ic.KeyHeaderName);
        Assert.Equal(409, ic.Status);
        Assert.Null(ic.Body);
        Assert.Equal(60_000, ic.WindowMs);
    }

    [Fact]
    public void LoadDeclaredPolicies_FullConfig_LoadsAllFields()
    {
        var json = """[{"id":"ic","idempotencyCollision":{"keyHeaderName":"X-Trace-Id","status":422,"body":"dup","windowMs":120000}}]""";
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Single(policies);
        var ic = policies[0].IdempotencyCollision!;
        Assert.Equal("X-Trace-Id", ic.KeyHeaderName);
        Assert.Equal(422, ic.Status);
        Assert.Equal("dup", ic.Body);
        Assert.Equal(120_000, ic.WindowMs);
    }

    [Fact]
    public void LoadDeclaredPolicies_DefaultsApplied()
    {
        var json = """[{"id":"ic","idempotencyCollision":{}}]""";
        var cfg = BuildConfig(new Dictionary<string, string?> { ["CHAOS_POLICIES_JSON"] = json });

        var policies = EnvironmentPolicyLoader.LoadDeclaredPolicies(cfg);

        Assert.Single(policies);
        var ic = policies[0].IdempotencyCollision!;
        Assert.Equal("Idempotency-Key", ic.KeyHeaderName);
        Assert.Equal(409, ic.Status);
        Assert.Equal(60_000, ic.WindowMs);
    }
}
