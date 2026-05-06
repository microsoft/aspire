// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Telemetry;

public class ProfilingTelemetryTests
{
    [Fact]
    public void StartRunCommand_ReturnsInactiveScopeWhenProfilingIsDisabled()
    {
        Activity? startedActivity = null;
        using var listener = CreateProfilingActivityListener(activity => startedActivity = activity);
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration());

        using var activity = profilingTelemetry.StartRunCommand(profilingTelemetryContext: null);

        Assert.False(activity.IsRunning);
        Assert.Null(startedActivity);
    }

    [Fact]
    public void StartRunCommand_UsesDedicatedProfilingActivitySource()
    {
        Activity? startedActivity = null;
        using var listener = CreateProfilingActivityListener(activity => startedActivity = activity);
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (ProfilingTelemetryContext.EnabledEnvironmentVariable, "true"),
            (ProfilingTelemetryContext.SessionIdEnvironmentVariable, "session-1")));

        using var activity = profilingTelemetry.StartRunCommand(profilingTelemetryContext: null);

        Assert.True(activity.IsRunning);
        Assert.NotNull(startedActivity);
        Assert.Equal(ProfilingTelemetry.ActivitySourceName, startedActivity.Source.Name);
        Assert.Equal("session-1", startedActivity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId));
        Assert.Equal("session-1", startedActivity.GetTagItem(ProfilingTelemetry.Tags.LegacyStartupOperationId));
    }

    private static ActivityListener CreateProfilingActivityListener(Action<Activity> activityStarted)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ProfilingTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activityStarted
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static IConfiguration CreateConfiguration(params (string Key, string? Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
    }
}
