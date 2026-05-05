// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Telemetry;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Telemetry;

public class ProfilingTelemetryContextTests
{
    [Fact]
    public void Create_GeneratesSessionIdAndCapturesParentTraceContext()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "test-startup-context",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var source = new ActivitySource("test-startup-context");
        using var activity = source.StartActivity("parent");
        Assert.NotNull(activity);

        var context = ProfilingTelemetryContext.Create(activity);

        Assert.False(string.IsNullOrWhiteSpace(context.SessionId));
        Assert.Equal(activity.Id, context.TraceParent);
        Assert.True(context.TryGetActivityContext(out var activityContext));
        Assert.Equal(activity.TraceId, activityContext.TraceId);
        Assert.Equal(activity.SpanId, activityContext.SpanId);
    }

    [Fact]
    public void AddToEnvironment_EmitsOnlyAvailableValues()
    {
        var context = ProfilingTelemetryContext.FromConfiguration(CreateConfiguration(
            (ProfilingTelemetryContext.EnabledEnvironmentVariable, "true"),
            (ProfilingTelemetryContext.SessionIdEnvironmentVariable, "session-1")));

        Assert.NotNull(context);

        var environment = new Dictionary<string, string>();
        context.AddToEnvironment(environment);

        Assert.Equal("true", environment[ProfilingTelemetryContext.EnabledEnvironmentVariable]);
        Assert.Equal("session-1", environment[ProfilingTelemetryContext.SessionIdEnvironmentVariable]);
        Assert.Equal("true", environment[KnownConfigNames.Legacy.StartupProfilingEnabled]);
        Assert.Equal("session-1", environment[KnownConfigNames.Legacy.StartupOperationId]);
        Assert.False(environment.ContainsKey(ProfilingTelemetryContext.TraceParentEnvironmentVariable));
        Assert.False(environment.ContainsKey(ProfilingTelemetryContext.TraceStateEnvironmentVariable));
    }

    [Fact]
    public void FromConfiguration_ReturnsNullWhenSessionIdIsMissing()
    {
        var context = ProfilingTelemetryContext.FromConfiguration(CreateConfiguration(
            (ProfilingTelemetryContext.EnabledEnvironmentVariable, "true")));

        Assert.Null(context);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("0")]
    public void FromConfiguration_ReturnsNullWhenProfilingIsNotEnabled(string? enabled)
    {
        var context = ProfilingTelemetryContext.FromConfiguration(CreateConfiguration(
            (ProfilingTelemetryContext.EnabledEnvironmentVariable, enabled),
            (ProfilingTelemetryContext.SessionIdEnvironmentVariable, "session-1")));

        Assert.Null(context);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("1")]
    public void IsEnabled_ReturnsTrueForSupportedTrueValues(string enabled)
    {
        var isEnabled = ProfilingTelemetryContext.IsEnabled(CreateConfiguration(
            (ProfilingTelemetryContext.EnabledEnvironmentVariable, enabled)));

        Assert.True(isEnabled);
    }

    [Fact]
    public void FromConfiguration_ReadsLegacyStartupNames()
    {
        var context = ProfilingTelemetryContext.FromConfiguration(CreateConfiguration(
            (KnownConfigNames.Legacy.StartupProfilingEnabled, "true"),
            (KnownConfigNames.Legacy.StartupOperationId, "session-1"),
            (KnownConfigNames.Legacy.StartupTraceParent, "00-0102030405060708090a0b0c0d0e0f10-1112131415161718-01"),
            (KnownConfigNames.Legacy.StartupTraceState, "state-1")));

        Assert.NotNull(context);
        Assert.Equal("session-1", context.SessionId);
        Assert.Equal("00-0102030405060708090a0b0c0d0e0f10-1112131415161718-01", context.TraceParent);
        Assert.Equal("state-1", context.TraceState);
    }

    private static IConfiguration CreateConfiguration(params (string Key, string? Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
    }
}
