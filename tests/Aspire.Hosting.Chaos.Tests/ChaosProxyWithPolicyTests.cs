// <copyright file="ChaosProxyWithPolicyTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Chaos;
using Aspire.Chaos.Client;

namespace Aspire.Hosting.Chaos.UnitTests;

[SuppressMessage("AspireExperimental", "ASPIRECHAOS001", Justification = "test")]
public class ChaosProxyWithPolicyTests
{
    private static IDistributedApplicationBuilder CreateBuilder()
    {
        return DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            DisableDashboard = true,
            AssemblyName = typeof(ChaosProxyWithPolicyTests).Assembly.GetName().Name,
        });
    }

    private static string? GetPoliciesJson(IResource resource)
    {
        var dict = new Dictionary<string, object>();
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            var ctx = new EnvironmentCallbackContext(
                new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
                resource,
                dict,
                CancellationToken.None);
            annotation.Callback(ctx);
        }
        return dict.TryGetValue("CHAOS_POLICIES_JSON", out var value) ? value?.ToString() : null;
    }

    [Fact]
    public void WithPolicy_NoTransforms_Throws()
    {
        var builder = CreateBuilder();

        Assert.Throws<ArgumentException>(() => builder.AddChaosProxy("p")
            .WithPolicy(new ChaosPolicy { Id = "empty" }));
    }

    [Fact]
    public void WithPolicy_SingleLatency_SerializesPolicyToEnvVar()
    {
        var builder = CreateBuilder();
        var proxy = builder.AddChaosProxy("p").WithPolicy(new ChaosPolicy
        {
            Id = "slow",
            Latency = new ChaosLatency
            {
                Min = TimeSpan.FromMilliseconds(100),
                Max = TimeSpan.FromMilliseconds(300),
            },
        });

        var json = GetPoliciesJson(proxy.Resource);

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var arr = doc.RootElement;
        Assert.Equal(1, arr.GetArrayLength());
        var policy = arr[0];
        Assert.Equal("slow", policy.GetProperty("id").GetString());
        Assert.Equal(100, policy.GetProperty("latency").GetProperty("minMs").GetInt32());
        Assert.Equal(300, policy.GetProperty("latency").GetProperty("maxMs").GetInt32());
    }

    [Fact]
    public void WithPolicy_MultipleCalls_AccumulatePolicies()
    {
        var builder = CreateBuilder();
        var proxy = builder.AddChaosProxy("p")
            .WithPolicy(new ChaosPolicy
            {
                Id = "first",
                Error = new ChaosError { Status = 503 },
            })
            .WithPolicy(new ChaosPolicy
            {
                Id = "second",
                Latency = new ChaosLatency { Min = TimeSpan.FromMilliseconds(10), Max = TimeSpan.FromMilliseconds(20) },
            })
            .WithPolicy(new ChaosPolicy
            {
                Id = "third",
                DropResponse = new ChaosDropResponse(),
            });

        var json = GetPoliciesJson(proxy.Resource);

        using var doc = JsonDocument.Parse(json!);
        Assert.Equal(3, doc.RootElement.GetArrayLength());
        Assert.Equal(new[] { "first", "second", "third" },
            doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToArray());
    }

    [Fact]
    public void WithPolicy_AllTransforms_SerializesEach()
    {
        var builder = CreateBuilder();
        var proxy = builder.AddChaosProxy("p").WithPolicy(new ChaosPolicy
        {
            Id = "kitchen-sink",
            Matcher = new ChaosMatcher { PathPrefix = "/api/" },
            Latency = new ChaosLatency { Min = TimeSpan.FromMilliseconds(10), Max = TimeSpan.FromMilliseconds(20) },
            Error = new ChaosError { Status = 500 },
            ReplayDuplicate = new ChaosReplayDuplicate { Probability = 0.5 },
            DropResponse = new ChaosDropResponse { Probability = 0.1 },
            RateLimit = new ChaosRateLimit { RequestsPerWindow = 100, Window = TimeSpan.FromMinutes(1) },
            HeaderTamper = new ChaosHeaderTamper { Set = new Dictionary<string, string> { ["X-Foo"] = "bar" } },
            PartialResponse = new ChaosPartialResponse { Body = "hi", AdvertisedContentLength = 999 },
            IdempotencyCollision = new ChaosIdempotencyKeyCollision { Window = TimeSpan.FromSeconds(30) },
        });

        var json = GetPoliciesJson(proxy.Resource);

        using var doc = JsonDocument.Parse(json!);
        var policy = doc.RootElement[0];
        Assert.True(policy.TryGetProperty("matcher", out _));
        Assert.True(policy.TryGetProperty("latency", out _));
        Assert.True(policy.TryGetProperty("error", out _));
        Assert.True(policy.TryGetProperty("replayDuplicate", out _));
        Assert.True(policy.TryGetProperty("dropResponse", out _));
        Assert.True(policy.TryGetProperty("rateLimit", out _));
        Assert.True(policy.TryGetProperty("headerTamper", out _));
        Assert.True(policy.TryGetProperty("partialResponse", out _));
        Assert.True(policy.TryGetProperty("idempotencyCollision", out _));
    }
}
