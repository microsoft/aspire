// <copyright file="ChaosProxyClientTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using Aspire.Hosting.Chaos;
using Aspire.Chaos.Client;

namespace Aspire.Hosting.Chaos.UnitTests;

/// <summary>
/// End-to-end contract tests for <see cref="ChaosProxyClient"/> wired against the
/// in-process <see cref="ChaosEndpointsFixture"/>. Each test exercises one client
/// method + asserts on the resulting state via the store the endpoints share.
/// Validates that wire serialization round-trips correctly for every method on the
/// public client surface.
/// </summary>
public class ChaosProxyClientTests
{
    [Fact]
    public async Task HealthAsync_ReturnsTrue_WhenServerReachable()
    {
        await using var fx = new ChaosEndpointsFixture();
        var client = new ChaosProxyClient(WithBaseAddress(fx));

        Assert.True(await client.HealthAsync());
    }

    [Fact]
    public async Task InstallPolicyAsync_RoundTrips_AndReturnsServerAssignedId()
    {
        await using var fx = new ChaosEndpointsFixture();
        var client = new ChaosProxyClient(WithBaseAddress(fx));

        var id = await client.InstallPolicyAsync(new ChaosPolicy
        {
            Latency = new ChaosLatency { Min = TimeSpan.FromMilliseconds(10), Max = TimeSpan.FromMilliseconds(20), Probability = 1.0 },
        });

        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.NotNull(fx.Store.TryGet(id));
    }

    [Fact]
    public async Task InstallPolicyAsync_RespectsSuppliedId()
    {
        await using var fx = new ChaosEndpointsFixture();
        var client = new ChaosProxyClient(WithBaseAddress(fx));

        var id = await client.InstallPolicyAsync(new ChaosPolicy
        {
            Id = "my-explicit-id",
            Error = new ChaosError { Status = 503, Probability = 1.0 },
        });

        Assert.Equal("my-explicit-id", id);
    }

    [Fact]
    public async Task InstallPoliciesAsync_BulkInstall_ReturnsAllIdsInOrder()
    {
        await using var fx = new ChaosEndpointsFixture();
        var client = new ChaosProxyClient(WithBaseAddress(fx));

        var ids = await client.InstallPoliciesAsync(new[]
        {
            new ChaosPolicy { Id = "a", Error = new ChaosError { Status = 503, Probability = 1.0 } },
            new ChaosPolicy { Id = "b", Error = new ChaosError { Status = 504, Probability = 1.0 } },
        });

        Assert.Equal(new[] { "a", "b" }, ids);
        Assert.Equal(2, fx.Store.GetActive().Count);
    }

    [Fact]
    public async Task PreviewPolicyAsync_ReturnsCanonical_WithoutInstalling()
    {
        await using var fx = new ChaosEndpointsFixture();
        var client = new ChaosProxyClient(WithBaseAddress(fx));

        var preview = await client.PreviewPolicyAsync(new ChaosPolicy
        {
            Id = "preview-only",
            Latency = new ChaosLatency { Min = TimeSpan.FromMilliseconds(10), Max = TimeSpan.FromMilliseconds(20), Probability = 1.0 },
        });

        Assert.Equal("preview-only", preview.Id);
        Assert.NotNull(preview.Latency);
        Assert.Empty(fx.Store.GetActive());
    }

    [Fact]
    public async Task ListPoliciesAsync_ReturnsAllInstalledPolicies()
    {
        await using var fx = new ChaosEndpointsFixture();
        var client = new ChaosProxyClient(WithBaseAddress(fx));

        await client.InstallPolicyAsync(new ChaosPolicy { Id = "p1", Error = new ChaosError { Status = 503, Probability = 1.0 } });
        await client.InstallPolicyAsync(new ChaosPolicy { Id = "p2", Error = new ChaosError { Status = 504, Probability = 1.0 } });

        var policies = await client.ListPoliciesAsync();
        Assert.Equal(2, policies.Count);
        Assert.Contains(policies, p => p.Id == "p1");
        Assert.Contains(policies, p => p.Id == "p2");
    }

    [Fact]
    public async Task GetPolicyAsync_ReturnsNull_WhenIdUnknown()
    {
        await using var fx = new ChaosEndpointsFixture();
        var client = new ChaosProxyClient(WithBaseAddress(fx));

        Assert.Null(await client.GetPolicyAsync("nope"));
    }

    [Fact]
    public async Task GetFireCountsAsync_ReturnsNull_WhenIdUnknown()
    {
        await using var fx = new ChaosEndpointsFixture();
        var client = new ChaosProxyClient(WithBaseAddress(fx));

        Assert.Null(await client.GetFireCountsAsync("nope"));
    }

    [Fact]
    public async Task GetStateAsync_ReflectsCurrentStoreState()
    {
        await using var fx = new ChaosEndpointsFixture();
        var client = new ChaosProxyClient(WithBaseAddress(fx));

        await client.InstallPolicyAsync(new ChaosPolicy { Id = "p", Error = new ChaosError { Status = 503, Probability = 1.0 } });
        await client.PauseAsync();

        var state = await client.GetStateAsync();
        Assert.True(state.Paused);
        Assert.Equal(1, state.PolicyCount);
    }

    [Fact]
    public async Task PauseResume_ToggleStoreState()
    {
        await using var fx = new ChaosEndpointsFixture();
        var client = new ChaosProxyClient(WithBaseAddress(fx));

        await client.PauseAsync();
        Assert.True(fx.Store.IsPaused);

        await client.ResumeAsync();
        Assert.False(fx.Store.IsPaused);
    }

    [Fact]
    public async Task RemovePolicyAsync_ReturnsFalse_WhenIdUnknown()
    {
        await using var fx = new ChaosEndpointsFixture();
        var client = new ChaosProxyClient(WithBaseAddress(fx));

        Assert.False(await client.RemovePolicyAsync("nope"));
    }

    [Fact]
    public async Task RemovePolicyAsync_ReturnsTrue_AndRemovesFromStore()
    {
        await using var fx = new ChaosEndpointsFixture();
        var client = new ChaosProxyClient(WithBaseAddress(fx));
        await client.InstallPolicyAsync(new ChaosPolicy { Id = "p", Error = new ChaosError { Status = 503, Probability = 1.0 } });

        Assert.True(await client.RemovePolicyAsync("p"));
        Assert.Empty(fx.Store.GetActive());
    }

    [Fact]
    public async Task ClearPoliciesAsync_RemovesAllAndReturnsCount()
    {
        await using var fx = new ChaosEndpointsFixture();
        var client = new ChaosProxyClient(WithBaseAddress(fx));
        await client.InstallPolicyAsync(new ChaosPolicy { Id = "p1", Error = new ChaosError { Status = 503, Probability = 1.0 } });
        await client.InstallPolicyAsync(new ChaosPolicy { Id = "p2", Error = new ChaosError { Status = 504, Probability = 1.0 } });

        Assert.Equal(2, await client.ClearPoliciesAsync());
        Assert.Empty(fx.Store.GetActive());
    }

    [Fact]
    public async Task MatchAsync_PredictsFiringPolicies()
    {
        await using var fx = new ChaosEndpointsFixture();
        var client = new ChaosProxyClient(WithBaseAddress(fx));

        await client.InstallPolicyAsync(new ChaosPolicy
        {
            Id = "match-me",
            Matcher = new ChaosMatcher { PathPrefix = "/api/" },
            Error = new ChaosError { Status = 503, Probability = 1.0 },
        });

        var matches = await client.MatchAsync(method: "GET", path: "/api/x");
        Assert.Single(matches);
        Assert.Equal("match-me", matches[0].PolicyId);
        Assert.Contains("error", matches[0].TransformsThatWouldFire);
    }

    [Fact]
    public async Task FireOnceAsync_Global_ArmsTrigger()
    {
        await using var fx = new ChaosEndpointsFixture();
        var client = new ChaosProxyClient(WithBaseAddress(fx));

        await client.FireOnceAsync("error");

        Assert.Contains("error", fx.Store.GetArmedFireOnceTriggers());
    }

    [Fact]
    public async Task FireOnceAsync_PerPolicy_ArmsCompositeTrigger()
    {
        await using var fx = new ChaosEndpointsFixture();
        var client = new ChaosProxyClient(WithBaseAddress(fx));
        await client.InstallPolicyAsync(new ChaosPolicy { Id = "p", Error = new ChaosError { Status = 503, Probability = 1.0 } });

        await client.FireOnceAsync("p", "error");

        Assert.Contains("p:error", fx.Store.GetArmedFireOnceTriggers());
    }

    [Fact]
    public async Task ResetFireCountsAsync_ZeroesPolicyCounters()
    {
        await using var fx = new ChaosEndpointsFixture();
        var client = new ChaosProxyClient(WithBaseAddress(fx));
        await client.InstallPolicyAsync(new ChaosPolicy { Id = "p", Error = new ChaosError { Status = 503, Probability = 1.0 } });
        fx.Store.RecordFire("p", "error");
        fx.Store.RecordFire("p", "error");

        await client.ResetFireCountsAsync("p");

        Assert.Empty(fx.Store.GetFireCounts("p"));
    }

    [Fact]
    public async Task ExtendTtlAsync_BumpsExpirationForward()
    {
        await using var fx = new ChaosEndpointsFixture();
        var client = new ChaosProxyClient(WithBaseAddress(fx));
        await client.InstallPolicyAsync(new ChaosPolicy
        {
            Id = "p",
            TtlSeconds = 60,
            Error = new ChaosError { Status = 503, Probability = 1.0 },
        });

        var before = fx.Store.TryGet("p")!.ExpiresAt;
        var bumped = await client.ExtendTtlAsync("p", 600);

        Assert.NotNull(bumped);
        Assert.True(bumped > before);
    }

    [Fact]
    public async Task NonSuccessResponse_ThrowsWithStatusAndBody()
    {
        await using var fx = new ChaosEndpointsFixture();
        var client = new ChaosProxyClient(WithBaseAddress(fx));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.FireOnceAsync("not-a-real-transform"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.StatusCode);
    }

    /// <summary>
    /// Wraps the fixture's TestServer-backed HttpClient with a BaseAddress so the
    /// client can issue relative URLs (the production client expects BaseAddress set
    /// to the chaos proxy resource URL).
    /// </summary>
    private static HttpClient WithBaseAddress(ChaosEndpointsFixture fx)
    {
        fx.Client.BaseAddress ??= new Uri("http://localhost/");
        return fx.Client;
    }
}
