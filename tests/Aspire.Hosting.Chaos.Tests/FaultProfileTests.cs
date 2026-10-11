// <copyright file="FaultProfileTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Globalization;
using ChaosProxy.Container.Policy.Profiles;

namespace Aspire.Hosting.Chaos.UnitTests;

public sealed class FaultProfileTests
{
    [Fact]
    public void Registry_LoadsEmbeddedServiceHttpProfile()
    {
        var registry = FaultProfileRegistry.CreateDefault();

        Assert.Contains(FaultProfileRegistry.DefaultProfileId, registry.Ids);

        var profile = registry.TryGet("service.http");
        Assert.NotNull(profile);
        Assert.Equal("service.http", profile!.Id);
        Assert.NotEmpty(profile.Entries);
        Assert.Equal(4, profile.SafeFailFirstMax);
    }

    [Fact]
    public void Registry_Resolve_FallsBackToDefaultForUnknownId()
    {
        var registry = FaultProfileRegistry.CreateDefault();

        var resolved = registry.Resolve("does.not.exist");

        Assert.Equal(FaultProfileRegistry.DefaultProfileId, resolved.Id);
    }

    [Fact]
    public void Sampler_IsDeterministic_ForSameSeed()
    {
        var profile = FaultProfileRegistry.CreateDefault().Resolve("service.http");

        var first = DrawSequence(profile, seed: 1234, count: 50);
        var second = DrawSequence(profile, seed: 1234, count: 50);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Sampler_DiffersForDifferentSeeds()
    {
        var profile = FaultProfileRegistry.CreateDefault().Resolve("service.http");

        var a = DrawSequence(profile, seed: 1, count: 50);
        var b = DrawSequence(profile, seed: 2, count: 50);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Sampler_HonorsWeights_OverManyDraws()
    {
        var profile = FaultProfileRegistry.CreateDefault().Resolve("service.http");
        var rng = new Random(7);
        const int draws = 20_000;
        var counts = new int[profile.Entries.Count];

        for (var i = 0; i < draws; i++)
        {
            counts[FaultProfileSampler.Sample(profile, rng).EntryIndex]++;
        }

        var totalWeight = profile.Entries.Sum(e => e.Weight);
        for (var i = 0; i < profile.Entries.Count; i++)
        {
            var expected = profile.Entries[i].Weight / totalWeight;
            var actual = (double)counts[i] / draws;
            Assert.True(
                Math.Abs(expected - actual) < 0.03,
                $"Entry {i}: expected ~{expected:F3}, got {actual:F3}");
        }
    }

    [Fact]
    public void Sampler_SubstitutesParamRangeTokens_InHeaders()
    {
        var profile = FaultProfileRegistry.FromProfiles(new FaultProfile
        {
            Id = "test.throttle",
            Entries = new[]
            {
                new FaultProfileEntry
                {
                    Weight = 1.0,
                    Kind = "error",
                    Status = 429,
                    Headers = new Dictionary<string, string> { ["x-ms-retry-after-ms"] = "${retryAfterMs}" },
                    ParamRanges = new Dictionary<string, int[]> { ["retryAfterMs"] = new[] { 50, 500 } },
                },
            },
        }).Resolve("test.throttle");

        var rng = new Random(99);
        for (var i = 0; i < 100; i++)
        {
            var fault = FaultProfileSampler.Sample(profile, rng);

            Assert.Equal(SampledFaultKind.Error, fault.Kind);
            Assert.NotNull(fault.Error);
            var headerValue = fault.Error!.Headers!["x-ms-retry-after-ms"];
            Assert.DoesNotContain("${", headerValue);
            var parsed = int.Parse(headerValue, CultureInfo.InvariantCulture);
            Assert.InRange(parsed, 50, 500);
            Assert.Equal(parsed, fault.SampledParams["retryAfterMs"]);
        }
    }

    [Fact]
    public void Sampler_LatencyDelay_IsWithinDeclaredRange()
    {
        var profile = FaultProfileRegistry.FromProfiles(new FaultProfile
        {
            Id = "test.latency",
            Entries = new[]
            {
                new FaultProfileEntry { Weight = 1.0, Kind = "latency", MinMs = 200, MaxMs = 1500 },
            },
        }).Resolve("test.latency");

        var rng = new Random(5);
        for (var i = 0; i < 100; i++)
        {
            var fault = FaultProfileSampler.Sample(profile, rng);

            Assert.Equal(SampledFaultKind.Latency, fault.Kind);
            Assert.NotNull(fault.Latency);
            Assert.InRange(fault.Latency!.MinMs, 200, 1500);
            Assert.Equal(fault.Latency.MinMs, fault.Latency.MaxMs);
        }
    }

    [Fact]
    public void Sampler_DropResponse_MaterializesDropConfig()
    {
        var profile = FaultProfileRegistry.FromProfiles(new FaultProfile
        {
            Id = "test.drop",
            Entries = new[] { new FaultProfileEntry { Weight = 1.0, Kind = "dropResponse" } },
        }).Resolve("test.drop");

        var fault = FaultProfileSampler.Sample(profile, new Random(1));

        Assert.Equal(SampledFaultKind.DropResponse, fault.Kind);
        Assert.NotNull(fault.DropResponse);
        Assert.Equal(1.0, fault.DropResponse!.Probability);
    }

    [Fact]
    public void Registry_RejectsProfileWithNoEntries()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FaultProfileRegistry.FromProfiles(new FaultProfile
            {
                Id = "empty",
                Entries = Array.Empty<FaultProfileEntry>(),
            }));

        Assert.Contains("no entries", ex.Message);
    }

    [Fact]
    public void Registry_LoadsAllAzureProfiles()
    {
        var registry = FaultProfileRegistry.CreateDefault();

        Assert.Contains("azure.cosmos", registry.Ids);
        Assert.Contains("azure.storagequeue", registry.Ids);
        Assert.Contains("azure.keyvault", registry.Ids);

        Assert.Equal(8, registry.TryGet("azure.cosmos")!.SafeFailFirstMax);
        Assert.Equal(4, registry.TryGet("azure.storagequeue")!.SafeFailFirstMax);
        Assert.Equal(4, registry.TryGet("azure.keyvault")!.SafeFailFirstMax);
    }

    [Fact]
    public void Sampler_AzureCosmos_ProducesOnlyRealisticCosmosStatuses()
    {
        var profile = FaultProfileRegistry.CreateDefault().Resolve("azure.cosmos");
        var rng = new Random(11);
        var allowedStatuses = new HashSet<int> { 429, 503, 449, 412 };

        for (var i = 0; i < 500; i++)
        {
            var fault = FaultProfileSampler.Sample(profile, rng);
            if (fault.Kind == SampledFaultKind.Error)
            {
                Assert.Contains(fault.Error!.Status, allowedStatuses);
            }
            else
            {
                Assert.Equal(SampledFaultKind.Latency, fault.Kind);
            }
        }
    }

    [Fact]
    public void Sampler_AzureCosmosThrottle_SubstitutesRetryAfterMsInHeaderAndBody()
    {
        // Single-entry cosmos throttle profile to isolate the 429 token substitution.
        var profile = FaultProfileRegistry.FromProfiles(new FaultProfile
        {
            Id = "cosmos.throttle.only",
            Entries = new[]
            {
                new FaultProfileEntry
                {
                    Weight = 1.0,
                    Kind = "error",
                    Status = 429,
                    ContentType = "application/json",
                    Headers = new Dictionary<string, string> { ["x-ms-retry-after-ms"] = "${retryAfterMs}" },
                    ParamRanges = new Dictionary<string, int[]> { ["retryAfterMs"] = new[] { 50, 500 } },
                    Body = "{\"retryAfterMilliseconds\":${retryAfterMs}}",
                },
            },
        }).Resolve("cosmos.throttle.only");

        var rng = new Random(3);
        for (var i = 0; i < 50; i++)
        {
            var fault = FaultProfileSampler.Sample(profile, rng);
            var sampled = fault.SampledParams["retryAfterMs"];
            Assert.Equal(sampled.ToString(CultureInfo.InvariantCulture), fault.Error!.Headers!["x-ms-retry-after-ms"]);
            Assert.Contains($"\"retryAfterMilliseconds\":{sampled}", fault.Error.Body);
            Assert.DoesNotContain("${", fault.Error.Body);
        }
    }

    private static List<string> DrawSequence(FaultProfile profile, int seed, int count)
    {
        var rng = new Random(seed);
        var sequence = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var fault = FaultProfileSampler.Sample(profile, rng);
            var detail = fault.Kind switch
            {
                SampledFaultKind.Error => $"error:{fault.Error!.Status}",
                SampledFaultKind.Latency => $"latency:{fault.Latency!.MinMs}",
                SampledFaultKind.DropResponse => "drop",
                _ => "?",
            };
            sequence.Add($"{fault.EntryIndex}:{detail}");
        }

        return sequence;
    }
}
