// <copyright file="ChaosFreezeTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChaosProxy.Container;
using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

public class ChaosFreezeTests
{
    [Fact]
    public void BuildFreezePolicies_MapsEachKindAndDedups()
    {
        var faults = new List<FrozenFault>
        {
            new("rnd", "GET", "/api/a", "error", 503, null),
            new("rnd", "GET", "/api/a", "error", 503, null),  // duplicate -> collapsed
            new("rnd", "POST", "/api/b", "drop", null, null),
            new("rnd", "GET", "/api/c", "latency", null, 250),
            new("rnd", "GET", "/api/a", "error", 500, null),  // different status -> separate
        };

        var policies = ChaosEndpoints.BuildFreezePolicies(faults);

        Assert.Equal(4, policies.Count);

        var err503 = policies.Single(p => p.Error?.Status == 503);
        Assert.Equal("GET", err503.Matcher!.Method);
        Assert.Equal("/api/a", err503.Matcher.PathPrefix);
        Assert.Equal(1, err503.Error!.FailFirst);
        Assert.Equal(300, err503.TtlSeconds);

        var drop = policies.Single(p => p.DropResponse is not null);
        Assert.Equal(1, drop.DropResponse!.FailFirst);
        Assert.Equal(1, drop.DropResponse.MaxFires);

        var latency = policies.Single(p => p.Latency is not null);
        Assert.Equal(250, latency.Latency!.MinMs);
        Assert.Equal(250, latency.Latency.MaxMs);
        Assert.Equal(1, latency.Latency.FailFirst);
    }

    [Fact]
    public void BuildFreezePolicies_EmptyLog_ReturnsEmpty()
    {
        Assert.Empty(ChaosEndpoints.BuildFreezePolicies(new List<FrozenFault>()));
    }

    [Fact]
    public async Task FreezeEndpoint_ReturnsDeterministicPoliciesFromFiredLog()
    {
        await using var fx = new ChaosEndpointsFixture();
        fx.Store.RecordFrozenFault(new FrozenFault("rnd", "GET", "/api/x", "error", 429, null));
        fx.Store.RecordFrozenFault(new FrozenFault("rnd", "POST", "/api/y", "drop", null, null));

        var resp = await fx.Client.PostAsync("/chaos/freeze", content: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var policies = doc.RootElement.GetProperty("policies");
        Assert.Equal(2, policies.GetArrayLength());

        var statuses = policies.EnumerateArray()
            .Where(p => p.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
            .Select(p => p.GetProperty("error").GetProperty("status").GetInt32())
            .ToList();
        Assert.Contains(429, statuses);
    }

    [Fact]
    public async Task FreezeEndpoint_EmptyLog_ReturnsEmptyPolicies()
    {
        await using var fx = new ChaosEndpointsFixture();

        var resp = await fx.Client.PostAsync("/chaos/freeze", content: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("policies").GetArrayLength());
    }

    [Fact]
    public async Task InstallEndpoint_AcceptsRandomFaultPolicy_NoAppHostSetupRequired()
    {
        // Proves run-to-green can install random chaos purely at runtime (the same path it
        // uses for deterministic chaos_policies) — no build-time WithRandomChaos needed.
        await using var fx = new ChaosEndpointsFixture();

        var body = new
        {
            id = "rnd-runtime",
            matcher = new { pathPrefix = "/api" },
            randomFault = new { profileId = "azure.cosmos", intensity = 0.2, seed = 7 },
        };

        var resp = await fx.Client.PostAsJsonAsync("/chaos/policies", body);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var installed = fx.Store.TryGet("rnd-runtime");
        Assert.NotNull(installed);
        Assert.NotNull(installed!.RandomFault);
        Assert.Equal("azure.cosmos", installed.RandomFault!.ProfileId);
        Assert.Equal(0.2, installed.RandomFault.Intensity);
        Assert.Equal(7, installed.RandomFault.Seed);
    }
}
