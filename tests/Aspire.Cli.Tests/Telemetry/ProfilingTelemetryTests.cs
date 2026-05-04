// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Telemetry;

namespace Aspire.Cli.Tests.Telemetry;

public class ProfilingTelemetryTests
{
    [Fact]
    public void StartRunCommand_ReturnsInactiveScopeWhenStartupProfilingIsDisabled()
    {
        Activity? startedActivity = null;
        using var listener = CreateProfilingActivityListener(activity => startedActivity = activity);
        using var profilingTelemetry = new ProfilingTelemetry(CreateExecutionContext(new Dictionary<string, string?>()));

        using var activity = profilingTelemetry.StartRunCommand(startupTelemetryContext: null);

        Assert.False(activity.IsRunning);
        Assert.Null(startedActivity);
    }

    [Fact]
    public void StartRunCommand_UsesDedicatedProfilingActivitySource()
    {
        Activity? startedActivity = null;
        using var listener = CreateProfilingActivityListener(activity => startedActivity = activity);
        using var profilingTelemetry = new ProfilingTelemetry(CreateExecutionContext(new Dictionary<string, string?>
        {
            [StartupTelemetryContext.EnabledEnvironmentVariable] = "true",
            [StartupTelemetryContext.OperationIdEnvironmentVariable] = "operation-1"
        }));

        using var activity = profilingTelemetry.StartRunCommand(startupTelemetryContext: null);

        Assert.True(activity.IsRunning);
        Assert.NotNull(startedActivity);
        Assert.Equal(ProfilingTelemetry.ActivitySourceName, startedActivity.Source.Name);
        Assert.Equal("operation-1", startedActivity.GetTagItem(ProfilingTelemetry.Tags.StartupOperationId));
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

    private static CliExecutionContext CreateExecutionContext(IReadOnlyDictionary<string, string?> environmentVariables)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        return new CliExecutionContext(
            directory,
            directory,
            directory,
            directory,
            directory,
            "test.log",
            environmentVariables: environmentVariables,
            homeDirectory: directory);
    }
}
