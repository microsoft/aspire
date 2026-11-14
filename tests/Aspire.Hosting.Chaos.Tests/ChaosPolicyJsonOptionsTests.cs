// <copyright file="ChaosPolicyJsonOptionsTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Text.Json;
using Aspire.Hosting.Chaos;
using Aspire.Chaos.Client;

namespace Aspire.Hosting.Chaos.UnitTests;

/// <summary>
/// Tests the JSON contract that the AppHost-side <see cref="ChaosPolicy"/> records
/// serialize to and that the container's policy loader consumes. Wire incompatibility
/// here means policies set via WithPolicy(...) silently fail at container startup -
/// these tests are the canary for that.
/// </summary>
public class ChaosPolicyJsonOptionsTests
{
    [Fact]
    public void Serialize_TimeSpan_BecomesMilliseconds()
    {
        var policy = new ChaosPolicy
        {
            Id = "p",
            Latency = new ChaosLatency
            {
                Min = TimeSpan.FromMilliseconds(100),
                Max = TimeSpan.FromMilliseconds(500),
            },
        };

        var json = SerializeCamelCase(policy);

        Assert.Contains("\"minMs\":100", json);
        Assert.Contains("\"maxMs\":500", json);
    }

    [Fact]
    public void Serialize_TimeSpan_RoundTripsViaContainerDtoShape()
    {
        var policy = new ChaosPolicy
        {
            Id = "p",
            Latency = new ChaosLatency
            {
                Min = TimeSpan.FromSeconds(2),
                Max = TimeSpan.FromSeconds(5),
            },
        };

        var json = SerializeCamelCase(policy);
        using var doc = JsonDocument.Parse(json);
        var latency = doc.RootElement.GetProperty("latency");

        Assert.Equal(2000, latency.GetProperty("minMs").GetInt32());
        Assert.Equal(5000, latency.GetProperty("maxMs").GetInt32());
    }

    [Fact]
    public void Serialize_EnumDirection_BecomesString()
    {
        var policy = new ChaosPolicy
        {
            Id = "p",
            HeaderTamper = new ChaosHeaderTamper
            {
                Direction = ChaosHeaderTamperDirection.Request,
                Set = new Dictionary<string, string> { ["X-Foo"] = "bar" },
            },
        };

        var json = SerializeCamelCase(policy);

        // Without the JsonStringEnumConverter we registered, this would serialize as
        // an integer (0/1/2) which the container can't parse from string?.
        Assert.Contains("\"direction\":\"Request\"", json);
        Assert.DoesNotContain("\"direction\":0", json);
    }

    [Fact]
    public void Serialize_NullProperties_AreOmitted()
    {
        // The container's DTOs use null-checks to decide whether each transform is
        // enabled. Serializing null fields would either crash the deserializer or
        // accidentally enable transforms that the author didn't configure.
        var policy = new ChaosPolicy
        {
            Id = "p",
            Latency = new ChaosLatency { Min = TimeSpan.FromMilliseconds(10), Max = TimeSpan.FromMilliseconds(20) },
        };

        var json = SerializeCamelCase(policy);

        Assert.DoesNotContain("\"error\":null", json);
        Assert.DoesNotContain("\"replayDuplicate\":null", json);
        Assert.DoesNotContain("\"dropResponse\":null", json);
        Assert.DoesNotContain("\"rateLimit\":null", json);
        Assert.DoesNotContain("\"headerTamper\":null", json);
        Assert.DoesNotContain("\"partialResponse\":null", json);
        Assert.DoesNotContain("\"idempotencyCollision\":null", json);
    }

    [Fact]
    public void Serialize_PropertyNames_AreCamelCase()
    {
        var policy = new ChaosPolicy
        {
            Id = "p",
            RateLimit = new ChaosRateLimit
            {
                RequestsPerWindow = 10,
                Window = TimeSpan.FromSeconds(5),
            },
        };

        var json = SerializeCamelCase(policy);

        Assert.Contains("\"requestsPerWindow\":10", json);
        Assert.Contains("\"windowMs\":5000", json);
        // PascalCase originals should NOT appear.
        Assert.DoesNotContain("\"RequestsPerWindow\"", json);
        Assert.DoesNotContain("\"WindowMs\"", json);
    }

    [Fact]
    public void Serialize_IdempotencyKeyCollision_RoundTripsWindowAsMs()
    {
        var policy = new ChaosPolicy
        {
            Id = "p",
            IdempotencyCollision = new ChaosIdempotencyKeyCollision
            {
                Window = TimeSpan.FromMinutes(2),
                Status = 409,
            },
        };

        var json = SerializeCamelCase(policy);

        Assert.Contains("\"windowMs\":120000", json);
        Assert.Contains("\"status\":409", json);
    }

    private static string SerializeCamelCase<T>(T value)
        => JsonSerializer.Serialize(value, GetCamelCaseOptions());

    /// <summary>
    /// Reflects out the internal ChaosPolicyJsonOptions.CamelCase used by the library
    /// at serialize time, so these tests assert the EXACT same options that
    /// WithPolicy(...) uses.
    /// </summary>
    private static JsonSerializerOptions GetCamelCaseOptions()
    {
        var optionsType = typeof(ChaosPolicy).Assembly.GetType("Aspire.Chaos.Client.ChaosPolicyJsonOptions", throwOnError: true)!;
        var field = optionsType.GetField("CamelCase", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        return (JsonSerializerOptions)field.GetValue(null)!;
    }
}
