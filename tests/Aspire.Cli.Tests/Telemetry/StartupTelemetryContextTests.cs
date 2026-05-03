// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Telemetry;

namespace Aspire.Cli.Tests.Telemetry;

public class StartupTelemetryContextTests
{
    [Fact]
    public void Create_GeneratesOperationIdAndCapturesParentTraceContext()
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

        var context = StartupTelemetryContext.Create(activity);

        Assert.False(string.IsNullOrWhiteSpace(context.OperationId));
        Assert.Equal(activity.Id, context.TraceParent);
        Assert.True(context.TryGetActivityContext(out var activityContext));
        Assert.Equal(activity.TraceId, activityContext.TraceId);
        Assert.Equal(activity.SpanId, activityContext.SpanId);
    }

    [Fact]
    public void AddToEnvironment_EmitsOnlyAvailableValues()
    {
        var context = StartupTelemetryContext.FromEnvironment(name => name switch
        {
            StartupTelemetryContext.OperationIdEnvironmentVariable => "operation-1",
            _ => null
        });

        Assert.NotNull(context);

        var environment = new Dictionary<string, string>();
        context.AddToEnvironment(environment);

        Assert.Equal("operation-1", environment[StartupTelemetryContext.OperationIdEnvironmentVariable]);
        Assert.False(environment.ContainsKey(StartupTelemetryContext.TraceParentEnvironmentVariable));
        Assert.False(environment.ContainsKey(StartupTelemetryContext.TraceStateEnvironmentVariable));
    }

    [Fact]
    public void FromEnvironment_ReturnsNullWhenOperationIdIsMissing()
    {
        var context = StartupTelemetryContext.FromEnvironment(_ => null);

        Assert.Null(context);
    }
}
